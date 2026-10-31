//      INDEX ON UPPER(LAST) TAG LASTNAME          PACK
//     +-----+-----+-----+-----+             +-----+-----+-----+-----+-----+
//     |  1  |  2  |  3  |  4  |             |  1  | *2* |  3  | *4* |  5  |    * = deleted
//     +-----+-----+-----+-----+             +-----+-----+-----+-----+-----+
//        |     |     |     |                   |           |           |
//     [ keys in a new .mdx tag ]             +-----+-----+-----+
//                                            |  1  |  2  |  3  |   renumbered, then every tag rebuilt
//     ProjectThunder :: Thunder.Dbf          +-----+-----+-----+
//     The two maintenance commands dBASE users reach for: INDEX ON and PACK.

namespace Thunder.Dbf;

public sealed partial class DbfTable
{
    private const int MaxCharacterKeyLength = 100; // dBASE IV's limit for character keys

    /// <summary>
    /// Adds a tag to the production .mdx, like dBASE's <c>INDEX ON expression TAG name [FOR filter] [UNIQUE] [DESCENDING]</c>,
    /// creating the .mdx when the table has none. The tag is filled from every record (deleted ones too,
    /// as dBASE does) and kept up to date by every write after that.
    /// </summary>
    /// <remarks>
    /// Needs the table opened exclusively (as dBASE needs EXCLUSIVE for INDEX ON), so the legacy app must be
    /// closed: a program that already had the .mdx open wouldn't see the new tag until it reopened it.
    /// On a FoxPro or Visual FoxPro table the tag goes in the structural .cdx (created when there's none),
    /// like Visual FoxPro's <c>INDEX ON ... TAG</c>; that needs <c>maintainFoxProIndexes</c>, so the table
    /// keeps it up to date afterwards.
    /// </remarks>
    public void CreateIndexTag(string name, string expression, string? forExpression = null, bool unique = false, bool descending = false)
        => CreateIndexTag(name, expression, forExpression, unique, descending, candidate: false);

    /// <summary>
    /// <inheritdoc cref="CreateIndexTag(string, string, string?, bool, bool)"/> With <paramref name="candidate"/>, a Visual
    /// FoxPro CANDIDATE tag: no two records may share its key, and a write that would give one a taken key is refused.
    /// </summary>
    public void CreateIndexTag(string name, string expression, string? forExpression, bool unique, bool descending, bool candidate)
    {
        using var forced = ForceEveryWrite();
        CreateIndexTagCore(name, expression, forExpression, unique, descending, candidate);
    }

    private void CreateIndexTagCore(string name, string expression, string? forExpression, bool unique, bool descending, bool candidate)
    {
        EnsureWritable();
        var tagName = ValidTagName(name);
        if (Header.IsFoxPro || Header.IsFoxProByCompanion)
        {
            CreateCdxTag(tagName, expression, forExpression, unique, descending, candidate);
            return;
        }
        if (candidate)
            throw new ArgumentException("CANDIDATE tags are Visual FoxPro's; a dBASE .mdx tag can be UNIQUE instead.", nameof(candidate));
        if (_path is null && _index is null)
            throw new DbfException(DbfErrorCode.UnsupportedFormat, "A table opened from a stream can't get a new .mdx file.",
                "Open the table by path, or pass its .mdx stream to DbfTable.Open.");

        RequireExclusive("INDEX ON");
        var parsed = ParseIndexExpression(expression, "key expression");
        var filter = forExpression is null ? null : ParseIndexExpression(forExpression, "FOR filter");

        using var tableLock = Locks.Table();
        Refresh();
        ThrowIfPackUnfinished();
        if (Header.HasIncompleteTransaction)
            throw WriteBlocker(Header, allowUnmaintainedIndexes: true, indexMaintained: true, _path)!;

        // Evaluate the expression on every record first, then size the key from the results: the
        // widest real value (or the widest a record could make, when that can be worked out), so no
        // existing record's key is cut short. Nothing is written until every key is encoded.
        var values = new List<(int Record, object? Value)>();
        foreach (var record in ReadAll())
        {
            try
            {
                if (filter is not null && filter.Evaluate(record) is not (bool or null))
                    throw new FormatException("A FOR filter must be a logical expression, like .NOT. DELETED().");
                if (filter is null || filter.Evaluate(record) is true)
                    values.Add((record.RecordNumber, parsed.Evaluate(record)));
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or InvalidOperationException
                                           or NotSupportedException or OverflowException or DivideByZeroException)
            {
                throw new DbfException(DbfErrorCode.InvalidValue, $"The {(filter is null ? "key expression" : "key expression or FOR filter")} fails on record {record.RecordNumber}: {ex.Message}",
                    "Fix that record, or change the expression (for example wrap a field in TRIM() or STR()).", _path, ex);
            }
        }
        var (keyType, keyLength) = KeyShape(parsed, values.Select(v => v.Value));

        var keys = new List<(byte[] Key, int Record)>(values.Count);
        foreach (var (record, value) in values)
        {
            try
            {
                keys.Add((MdxTag.EncodeKey(keyType, keyLength, Encoding, value, tagName, expression), record));
            }
            catch (InvalidDataException ex)
            {
                throw new DbfException(DbfErrorCode.InvalidValue, $"Record {record} gives a key of a different type than the others: {ex.Message}",
                    "Make every branch of the expression return the same type (for example STR() around numbers).", _path, ex);
            }
        }

        if (_index is null)
            CreateProductionIndex();
        if (_index!.Tags.Any(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase)))
            throw new DbfException(DbfErrorCode.InvalidValue, $"The index already has a tag named {tagName}.",
                "Pick another name, or rebuild the existing tag with RebuildIndex.", _path);
        if (_index.Tags.Count >= 47)
            throw new DbfException(DbfErrorCode.InvalidValue, "The .mdx already holds 47 tags, the most dBASE IV allows.",
                "Remove a tag in dBASE (DELETE TAG) first.", _path);

        MdxTag tag;
        try
        {
            tag = _index.PrepareTag(tagName, expression.Trim(), forExpression?.Trim(), keyType, keyLength, unique, descending);
        }
        catch (ArgumentException ex)
        {
            throw new DbfException(DbfErrorCode.InvalidValue, $"The tag can't be stored: {ex.Message}", "Shorten the expression, or use characters the table's code page has.", _path, ex);
        }

        // Fill the tag while it's still unlisted, then list it. A unique tag keeps the lowest record
        // number per key, compared the way the tag compares keys (1E2 and 100 are one key).
        foreach (var (key, record) in keys)
        {
            if (!unique || tag.FirstRecordFor(key) is null)
                tag.Insert(key, record);
        }
        _index.Commit();
        _index.PublishTag(tag);
        _index.Commit();
    }

    // Visual FoxPro's INDEX ON ... TAG: the tag goes in the structural .cdx, created when there's none.
    // Everything is checked first, with the whole tag built in a scratch .cdx in memory, so a refusal
    // writes nothing at all, not even a new .cdx or the header flag for it.
    private void CreateCdxTag(string tagName, string expression, string? forExpression, bool unique, bool descending, bool candidate)
    {
        if (_path is null)
            throw new DbfException(DbfErrorCode.UnsupportedFormat, "A table opened from a stream can't get a new .cdx tag.", "Open the table by path.");
        if (!_maintainFoxProIndexes)
            throw new DbfException(DbfErrorCode.UnsupportedFormat, "Creating .cdx tags is part of the FoxPro index preview, and this table wasn't opened with it.",
                "Open with maintainFoxProIndexes: true (Maintain FoxPro Indexes=true), so the table keeps the new tag up to date afterwards.", _path);
        if (unique && candidate)
            throw new ArgumentException("A tag is UNIQUE or CANDIDATE, not both.", nameof(candidate));
        RequireExclusive("INDEX ON");
        expression = expression.Trim();
        forExpression = forExpression?.Trim();
        ParseIndexExpression(expression, "key expression");
        var filter = forExpression is null ? null : ParseIndexExpression(forExpression, "FOR filter");

        using (Locks.Table())
        {
            Refresh();
            ThrowIfPackUnfinished();
            ReplayJournal(); // a cut-off write's note is finished before the index grows

            var existing = StructuralCdx();
            if (existing is not null && existing.Tags.Any(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase)))
                throw new DbfException(DbfErrorCode.InvalidValue, $"The index already has a tag named {tagName}.",
                    "Pick another name, or rebuild the existing tag with RebuildIndex.", _path);

            // Visual FoxPro wants a logical FOR filter.
            if (filter is not null)
            {
                foreach (var record in ReadAll().Prepend(IndexKeys.BlankRecord(this)))
                {
                    if (filter.Evaluate(record) is not (bool or null))
                        throw new DbfException(DbfErrorCode.InvalidValue, "A FOR filter must be a logical expression, like status = \"A\" or !DELETED().",
                            "Write the filter as a condition.", _path);
                }
            }

            int keyLength;
            try
            {
                keyLength = CdxIndex.KeyLengthFor(this, expression);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                // The expression parser's and evaluator's own messages name fields, functions and limits,
                // never a path (guideline 1.2), and they're what tells the user what to change.
                throw new DbfException(DbfErrorCode.InvalidValue, $"The key expression can't be evaluated: {ex.Message}",
                    "Use the table's fields, with UPPER(), DTOS() or STR(), joined with +.", _path, ex);
            }
            var nullableKey = Fields.FirstOrDefault(f => f.Name.Equals(expression, StringComparison.OrdinalIgnoreCase)) is { IsNullable: true };

            using (var scratch = new MemoryStream())
            {
                CdxIndex.WriteEmptyCompound(scratch);
                using var dry = CdxIndex.Open(scratch, Encoding, leaveOpen: true, table: this);
                dry.AddTag(this, tagName, expression, forExpression, keyLength, unique, candidate, descending, nullableKey);
            }

            // The dry run above checked everything a refusal could come from. Only an I/O failure can
            // still stop AddTag, and then a new .cdx stays behind, empty but valid: FoxPro opens it, and
            // a later CreateIndexTag adds to it.
            var index = existing ?? CreateStructuralCdx();
            index.AddTag(this, tagName, expression, forExpression, keyLength, unique, candidate, descending, nullableKey);
        }
        // A structural .cdx moves Visual FoxPro's lock positions; the next lock takes the new ones.
        if (_locks is { HoldsNothing: true })
            _locks = null;
    }

    private CdxIndex? StructuralCdx()
    {
        if (CdxIndex.FindFor(_path!) is not { } path)
            return null;
        if (_attachedIndexes.TryGetValue(Path.GetFullPath(path), out var attached) && attached is CdxIndex index)
            return index;
        throw new DbfException(DbfErrorCode.IndexWouldGoStale, "The table's .cdx isn't one Thunder.Dbf keeps up to date, so a tag can't be added to it.",
            "Rebuild it in Visual FoxPro (REINDEX), or take it over with MaintainIndex(path, rebuild: true) first.", _path,
            diagnostic: _structuralCdxRefusal);
    }

    // A new structural .cdx next to the table: attached first, and only then named in the table's
    // header (the flag that tells FoxPro to open it). Until that flag is written, a failure takes the
    // file away again; after it, the file stays, since FoxPro now expects it.
    private CdxIndex CreateStructuralCdx()
    {
        var path = Path.ChangeExtension(_path!, Path.GetExtension(_path!).Any(char.IsUpper) ? ".CDX" : ".cdx");
        using (var stream = DbfException.OpenFile(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            CdxIndex.WriteEmptyCompound(stream);
        try
        {
            if (!TryAttachIndex(path))
                throw new InvalidDataException("The new .cdx couldn't be opened for updating.");
            _indexFilesAtOpen.Add(Path.GetFileName(path)); // made and kept here, so not one that "appeared"
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        Header.MarkStructuralCdx();
        if (Header.Version is not (0x30 or 0x31 or 0x32 or 0xF5 or 0xFB))
            Header.TreatIndexFlagAsCdx(); // a FoxPro 2 table without memos says 0x03, like dBASE
        CommitHeader();
        return (CdxIndex)_attachedIndexes[Path.GetFullPath(path)];
    }

    // A new .mdx next to the table, and the header flag that tells dBASE to open it.
    private void CreateProductionIndex()
    {
        var path = Path.ChangeExtension(_path!, Path.GetExtension(_path!).Any(char.IsUpper) ? ".MDX" : ".mdx");
        if (MdxIndex.FindFor(_path!) is not null)
            throw new DbfException(DbfErrorCode.IndexDamaged,
                "An .mdx file is next to the table, but the table's header doesn't list it, so it may belong to something else.",
                "Move it aside or delete it if it's stale, then try again.", _path);

        var stream = DbfException.OpenFile(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            MdxIndex.WriteEmpty(stream, Path.GetFileNameWithoutExtension(_path!));
            AttachIndex(stream);
        }
        catch
        {
            stream.Dispose();
            File.Delete(path);
            throw;
        }
        Header.MarkProductionMdx();
        CommitHeader();
    }

    private static string ValidTagName(string name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > 10 || !char.IsAsciiLetter(trimmed[0]) || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException($"Tag name '{name}' must be 1-10 letters, digits or underscores, starting with a letter.", nameof(name));
        return trimmed.ToUpperInvariant();
    }

    private DbfExpression ParseIndexExpression(string text, string what)
    {
        try
        {
            CheckFields(text);
            return DbfExpression.Parse(text);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            throw new DbfException(DbfErrorCode.InvalidValue, $"The {what} can't be used: {ex.Message}",
                "Use this table's fields with dBASE functions, like UPPER(LAST)+DTOS(HIRED), or .NOT. DELETED() for a filter.", _path, ex);
        }
    }

    // The key's type and stored width. Character keys get the widest of the real values and of the
    // value on a record whose fields are all at their widest (when the expression can be evaluated
    // there), so no existing key is cut short and a typical future one fits too.
    private (char Type, int Length) KeyShape(DbfExpression expression, IEnumerable<object?> realValues)
    {
        object? probe;
        try
        {
            probe = expression.Evaluate(WidestRecord());
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or InvalidOperationException
                                       or NotSupportedException or OverflowException or DivideByZeroException)
        {
            probe = null; // 100/ID on an all-zero record, say: the real records still decide
        }

        var samples = realValues.Prepend(probe).Where(v => v is not null).ToList();
        if (samples.Count == 0)
            throw new DbfException(DbfErrorCode.InvalidValue, "The key expression gives no value to work out the key's type from.",
                "Add a record first, or use an expression that works on a blank record.", _path);

        char type = samples[0] switch
        {
            string => 'C',
            decimal or double or int or long => 'N',
            DateOnly or DateTime => 'D',
            var other => throw new DbfException(DbfErrorCode.InvalidValue,
                $"The key expression gives {(other is bool ? "a logical value" : other?.GetType().Name ?? "nothing")}; .mdx keys are text, numbers or dates.",
                "Convert it, for example IIF(ACTIVE, \"Y\", \"N\") for a logical field.", _path),
        };
        if (type != 'C')
            return (type, type == 'N' ? MdxKeys.NumericKeyLength : 8);

        var width = samples.OfType<string>().Select(text => Encoding.GetByteCount(text)).DefaultIfEmpty(0).Max();
        return width is > 0 and <= MaxCharacterKeyLength
            ? ('C', width)
            : throw new DbfException(DbfErrorCode.InvalidValue,
                $"The key expression makes keys of {width} characters; dBASE IV allows 1 to {MaxCharacterKeyLength}.",
                "Shorten it with LEFT() or SUBSTR().", _path);
    }

    private void CheckFields(string expression)
    {
        foreach (var name in DbfExpression.FieldNamesIn(expression))
        {
            var field = Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"The table has no field named {name}.");
            if (DbfValueCodec.IsMemoField(field))
                throw new ArgumentException($"{field.Name} is a memo field; dBASE can't index memos.");
        }
    }

    private DbfRecord WidestRecord()
    {
        var values = Fields.Select<DbfField, object?>(f => f.Type switch
        {
            'C' => new string('W', f.Length),
            'N' or 'F' or 'Y' => 0m,
            'I' => 0,
            'B' => 0d,
            'D' => new DateOnly(2000, 1, 1),
            'T' => new DateTime(2000, 1, 1),
            'L' => false,
            _ => null,
        }).ToArray();
        return new DbfRecord(Header, 0, isDeleted: false, values, Encoding);
    }

    // ---- PACK -----------------------------------------------------------------------------

    /// <summary>
    /// Removes deleted records for good and renumbers the rest, like dBASE's <c>PACK</c>, then rebuilds
    /// every index the table keeps. Returns how many records were removed. Memo blocks of removed records
    /// are left in the memo file as unused space, as dBASE IV leaves them.
    /// </summary>
    /// <remarks>
    /// Record numbers change, so anything that remembered one (a web page, the SQL bridge, another
    /// program's open table) is wrong afterwards, so, as dBASE needs EXCLUSIVE for PACK, the table must be
    /// opened exclusively (the legacy app closed). A copy of the .dbf is kept beside it (<c>.dbf.packing</c>) until
    /// the pack has finished. If a crash interrupts it, every write is refused until the table is put
    /// back: replace the .dbf with that copy, delete the copy, and call <see cref="RebuildIndex"/>.
    /// </remarks>
    public int Pack()
    {
        using var forced = ForceEveryWrite();
        return PackCore();
    }

    private int PackCore()
    {
        EnsureWritable();
        if (_path is null)
            throw new DbfException(DbfErrorCode.UnsupportedFormat, "A table opened from a stream can't be packed, because there's nowhere to keep its safety copy.",
                "Open the table by path.");
        RequireExclusive("PACK");

        using var tableLock = Locks.Table();
        PrepareWrite(appending: false); // also refuses while an earlier pack is unfinished

        // Every surviving record's keys, under its new number, before anything changes: an expression
        // or memo that fails stops the pack with the table untouched.
        var count = RecordCount;
        var tags = MaintainedIndexes.SelectMany(i => i.MaintainedTags).ToList();
        var keysByTag = tags.ToDictionary(tag => tag, _ => new List<(byte[] Key, int Record)>());
        var kept = 0;
        foreach (var record in ReadAll())
        {
            if (record.IsDeleted)
                continue;
            kept++;
            foreach (var tag in tags)
            {
                if (tag.Includes(record))
                    keysByTag[tag].Add((tag.KeyFor(record), kept));
            }
        }
        ThrowIfRebuiltKeysRepeat(keysByTag); // before a record moves

        var length = Header.RecordLength;
        var start = Header.HeaderLength;
        var backup = PackBackupPath!;
        BackUpForPack(backup, start + (long)count * length + 1);

        // 1. Slide the surviving records down, and flush them.
        var buffer = new byte[length];
        var next = 0;
        for (var i = 0; i < count; i++)
        {
            _stream.Position = start + (long)i * length;
            _stream.ReadExactly(buffer);
            if (buffer[0] == DeletedFlag)
                continue;
            if (next != i)
            {
                _stream.Position = start + (long)next * length;
                _stream.Write(buffer);
            }
            next++;
        }
        _stream.Position = start + (long)kept * length;
        _stream.WriteByte(EndOfFile);
        FlushToDisk(_stream);

        // 2. Make the smaller count durable. Only then cut the file, so the header never counts past the end.
        Header.RecordCount = (uint)kept;
        CommitHeader();
        _stream.SetLength(start + (long)kept * length + 1);
        FlushToDisk(_stream);

        // 3. Every index, from the keys worked out above.
        ReplaceAllKeys(keysByTag);
        File.Delete(backup);
        return count - kept;
    }

    private string? PackBackupPath => _path is null ? null : _path + ".packing";

    // An unfinished pack leaves records half moved: no write may touch the table until it's restored.
    private void ThrowIfPackUnfinished()
    {
        if (PackBackupPath is { } backup && File.Exists(backup))
            throw new DbfException(DbfErrorCode.IncompleteTransaction,
                "A PACK of this table didn't finish, so its records may be half moved.",
                "Replace the .dbf with the .packing copy beside it (the table as it was before the pack), delete the copy, then open the table with exclusive: true and call RebuildIndex().", _path);
    }

    private void BackUpForPack(string backup, long bytes)
    {
        using var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        _stream.Position = 0;
        var buffer = new byte[81920];
        for (long copied = 0; copied < bytes;)
        {
            var read = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes - copied));
            if (read == 0)
                break;
            output.Write(buffer, 0, read);
            copied += read;
        }
        output.Flush(flushToDisk: true);
    }
}
