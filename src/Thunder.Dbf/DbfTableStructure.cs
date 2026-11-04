//   MODIFY STRUCTURE
//
//   PEOPLE.DBF          PEOPLE.DBF.restructuring/          PEOPLE.DBF            ProjectThunder :: Thunder.Dbf
//   +-----------+       +--------------------------+       +-----------+         A table's fields changed the way
//   | NAME C 20 | copy  | PEOPLE.DBF  NAME C 40    | swap  | NAME C 40 |         dBASE and FoxPro change them:
//   | AGE  N 3  | ----> |             AGE  N 3     | ----> | AGE  N 3  |         a new table written beside the
//   +-----------+       |             CITY C 30    |       | CITY C 30 |         old one, every record carried
//                       | PEOPLE.DBT  (a copy)     |       +-----------+         across, swapped in once it's
//                       | PEOPLE.MDX  (rebuilt)    |                             whole. The old files are kept.
//                       +--------------------------+
//
//   The swap goes memo -> table -> index. The new memo file is the old one copied whole, plus blocks
//   for memos the change made, so the old .dbf reads it as well as the new one does: the .dbf is the
//   one file whose swap commits the change.

using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

public sealed partial class DbfTable
{
    private const string RestructurePartialSuffix = ".restructure-partial";
    private const string RestructureReadySuffix = ".restructuring";
    private const string RestructureLockName = "restructure.lock";

    /// <summary>
    /// The table's fields as definitions, in table order, without hidden system fields such as Visual
    /// FoxPro's <c>_NullFlags</c>: a starting point for <see cref="ModifyStructure"/>. Autoincrement fields
    /// carry the table's next number.
    /// </summary>
    public IReadOnlyList<DbfFieldDefinition> FieldDefinitions =>
        Fields.Select((f, i) => (Field: f, Index: i)).Where(x => !x.Field.IsSystem).Select(x => new DbfFieldDefinition(x.Field.Name, x.Field.Type, x.Field.Length, x.Field.DecimalCount)
        {
            Nullable = x.Field.IsNullable,
            AutoIncrement = x.Field.IsAutoIncrement,
            AutoIncrementNext = x.Field.IsAutoIncrement ? Header.NextAutoIncrement(x.Index) : 1,
            AutoIncrementStep = x.Field.IsAutoIncrement ? Math.Max(1, x.Field.AutoIncrementStep) : 1,
            Binary = Header.IsVisualFoxPro && x.Field.IsBinary && x.Field.Type is 'C' or 'M',
        }).ToList().AsReadOnly();

    /// <summary>
    /// Where <see cref="ModifyStructure"/> moved the table's old files (a folder named
    /// <c>TABLE.DBF.restructured-&lt;UTC time&gt;</c> beside it), on the table it returned; or where an
    /// exclusive open that finished a cut-off change moved them. Null otherwise.
    /// </summary>
    public string? StructureBackupPath { get; private set; }

    /// <summary>
    /// How many values <see cref="ModifyStructure"/> had to change to fit the new fields (shortened, rounded,
    /// or blanked), on the table it returned. Always 0 unless it was called with <c>allowLoss: true</c>.
    /// </summary>
    public int StructureValuesChanged { get; private set; }

    /// <summary>
    /// Changes the table's fields, like dBASE's and FoxPro's <c>MODIFY STRUCTURE</c>: adds, drops, renames,
    /// widens, narrows or retypes them. A new table is written beside this one with every record (deleted
    /// ones too, still deleted) and its values carried across by field name, and the production .mdx or
    /// structural .cdx is rebuilt with the same tags. Only once all of it is on disk does it replace this
    /// table, whose files are kept in <see cref="StructureBackupPath"/>.
    /// </summary>
    /// <param name="fields">The new fields, in order. Start from <see cref="FieldDefinitions"/>.</param>
    /// <param name="renames">Old field name to new name, for fields that keep their values under another name.</param>
    /// <param name="allowLoss">Shorten text, round numbers and blank values that don't fit their new field,
    /// instead of refusing the change (the default). <see cref="StructureValuesChanged"/> says how many changed.</param>
    /// <returns>The table with its new structure, opened as this one was. This table is closed: use the one returned.</returns>
    /// <remarks>
    /// Needs the table opened exclusively, as dBASE and FoxPro do. Values convert where the types allow:
    /// text holding a number to a numeric field, numbers and dates to text (dates as YYYYMMDD), a date to a
    /// datetime, and the other way when no time of day is lost. A change that would lose a value (text too
    /// long for its new width, a number too wide or with too many decimals, text that isn't a number, a
    /// Visual FoxPro .NULL. in a field that can't hold one) is refused, naming the record, unless
    /// <paramref name="allowLoss"/> is set. Memo fields keep their memos; the new memo file is a copy of the
    /// old, so memos of dropped fields stay in it as unused space, as dBASE leaves them. A tag whose
    /// expression names a field the change drops or renames is refused: drop it first with
    /// <see cref="DropIndexTag"/>. Other indexes (.ndx, .ntx, .idx) and Visual FoxPro tables in a database
    /// container (.dbc) are refused too.
    /// If the process is killed partway, the next exclusive writable open finishes the change, or, when the
    /// new .dbf wasn't in place yet, throws the new files away and leaves the table as it was.
    /// </remarks>
    public DbfTable ModifyStructure(IReadOnlyList<DbfFieldDefinition> fields, IReadOnlyDictionary<string, string>? renames = null, bool allowLoss = false)
    {
        using var forced = ForceEveryWrite();
        return ModifyStructureCore(fields, renames, allowLoss);
    }

    /// <summary>Adds a field after the others: <see cref="ModifyStructure"/> with one more field. Existing records get it blank.</summary>
    /// <returns>The table with its new structure. This table is closed: use the one returned.</returns>
    public DbfTable AddField(DbfFieldDefinition field) => ModifyStructure([.. FieldDefinitions, field]);

    /// <summary>
    /// Drops a field and its values: <see cref="ModifyStructure"/> without it. A dropped memo field's memos
    /// stay in the memo file, which is copied whole, as unused space, and in the backup. To be rid of
    /// sensitive text for good, copy the records you keep into a new table and delete the old files.
    /// </summary>
    /// <returns>The table with its new structure. This table is closed: use the one returned.</returns>
    public DbfTable DropField(string name)
    {
        var definitions = FieldDefinitions;
        if (!definitions.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"The table has no field named {name}.", nameof(name));
        return ModifyStructure(definitions.Where(f => !f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    /// <summary>
    /// Replaces one field's definition, keeping its place and its values (converted where the types allow):
    /// widen it, change its type, or rename it by giving <paramref name="field"/> another name.
    /// </summary>
    /// <returns>The table with its new structure. This table is closed: use the one returned.</returns>
    public DbfTable AlterField(string name, DbfFieldDefinition field, bool allowLoss = false)
    {
        var definitions = FieldDefinitions.ToList();
        var at = definitions.FindIndex(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            throw new ArgumentException($"The table has no field named {name}.", nameof(name));
        var oldName = definitions[at].Name;
        definitions[at] = field;
        var renames = oldName.Equals(field.Name, StringComparison.OrdinalIgnoreCase) ? null : new Dictionary<string, string> { [oldName] = field.Name };
        return ModifyStructure(definitions, renames, allowLoss);
    }

    private DbfTable ModifyStructureCore(IReadOnlyList<DbfFieldDefinition> fields, IReadOnlyDictionary<string, string>? renames, bool allowLoss)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(fields);
        if (_path is null)
            throw new DbfException(DbfErrorCode.UnsupportedFormat, "A table opened from a stream can't change its structure, since the new table is written beside it.",
                "Open the table by path.");
        RequireExclusive("MODIFY STRUCTURE");
        if (_locks is { HoldsNothing: false })
            throw new InvalidOperationException("Release the table's locks before changing its structure: the table is closed and opened again.");

        var format = RestructureFormat(fields);
        DbfHeader.Create(format, fields, Header.LanguageDriver); // the new fields are valid before anything else is looked at
        var sources = FieldSources(fields, renames);
        var tags = TagsToRebuild(fields, sources);

        var partial = _path + RestructurePartialSuffix;
        var ready = _path + RestructureReadySuffix;
        int changed;
        using (Locks.Table())
        {
            PrepareWrite(appending: false); // an unfinished pack or journal note comes first
            ThrowIfRestructureUnfinished();
            ThrowIfCantRestructure(fields);

            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true); // a change killed while it was being written: nothing ever used it
            try
            {
                changed = WriteRestructured(partial, format, fields, sources, tags, allowLoss);
            }
            catch
            {
                try
                {
                    Directory.Delete(partial, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the next change to clear; the original error is what the caller needs.
                }
                throw;
            }
            // Renamed whole once complete, so a folder under the ready name always holds a finished table.
            Directory.Move(partial, ready);
        }
        CrashPoint?.Invoke("staged");
        return SwapInRestructured(ready, changed, fields.Any(f => DbfValueCodec.IsMemoField(new DbfField(f.Name, char.ToUpperInvariant(f.Type), f.Length, 0, 0))));
    }

    // The format the table is in now, so the new one opens in the same programs. dBASE III and IV
    // both write 0x03 for a table without memos; without an .mdx it stays dBASE III when the new
    // fields allow (a memo then makes it 0x83, which dBASE IV, FoxPro and Clipper all open).
    private DbfTableFormat RestructureFormat(IReadOnlyList<DbfFieldDefinition> fields)
    {
        switch (Header.Version)
        {
            case 0x30 or 0x31:
                return DbfTableFormat.VisualFoxPro;
            case 0xF5:
                return DbfTableFormat.FoxPro2;
            case 0x83:
                return DbfTableFormat.DbaseIII;
            case 0x8B:
                return DbfTableFormat.DbaseIV;
            case 0x03 when Header.IsFoxProByCompanion:
                return DbfTableFormat.FoxPro2;
            case 0x03 when !Header.HasProductionMdx:
                try
                {
                    DbfHeader.Create(DbfTableFormat.DbaseIII, fields, Header.LanguageDriver);
                    return DbfTableFormat.DbaseIII;
                }
                catch (ArgumentException)
                {
                    return DbfTableFormat.DbaseIV; // a float field, a 20-digit number, more than 128 fields
                }
            case 0x03:
                return DbfTableFormat.DbaseIV;
            default:
                throw new DbfException(DbfErrorCode.UnsupportedFormat, $"Thunder.Dbf doesn't write tables of version 0x{Header.Version:X2}, so it can't change this one's structure.",
                    "Change it in the program that made it.", _path);
        }
    }

    // For each new field, the old field whose values it takes (by name, or by a rename), or -1 for a new one.
    private int[] FieldSources(IReadOnlyList<DbfFieldDefinition> fields, IReadOnlyDictionary<string, string>? renames)
    {
        int Old(string name) => Fields.ToList().FindIndex(f => !f.IsSystem && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var renamedTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in renames ?? new Dictionary<string, string>())
        {
            if (Old(from) < 0)
                throw new ArgumentException($"The table has no field named {from} to rename.", nameof(renames));
            if (!fields.Any(f => f.Name.Equals(to, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"{from} is renamed to {to}, which isn't one of the new fields.", nameof(renames));
            if (!renamedTo.TryAdd(to, from))
                throw new ArgumentException($"Two fields are renamed to {to}.", nameof(renames));
        }
        var renamedAway = (renames ?? new Dictionary<string, string>()).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return fields.Select(f => renamedTo.TryGetValue(f.Name, out var from) ? Old(from) : renamedAway.Contains(f.Name) ? -1 : Old(f.Name)).ToArray();
    }

    private sealed record RebuiltTag(string Name, string Expression, string? ForExpression, bool Unique, bool Descending, bool Candidate);

    // The tags of the production .mdx or structural .cdx, to build again over the new fields. A tag
    // naming a field the change drops or renames is refused here, before anything is written.
    private List<RebuiltTag> TagsToRebuild(IReadOnlyList<DbfFieldDefinition> fields, int[] sources)
    {
        var tags = new List<RebuiltTag>();
        if (_index is not null)
            tags.AddRange(_index.Tags.Select(t => new RebuiltTag(t.Name, t.Expression, t.ForExpression, t.IsUnique, t.IsDescending, Candidate: false)));
        foreach (var (file, index) in _attachedIndexes)
        {
            if (IsStructuralCdx(file) && index is CdxIndex cdx)
                tags.AddRange(cdx.Tags.Select(t => new RebuiltTag(t.Name, t.Expression, t.ForExpression, t.IsUnique && !t.IsCandidate, t.IsDescending, t.IsCandidate)));
        }

        foreach (var tag in tags)
        {
            var names = DbfExpression.FieldNamesIn(tag.Expression).Concat(tag.ForExpression is null ? [] : DbfExpression.FieldNamesIn(tag.ForExpression));
            foreach (var name in names)
            {
                var old = Fields.ToList().FindIndex(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var kept = fields.ToList().FindIndex(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (old >= 0 && (kept < 0 || sources[kept] != old))
                    throw new DbfException(DbfErrorCode.InvalidValue,
                        $"Tag {tag.Name} uses field {Fields[old].Name}, which this change {(sources.Contains(old) ? "renames" : "drops")}.",
                        "Drop the tag first (DropIndexTag), change the structure, then create it again over the new fields.", _path);
            }
        }
        return tags;
    }

    private void ThrowIfCantRestructure(IReadOnlyList<DbfFieldDefinition> fields)
    {
        if (Fields.Any(DbfValueCodec.IsMemoField) && _memo is null)
            throw new DbfException(DbfErrorCode.MemoFileMissing, "The table's memo file isn't open, so its memos can't be carried into the new structure.",
                "Open the table with its .dbt/.fpt next to it, not with ignoreMissingMemo.", _path);
        if (_attachedIndexes.Keys.FirstOrDefault(file => !IsStructuralCdx(file)) is { } other)
            throw new DbfException(DbfErrorCode.IndexWouldGoStale,
                $"The index {Path.GetFileName(other)} would go stale: changing the structure rebuilds only the production .mdx or structural .cdx.",
                "Open the table without that index, change the structure, then create the index again in the legacy app.", _path);
        if (Header.IsVisualFoxPro && (Header.BelongsToDatabase || BacklinkInUse()))
            throw new DbfException(DbfErrorCode.UnsupportedFormat,
                "The table belongs to a Visual FoxPro database (.dbc), which keeps its own record of the fields, so only Visual FoxPro can change them.",
                "Use MODIFY STRUCTURE in Visual FoxPro, or REMOVE TABLE it from the database first.", _path);
        var newLength = DbfHeader.Create(RestructureFormat(fields), fields, Header.LanguageDriver);
        if (newLength.HeaderLength + (long)RecordCount * newLength.RecordLength + 1 > MaxTableBytes)
            throw new DbfException(DbfErrorCode.InvalidValue, "With the new fields the table would pass the 2 GB dBASE and FoxPro can handle.",
                "Archive or pack older records first, or narrow the new fields.", _path);
    }

    // Visual FoxPro keeps the path of a table's .dbc in the 263 bytes after the descriptors; a free table leaves them zero.
    private bool BacklinkInUse()
    {
        var start = Header.HeaderLength - DbfHeader.BacklinkSize;
        if (start < 32 + 32 * Fields.Count + 1)
            return false;
        var backlink = new byte[DbfHeader.BacklinkSize];
        _stream.Position = start;
        _stream.ReadExactly(backlink);
        return backlink.Any(b => b != 0);
    }

    // Writes the new table into the partial folder: header, memo file (a copy of the old one), every
    // record, then the index, each tag built afresh. Returns how many values changed to fit.
    private int WriteRestructured(string partial, DbfTableFormat format, IReadOnlyList<DbfFieldDefinition> fields, int[] sources,
        List<RebuiltTag> tags, bool allowLoss)
    {
        Directory.CreateDirectory(partial);
        var stagedPath = Path.Combine(partial, Path.GetFileName(_path!));
        using (Create(stagedPath, format, fields, Header.LanguageDriver))
        {
        }
        if (_memo is not null && DbfMemoFile.FindFor(stagedPath, ReadHeaderOf(stagedPath)) is { } freshMemo)
        {
            // The old memo file, whole, under its own name: once it's swapped in, the old .dbf still finds every memo in it.
            File.Delete(freshMemo);
            using var copy = new FileStream(Path.Combine(partial, Path.GetFileName(DbfMemoFile.FindFor(_path!, Header)!)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _memo.CopyTo(copy);
            copy.Flush(flushToDisk: true);
        }

        using var staged = OpenPath(stagedPath, writable: true, Encoding, ignoreMissingMemo: false, allowUnmaintainedIndexes: false,
            maintainFoxProIndexes: true, exclusive: true);
        if (format == DbfTableFormat.FoxPro2)
            staged.Header.TreatIndexFlagAsCdx(); // a FoxPro 2 table without memos says 0x03, like dBASE: its tags go in a .cdx
        staged.FlushWritesToDisk = false; // nobody sees it until it's renamed; it's forced to disk whole before that

        // A field that stays autoincrement keeps counting from where it was (or later, if asked).
        for (var j = 0; j < fields.Count; j++)
        {
            if (staged.Fields[j].IsAutoIncrement && sources[j] >= 0 && Fields[sources[j]].IsAutoIncrement)
            {
                staged.Header.SetNextAutoIncrement(j, Math.Max(fields[j].AutoIncrementNext, Header.NextAutoIncrement(sources[j])));
                staged._dirtyCounters.Add(j);
            }
        }

        var changed = 0;
        var values = new object?[staged.Fields.Count];
        var encode = new bool[staged.Fields.Count];
        var copiedNulls = new List<(int Bit, bool Null)>();
        foreach (var record in ReadAll())
        {
            var raw = record.Raw!;
            var buffer = staged._buffer;
            Array.Clear(values);
            Array.Clear(encode);
            copiedNulls.Clear();
            buffer[0] = record.IsDeleted ? DeletedFlag : LiveFlag;
            if (staged.Header.NullFlagsFieldIndex is { } n)
                buffer.AsSpan(staged.Fields[n].Offset, staged.Fields[n].Length).Clear();

            for (var j = 0; j < fields.Count; j++)
            {
                var to = staged.Fields[j];
                encode[j] = true;
                if (sources[j] < 0)
                    continue; // a new field: blank, or numbered if it's autoincrement
                var from = Fields[sources[j]];
                var wasNull = record.IsNull(sources[j]);

                if (DbfValueCodec.IsMemoField(from) && DbfValueCodec.IsMemoField(to))
                {
                    // The memo stays where it is in the copied memo file: the new record points at the same block.
                    var block = DbfValueCodec.Decode(from, raw.AsSpan(from.Offset, from.Length), Encoding) as int?;
                    if (wasNull && !to.IsNullable)
                        changed += Lost(record, from, to, allowLoss);
                    values[j] = wasNull && to.IsNullable ? null : block is { } pointer ? pointer : "";
                    continue;
                }
                if (from.Type == 'C' && to.Type == 'C' && from.Length != to.Length && (to.Length > from.Length || from.IsBinary) && !(wasNull && !to.IsNullable))
                {
                    // Text widened, or binary (NOCPTRANS) text resized: its bytes go across as they are, padded
                    // with spaces as dBASE pads, never through the code page and back.
                    var kept = Math.Min(from.Length, to.Length);
                    raw.AsSpan(from.Offset, kept).CopyTo(buffer.AsSpan(to.Offset, kept));
                    buffer.AsSpan(to.Offset + kept, to.Length - kept).Fill((byte)' ');
                    if (raw.AsSpan(from.Offset + kept, from.Length - kept).IndexOfAnyExcept((byte)' ', (byte)0) >= 0)
                        changed += Lost(record, from, to, allowLoss);
                    values[j] = record[sources[j]];
                    encode[j] = false;
                    if (to.NullBit is { } bit)
                        copiedNulls.Add((bit, wasNull));
                    continue;
                }
                if (from.Type == to.Type && from.Length == to.Length && from.DecimalCount == to.DecimalCount && !(wasNull && !to.IsNullable))
                {
                    // Stored the same way: the bytes go across untouched, as the legacy app wrote them.
                    raw.AsSpan(from.Offset, from.Length).CopyTo(buffer.AsSpan(to.Offset, to.Length));
                    values[j] = record[sources[j]]; // for the autoincrement counter
                    encode[j] = false;
                    if (to.NullBit is { } bit)
                        copiedNulls.Add((bit, wasNull));
                    continue;
                }

                var (value, exact) = staged.Carried(record[sources[j]], wasNull, to);
                if (!exact)
                    changed += Lost(record, from, to, allowLoss);
                values[j] = value;
            }

            var counters = staged.AssignAutoIncrements(values);
            staged.EncodeFields(values, encode);
            if (staged.Header.NullFlagsFieldIndex is { } flags)
            {
                foreach (var (bit, isNull) in copiedNulls)
                    SetBit(buffer.AsSpan(staged.Fields[flags].Offset, staged.Fields[flags].Length), bit, isNull);
            }
            staged.AppendStaged(counters);
            CrashPoint?.Invoke("copying");
        }
        staged.Commit();
        FlushToDisk(staged._stream);
        staged._memo?.FlushToDisk();

        foreach (var tag in tags)
        {
            try
            {
                staged.CreateIndexTag(tag.Name, tag.Expression, tag.ForExpression, tag.Unique, tag.Descending, tag.Candidate);
            }
            catch (DbfException ex)
            {
                var reason = ex.Message.EndsWith(ex.Hint, StringComparison.Ordinal) ? ex.Message[..^ex.Hint.Length].TrimEnd() : ex.Message;
                throw new DbfException(ex.Code, $"Tag {tag.Name} can't be built over the new structure: {reason}", ex.Hint, _path, ex);
            }
        }
        // An index with no tags is kept too, as dBASE and FoxPro left it.
        if (tags.Count == 0 && _index is not null)
            staged.CreateProductionIndex();
        if (tags.Count == 0 && HasMaintainedStructuralCdx)
            staged.CreateStructuralCdx();
        return changed;
    }

    private int Lost(DbfRecord record, DbfField from, DbfField to, bool allowLoss) => allowLoss
        ? 1
        : throw new DbfException(DbfErrorCode.InvalidValue,
            $"Record {record.RecordNumber}'s {from.Name} doesn't fit the new field {to.Name} ({to.Type} {to.Length}{(to.DecimalCount > 0 ? $",{to.DecimalCount}" : "")}) without changing.",
            "Make the new field wide enough, or pass allowLoss: true to shorten, round or blank values that don't fit.", _path);

    private static DbfHeader ReadHeaderOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return DbfHeader.Read(stream);
    }

    // ModifyStructure's new table: the record built in _buffer goes after the last. The final Commit counts them all.
    private void AppendStaged(List<(int Field, int Next)> counters)
    {
        Seek(RecordCount, forAppend: true);
        _stream.Write(_buffer);
        Header.RecordCount++;
        foreach (var (field, next) in counters)
        {
            Header.SetNextAutoIncrement(field, next);
            _dirtyCounters.Add(field);
        }
    }

    // ---- converting a value to its new field ---------------------------------------------------

    // The value for field `to`, and whether it holds the old one exactly. It's encoded into a scratch
    // copy of the field and read back, so a number too wide, a decimal rounded away or text too long
    // shows up as a difference rather than as a write that fails halfway through the table.
    private (object? Value, bool Exact) Carried(object? value, bool wasNull, DbfField to)
    {
        if (wasNull)
            return (null, to.IsNullable);
        var (converted, exact) = Converted(value, to);
        if (converted is null || DbfValueCodec.IsMemoField(to))
            return (converted, exact);

        var scratch = new byte[to.Length];
        try
        {
            DbfValueCodec.Encode(to, converted, scratch, _strictEncoding);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or FormatException)
        {
            return (converted is string text ? Shortened(text, to) : null, false);
        }
        return (converted, exact && SameValueAfter(converted, DbfValueCodec.Decode(to, scratch, Encoding)));
    }

    private string? Shortened(string text, DbfField to)
    {
        text = text.TrimEnd(' ', '\0');
        try
        {
            while (text.Length > 0 && DbfValueCodec.StrictBytes(to, text, _strictEncoding).Length > to.Length)
                text = text[..^1];
            return text;
        }
        catch (ArgumentException)
        {
            return null; // a character the code page can't hold
        }
    }

    private static bool SameValueAfter(object? written, object? read) => (written, read) switch
    {
        (string a, string b) => a.TrimEnd(' ', '\0') == b,
        (string a, null) => a.TrimEnd(' ', '\0').Length == 0,
        (decimal or double or int or long, decimal or double or int or long) => SameNumber(written, read),
        _ => Equals(written, read),
    };

    private static bool SameNumber(object a, object b)
    {
        try
        {
            return Convert.ToDecimal(a, CultureInfo.InvariantCulture) == Convert.ToDecimal(b, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return Convert.ToDouble(a, CultureInfo.InvariantCulture).Equals(Convert.ToDouble(b, CultureInfo.InvariantCulture));
        }
    }

    // What a value read from the old field becomes in a field of another type, as dBASE's own
    // conversions do it: text and numbers both ways, dates as YYYYMMDD, logicals as T and F.
    private static (object? Value, bool Exact) Converted(object? value, DbfField to)
    {
        var invariant = CultureInfo.InvariantCulture;
        if (value is null)
            return (null, true);
        if (value is string blank && blank.Trim().Length == 0 && to.Type is not ('C' or 'M'))
            return (null, true);
        switch (to.Type)
        {
            case 'C' or 'M':
                return value switch
                {
                    string text => (text, true),
                    byte[] bytes when to.Type == 'M' => (bytes, true),
                    byte[] => (null, false),
                    bool flag => (flag ? "T" : "F", true),
                    DateOnly date => (date.ToString("yyyyMMdd", invariant), true),
                    DateTime moment => (moment.ToString("yyyyMMddHHmmss", invariant), moment.Millisecond == 0),
                    double number => (number.ToString("R", invariant), true),
                    IFormattable other => (other.ToString(null, invariant), true),
                    _ => (null, false),
                };

            case 'N' or 'F' or 'B' or 'Y':
                return Number(value);

            case 'I':
            {
                var (number, exact) = Number(value);
                if (number is null)
                    return (null, exact);
                decimal whole;
                try
                {
                    whole = Convert.ToDecimal(number, invariant);
                }
                catch (OverflowException)
                {
                    return (null, false);
                }
                var rounded = decimal.Round(whole, MidpointRounding.AwayFromZero);
                return rounded is < int.MinValue or > int.MaxValue ? (null, false) : ((int)rounded, exact && rounded == whole);
            }

            case 'D':
                return value switch
                {
                    DateOnly date => (date, true),
                    DateTime moment => (DateOnly.FromDateTime(moment), moment.TimeOfDay == TimeSpan.Zero),
                    string text when DateOnly.TryParseExact(text.Trim(), ["yyyyMMdd", "yyyy-MM-dd"], invariant, DateTimeStyles.None, out var date) => (date, true),
                    _ => (null, false),
                };

            case 'T':
                return value switch
                {
                    DateTime moment => (moment, true),
                    DateOnly date => (date.ToDateTime(TimeOnly.MinValue), true),
                    string text when DateTime.TryParseExact(text.Trim(), ["yyyyMMddHHmmss", "yyyyMMdd", "s", "yyyy-MM-dd"], invariant, DateTimeStyles.None, out var moment) => (moment, true),
                    _ => (null, false),
                };

            case 'L':
                return value switch
                {
                    bool flag => (flag, true),
                    string text => text.Trim().ToUpperInvariant() switch
                    {
                        "T" or "Y" or ".T." or "TRUE" => (true, true),
                        "F" or "N" or ".F." or "FALSE" => (false, true),
                        _ => (null, false),
                    },
                    _ => (null, false),
                };

            default:
                return (null, false);
        }

        static (object? Value, bool Exact) Number(object value) => value switch
        {
            decimal or double or int or long => (value, true),
            string text when decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => (number, true),
            _ => (null, false),
        };
    }

    // ---- the swap -------------------------------------------------------------------------------

    // memo -> table -> index. Up to the .dbf the change can be given up (the old .dbf reads the new
    // memo file, a copy of its own with more blocks); after it, only finished.
    private DbfTable SwapInRestructured(string ready, int changed, bool newHasMemo)
    {
        var path = _path!;
        var oldMemo = _memo is null ? null : DbfMemoFile.FindFor(path, Header);
        var staged = Directory.GetFiles(ready);
        var stagedMemo = staged.FirstOrDefault(f => Path.GetExtension(f).ToLowerInvariant() is ".dbt" or ".fpt");
        var stagedTable = staged.First(f => Path.GetFileName(f).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        var backup = NewRestructureBackup();

        using (var hold = HoldRestructure(ready, backup))
        {
            DisposeFiles(); // Windows won't rename a file that's open
            try
            {
                ThrowIfAnyInUse(path, staged, oldMemo);
                // Before the .dbf only when it replaces the old table's own memo file, which the old .dbf
                // reads just as well; a memo file the old table never had follows the .dbf instead, so
                // giving up leaves nothing new beside it.
                if (stagedMemo is not null && oldMemo is not null)
                    SwapFile(stagedMemo, oldMemo, backup);
                CrashPoint?.Invoke("memo swapped");
                SwapFile(stagedTable, path, backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hold.Dispose();
                GiveUpRestructure(ready, backup);
                throw new DbfException(DbfErrorCode.FileInUse, "Another program opened the table while its structure was being changed, so the table was left as it was.",
                    "Close the other program, open the table exclusively again and repeat the change.", path, ex);
            }
            CrashPoint?.Invoke("table swapped");
            try
            {
                CompleteRestructure(path, ready, backup, newHasMemo);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DbfException(DbfErrorCode.IncompleteTransaction, "The table has its new structure, but its index couldn't be put in place beside it.",
                    "Open the table with writable: true, exclusive: true, which finishes the change.", path, ex);
            }
        }
        DeleteRestructureFolder(ready);

        var table = OpenPath(path, writable: true, Encoding, ignoreMissingMemo: false, _allowUnmaintainedIndexes, _maintainFoxProIndexes, exclusive: true);
        table.FlushWritesToDisk = FlushWritesToDisk;
        table.UseJournal = _useJournal;
        table.DeferIndexWrites = DeferIndexWrites;
        table.RefuseLinks = RefuseLinks;
        table._lockScheme = _lockScheme;
        table._lockTimeout = _lockTimeout;
        table._requireLocks = _requireLocks;
        table.StructureBackupPath = backup;
        table.StructureValuesChanged = changed;
        return table;
    }

    // Held for the whole swap, so an exclusive open in another process (which can get in while the
    // files are closed) waits instead of finishing or undoing a change still under way. It also names
    // the backup folder, for an open that finishes a change cut off.
    private static FileStream HoldRestructure(string ready, string backup)
    {
        var hold = new FileStream(Path.Combine(ready, RestructureLockName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        hold.Write(Encoding.UTF8.GetBytes(Path.GetFileName(backup)));
        hold.Flush(flushToDisk: true);
        return hold;
    }

    // TABLE.DBF.restructured-<UTC time to the millisecond>, named like the cut-off backups.
    private string NewRestructureBackup()
    {
        var stem = $"{_path}.restructured-{CutOffClock():yyyyMMdd'T'HHmmss'.'fff'Z'}";
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? stem : $"{stem}-{attempt}";
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            if (attempt == 100)
                throw new DbfException(DbfErrorCode.TableAlreadyExists, "Too many backup folders from earlier changes share this moment's name, and none is reused, since it may hold an earlier change's files.",
                    "Move the .restructured- folders beside the table away, then try again.", _path);
        }
    }

    // Every file the swap will replace, tried unshared now that this table's own handles are closed,
    // so one another program holds open (an index opened to read, say) stops the change before the
    // .dbf moves, rather than after it with the index left to follow.
    private static void ThrowIfAnyInUse(string path, string[] staged, string? oldMemo)
    {
        var replaced = staged.Select(f => FindCompanion(path, Path.GetExtension(f))).Append(oldMemo).Append(path);
        foreach (var file in replaced.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(file))
                new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
        }
    }

    private static void SwapFile(string source, string destination, string backup)
    {
        if (File.Exists(destination))
            File.Replace(source, destination, Path.Combine(backup, Path.GetFileName(destination)), ignoreMetadataErrors: true);
        else
            File.Move(source, destination);
    }

    // After the .dbf: whatever else is staged (the index) goes in, and a memo file the new structure
    // has no use for joins the backup.
    private static void CompleteRestructure(string path, string ready, string backup, bool newHasMemo)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var file in Directory.GetFiles(ready))
        {
            // Only what a change stages after its .dbf, named after this table: a stale or foreign folder can't put anything else in place.
            if (Path.GetExtension(file).ToLowerInvariant() is not (".mdx" or ".cdx" or ".dbt" or ".fpt")
                || !Path.GetFileNameWithoutExtension(file).Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
                continue;
            SwapFile(file, FindCompanion(path, Path.GetExtension(file)) ?? Path.Combine(folder, Path.GetFileName(file)), backup);
        }
        if (newHasMemo)
            return;
        foreach (var extension in new[] { ".dbt", ".fpt" })
        {
            if (FindCompanion(path, extension) is { } unused)
                File.Move(unused, Path.Combine(backup, Path.GetFileName(unused)));
        }
    }

    private static void GiveUpRestructure(string ready, string backup)
    {
        DeleteRestructureFolder(ready);
        try
        {
            if (Directory.Exists(backup) && !Directory.EnumerateFileSystemEntries(backup).Any())
                Directory.Delete(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind costs nothing.
        }
    }

    private static void DeleteRestructureFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Another exclusive open finished the last step first.
        }
    }

    // On a writable exclusive open, before the memo and index are opened: a change of structure cut
    // off partway is undone while the old .dbf is still in place (the old table is whole: its memo
    // file is either its own or the copy, which holds every memo it had), and finished once the new
    // .dbf is in. Returns the backup folder of a change it finished.
    private static string? FinishRestructure(string path, DbfHeader header)
    {
        var ready = path + RestructureReadySuffix;
        if (!Directory.Exists(ready))
            return null;

        string backupName;
        var lockPath = Path.Combine(ready, RestructureLockName);
        try
        {
            using var hold = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var name = new byte[512]; // a backup folder's name is far shorter; a longer file isn't one this wrote
            var read = hold.ReadAtLeast(name, name.Length, throwOnEndOfStream: false);
            backupName = read == name.Length ? "" : Encoding.UTF8.GetString(name, 0, read).Trim();
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new DbfException(DbfErrorCode.FileInUse, "Another program is changing this table's structure right now.",
                "Wait for it to finish, then open the table again.", path, ex);
        }
        catch (DirectoryNotFoundException)
        {
            return null; // the change finished meanwhile
        }

        var tableStillStaged = Directory.EnumerateFiles(ready).Any(f => Path.GetFileName(f).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        if (tableStillStaged)
        {
            DeleteRestructureFolder(ready);
            return null;
        }

        // The backup's name comes from a file: it must be the plain folder name this table's changes use.
        if (backupName.Length == 0 || Path.GetFileName(backupName) != backupName
            || !backupName.StartsWith(Path.GetFileName(path) + ".restructured-", StringComparison.OrdinalIgnoreCase))
            throw new DbfException(DbfErrorCode.IncompleteTransaction,
                "A change of this table's structure didn't finish, and its notes about where the old files went can't be read.",
                $"Move the files in the {RestructureReadySuffix} folder beside the table into place by hand (the old ones are in the newest .restructured- folder), then delete that folder.", path,
                diagnostic: ready);
        var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, backupName);
        Directory.CreateDirectory(backup);
        CompleteRestructure(path, ready, backup, header.Fields.Any(DbfValueCodec.IsMemoField));
        DeleteRestructureFolder(ready);
        return backup;
    }

    // A change cut off between its swaps leaves the files a mix of old and new: no write may touch them
    // until an exclusive open has finished or undone it.
    private void ThrowIfRestructureUnfinished()
    {
        if (_path is not null && Directory.Exists(_path + RestructureReadySuffix))
            throw new DbfException(DbfErrorCode.IncompleteTransaction,
                "A change of this table's structure didn't finish, so its files may be a mix of old and new.",
                "Open the table with writable: true, exclusive: true: that finishes the change, or puts the table back as it was.", _path);
    }
}
