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
    /// Takes the table lock. dBASE needs the table opened EXCLUSIVE for INDEX ON, so do this while the legacy
    /// app is closed: a program that already has the .mdx open won't see the new tag until it reopens it.
    /// dBASE IV tables only; FoxPro tables use .cdx indexes.
    /// </remarks>
    public void CreateIndexTag(string name, string expression, string? forExpression = null, bool unique = false, bool descending = false)
    {
        EnsureWritable();
        var tagName = ValidTagName(name);
        if (Header.IsFoxPro)
            throw new DbfException(DbfErrorCode.UnsupportedFormat, "FoxPro tables keep their tags in a .cdx file, which Thunder.Dbf can't create yet.",
                "Create the tag in FoxPro (INDEX ON ... TAG ...).", _path);
        if (_path is null && _index is null)
            throw new DbfException(DbfErrorCode.UnsupportedFormat, "A table opened from a stream can't get a new .mdx file.",
                "Open the table by path, or pass its .mdx stream to DbfTable.Open.");

        var (keyType, keyLength) = KeyShape(expression);
        if (forExpression is not null)
            CheckFilter(forExpression);

        using var tableLock = Locks.Table();
        Refresh();
        if (Header.HasIncompleteTransaction)
            throw WriteBlocker(Header, allowUnmaintainedIndexes: true, indexMaintained: true, _path)!;

        // Work out every key before the index file is touched, so a record the expression can't handle
        // stops the command with nothing changed.
        var probe = DbfExpression.Parse(expression);
        var filter = forExpression is null ? null : DbfExpression.Parse(forExpression);
        foreach (var record in ReadAll())
        {
            try
            {
                if (filter is null || filter.Evaluate(record) is true)
                    _ = probe.Evaluate(record);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                throw new DbfException(DbfErrorCode.InvalidValue, $"The key expression fails on record {record.RecordNumber}: {ex.Message}",
                    "Fix that record, or change the expression (for example wrap a field in TRIM() or STR()).", _path, ex);
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

        var tag = _index.AddTag(tagName, expression.Trim(), forExpression?.Trim(), keyType, keyLength, unique, descending);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in ReadAll())
        {
            if (!tag.Includes(record))
                continue;
            var key = tag.KeyFor(record);
            // Records arrive in record order, so a unique tag keeps the lowest number per key, as dBASE does.
            if (!unique || seen.Add(Convert.ToHexString(key)))
                tag.Insert(key, record.RecordNumber);
        }
        _index.Commit();
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

    // The key's type and stored width, from the expression's result on a record whose fields are
    // all at their widest, as dBASE sizes a key from the expression's declared width.
    private (char Type, int Length) KeyShape(string expression)
    {
        DbfExpression parsed;
        try
        {
            parsed = DbfExpression.Parse(expression);
            CheckFields(expression);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException)
        {
            throw new DbfException(DbfErrorCode.InvalidValue, $"The key expression can't be used: {ex.Message}",
                "Use fields of this table with dBASE functions, like UPPER(LAST)+DTOS(HIRED).", _path, ex);
        }

        var value = parsed.Evaluate(WidestRecord());
        return value switch
        {
            string text when Encoding.GetByteCount(text) is var width and > 0 and <= MaxCharacterKeyLength => ('C', width),
            string => throw new DbfException(DbfErrorCode.InvalidValue,
                $"The key expression makes keys of {Encoding.GetByteCount((string)value)} characters; dBASE IV allows 1 to {MaxCharacterKeyLength}.",
                "Shorten it with LEFT() or SUBSTR().", _path),
            decimal or double or int or long => ('N', MdxKeys.NumericKeyLength),
            DateOnly or DateTime => ('D', 8),
            _ => throw new DbfException(DbfErrorCode.InvalidValue,
                $"The key expression gives {(value is null ? "nothing" : value is bool ? "a logical value" : value.GetType().Name)}; .mdx keys are text, numbers or dates.",
                "Convert it, for example IIF(ACTIVE, \"Y\", \"N\") for a logical field.", _path),
        };
    }

    private void CheckFilter(string forExpression)
    {
        try
        {
            CheckFields(forExpression);
            if (DbfExpression.Parse(forExpression).Evaluate(WidestRecord()) is not (bool or null))
                throw new ArgumentException("A FOR filter must be a logical expression, like .NOT. DELETED().");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException)
        {
            throw new DbfException(DbfErrorCode.InvalidValue, $"The FOR filter can't be used: {ex.Message}",
                "Use a logical expression over this table's fields, like .NOT. DELETED() or STATE = \"CA\".", _path, ex);
        }
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
    /// every .mdx tag. Returns how many records were removed. Memo blocks of removed records are left
    /// in the memo file as unused space, as dBASE IV leaves them.
    /// </summary>
    /// <remarks>
    /// Record numbers change, so anything that remembered one (a web page, the SQL bridge, another
    /// program's open table) is wrong afterwards. dBASE needs the table opened EXCLUSIVE for PACK: run it
    /// while the legacy app is closed. A copy of the .dbf is kept beside it (<c>.dbf.packing</c>) until the
    /// pack has finished, so a crash halfway leaves something to restore.
    /// </remarks>
    public int Pack()
    {
        EnsureWritable();
        using var tableLock = Locks.Table();
        PrepareWrite(appending: false);

        var backup = _path is null ? null : _path + ".packing";
        if (backup is not null && File.Exists(backup))
            throw new DbfException(DbfErrorCode.IncompleteTransaction,
                "A previous PACK of this table didn't finish; its backup copy is still beside the table.",
                "Check the table. If it's damaged, restore it from the .packing copy; then delete that copy and pack again.", _path);

        var count = RecordCount;
        var length = Header.RecordLength;
        var start = Header.HeaderLength;
        if (backup is not null)
            BackUpForPack(backup, start + (long)count * length + 1);

        var buffer = new byte[length];
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            _stream.Position = start + (long)i * length;
            _stream.ReadExactly(buffer);
            if (buffer[0] == DeletedFlag)
                continue;
            if (kept != i)
            {
                _stream.Position = start + (long)kept * length;
                _stream.Write(buffer);
            }
            kept++;
        }

        Header.RecordCount = (uint)kept;
        _stream.Position = start + (long)kept * length;
        _stream.WriteByte(EndOfFile);
        _stream.SetLength(_stream.Position);
        CommitHeader();

        if (_index is not null)
            RebuildIndex();
        if (backup is not null)
            File.Delete(backup);
        return count - kept;
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
