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
//   The swap goes memo -> decision -> table -> index. The new memo file is the old one copied whole,
//   plus blocks for memos the change made, so the old .dbf reads it as well as the new one does; the
//   decision is a file forced to disk, and from it on the change is only ever finished.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

public sealed partial class DbfTable
{
    // Changing a table's structure is a preview: callers opt in by suppressing this diagnostic.
    internal const string PreviewDiagnostic = "THDBF001";
    internal const string PreviewDocs = "https://github.com/pthunder/Thunder.Dbf/blob/main/docs/reading-and-writing.md#changing-a-tables-structure";

    private const string RestructurePartialSuffix = ".restructure-partial";
    internal const string RestructureReadySuffix = ".restructuring";
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
    /// Preview (THDBF001): back up tables you can't afford to lose before using it.
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
    /// If the process is killed partway, the next exclusive writable open finishes the change once it was
    /// decided, or, before that, puts the table back byte for byte. Each file moves in two renames around
    /// a decision forced to disk; see docs/reading-and-writing.md for the steps.
    /// </remarks>
    [Experimental(PreviewDiagnostic, UrlFormat = PreviewDocs)]
    public DbfTable ModifyStructure(IReadOnlyList<DbfFieldDefinition> fields, IReadOnlyDictionary<string, string>? renames = null, bool allowLoss = false)
    {
        using var forced = ForceEveryWrite();
        try
        {
            return ModifyStructureCore(fields, renames, allowLoss);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not DbfException && _path is not null)
        {
            // Writing the new table beside this one: nothing has replaced anything yet.
            throw FileFailure(ex, _path, "The table's structure couldn't be changed, so it was left as it was.",
                "Check that the folder can be written and has room for a copy of the table, then try again.");
        }
    }

    /// <summary>Preview (THDBF001). Adds a field after the others: <see cref="ModifyStructure"/> with one more field. Existing records get it blank.</summary>
    /// <returns>The table with its new structure. This table is closed: use the one returned.</returns>
    [Experimental(PreviewDiagnostic, UrlFormat = PreviewDocs)]
    public DbfTable AddField(DbfFieldDefinition field) => ModifyStructure([.. FieldDefinitions, field]);

    /// <summary>
    /// Preview (THDBF001). Drops a field and its values: <see cref="ModifyStructure"/> without it. A dropped memo field's memos
    /// stay in the memo file, which is copied whole, as unused space, and in the backup. To be rid of
    /// sensitive text for good, copy the records you keep into a new table and delete the old files.
    /// </summary>
    /// <returns>The table with its new structure. This table is closed: use the one returned.</returns>
    [Experimental(PreviewDiagnostic, UrlFormat = PreviewDocs)]
    public DbfTable DropField(string name)
    {
        var definitions = FieldDefinitions;
        if (!definitions.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"The table has no field named {name}.", nameof(name));
        return ModifyStructure(definitions.Where(f => !f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    /// <summary>
    /// Preview (THDBF001). Replaces one field's definition, keeping its place and its values (converted where the types allow):
    /// widen it, change its type, or rename it by giving <paramref name="field"/> another name.
    /// </summary>
    /// <returns>The table with its new structure. This table is closed: use the one returned.</returns>
    [Experimental(PreviewDiagnostic, UrlFormat = PreviewDocs)]
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
        string backup;
        RestructureManifest manifest;
        FileStream? held = null;
        using var release = new Disposer(() => held?.Dispose()); // let go however this ends, as a kill would
        using (Locks.Table())
        {
            PrepareWrite(appending: false); // an unfinished pack or journal note comes first
            ThrowIfRestructureUnfinished();
            ThrowIfCantRestructure(fields);

            // Left by a change killed while it was being written, or while its folder was being
            // deleted (nothing reads either), and finished changes' bookkeeping.
            ClearLeftoversHeld(_path); // this table holds itself unshared: it's open exclusively
            if (FolderThere(partial))
            {
                RequireRealFolder(partial);
                Directory.Delete(partial, recursive: true);
            }
            // The transaction: its backup folder, under a name never used before, and its lock, held
            // from here until the reopened table is handed back.
            backup = NewRestructureBackup();
            var hold = held = HoldRestructure(backup, create: true);
            try
            {
                changed = WriteRestructured(partial, format, fields, sources, tags, allowLoss);
                manifest = StageForSwap(partial, backup);
                RestructurePoint?.Invoke("manifest");
                // Renamed whole once complete, so a folder under the ready name always holds a finished table and its id.
                Directory.Move(partial, ready);
            }
            catch
            {
                DeleteQuietly(partial);
                hold.Dispose();
                DeleteAbandoned(backup);
                throw;
            }
        }
        RestructurePoint?.Invoke("staged");
        return SwapInRestructured(ready, backup, held, manifest, changed);
    }

    private readonly struct Disposer(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    // Names every staged file as it will be named in place (the old file's own name where there is
    // one, so a .FPT stays .FPT whatever case the new one was made in), then writes the manifest,
    // forced to disk, and the staged folder's id naming the transaction.
    private RestructureManifest StageForSwap(string partial, string backup)
    {
        var tableName = Path.GetFileName(FindCompanion(_path!, Path.GetExtension(_path!)) ?? _path!);
        // The old memo file as the old table's format names it: a stray .dbt beside a FoxPro table isn't it.
        var memoOriginal = _memo is not null && DbfMemoFile.FindFor(_path!, Header) is { } memoPath ? Path.GetFileName(memoPath) : null;
        string? indexOriginal = null;
        if (_index is not null && MdxIndex.FindFor(_path!) is { } mdx)
            indexOriginal = Path.GetFileName(mdx);
        else if (_attachedIndexes.Keys.FirstOrDefault(IsStructuralCdx) is { } cdx)
            indexOriginal = Path.GetFileName(cdx);

        string? Staged(string[] extensions, string? original)
        {
            if (Directory.EnumerateFiles(partial).FirstOrDefault(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) is not { } file)
                return null;
            if (original is not null && Path.GetExtension(original).Equals(Path.GetExtension(file), StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(file).Equals(original, StringComparison.Ordinal))
            {
                File.Move(file, Path.Combine(partial, original + ".naming"));
                File.Move(Path.Combine(partial, original + ".naming"), Path.Combine(partial, original));
                return original;
            }
            return Path.GetFileName(file);
        }
        var manifest = new RestructureManifest(Path.GetFileName(backup), tableName,
            Staged([".dbt", ".fpt"], memoOriginal), memoOriginal, Staged([".mdx", ".cdx"], indexOriginal), indexOriginal);
        KeepFileModes(partial, manifest);
        WriteRecord(Path.Combine(backup, RestructureManifestName), manifest.Write());
        WriteRecord(Path.Combine(partial, RestructureIdName), manifest.Id);
        return manifest;
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
        // Named as the file on disk is, whatever case the table was opened with, so the swap keeps its name.
        var stagedPath = Path.Combine(partial, Path.GetFileName(FindCompanion(_path!, Path.GetExtension(_path!)) ?? _path!));
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
                if (value is null && !wasNull && to.NullBit is { } blankBit)
                {
                    // Blank isn't .NULL.: written blank here, since a null value would set the field's null bit.
                    DbfValueCodec.Encode(to, null, buffer.AsSpan(to.Offset, to.Length), staged.Encoding);
                    encode[j] = false;
                    copiedNulls.Add((blankBit, false));
                }
            }

            List<(int Field, int Next)> counters;
            try
            {
                counters = staged.AssignAutoIncrements(values);
            }
            catch (InvalidOperationException ex)
            {
                throw new DbfException(DbfErrorCode.InvalidValue, $"Record {record.RecordNumber} can't be numbered: {ex.Message}",
                    "Start the autoincrement field lower, or give it a smaller step.", _path, ex);
            }
            staged.EncodeFields(values, encode);
            if (staged.Header.NullFlagsFieldIndex is { } flags)
            {
                foreach (var (bit, isNull) in copiedNulls)
                    SetBit(buffer.AsSpan(staged.Fields[flags].Offset, staged.Fields[flags].Length), bit, isNull);
            }
            staged.AppendStaged(counters);
            RestructurePoint?.Invoke("copying");
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
        // A blank can't be stored in Visual FoxPro's binary integer, double and currency fields: they
        // have no blank, only zero, and a nullable one's .NULL. isn't a blank either.
        if (converted is null && to.Type is 'I' or 'B' or 'Y')
            return (null, false);
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
        var read = DbfValueCodec.Decode(to, scratch, Encoding);
        // A number is checked against the value it came from, never a rounded copy of it: a double
        // through decimal loses digits past the 15th (and 1E-100 becomes 0) on both sides alike.
        return to.Type is 'N' or 'F' or 'B' or 'Y' or 'I'
            ? (converted, exact && SameNumberExactly(value is decimal or double or int or long ? value : converted, read))
            : (converted, exact && SameValueAfter(converted, read));
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
        (decimal or double or int or long, decimal or double or int or long) => SameNumberExactly(written, read),
        _ => Equals(written, read),
    };

    // A double compares as a double (what was read back, made a double, must be that very double);
    // anything else compares as decimal, which holds an integer or a decimal exactly.
    private static bool SameNumberExactly(object reference, object? read)
    {
        if (read is null)
            return false;
        var invariant = CultureInfo.InvariantCulture;
        if (reference is double wanted)
            return Convert.ToDouble(read, invariant).Equals(wanted);
        try
        {
            // A double read back is made decimal through its round-trip text: the (decimal) cast keeps
            // only 15 digits, and would call a 16-digit integer the double holds exactly a loss.
            return read is double got
                ? double.IsFinite(got) && decimal.TryParse(got.ToString("R", invariant), NumberStyles.Float, invariant, out var exact)
                    && exact == Convert.ToDecimal(reference, invariant)
                : Convert.ToDecimal(read, invariant) == Convert.ToDecimal(reference, invariant);
        }
        catch (OverflowException)
        {
            return false;
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
                if (number is double real)
                {
                    if (!double.IsFinite(real) || real < int.MinValue || real > int.MaxValue)
                        return (null, false);
                    var nearest = Math.Round(real, MidpointRounding.AwayFromZero);
                    return ((int)nearest, exact && nearest == real);
                }
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

        // Text is read as the numeric codec reads a field: decimal when that holds every digit, else
        // double, and inexact when neither does (decimal.TryParse quietly makes 1E-100 zero).
        static (object? Value, bool Exact) Number(object value)
        {
            if (value is decimal or double or int or long)
                return (value, true);
            if (value is not string text)
                return (null, false);
            text = text.Trim();
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && DbfValueCodec.SameDigits(text, number.ToString(CultureInfo.InvariantCulture)))
                return (number, true);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real))
                return (real, DbfValueCodec.SameDigits(text, real.ToString("R", CultureInfo.InvariantCulture)));
            return (null, false);
        }
    }

    // ---- the swap -------------------------------------------------------------------------------
    //
    // The protocol, state by state, with what recovery does in each: docs/formats/restructure.md.
    // In short: B (the backup folder) is the transaction. It holds the lock, the manifest naming
    // every file that moves, and the decision; R (the ready folder) holds the new files and names B.
    // Each file moves in two renames, the file at its destination into B, then the staged one in.
    // Before the decision is published the change can only be undone; after it, only finished.

    private const string RestructureIdName = "restructure.id";
    private const string RestructureManifestName = "restructure.manifest";
    private const string RestructureCommitName = "restructure.commit";
    private const string RestructureRetiredSuffix = ".restructure-retired";
    private const string RestructureBackupInfix = ".restructured-";
    private const int MaxRestructureRecord = 4096; // the id and manifest are a few names long

    private static readonly string[] BookkeepingNames =
        [RestructureLockName, RestructureManifestName, RestructureCommitName, RestructureCommitName + ".tmp"];

    /// <summary>For the tests: called at each step of a change of structure, and of finishing one.</summary>
    [ThreadStatic]
    internal static Action<string>? RestructurePoint;

    // The files a change moves, as plain names in the table's folder. Memo and index are null when the
    // new table has none; their originals are null when the old table had none. Nothing is ever
    // picked by looking for whatever exists.
    // On Linux and macOS a new file gets the process's default mode (0644 under umask 022), so a table
    // shared through its group (0660, say) would lose that: each new file takes its old file's mode,
    // or the .dbf's where it had none. Done before the folder is renamed ready, so a mode that can't be
    // kept stops the change before anything moves. Windows files inherit their folder's permissions,
    // as the old ones did.
    private void KeepFileModes(string partial, RestructureManifest manifest)
    {
        if (OperatingSystem.IsWindows())
            return;
        var folder = Path.GetDirectoryName(Path.GetFullPath(_path!))!;
        var tableMode = File.GetUnixFileMode(Path.Combine(folder, manifest.Table));
        void Keep(string? staged, string? original)
        {
            if (staged is null)
                return;
            var mode = original is not null && FileThere(Path.Combine(folder, original)) ? File.GetUnixFileMode(Path.Combine(folder, original)) : tableMode;
            File.SetUnixFileMode(Path.Combine(partial, staged), mode);
        }
        Keep(manifest.Table, manifest.Table);
        Keep(manifest.Memo, manifest.MemoOriginal);
        Keep(manifest.Index, manifest.IndexOriginal);
        RestructurePoint?.Invoke("modes kept");
    }

    private sealed record RestructureManifest(string Id, string Table, string? Memo, string? MemoOriginal, string? Index, string? IndexOriginal)
    {
        // The last line is the SHA-256 of all the others: a manifest cut short anywhere (even right after
        // a field whose value is empty) has none, or the wrong one.
        public string Write()
        {
            var body = string.Join('\n',
                $"id={Id}", $"table={Table}", $"memo={Memo}", $"memo.original={MemoOriginal}", $"index={Index}", $"index.original={IndexOriginal}");
            return $"{body}\nsha256={Hash(body)}";
        }

        private static string Hash(string body) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        private static readonly string[] Keys = ["id", "table", "memo", "memo.original", "index", "index.original"];

        // Every field must be there exactly once (an empty value is an explicit "none", a missing line
        // is a manifest cut short), nothing else may be, and every name is checked before it's used as
        // a path: it comes from a file.
        public static RestructureManifest? Read(string text, string tablePath)
        {
            var end = text.LastIndexOf("\nsha256=", StringComparison.Ordinal);
            if (end < 0 || !text[(end + "\nsha256=".Length)..].Trim().Equals(Hash(text[..end]), StringComparison.Ordinal))
                return null;
            text = text[..end];
            var lines = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2 || !Keys.Contains(parts[0]) || !lines.TryAdd(parts[0], parts[1].Trim()))
                    return null;
            }
            if (lines.Count != Keys.Length)
                return null;
            var stem = Path.GetFileNameWithoutExtension(tablePath);
            string? Name(string key, params string[] extensions)
            {
                var value = lines[key];
                if (value.Length == 0)
                    return null;
                return Path.GetFileName(value) == value && Path.GetFileNameWithoutExtension(value).Equals(stem, StringComparison.OrdinalIgnoreCase)
                    && extensions.Contains(Path.GetExtension(value).ToLowerInvariant())
                    ? value : throw new InvalidDataException($"The manifest's {key} isn't one of this table's files.");
            }
            try
            {
                var id = lines["id"];
                var table = lines["table"];
                if (!IsBackupName(id, tablePath) || !table.Equals(Path.GetFileName(tablePath), StringComparison.OrdinalIgnoreCase) || Path.GetFileName(table) != table)
                    return null;
                return new RestructureManifest(id, table, Name("memo", ".dbt", ".fpt"), Name("memo.original", ".dbt", ".fpt"),
                    Name("index", ".mdx", ".cdx"), Name("index.original", ".mdx", ".cdx"));
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }

    private static bool IsBackupName(string name, string tablePath) =>
        name.Length > 0 && Path.GetFileName(name) == name && name.IndexOfAny(['/', '\\', ':']) < 0
        && name.StartsWith(Path.GetFileName(tablePath) + RestructureBackupInfix, StringComparison.OrdinalIgnoreCase);

    // What the change has got to, for what it reports when something fails.
    private enum RestructurePhase { Undecided, Publishing, Decided, Retiring }

    private DbfTable SwapInRestructured(string ready, string backup, FileStream hold, RestructureManifest manifest, int changed)
    {
        var path = _path!;
        var phase = RestructurePhase.Undecided;
        using (hold)
        {
            DisposeFiles(); // Windows won't rename a file that's open
            try
            {
                RestructurePoint?.Invoke("closed");
                ThrowIfAnyInUse(path, manifest);
                // Only the old table's own memo file moves before the decision: the old .dbf reads the copy as well as its own.
                if (manifest.Memo is not null && manifest.MemoOriginal is not null)
                    MoveIntoPlace(path, ready, backup, manifest.Memo, "memo");
                phase = RestructurePhase.Publishing;
                PublishDecision(backup);
                phase = RestructurePhase.Decided;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !IsRefusal(ex))
            {
                // Looked at once: what it says decides, and nothing after it looks again.
                var decided = phase == RestructurePhase.Publishing ? DecisionState(backup) : false;
                if (decided is null)
                {
                    throw Phased(ex, path, "The table's new structure may or may not have been decided: publishing the decision failed partway.",
                        "Open the table with writable: true, exclusive: true, which finishes the change or puts the table back, as the files on disk say.",
                        DbfErrorCode.IncompleteTransaction);
                }
                if (decided is true)
                {
                    // Published after all (the rename went through): the change is decided, and only finished.
                    phase = RestructurePhase.Decided;
                }
                else
                {
                    try
                    {
                        Undo(path, ready, backup, manifest);
                        Retire(path, ready);
                    }
                    catch (Exception undo) when (undo is IOException or UnauthorizedAccessException && !IsRefusal(undo))
                    {
                        throw Phased(undo, path, "The table's structure couldn't be changed, and putting it back as it was hasn't finished yet.",
                            "Open the table with writable: true, exclusive: true, which puts the table back as it was.", DbfErrorCode.IncompleteTransaction);
                    }
                    hold.Dispose();
                    DeleteAbandoned(backup);
                    throw Phased(ex, path, "The table's structure couldn't be changed, so it was left as it was.",
                        "Close any other program using the table, open it exclusively again and repeat the change.");
                }
            }

            try
            {
                RestructurePoint?.Invoke("committed");
                RollForward(path, ready, backup, manifest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !IsRefusal(ex))
            {
                throw Phased(ex, path, "The table's new structure is decided, but not every file could be put in place yet.",
                    "Open the table with writable: true, exclusive: true, which finishes the change.", DbfErrorCode.IncompleteTransaction);
            }
            phase = RestructurePhase.Retiring;
            try
            {
                Retire(path, ready);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !IsRefusal(ex))
            {
                throw Phased(ex, path, "The table has its new structure, but the folder its new files came from couldn't be cleared away, and writes wait until it is.",
                    "Close any other program using the table's folder, then open the table with writable: true, exclusive: true.", DbfErrorCode.IncompleteTransaction);
            }

            // Handed over still under the lock: nothing finishes or undoes this change behind the reopened table.
            DbfTable table;
            try
            {
                RestructurePoint?.Invoke("reopening");
                table = OpenPath(path, writable: true, Encoding, ignoreMissingMemo: false, _allowUnmaintainedIndexes, _maintainFoxProIndexes, exclusive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Done: only opening the result failed. Never "left as it was".
                var reason = ex is DbfException known ? known : FileFailure(ex, path, "", "");
                throw new DbfException(reason.Code,
                    "The table has its new structure, but opening it again afterwards failed.",
                    "Open the table again; the change itself is finished.", path, ex);
            }
            RestructurePoint?.Invoke("reopened");
            table.FlushWritesToDisk = FlushWritesToDisk;
            table.UseJournal = _useJournal;
            table.DeferIndexWrites = DeferIndexWrites;
            table.RefuseLinks = RefuseLinks;
            table._lockScheme = _lockScheme;
            table._lockTimeout = _lockTimeout;
            table._requireLocks = _requireLocks;
            table.StructureBackupPath = backup;
            table.StructureValuesChanged = changed;
            hold.Dispose();
            ClearBookkeeping(backup);
            return table;
        }
    }

    // Whether a file (or folder) is there: true or false only when that's known. File.Exists and
    // Directory.Exists also answer false when the look itself failed (access denied, a share gone),
    // and acting on that could undo a decided change or take a staged file for installed; so any
    // failure but "not there" throws, as a DbfException naming no path (it's in FilePath), and
    // the step stops.
    internal static bool FileThere(string file) => There(file, folder: false);

    internal static bool FolderThere(string folder) => There(folder, folder: true);

    /// <summary>For the tests: makes looking for a file fail (throw) where it returns true.</summary>
    [ThreadStatic]
    internal static Func<string, bool>? LookFails;

    private static bool There(string path, bool folder)
    {
        try
        {
            if (LookFails?.Invoke(path) is true)
                throw new UnauthorizedAccessException("Looking for the file failed.");
            return File.GetAttributes(path).HasFlag(FileAttributes.Directory) == folder;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not DbfException)
        {
            var known = DbfException.Translate(ex, path, FileAccess.Read);
            throw new DbfException(known?.Code ?? DbfErrorCode.WriteFailed,
                "Thunder.Dbf couldn't tell whether a file it needs beside the table is there, so it stopped rather than guess.",
                known?.Hint ?? "Check that the folder can be read, then try again.", path, ex);
        }
    }

    // The change creates its lock; an open finishing one only ever takes a lock that's there, so it
    // never leaves one in a backup whose change has ended.

    private static FileStream HoldRestructure(string backup, bool create = false) =>
        new(Path.Combine(backup, RestructureLockName), create ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    // The decision: written under a temporary name and forced to disk, then renamed. Only the final
    // name counts, so a failure before the rename leaves the change undecided.
    private static void PublishDecision(string backup)
    {
        var temporary = Path.Combine(backup, RestructureCommitName + ".tmp");
        using (var commit = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            commit.Flush(flushToDisk: true);
        RestructurePoint?.Invoke("deciding");
        File.Move(temporary, Path.Combine(backup, RestructureCommitName));
        RestructurePoint?.Invoke("decided");
    }

    // True when the decision is published, false when it plainly isn't, null when that can't be told.
    private static bool? DecisionState(string backup)
    {
        try
        {
            return FileThere(Path.Combine(backup, RestructureCommitName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteRecord(string file, string text)
    {
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.Flush(flushToDisk: true);
    }

    // A small record the change wrote, or null when it's missing or longer than any it writes.
    private static string? ReadRecord(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[MaxRestructureRecord + 1];
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            return read > MaxRestructureRecord ? null : Encoding.UTF8.GetString(bytes, 0, read).Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    // TABLE.DBF.restructured-<UTC time to the millisecond>, named like the cut-off backups. Never reused.
    private string NewRestructureBackup()
    {
        var stem = $"{_path}{RestructureBackupInfix}{CutOffClock():yyyyMMdd'T'HHmmss'.'fff'Z'}";
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? stem : $"{stem}-{attempt}";
            if (!FolderThere(candidate) && !FileThere(candidate))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            if (attempt == 100)
                throw new DbfException(DbfErrorCode.TableAlreadyExists, "Too many earlier backup folders already use this timestamp, and none can be reused, since each may hold an earlier change's files.",
                    "Move the .restructured- folders beside the table away, then try again.", _path);
        }
    }

    // Every file the swap will replace, tried unshared now that this table's own handles are closed,
    // so one another program holds open (an index opened to read, say) stops the change before
    // anything moves. One opened after this is caught by the moves themselves on Windows, and by an
    // open's identity check elsewhere (see OpenTableFile).
    // An unshared open of a table's file, as a test that nothing else has it or to hold it. Refused
    // up front when this process holds locks on it: see DbfLocks.ThrowIfHeldHere.
    private static FileStream OpenUnshared(string file)
    {
        DbfLocks.ThrowIfHeldHere(file);
        return new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static void ThrowIfAnyInUse(string path, RestructureManifest manifest)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var name in new[] { manifest.Table, manifest.Memo, manifest.MemoOriginal, manifest.Index, manifest.IndexOriginal }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(folder, name);
            if (FileThere(file))
                OpenUnshared(file).Dispose();
        }
    }

    // Two renames: the file at the destination into the backup, then the staged one into its place.
    // Run again after a kill, it carries on from whichever of them was done.
    private static void MoveIntoPlace(string path, string ready, string backup, string name, string what)
    {
        var staged = Path.Combine(ready, name);
        if (!FileThere(staged))
            return; // moved in already
        RequireRealFile(staged);
        var destination = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name);
        if (FileThere(destination))
        {
            RequireRealFile(destination);
            var aside = Path.Combine(backup, name);
            if (FileThere(aside))
                throw new DbfException(DbfErrorCode.IncompleteTransaction,
                    "A change of this table's structure found an old file already in its backup, so it stopped rather than overwrite either.",
                    "Compare the file beside the table with the one in the change's .restructured- folder, keep the right one, and open the table exclusively again.",
                    destination);
            File.Move(destination, aside);
            RestructurePoint?.Invoke($"{what} moved aside");
        }
        File.Move(staged, destination);
        RestructurePoint?.Invoke($"{what} moved in");
    }

    // After the decision: the .dbf, then the index and a memo file the old table didn't have; then
    // the old memo file, when the new table has none, joins the backup.
    private static void RollForward(string path, string ready, string backup, RestructureManifest manifest)
    {
        MoveIntoPlace(path, ready, backup, manifest.Table, "table");
        if (manifest.Index is not null)
            MoveIntoPlace(path, ready, backup, manifest.Index, "index");
        if (manifest.Memo is not null)
            MoveIntoPlace(path, ready, backup, manifest.Memo, "memo");
        if (manifest.Memo is null && manifest.MemoOriginal is not null)
        {
            var unused = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, manifest.MemoOriginal);
            if (FileThere(unused))
            {
                RequireRealFile(unused);
                File.Move(unused, Path.Combine(backup, manifest.MemoOriginal));
                RestructurePoint?.Invoke("unused memo moved aside");
            }
        }
    }

    // Before the decision: every file the backup holds goes back over whatever is at its place now,
    // and a file the change put where there was none (a memo file or index the table didn't have)
    // goes back into the ready folder. The old table is then byte for byte as it was.
    private static void Undo(string path, string ready, string backup, RestructureManifest manifest)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var name in new[] { manifest.Table, manifest.Memo, manifest.Index, manifest.MemoOriginal }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var destination = Path.Combine(folder, name);
            var aside = Path.Combine(backup, name);
            if (FileThere(aside))
            {
                RequireRealFile(aside);
                File.Move(aside, destination, overwrite: true);
                RestructurePoint?.Invoke("put back");
            }
            else if (!FileThere(Path.Combine(ready, name)) && FileThere(destination) && name != manifest.MemoOriginal
                     && name != manifest.IndexOriginal && !name.Equals(manifest.Table, StringComparison.OrdinalIgnoreCase))
            {
                RequireRealFile(destination);
                File.Move(destination, Path.Combine(ready, name));
                RestructurePoint?.Invoke("taken back");
            }
        }
    }

    // The ready folder goes out of the way under another name before anything in it is deleted, so a
    // process killed meanwhile leaves only a folder nothing reads.
    private static void Retire(string path, string ready)
    {
        var retired = path + RestructureRetiredSuffix;
        DeleteQuietly(retired);
        try
        {
            RestructurePoint?.Invoke("retiring");
            Directory.Move(ready, retired);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        RestructurePoint?.Invoke("retired");
        DeleteQuietly(retired);
    }

    // A folder only this code writes, emptied and deleted, never entered when it's a link: that
    // could reach files anywhere.
    private static void DeleteQuietly(string folder)
    {
        try
        {
            if (!FolderThere(folder))
                return;
            RequireRealFolder(folder);
            foreach (var file in Directory.GetFiles(folder))
            {
                File.Delete(file); // a link here is deleted itself, never followed
                RestructurePoint?.Invoke("deleted");
            }
            foreach (var inner in Directory.GetDirectories(folder))
                DeleteQuietly(inner);
            Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not DbfException { Code: DbfErrorCode.PathEscapesFolder })
        {
            // Nothing reads it; the next change or exclusive open tries again.
        }
    }

    private static void RequireRealFolder(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new DbfException(DbfErrorCode.PathEscapesFolder, "A folder Thunder.Dbf keeps beside the table for changing its structure is a link, so it wasn't entered.",
                "Remove the link (it isn't one Thunder.Dbf made), then open the table again.", folder);
    }

    private static void RequireRealFile(string file)
    {
        if (new FileInfo(file).LinkTarget is not null)
            throw new DbfException(DbfErrorCode.PathEscapesFolder, "A file a change of this table's structure would move is a link, so it wasn't moved.",
                "Replace the link with the real file, or remove it, then open the table again.", file);
    }

    // After the change is finished: the backup keeps the old files, and loses the lock, manifest and decision.
    private static void ClearBookkeeping(string backup)
    {
        foreach (var name in BookkeepingNames)
        {
            try
            {
                File.Delete(Path.Combine(backup, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the backup; the next exclusive open clears it.
            }
        }
        RestructurePoint?.Invoke("cleaned");
    }

    // A backup no change finished: it holds only bookkeeping (the undoing put every old file back), so it goes.
    private static void DeleteAbandoned(string backup)
    {
        try
        {
            if (!FolderThere(backup))
                return;
            RequireRealFolder(backup);
            if (Directory.EnumerateFileSystemEntries(backup).Any(e => !BookkeepingNames.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase)))
                return;
            foreach (var name in BookkeepingNames)
                File.Delete(Path.Combine(backup, name));
            Directory.Delete(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not DbfException)
        {
            // An empty folder left behind costs nothing; the next exclusive open tries again.
        }
    }

    // On a writable exclusive open, before the table's files are opened (the .dbf may be in the
    // backup, between its two renames). Returns the backup folder of a change it finished.
    private static string? FinishRestructure(string path)
    {
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (FinishRestructureOnce(path, out var backup))
                    return backup;
            }
            throw new DbfException(DbfErrorCode.FileInUse, "This table's structure kept changing while the open looked at it.",
                "Wait for the other program to finish, then open the table again.", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not DbfException)
        {
            throw FileFailure(ex, path, "A change of this table's structure was cut off, and it couldn't be finished or undone yet.",
                "Close any other program using the table and open it exclusively again; nothing is lost meanwhile.", DbfErrorCode.IncompleteTransaction);
        }
    }

    // One look. False when the transaction it read about changed under it, so the caller looks again.
    private static bool FinishRestructureOnce(string path, out string? finished)
    {
        finished = null;
        var ready = path + RestructureReadySuffix;
        if (!FolderThere(ready))
            return ClearLeftovers(path);
        RequireRealFolder(ready);
        var id = ReadRecord(Path.Combine(ready, RestructureIdName));
        if (id is null || !IsBackupName(id, path))
            throw UnreadableRecord(path, ready);
        var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, id);
        RestructurePoint?.Invoke("recovery read");

        // Whoever holds the table, or the transaction's lock, has the change in hand.
        try
        {
            if (FileThere(path))
                OpenUnshared(path).Dispose();
            if (!FolderThere(backup))
                return !FolderThere(ready) || ReadRecord(Path.Combine(ready, RestructureIdName)) != id
                    ? false
                    : throw UnreadableRecord(path, ready);
            RequireRealFolder(backup);
            using var hold = HoldRestructure(backup);
            RestructurePoint?.Invoke("recovery locked");
            // The same transaction still? A paused open may have read about one that has since ended.
            if (!FolderThere(ready) || ReadRecord(Path.Combine(ready, RestructureIdName)) != id)
                return false;
            var manifest = ReadRecord(Path.Combine(backup, RestructureManifestName)) is { } text ? RestructureManifest.Read(text, path) : null;
            if (manifest is null || !manifest.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                throw UnreadableRecord(path, ready);

            if (FileThere(Path.Combine(backup, RestructureCommitName)))
            {
                RollForward(path, ready, backup, manifest);
                Retire(path, ready);
                hold.Dispose();
                ClearBookkeeping(backup);
                finished = backup;
            }
            else
            {
                Undo(path, ready, backup, manifest);
                Retire(path, ready);
                hold.Dispose();
                DeleteAbandoned(backup);
            }
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false; // the backup or its lock went: that transaction ended meanwhile
        }
        catch (IOException ex) when (ex is not DbfException && DbfException.Translate(ex, path, FileAccess.ReadWrite) is { Code: DbfErrorCode.FileInUse })
        {
            throw new DbfException(DbfErrorCode.FileInUse, "Another program has this table open, or is changing its structure right now.",
                "Wait for it to finish (or close it), then open the table again.", path, ex);
        }
    }

    // With no change under way: folders left by kills, which nothing reads, and the bookkeeping of
    // finished changes (once nobody holds their lock). A change being written holds the table
    // unshared from before its partial folder exists until after it's renamed ready, so the table
    // is held unshared here for the whole of it: when that fails, whatever is there belongs to
    // whoever holds the table, and is left alone; while it's held, no change can start.
    // False when what it saw changed while it was taking the table (a change began, or the .dbf it
    // holds is no longer the one at the path): the caller looks again from the start.
    private static bool ClearLeftovers(string path)
    {
        RestructurePoint?.Invoke("leftovers seen");
        FileStream held;
        try
        {
            held = OpenUnshared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // missing, or in use: the open that follows says which
        }
        using (held)
        {
            RestructurePoint?.Invoke("leftovers held");
            // "No change under way" was seen before the table was held: a change may have started,
            // staged and been cut off since, or renamed this very .dbf away. Never clear up on a stale look.
            if (FolderThere(path + RestructureReadySuffix) || FileIdentity.Same(held, path) is false)
                return false;
            ClearLeftoversHeld(path);
        }
        return true;
    }

    // Only ever called holding the table unshared: by ClearLeftovers, and by a change before it stages.
    private static void ClearLeftoversHeld(string path)
    {
        RestructurePoint?.Invoke("clearing leftovers");
        DeleteQuietly(path + RestructurePartialSuffix);
        DeleteQuietly(path + RestructureRetiredSuffix);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var backup in Directory.EnumerateDirectories(folder, Path.GetFileName(path) + RestructureBackupInfix + "*"))
        {
            if (!BookkeepingNames.Any(n => FileThere(Path.Combine(backup, n))))
                continue;
            RequireRealFolder(backup);
            try
            {
                using (HoldRestructure(backup))
                {
                }
            }
            catch (FileNotFoundException)
            {
                // No lock left to hold: nobody is using it.
            }
            catch (IOException)
            {
                continue; // a change holds it: it clears up after itself
            }
            if (FileThere(Path.Combine(backup, RestructureCommitName)))
                ClearBookkeeping(backup);
            else
                DeleteAbandoned(backup);
        }
    }

    private static DbfException UnreadableRecord(string path, string ready) => new(DbfErrorCode.IncompleteTransaction,
        "A change of this table's structure was cut off, and its record of which files go where can't be read.",
        $"Compare the table's files with those in the {RestructureReadySuffix} folder and the newest .restructured- folder beside it, put the right ones in place by hand, then delete the {RestructureReadySuffix} folder.",
        path, diagnostic: ready);

    // What the swap's steps refuse on purpose (a link, two old copies of one file), which say what to
    // do themselves; any other failure, a DbfException from a look included, is reported by phase.
    private static bool IsRefusal(Exception ex) =>
        ex is DbfException { Code: DbfErrorCode.PathEscapesFolder or DbfErrorCode.IncompleteTransaction };

    // A failure reported for the phase the change was in, its own explanation kept as the reason.
    private static DbfException Phased(Exception ex, string path, string what, string hint, DbfErrorCode? code = null)
    {
        if (ex is not DbfException known)
            return FileFailure(ex, path, what, hint, code);
        var reason = known.Message.EndsWith(known.Hint, StringComparison.Ordinal) ? known.Message[..^known.Hint.Length].TrimEnd() : known.Message;
        return new DbfException(code ?? known.Code, $"{what} The reason: {reason}", hint, path, ex);
    }

    // A filesystem failure in a change of structure, explained without the paths it names (they go to
    // the log through FilePath and the inner exception).
    private static DbfException FileFailure(Exception ex, string path, string what, string hint, DbfErrorCode? code = null)
    {
        var known = DbfException.Translate(ex, path, FileAccess.ReadWrite);
        var reason = known switch
        {
            { Code: DbfErrorCode.FileInUse } => "another program has one of its files open",
            { Code: DbfErrorCode.AccessDenied } => "the operating system refused access to a file or folder beside it",
            { Code: DbfErrorCode.ReadOnlyLocation } => "the folder, or a file in it, is read-only",
            { Code: DbfErrorCode.TableNotFound or DbfErrorCode.FolderNotFound } => "a file or folder it needed wasn't there",
            _ => "a file beside it couldn't be written, moved or deleted (the disk may be full)",
        };
        return new DbfException(code ?? known?.Code ?? DbfErrorCode.WriteFailed, $"{what} The reason: {reason}.", hint, path, ex);
    }

    // A change cut off between its swaps leaves the files a mix of old and new: no write may touch them
    // until an exclusive open has finished or undone it.
    private void ThrowIfRestructureUnfinished()
    {
        if (_path is not null && FolderThere(_path + RestructureReadySuffix))
            throw RestructureUnderWay(_path);
    }

    private static DbfException RestructureUnderWay(string path) => new(DbfErrorCode.IncompleteTransaction,
        "This table's structure is being changed, or a change of it was cut off, so its files may be a mix of old and new.",
        "Wait a moment and try again; if it persists, open the table with writable: true, exclusive: true, which finishes the change or puts the table back as it was.", path);
}
