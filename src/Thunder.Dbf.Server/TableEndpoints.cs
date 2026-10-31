//      GET    /tables                       ProjectThunder :: Thunder.Dbf
//      GET    /tables/{name}                 ___________________
//      GET    /tables/{name}/records        |  ______________   |
//      GET    /tables/{name}/records/7      | | > SEEK "Smith"|  |    The routes. Every one checks, in this order:
//      GET    /tables/{name}/seek           | | Record 7 of 8 |  |      1. which key is calling
//      POST   /tables/{name}/records        | |______________|  |      2. that the table exists AND the key may read it
//      PATCH  /tables/{name}/records/7      |___________________|      3. for writes: the table accepts writes AND the key may write it
//      DELETE /tables/{name}/records/7          _|_______|_            4. which fields the key may see
//                                              [___________]

using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Thunder.Dbf.Server;

/// <summary>Maps the HTTP API onto <see cref="TableStore"/>.</summary>
public static partial class TableEndpoints
{
    public static void MapThunderApi(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ThunderOptions>>().Value;

        app.MapGet("/health", (TableStore store) => Directory.Exists(store.Folder)
                ? Results.Ok(new HealthResponse("ok"))
                : Results.Json(new HealthResponse("unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous()
            .WithTags("Health")
            .WithSummary("Whether the service is up and can see its data folder.")
            .Produces<HealthResponse>()
            .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

        var tables = app.MapGroup("/tables")
            .RequireAuthorization()
            .WithTags("Tables")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        tables.MapGet("/", ListTables)
            .WithSummary("The tables this key may read.")
            .Produces<TableListResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        tables.MapGet("/{name}", GetSchema)
            .WithSummary("A table's fields, types, widths and index tags.")
            .Produces<TableSchema>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tables.MapGet("/{name}/records", ListRecords)
            .WithSummary("A page of records in record-number order.")
            .WithDescription("skip and take count record numbers, so a page can hold fewer than take records when some are deleted. take is capped by Thunder:Paging:MaxTake.")
            .Produces<RecordPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tables.MapGet("/{name}/records/{recno:int}", GetRecord)
            .WithSummary("One record by its 1-based record number (dBASE's RECNO()).")
            .Produces<Dictionary<string, object?>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tables.MapGet("/{name}/seek", Seek)
            .WithSummary("Records whose index key matches, like dBASE's SEEK. Character keys match as prefixes.")
            .Produces<SeekResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Write routes exist only when the data folder is writable at all, so a read-only
        // deployment doesn't even advertise them.
        if (!options.WritesEnabled)
            return;

        tables.MapPost("/{name}/records", AppendRecord)
            .WithSummary("Adds a record. Fields left out are blank.")
            .Accepts<Dictionary<string, object?>>("application/json")
            .Produces<Dictionary<string, object?>>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        tables.MapPatch("/{name}/records/{recno:int}", UpdateRecord)
            .WithSummary("Changes the given fields of a record. Every other field keeps its exact bytes.")
            .Accepts<Dictionary<string, object?>>("application/json")
            .Produces<Dictionary<string, object?>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        tables.MapDelete("/{name}/records/{recno:int}", DeleteRecord)
            .WithSummary("Marks a record deleted, like dBASE's DELETE. It stays in the file until the legacy app packs the table.")
            .Produces<Dictionary<string, object?>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    // ---- reads ----------------------------------------------------------------------------

    private static IResult ListTables(HttpContext http, AccessRegistry registry, TableStore store, IOptions<ThunderOptions> options)
    {
        var key = registry.For(http.User);
        if (!key.CanListTables)
            throw ApiProblem.Forbidden("This key isn't allowed to list tables.", "Ask for CanListTables on the key, or use the table names you were given.");

        var visible = store.TableNames.Where(key.CanRead)
            .Select(t => new TableSummary(t, CanWrite(key, t, options.Value)))
            .ToList();
        return Results.Ok(new TableListResponse(visible));
    }

    private static async Task<IResult> GetSchema(string name, HttpContext http, AccessRegistry registry, TableStore store,
        IOptions<ThunderOptions> options, ILogger<TableStore> logger)
    {
        var (key, table) = Resolve(http, name, registry, store);
        var canWrite = CanWrite(key, table, options.Value);

        var schema = await store.ReadAsync(table, t =>
        {
            var fields = RecordJson.VisibleFields(t, f => key.CanSeeField(table, f))
                .Select(x => new FieldSchema(x.Field.Name, x.Field.Type.ToString(), RecordJson.TypeName(x.Field), x.Field.Length,
                    x.Field.DecimalCount, x.Field.IsNullable, x.Field.IsAutoIncrement, RecordJson.JsonType(x.Field),
                    canWrite && RecordJson.IsWritable(x.Field)))
                .ToList();

            return new TableSchema(table, t.RecordCount, t.Header.LastUpdate, t.Encoding.CodePage, t.MemoFormat?.ToString(), canWrite,
                fields, VisibleTags(t, key, table, logger));
        }, http.RequestAborted);
        return Results.Ok(schema);
    }

    private static async Task<IResult> ListRecords(string name, int? skip, int? take, bool? includeDeleted, HttpContext http,
        AccessRegistry registry, TableStore store, IOptions<ThunderOptions> options)
    {
        var (key, table) = Resolve(http, name, registry, store);
        var paging = options.Value.Paging;
        var from = skip ?? 0;
        var count = Math.Min(take ?? paging.DefaultTake, paging.MaxTake);
        if (from < 0)
            throw ApiProblem.BadRequest("skip can't be negative.");
        if (count < 1)
            throw ApiProblem.BadRequest("take must be at least 1.");
        var withDeleted = includeDeleted ?? false;

        var page = await store.ReadAsync(table, t =>
        {
            var records = new List<IReadOnlyDictionary<string, object?>>();
            var budget = new ResponseBudget(paging.MaxResponseBytes);
            var end = (int)Math.Min((long)from + count, t.RecordCount);
            for (var i = from; i < end; i++)
            {
                http.RequestAborted.ThrowIfCancellationRequested();
                var record = t.Read(i);
                if (!withDeleted && record.IsDeleted)
                    continue;
                var json = RecordJson.ToJson(record, t, f => key.CanSeeField(table, f));
                if (!budget.TryAdd(json) && records.Count > 0)
                {
                    end = i; // big memos: stop here and let the next page start at this record
                    break;
                }
                records.Add(json);
            }
            var next = end < t.RecordCount
                ? $"/tables/{Uri.EscapeDataString(table)}/records?skip={end}&take={count}{(withDeleted ? "&includeDeleted=true" : "")}"
                : null;
            return new RecordPage(table, from, count, t.RecordCount, records, next);
        }, http.RequestAborted);
        return Results.Ok(page);
    }

    private static async Task<IResult> GetRecord(string name, int recno, HttpContext http, AccessRegistry registry, TableStore store)
    {
        var (key, table) = Resolve(http, name, registry, store);
        var record = await store.ReadAsync(table, t => RecordJson.ToJson(ReadExisting(t, recno), t, f => key.CanSeeField(table, f)), http.RequestAborted);
        return Results.Ok(record);
    }

    private static async Task<IResult> Seek(string name, string? tag, string? key, int? take, bool? includeDeleted, HttpContext http,
        AccessRegistry registry, TableStore store, IOptions<ThunderOptions> options, ILogger<TableStore> logger)
    {
        var (access, table) = Resolve(http, name, registry, store);
        if (string.IsNullOrWhiteSpace(tag) || key is null)
            throw ApiProblem.BadRequest("Give both tag and key, like ?tag=NAME_TAG&key=Smith.", "GET /tables/{name} lists the index tags.");
        var count = Math.Min(take ?? options.Value.Paging.DefaultTake, options.Value.Paging.MaxTake);
        if (count < 1)
            throw ApiProblem.BadRequest("take must be at least 1.");

        var result = await store.ReadAsync(table, t =>
        {
            using var index = t.OpenProductionIndex()
                ?? throw ApiProblem.NotFound("This table has no index to seek with.");
            var found = index.Tags.FirstOrDefault(x => string.Equals(x.Name, tag, StringComparison.OrdinalIgnoreCase));
            if (found is null || !TagIsVisible(found, t, access, table))
                throw ApiProblem.NotFound("The table's index has no tag by that name.", "GET /tables/{name} lists the index tags.");

            var probe = SeekKey(found, key);
            var records = new List<IReadOnlyDictionary<string, object?>>();
            var budget = new ResponseBudget(options.Value.Paging.MaxResponseBytes);
            var scanned = 0;
            foreach (var entry in found.SeekExact(probe))
            {
                http.RequestAborted.ThrowIfCancellationRequested();
                // A short key on a big table can match most of it, and deleted records don't count
                // toward take, so the number of index entries looked at is capped too.
                if (++scanned > options.Value.Paging.MaxSeekScan)
                    break;
                if (entry.RecordNumber < 1 || entry.RecordNumber > t.RecordCount)
                    continue; // a stale index can point past the end; never trust it with a read
                var record = t.Read(entry.RecordNumber - 1);
                if (record.IsDeleted && includeDeleted != true)
                    continue;
                var json = RecordJson.ToJson(record, t, f => access.CanSeeField(table, f));
                if (!budget.TryAdd(json) && records.Count > 0)
                    break;
                records.Add(json);
                if (records.Count == count)
                    break;
            }
            return new SeekResponse(table, found.Name, key, records);
        }, http.RequestAborted);
        return Results.Ok(result);
    }

    // ---- writes ---------------------------------------------------------------------------

    private static async Task<IResult> AppendRecord(string name, HttpContext http, AccessRegistry registry, TableStore store,
        WriteJournal journal, IOptions<ThunderOptions> options)
    {
        var (key, table) = ResolveForWrite(http, name, registry, store, options.Value);
        using var body = await ReadBodyAsync(http);

        var created = await store.WriteAsync(table, t =>
        {
            var changes = RecordJson.Parse(body.RootElement, t, f => key.CanSeeField(table, f));
            var values = new object?[t.Fields.Count];
            foreach (var (i, value) in changes)
                values[i] = value;

            // The intent goes in the journal before the file changes, so a crash between the two
            // still leaves a trace of what was being written.
            journal.Append(Entry(http, key, table, t.RecordCount + 1, "insert", null, Requested(changes, t), JournalStatus.Intent));
            var index = t.Append(values);
            var after = t.Read(index);
            journal.Append(Entry(http, key, table, after.RecordNumber, "insert", null, RecordJson.ForJournal(after, t)));
            return RecordJson.ToJson(after, t, f => key.CanSeeField(table, f));
        }, http.RequestAborted);

        return Results.Created($"/tables/{Uri.EscapeDataString(table)}/records/{created[RecordJson.RecnoProperty]}", created);
    }

    private static async Task<IResult> UpdateRecord(string name, int recno, HttpContext http, AccessRegistry registry, TableStore store,
        WriteJournal journal, IOptions<ThunderOptions> options)
    {
        var (key, table) = ResolveForWrite(http, name, registry, store, options.Value);
        using var body = await ReadBodyAsync(http);

        var updated = await store.WriteAsync(table, t =>
        {
            var before = ReadExisting(t, recno);
            var changes = RecordJson.Parse(body.RootElement, t, f => key.CanSeeField(table, f));
            if (changes.Count == 0)
                return RecordJson.ToJson(before, t, f => key.CanSeeField(table, f));

            // Only the fields named in the body are written, under the record lock, onto the record
            // as it is on disk at that moment: a change the legacy app made a second ago to another
            // field survives. The journal gets the before and after the write actually saw.
            journal.Append(Entry(http, key, table, recno, "update", null, Requested(changes, t), JournalStatus.Intent));
            var named = changes.ToDictionary(c => t.Fields[c.Key].Name, c => c.Value, StringComparer.OrdinalIgnoreCase);
            var (seen, after) = t.UpdateFields(recno - 1, named);
            journal.Append(Entry(http, key, table, recno, "update", RecordJson.ForJournal(seen, t), RecordJson.ForJournal(after, t)));
            return RecordJson.ToJson(after, t, f => key.CanSeeField(table, f));
        }, http.RequestAborted);
        return Results.Ok(updated);
    }

    private static async Task<IResult> DeleteRecord(string name, int recno, HttpContext http, AccessRegistry registry, TableStore store,
        WriteJournal journal, IOptions<ThunderOptions> options)
    {
        var (key, table) = ResolveForWrite(http, name, registry, store, options.Value);

        var deleted = await store.WriteAsync(table, t =>
        {
            var before = ReadExisting(t, recno);
            journal.Append(Entry(http, key, table, recno, "delete", RecordJson.ForJournal(before, t), null, JournalStatus.Intent));
            t.Delete(recno - 1);
            var after = t.Read(recno - 1);
            journal.Append(Entry(http, key, table, recno, "delete", RecordJson.ForJournal(before, t), RecordJson.ForJournal(after, t)));
            return RecordJson.ToJson(after, t, f => key.CanSeeField(table, f));
        }, http.RequestAborted);
        return Results.Ok(deleted);
    }

    // ---- helpers --------------------------------------------------------------------------

    // A table the key can't read answers exactly like one that doesn't exist, so a key can't probe
    // for table names. Only keys allowed to list tables get the list as a hint.
    private static (KeyAccess Key, string Table) Resolve(HttpContext http, string name, AccessRegistry registry, TableStore store)
    {
        var key = registry.For(http.User);
        var table = store.Find(name);
        if (table is not null && key.CanRead(table))
            return (key, table);

        var hint = key.CanListTables
            ? $"Tables this key can read: {string.Join(", ", store.TableNames.Where(key.CanRead).DefaultIfEmpty("none"))}."
            : "Check the table name.";
        throw ApiProblem.NotFound("There's no table by that name that this key can read.", hint);
    }

    private static (KeyAccess Key, string Table) ResolveForWrite(HttpContext http, string name, AccessRegistry registry, TableStore store,
        ThunderOptions options)
    {
        var (key, table) = Resolve(http, name, registry, store);
        if (!options.TableAcceptsWrites(table))
            throw ApiProblem.Forbidden("This table is read-only.", "Writes are turned on per table in the server's configuration (Thunder:Tables).");
        if (!key.CanWrite(table))
            throw ApiProblem.Forbidden("This key can read this table but not write it.");
        return (key, table);
    }

    private static bool CanWrite(KeyAccess key, string table, ThunderOptions options) => options.TableAcceptsWrites(table) && key.CanWrite(table);

    private static DbfRecord ReadExisting(DbfTable table, int recno) =>
        recno >= 1 && recno <= table.RecordCount
            ? table.Read(recno - 1)
            : throw ApiProblem.NotFound($"There's no record {recno}.", $"Record numbers run from 1 to {table.RecordCount}.");

    private static async Task<JsonDocument> ReadBodyAsync(HttpContext http)
    {
        try
        {
            return await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (JsonException)
        {
            throw ApiProblem.BadRequest("The body isn't valid JSON.", "Send a JSON object of field names and values.");
        }
    }

    private static JournalEntry Entry(HttpContext http, KeyAccess key, string table, int recno, string operation,
        IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?>? after, string status = JournalStatus.Done) =>
        new(DateTimeOffset.UtcNow, key.Id, table, recno, operation, before, after, http.Connection.RemoteIpAddress?.ToString(), status);

    // The values a request asked for, by field name, for the journal's intent line.
    private static Dictionary<string, object?> Requested(IReadOnlyDictionary<int, object?> changes, DbfTable table) =>
        changes.ToDictionary(c => table.Fields[c.Key].Name, c => RecordJson.ForJournalValue(c.Value));

    // Keeps one response under a size limit: memo fields can make a page of 500 records very large.
    private sealed class ResponseBudget(long limit)
    {
        private long _used;

        public bool TryAdd(IReadOnlyDictionary<string, object?> record)
        {
            long size = 2;
            foreach (var (name, value) in record)
                size += name.Length + 4 + value switch
                {
                    string s => s.Length * 2L + 2, // escapes can double a character
                    byte[] b => b.Length * 4L / 3 + 4,
                    null => 4,
                    _ => 32,
                };
            if (_used + size > limit)
                return false;
            _used += size;
            return true;
        }
    }

    // ---- index tags -----------------------------------------------------------------------

    private static List<IndexTagSchema> VisibleTags(DbfTable table, KeyAccess key, string name, ILogger logger)
    {
        if (!table.Header.HasProductionMdx)
            return [];
        try
        {
            using var index = table.OpenProductionIndex();
            return index is null
                ? []
                : index.Tags.Where(t => TagIsVisible(t, table, key, name))
                    .Select(t => new IndexTagSchema(t.Name, t.Expression, t.ForExpression, t.KeyType.ToString(), t.IsUnique, t.IsDescending))
                    .ToList();
        }
        catch (Exception ex) when (ex is InvalidDataException or DbfException or IOException)
        {
            logger.LogWarning(ex, "Couldn't read the index of {Table}; its schema is served without tags.", name);
            return [];
        }
    }

    // A tag over a hidden field would leak it twice: its expression names the field, and seeking
    // by it reveals values. So a tag shows only when every field its expressions mention is a field
    // of this table the key can see. An expression the parser can't read, or one that names
    // something that isn't a field, is treated as hidden: when unsure, don't reveal.
    private static bool TagIsVisible(MdxTag tag, DbfTable table, KeyAccess key, string name)
    {
        IEnumerable<string> mentioned;
        try
        {
            mentioned = DbfExpression.FieldNamesIn(tag.Expression);
            if (!string.IsNullOrWhiteSpace(tag.ForExpression))
                mentioned = mentioned.Concat(DbfExpression.FieldNamesIn(tag.ForExpression));
        }
        catch (Exception ex) when (ex is DbfException or FormatException or ArgumentException or InvalidDataException)
        {
            return false;
        }
        return mentioned.All(word =>
            table.Fields.FirstOrDefault(f => f.Name.Equals(word, StringComparison.OrdinalIgnoreCase)) is { } field
            && key.CanSeeField(name, field.Name));
    }

    private static object SeekKey(MdxTag tag, string key)
    {
        switch (tag.KeyType)
        {
            case 'N' or 'F':
                return decimal.TryParse(key, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    ? number
                    : throw ApiProblem.BadRequest($"Tag {tag.Name} has numeric keys; key must be a number like 42 or 3.5.");
            case 'D':
                return DateOnly.TryParseExact(key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date)
                    ? date
                    : throw ApiProblem.BadRequest($"Tag {tag.Name} has date keys; key must be a date like 1997-04-12.");
            default:
                return key;
        }
    }
}
