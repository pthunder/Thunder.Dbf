//    INDEX ON code TAG code
//        |
//        v         ProjectThunder :: Thunder.Dbf
//   +---------+    A new .cdx tag, laid out the way Visual FoxPro 9 lays one out: its 1,024-byte
//   | header  |    header, its root on the very next page, its keys built from the table, and only
//   | root    |    then its name in the tag directory, so until that last write nobody sees it.
//   +---------+

using System.Buffers.Binary;

namespace Thunder.Dbf;

public sealed partial class CdxIndex
{
    private const byte OptionStructural = 0x80;
    private const byte OptionNullableKey = 0x02; // Visual FoxPro sets it on a tag over a nullable field
    private const int StatisticsOffset = 16;     // Visual FoxPro: keys, distinct keys, then three it keeps for Rushmore

    /// <summary>
    /// Writes an empty structural .cdx: the tag directory's header (as Visual FoxPro 9 writes it, with
    /// empty expressions) and its empty root leaf on the next page.
    /// </summary>
    internal static void WriteEmptyCompound(Stream stream)
    {
        var file = new byte[HeaderLength + DefaultPageLength];
        BinaryPrimitives.WriteUInt32LittleEndian(file, HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), CdxTag.TagNameLength);
        file[14] = OptionCompact | OptionCompound | OptionStructural;
        file[15] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(CdxTag.ForPositionOffset), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(CdxTag.ForPositionOffset + 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(CdxTag.KeyPositionOffset + 2), 1);

        // The directory's root: an empty leaf (root and leaf), no neighbours, every byte after the
        // header free, and the bit widths FoxPro gives a 10-byte-key leaf.
        var root = file.AsSpan(HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(root, RootAndLeaf);
        BinaryPrimitives.WriteUInt32LittleEndian(root[4..], NoPage);
        BinaryPrimitives.WriteUInt32LittleEndian(root[8..], NoPage);
        BinaryPrimitives.WriteUInt16LittleEndian(root[12..], DefaultPageLength - CdxTag.LeafHeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(root[14..], 0xFFFF);
        root[18] = 0x0F;
        root[19] = 0x0F;
        root[20] = 16;
        root[21] = 4;
        root[22] = 4;
        root[23] = 3;

        stream.SetLength(0);
        stream.Position = 0;
        stream.Write(file);
        DbfTable.FlushToDisk(stream);
    }

    private const ushort RootAndLeaf = 0x03;

    /// <summary>
    /// Adds a tag to this structural .cdx and fills it from <paramref name="table"/>. The header and root
    /// go at the end of the file and are filled first; the tag directory names it last, so a crash
    /// leaves only unused pages. A tag whose keys this library can't build exactly as Visual FoxPro
    /// would, or a CANDIDATE tag the table's records would give a key twice, is refused before it's
    /// listed, and the file is cut back to what it was.
    /// </summary>
    internal CdxTag AddTag(DbfTable table, string name, string expression, string? forExpression, int keyLength,
        bool unique, bool candidate, bool descending, bool nullableKey)
    {
        var originalLength = _stream.Length;
        var published = false;
        try
        {
            var headerOffset = AllocatePage();
            if (AllocatePage() != headerOffset + DefaultPageLength)
                throw new InvalidDataException("The .cdx didn't grow by whole pages.");
            var rootOffset = AllocatePage();

            var header = new byte[HeaderLength];
            BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)rootOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), checked((ushort)keyLength));
            header[14] = (byte)(OptionCompact | OptionCompound
                | (unique ? OptionUnique : 0) | (nullableKey ? OptionNullableKey : 0)
                | (candidate ? OptionCandidate : 0) | (forExpression is not null ? OptionForFilter : 0));
            header[15] = 0x01;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(CdxTag.DescendingOffset), descending ? (ushort)1 : (ushort)0);

            // The pool: the key expression at 0, the FOR expression right after its NUL, as FoxPro puts them.
            byte[] key, filter;
            try
            {
                var strict = DbfEncoding.Strict(Encoding);
                key = strict.GetBytes(expression);
                filter = forExpression is null ? [] : strict.GetBytes(forExpression);
            }
            catch (System.Text.EncoderFallbackException ex)
            {
                throw new DbfException(DbfErrorCode.InvalidValue, "The tag's expression has a character the table's code page can't hold.",
                    "Use characters the code page has, as FoxPro stores the expression in it.", table.FilePath, ex);
            }
            if (key.Length + 1 + filter.Length + 1 > CdxTag.PoolLength)
                throw new DbfException(DbfErrorCode.InvalidValue, "The key and FOR expressions are too long for a .cdx tag.",
                    $"Keep them under {CdxTag.PoolLength - 2} bytes together.", table.FilePath);
            key.CopyTo(header.AsSpan(CdxTag.PoolOffset));
            filter.CopyTo(header.AsSpan(CdxTag.PoolOffset + key.Length + 1));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(CdxTag.KeyPositionOffset), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(CdxTag.KeyPositionOffset + 2), checked((ushort)(key.Length + 1)));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(CdxTag.ForPositionOffset), checked((ushort)(key.Length + 1)));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(CdxTag.ForPositionOffset + 2), checked((ushort)(filter.Length + 1)));
            Write(headerOffset, header);

            var tag = CdxTag.Load(this, name, headerOffset, table);
            tag.WriteEmptyRoot();
            if (tag.PrepareForWriting(table) is { } reason)
                throw new DbfException(DbfErrorCode.InvalidValue, $"Tag {name} can't be created here: {reason}.",
                    "Create it in Visual FoxPro (INDEX ON ... TAG ...), or use an expression this library builds keys for exactly (fields, UPPER(), DTOS(), STR() of whole numbers, joined with +).", table.FilePath);

            var keys = new List<(byte[] Key, int Record)>();
            foreach (var record in table.ReadAll())
            {
                if (tag.Includes(record))
                    keys.Add((tag.KeyBytes(record), record.RecordNumber));
            }
            if (candidate && keys.GroupBy(k => Convert.ToHexString(k.Key)).FirstOrDefault(g => g.Count() > 1) is { } taken)
                throw new DbfException(DbfErrorCode.InvalidValue,
                    $"Tag {name} can't be a candidate key: records {string.Join(" and ", taken.Select(k => k.Record).Take(2))} have the same key.",
                    "Give every record its own value first, or make the tag UNIQUE (which keeps the first record per key) instead.", table.FilePath);

            Update(tag, () => { tag.ReplaceAll(keys); return true; });

            // Visual FoxPro keeps statistics for its query optimiser: how many keys, and how many
            // different ones. It brings them up to date itself on REINDEX.
            var written = Read(headerOffset, HeaderLength);
            BinaryPrimitives.WriteInt32LittleEndian(written.AsSpan(StatisticsOffset), keys.Count);
            BinaryPrimitives.WriteInt32LittleEndian(written.AsSpan(StatisticsOffset + 4),
                keys.Select(k => Convert.ToHexString(k.Key)).Distinct().Count());
            Write(headerOffset, written);
            Commit();

            // Listed last: from here on FoxPro (and this library) see the tag, and the file is never cut back.
            published = true;
            var directory = CdxTag.LoadDirectory(this);
            var nameKey = new byte[CdxTag.TagNameLength];
            nameKey.AsSpan().Fill((byte)' ');
            System.Text.Encoding.ASCII.GetBytes(name.ToUpperInvariant(), nameKey);
            Update(directory, () => { directory.Insert(nameKey, checked((int)headerOffset)); return true; });
            Commit();

            Tags = LoadTags();
            _ourState = null; // the next write prepares the tags afresh, the new one with them
            return Tags.Single(t => t.HeaderOffset == headerOffset);
        }
        catch
        {
            // Until the directory is changed nothing lists the new pages, so cutting the file back loses
            // nothing; once it's begun, the directory may already point at them.
            if (!published && _stream.Length > originalLength)
            {
                _stream.SetLength(originalLength);
                _knownLength = originalLength;
                DbfTable.FlushToDisk(_stream);
            }
            throw;
        }
    }

    /// <summary>
    /// The key length Visual FoxPro gives an expression: 4 for a bare integer field, 8 for numbers,
    /// dates, datetimes and currency, 1 for logicals, and the text's width for character keys (one more
    /// over a nullable field, for VFP's null marker byte).
    /// </summary>
    internal static int KeyLengthFor(DbfTable table, string expression)
    {
        var bare = table.Fields.FirstOrDefault(f => f.Name.Equals(expression.Trim(), StringComparison.OrdinalIgnoreCase));
        if (bare is not null)
        {
            return bare.Type switch
            {
                'I' => 4,
                'N' or 'F' or 'B' or 'Y' or 'D' or 'T' => 8,
                'L' => 1,
                'C' => bare.Length + (bare.IsNullable ? 1 : 0),
                _ => throw new DbfException(DbfErrorCode.InvalidValue, $"A {bare.Type} field can't be indexed.", "Index a character, number, date or logical field.", table.FilePath),
            };
        }
        return DbfExpression.Parse(expression).Evaluate(IndexKeys.BlankRecord(table)) switch
        {
            string text => table.Encoding.GetByteCount(text),
            decimal or double or int or DateOnly or DateTime or null => 8,
            bool => 1,
            var other => throw new DbfException(DbfErrorCode.InvalidValue, $"The expression gives a {other.GetType().Name}, which can't be a key.",
                "Make it return text, a number, a date or a logical.", table.FilePath),
        };
    }
}
