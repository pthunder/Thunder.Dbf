//   ProjectThunder :: Thunder.Dbf
//   Keeping a FoxPro .cdx (and compact .idx) right while the table changes underneath it.
//
//   A leaf, 512 bytes:                                      key bytes grow down from the end
//   +------+-----+------+-------+------+------+--+--+--+--+--------------+  .....  +----------+
//   | attr |  n  | left | right | free | rec  |dm|tm|rb|db|tb|el| entries ->  (gap)  <- keys   |
//   |  2   |  2  |  4   |   4   |  2   | mask |  |  |  |  |  |  | n x el       zeroed          |
//   +------+-----+------+-------+------+------+--+--+--+--+--------------+  .....  +----------+
//    0      2     4      8       12     14     18 19 20 21 22 23 24
//
//   Each entry packs  record | duplicates << rb | trailing << (rb + db)  into el bytes. FoxPro picks
//   the widths per leaf: db = tb = the bits that hold the key length, and el the fewest bytes that
//   leave room for the leaf's highest record number. Every leaf re-encoded here gets fresh widths
//   the same way, so an untouched leaf comes out byte for byte as FoxPro wrote it (bar the gap,
//   where FoxPro leaves stale bytes behind and this writes zeros).
//
//   Splits, merges and the free list follow Harbour's src/rdd/dbfcdx (read, not copied): the tree
//   is always in ascending key order (DESCENDING is a reading direction), interior key i is the
//   highest key under child i, and siblings on every level link left and right.

using System.Buffers.Binary;
using System.Globalization;

namespace Thunder.Dbf;

public sealed partial class CdxIndex : IMaintainedIndex
{
    private const int FreeListOffset = 4;
    private const int CounterOffset = 8;

    /// <summary>The largest index file FoxPro handles: 2 GB.</summary>
    private const long MaxIndexBytes = int.MaxValue;

    private DbfTable? _table;
    private bool _changed;

    // Tags whose stored keys were already compared with the table, so reloading before every
    // write doesn't read the same records again. Keyed by everything that decides a tag's keys.
    private readonly HashSet<string> _verifiedTags = [];

    /// <summary>The table the index was opened with, whose records its keys are built from.</summary>
    internal DbfTable? Table => _table;

    // A writable table opens its index files by path, read-write, and passes itself along. Other
    // callers (a MemoryStream to read, say) aren't held to the checks writing needs; if they do
    // write through the index, each tag is still checked before its first key.
    private bool OpenedForWriting => _stream is FileStream { CanWrite: true } && _table?.FilePath is not null;

    IEnumerable<IMaintainedTag> IMaintainedIndex.MaintainedTags => Tags;
    void IMaintainedIndex.ReloadTags() => ReloadTags();
    void IMaintainedIndex.Commit() => Commit();

    // Opening for writing: an index that can't be kept exactly as FoxPro would keep it is refused as
    // a whole, so the table treats it like any other index it can't maintain (writes are blocked,
    // unless the caller allowed unmaintained indexes and will REINDEX in FoxPro).
    private void PrepareForWriting()
    {
        if (OwnedByAnotherTable())
            throw new NotSupportedException("This index is named after another table next to it, so it isn't this table's to update.");
        if (FirstRefusal() is { } reason)
            throw new NotSupportedException(reason);
    }

    // A .cdx or .idx that shares its name with a different .dbf belongs to that table, even when its
    // expressions happen to name fields this table also has. Updating it from this table's rows
    // would fill it with the wrong records.
    private bool OwnedByAnotherTable()
    {
        if (_stream is not FileStream file || _table?.FilePath is not { } tablePath)
            return false;
        var owner = DbfTable.FindCompanion(file.Name, ".dbf");
        return owner is not null && !string.Equals(Path.GetFullPath(owner), Path.GetFullPath(tablePath), StringComparison.OrdinalIgnoreCase);
    }

    private string? FirstRefusal()
    {
        foreach (var tag in Tags)
        {
            if (tag.PrepareForWriting(_table!) is { } reason)
                return $"Tag {tag.Name} can't be kept up to date: {reason}.";

            var identity = $"{tag.HeaderOffset}|{tag.KeyLength}|{tag.Expression}|{tag.ForExpression}";
            if (_verifiedTags.Contains(identity))
                continue;
            if (tag.CompareSampleWithTable(_table!) is { } mismatch)
                return $"Tag {tag.Name} can't be kept up to date: {mismatch}.";
            _verifiedTags.Add(identity);
        }
        return null;
    }

    /// <summary>Re-reads the tag directory and headers, in case FoxPro changed them (a REINDEX, a new tag).</summary>
    internal void ReloadTags()
    {
        Tags = LoadTags();
        if (!OpenedForWriting)
            return;
        if (FirstRefusal() is { } reason)
            throw new DbfException(DbfErrorCode.IndexWouldGoStale, $"{reason} Writing would leave the FoxPro index out of date.",
                "Rebuild the tag in FoxPro with MACHINE collation, or open with allowUnmaintainedIndexes: true only if it will be rebuilt (REINDEX) before it's used.",
                _table.FilePath);
    }

    /// <summary>Bumps the update counter (so other programs drop their cached pages) and pushes everything to disk.</summary>
    internal void Commit()
    {
        if (_changed)
        {
            // Harbour keeps this big-endian counter in the file's first header and compares it to
            // spot changes made by other processes; FoxPro leaves it alone.
            Span<byte> counter = stackalloc byte[4];
            _stream.Position = CounterOffset;
            _stream.ReadExactly(counter);
            BinaryPrimitives.WriteUInt32BigEndian(counter, unchecked(BinaryPrimitives.ReadUInt32BigEndian(counter) + 1));
            _stream.Position = CounterOffset;
            _stream.Write(counter);
            _changed = false;
        }
        DbfTable.FlushToDisk(_stream);
    }

    internal void Write(long offset, ReadOnlySpan<byte> bytes)
    {
        if (offset < 0 || offset % DefaultPageLength != 0 || offset + bytes.Length > _stream.Length)
            throw new InvalidOperationException($"Can't write the .cdx page at offset {offset}.");
        _stream.Position = offset;
        _stream.Write(bytes);
        _changed = true;
    }

    /// <summary>
    /// A zeroed page at the physical end of the file. The free list isn't reused: its links come
    /// from the file, and following a damaged one would overwrite a live page. Freed pages still go
    /// on the list, for FoxPro and Harbour, which do reuse them.
    /// </summary>
    internal long AllocatePage()
    {
        var offset = (_stream.Length + PageLength - 1) / PageLength * PageLength;
        if (offset + PageLength > MaxIndexBytes)
            throw new InvalidDataException("The .cdx index has reached the 2 GB FoxPro can handle.");
        _stream.SetLength(offset + PageLength);
        Write(offset, new byte[PageLength]);
        return offset;
    }

    /// <summary>Puts a page at the head of the free list, whose head lives at offset 4 of the file's first header.</summary>
    internal void FreePage(long offset)
    {
        // Like Harbour: a free page holds only the link to the next one, in its first four bytes.
        var header = Read(0, DefaultPageLength);
        var page = new byte[PageLength];
        header.AsSpan(FreeListOffset, 4).CopyTo(page);
        Write(offset, page);

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(FreeListOffset), checked((uint)offset));
        Write(0, header);
    }
}

public sealed partial class CdxTag : IMaintainedTag
{
    private const int MaxWritableKeyLength = 240; // FoxPro's limit, and what keeps the count fields in 8 bits
    private const int SampleSize = 32;

    private enum KeyKind { Character, Double, Date, Logical, Integer }

    private DbfExpression? _keyExpression;
    private DbfExpression? _forExpression;
    private KeyKind _kind;
    private bool _prepared;

    bool IMaintainedTag.IsUnique => IsUnique;
    bool IMaintainedTag.Includes(DbfRecord record) => Includes(record);
    byte[] IMaintainedTag.KeyFor(DbfRecord record) => KeyFor(record);
    void IMaintainedTag.Insert(byte[] key, int recordNumber) => Insert(key, recordNumber);
    bool IMaintainedTag.Remove(byte[] key, int recordNumber) => Remove(key, recordNumber);
    int? IMaintainedTag.FirstRecordFor(byte[] key) => FirstRecordFor(key);
    void IMaintainedTag.Clear() => Clear();

    // ---- can this tag be kept exactly? ------------------------------------------------------

    /// <summary>
    /// Parses the expressions and works out the key layout. Returns why the tag can't be kept up to
    /// date exactly as FoxPro would, or null when it can.
    /// </summary>
    internal string? PrepareForWriting(DbfTable table)
    {
        if (IgnoreCase)
            return "its keys were upper-cased by CLIP or Harbour (IGNORECASE), which isn't reproduced here";
        if (CollationMarker is { } collation && !collation.Equals("MACHINE", StringComparison.OrdinalIgnoreCase))
            return $"it's sorted by the collation '{collation}', and only FoxPro's MACHINE (byte) order is reproduced exactly";
        if (KeyLength > MaxWritableKeyLength)
            return $"its {KeyLength}-byte keys are longer than the {MaxWritableKeyLength} FoxPro allows";

        object? blank;
        try
        {
            _keyExpression = DbfExpression.Parse(Expression);
            _forExpression = string.IsNullOrWhiteSpace(ForExpression) ? null : DbfExpression.Parse(ForExpression);

            var names = DbfExpression.FieldNamesIn(Expression).Concat(ForExpression is null ? [] : DbfExpression.FieldNamesIn(ForExpression));
            foreach (var name in names)
            {
                var field = table.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                if (field is null)
                    return $"its expression uses '{name}', which isn't a field of the table (a long name from a database container?)";
                // Visual FoxPro adds a null marker byte to keys over nullable fields, and keys datetime and
                // currency values in layouts no real file here shows; guessing would mislead FoxPro.
                if (field.IsNullable)
                    return $"its expression uses the nullable field {field.Name}, whose keys carry a null marker that isn't reproduced here";
                if (field.Type is 'T' or 'Y')
                    return $"its expression uses the {(field.Type == 'T' ? "datetime" : "currency")} field {field.Name}, whose key layout isn't reproduced here";
            }
            blank = _keyExpression.Evaluate(IndexKeys.BlankRecord(table));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or KeyNotFoundException
                                       or FormatException or ArgumentException or OverflowException)
        {
            return $"its expression can't be evaluated here ({ex.Message})";
        }

        KeyKind? kind = blank switch
        {
            string when KeyType == 'C' => KeyKind.Character,
            decimal or double when KeyLength == 8 => KeyKind.Double,
            decimal or double when KeyLength == 4 && IsBareIntegerField(table) => KeyKind.Integer,
            DateOnly or null when KeyLength == 8 => KeyKind.Date,
            bool when KeyLength == 1 => KeyKind.Logical,
            _ => null,
        };
        if (kind is null)
            return $"its {KeyLength}-byte keys don't match what its expression returns ({blank?.GetType().Name ?? "a date"})";
        _kind = kind.Value;
        _prepared = true;
        return null;
    }

    // Visual FoxPro keys a bare integer (I) field as 4 bytes; any other numeric expression is a double.
    private bool IsBareIntegerField(DbfTable table) =>
        table.Fields.FirstOrDefault(f => string.Equals(f.Name, Expression.Trim(), StringComparison.OrdinalIgnoreCase)) is { Type: 'I' };

    /// <summary>
    /// Recomputes the keys stored in the first and last leaves from their records. Any difference
    /// means FoxPro built the tag some way this code doesn't reproduce (a collation, a key layout), or
    /// that the index isn't this table's; either way, writing to it would mislead FoxPro.
    /// </summary>
    internal string? CompareSampleWithTable(DbfTable table)
    {
        var first = EdgeLeaf(last: false);
        var last = EdgeLeaf(last: true);
        var sample = Enumerable.Range(0, Math.Min(first.Count, SampleSize)).Select(i => (first.Keys[i], first.Records[i]))
            .Concat(Enumerable.Range(Math.Max(0, last.Count - SampleSize), Math.Min(last.Count, SampleSize)).Select(i => (last.Keys[i], last.Records[i])));
        foreach (var (key, record) in sample)
        {
            if (record > table.RecordCount)
                return $"it points at record {record}, past the table's last record, so it isn't in step with the table";
            var row = table.Read(record - 1);
            if (!Includes(row) || !KeyBytes(row).AsSpan().SequenceEqual(key))
                return $"the key it stores for record {record} isn't the one FoxPro's MACHINE order gives that record (a collation or key layout that isn't reproduced, or an index out of step with the table)";
        }
        return null;
    }

    private Node EdgeLeaf(bool last)
    {
        var node = ReadNode(RootOffset(), inByteOrder: true);
        var visited = new HashSet<long> { node.Offset };
        for (var depth = 0; !node.IsLeaf; depth++)
        {
            GuardDepth(depth);
            node = ReadChild(node.Children![last ? ^1 : 0], visited);
        }
        return node;
    }

    // ---- keys for records -------------------------------------------------------------------

    internal bool Includes(DbfRecord record)
    {
        EnsurePrepared();
        return _forExpression is null || _forExpression.Evaluate(record) is true;
    }

    /// <summary>
    /// The key FoxPro stores for <paramref name="record"/>. For a candidate tag, a key another
    /// record already holds is refused here, before the table is touched, as FoxPro refuses it.
    /// </summary>
    internal byte[] KeyFor(DbfRecord record)
    {
        var key = KeyBytes(record);
        if (IsCandidate)
        {
            foreach (var hit in Walk(key, backwards: false))
            {
                if (!hit.Matches)
                    break;
                if (hit.Entry.RecordNumber != record.RecordNumber)
                    throw new DbfException(DbfErrorCode.InvalidValue,
                        $"Uniqueness of index {Name} would be violated: record {hit.Entry.RecordNumber} already has this key.",
                        $"Give the record a {Name} value no other record has; FoxPro refuses duplicates in a candidate or primary key the same way.");
            }
        }
        return key;
    }

    internal byte[] KeyBytes(DbfRecord record)
    {
        EnsurePrepared();
        var value = _keyExpression!.Evaluate(record);
        switch (_kind)
        {
            case KeyKind.Character:
            {
                var text = value as string ?? throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce text.");
                var key = new byte[KeyLength];
                key.AsSpan().Fill((byte)' ');
                var encoded = _index.Encoding.GetBytes(text);
                encoded.AsSpan(0, Math.Min(encoded.Length, KeyLength)).CopyTo(key);
                return key;
            }
            case KeyKind.Double:
                // Through text, so the double is the correctly rounded one FoxPro reads from the field.
                return IndexKeys.WriteOrderedDouble(value switch
                {
                    decimal d => double.Parse(d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                    double d => d,
                    _ => throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce a number."),
                });
            case KeyKind.Integer:
            {
                var number = value as decimal? ?? throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce a number.");
                var key = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(key, unchecked((uint)decimal.ToInt32(number) + 0x80000000));
                return key;
            }
            case KeyKind.Date:
                return IndexKeys.WriteOrderedDouble(value switch
                {
                    DateOnly date => IndexKeys.JulianDay(date),
                    null => 0d,
                    _ => throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce a date."),
                });
            default:
                return [(byte)(value is true ? 'T' : 'F')];
        }
    }

    private void EnsurePrepared()
    {
        if (_prepared)
            return;
        var table = _index.Table ?? throw new InvalidOperationException($"Tag {Name} wasn't opened with its table, so its keys can't be built.");
        if (PrepareForWriting(table) is { } reason)
            throw new DbfException(DbfErrorCode.IndexWouldGoStale, $"Tag {Name} can't be kept up to date: {reason}.",
                "Rebuild the tag in FoxPro with MACHINE collation, or open with allowUnmaintainedIndexes: true only if it will be rebuilt (REINDEX) before it's used.",
                table.FilePath);
    }

    /// <summary>The record number of the first entry with exactly this key, or null.</summary>
    internal int? FirstRecordFor(byte[] key)
    {
        foreach (var hit in Walk(key, backwards: false))
            return hit.Matches ? hit.Entry.RecordNumber : null;
        return null;
    }

    // ---- nodes ------------------------------------------------------------------------------

    private sealed class Node
    {
        public long Offset;
        public ushort Attributes;
        public uint Left = NoPage;
        public uint Right = NoPage;
        public List<byte[]> Keys = [];
        public List<int> Records = [];
        public List<uint>? Children; // null for a leaf

        public bool IsLeaf => Children is null;
        public int Count => Keys.Count;
    }

    private const ushort RootAttribute = 0x01;

    private int MaxInteriorKeys => (PageLength - InteriorHeaderLength) / (KeyLength + 8);

    private Node ReadNode(long offset, bool inByteOrder = false)
    {
        var page = _index.Read(offset, PageLength);
        var attributes = BinaryPrimitives.ReadUInt16LittleEndian(page);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(2));
        if (attributes > 7)
            throw new InvalidDataException($"Tag {Name} reaches the page at offset {offset}, which isn't an index node (type {attributes}).");

        var node = new Node
        {
            Offset = offset,
            Attributes = attributes,
            Left = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(4)),
            Right = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(8)),
        };

        if ((attributes & LeafAttribute) != 0)
        {
            var (keys, records) = DecodeLeaf(page, count, offset);
            node.Keys = [.. keys];
            node.Records = [.. records];
        }
        else
        {
            if (count == 0 || count > MaxInteriorKeys)
                throw new InvalidDataException($"The index page at offset {offset} claims {count} keys; tag {Name} holds 1 to {MaxInteriorKeys} per interior page.");
            node.Children = new List<uint>(count + 1);
            for (var i = 0; i < count; i++)
            {
                var item = page.AsSpan(InteriorHeaderLength + i * (KeyLength + 8));
                node.Keys.Add(item[..KeyLength].ToArray());
                node.Records.Add(IndexKeys.RecordNumber(BinaryPrimitives.ReadUInt32BigEndian(item[KeyLength..]), Name));
                var child = BinaryPrimitives.ReadUInt32BigEndian(item[(KeyLength + 4)..]);
                if (child is 0 or NoPage || child % CdxIndex.DefaultPageLength != 0)
                    throw new InvalidDataException($"The index page at offset {offset} has no valid child for key {i}; tag {Name} is corrupt.");
                node.Children.Add(child);
            }
        }

        // Changes are placed by byte order, so changing a node in any other order (X# sorts some tags
        // case-blind without saying so) would scramble it. Reading for a rebuild needs no order.
        for (var i = 1; inByteOrder && i < node.Count; i++)
        {
            if (node.Keys[i - 1].AsSpan().SequenceCompareTo(node.Keys[i]) > 0)
                throw new InvalidDataException($"Tag {Name}'s keys at offset {offset} aren't in byte order; it was sorted by a collation this code doesn't reproduce. Rebuild it with RebuildIndex (or REINDEX with MACHINE collation in FoxPro).");
        }
        return node;
    }

    private Node ReadChild(uint offset, HashSet<long> visited)
    {
        if (!visited.Add(offset))
            throw new InvalidDataException($"Tag {Name} reaches the index page at offset {offset} twice; the index is corrupt.");
        return ReadNode(offset, inByteOrder: true);
    }

    private void WriteNode(Node node)
    {
        var page = node.IsLeaf ? EncodeLeaf(node) : EncodeInterior(node);
        _index.Write(node.Offset, page ?? throw new InvalidOperationException($"Tag {Name}'s page at offset {node.Offset} doesn't fit."));
    }

    private byte[]? EncodeInterior(Node node)
    {
        if (node.Count > MaxInteriorKeys)
            return null;
        var page = new byte[PageLength];
        WriteNodeHeader(page, node);
        for (var i = 0; i < node.Count; i++)
        {
            var item = page.AsSpan(InteriorHeaderLength + i * (KeyLength + 8));
            node.Keys[i].CopyTo(item);
            BinaryPrimitives.WriteUInt32BigEndian(item[KeyLength..], (uint)node.Records[i]);
            BinaryPrimitives.WriteUInt32BigEndian(item[(KeyLength + 4)..], node.Children![i]);
        }
        return page;
    }

    private static void WriteNodeHeader(Span<byte> page, Node node)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(page, node.Attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(page[2..], (ushort)node.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(page[4..], node.Left);
        BinaryPrimitives.WriteUInt32LittleEndian(page[8..], node.Right);
    }

    /// <summary>
    /// Packs a leaf the way FoxPro does (see the diagram at the top), or returns null when the keys
    /// don't fit in one page.
    /// </summary>
    private byte[]? EncodeLeaf(Node node)
    {
        var countBits = BitLength((uint)KeyLength);
        var highestRecord = node.Count == 0 ? 0 : node.Records.Max();
        var recordBitsNeeded = Math.Max(BitLength((uint)highestRecord), 1);
        var entryLength = 1;
        while (entryLength * 8 - 2 * countBits < recordBitsNeeded)
            entryLength++;
        // Past 32 bits the mask field can't describe it; the spare bits then sit unused.
        var recordBits = Math.Min(entryLength * 8 - 2 * countBits, 32);

        var entriesEnd = LeafHeaderLength + node.Count * entryLength;
        if (entriesEnd > PageLength)
            return null;

        var page = new byte[PageLength];
        WriteNodeHeader(page, node);
        var dataStart = PageLength;
        var trail = TrailByte;
        for (var i = 0; i < node.Count; i++)
        {
            var key = node.Keys[i];
            var trailing = KeyLength - key.AsSpan().TrimEnd(trail).Length;
            var duplicates = 0;
            if (i > 0)
            {
                // A shared prefix never overlaps the dropped trailing bytes (FoxPro and Harbour agree).
                var previous = node.Keys[i - 1];
                while (duplicates < KeyLength - trailing && key[duplicates] == previous[duplicates])
                    duplicates++;
            }

            var stored = KeyLength - duplicates - trailing;
            dataStart -= stored;
            if (dataStart < entriesEnd)
                return null;
            key.AsSpan(duplicates, stored).CopyTo(page.AsSpan(dataStart));

            var packed = (ulong)(uint)node.Records[i] | (ulong)duplicates << recordBits | (ulong)trailing << (recordBits + countBits);
            for (var b = 0; b < entryLength; b++)
                page[LeafHeaderLength + i * entryLength + b] = (byte)(packed >> (8 * b));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12), (ushort)(dataStart - entriesEnd));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), recordBits == 32 ? uint.MaxValue : (1u << recordBits) - 1);
        page[18] = page[19] = (byte)((1 << countBits) - 1);
        (page[20], page[21], page[22], page[23]) = ((byte)recordBits, (byte)countBits, (byte)countBits, (byte)entryLength);
        return page;
    }

    private static int BitLength(uint value) => 32 - System.Numerics.BitOperations.LeadingZeroCount(value);

    /// <summary>Re-encodes a leaf exactly as stored, for the tests' fidelity check.</summary>
    internal byte[] ReencodeLeaf(long offset) => EncodeLeaf(ReadNode(offset)) ?? throw new InvalidDataException($"The leaf at offset {offset} doesn't fit once re-encoded.");

    /// <summary>The offsets of every leaf, left to right, for the tests.</summary>
    internal List<long> LeafOffsets()
    {
        var leaves = new List<long>();
        CollectPages(ReadNode(RootOffset()), [], leaves, leavesOnly: true, depth: 0);
        return leaves;
    }

    /// <summary>Every stored (key bytes, record) in tree order, for the tests.</summary>
    internal List<(byte[] Key, int Record)> RawEntries() =>
        LeafOffsets().Select(offset => ReadNode(offset)).SelectMany(leaf => leaf.Keys.Zip(leaf.Records)).ToList();

    /// <summary>Every page the tag's tree uses, for the tests.</summary>
    internal List<long> Pages()
    {
        var pages = new List<long>();
        CollectPages(ReadNode(RootOffset()), [], pages, leavesOnly: false, depth: 0);
        return pages;
    }

    /// <summary>Levels from the root down to the leaves, for the tests.</summary>
    internal int Height()
    {
        var node = ReadNode(RootOffset());
        var height = 1;
        for (; !node.IsLeaf; height++)
        {
            GuardDepth(height);
            node = ReadNode(node.Children![0]);
        }
        return height;
    }

    private void CollectPages(Node node, HashSet<long> visited, List<long> pages, bool leavesOnly, int depth)
    {
        GuardDepth(depth);
        if (!visited.Add(node.Offset))
            throw new InvalidDataException($"Tag {Name} reaches the index page at offset {node.Offset} twice; the index is corrupt.");
        if (node.IsLeaf || !leavesOnly)
            pages.Add(node.Offset);
        if (node.IsLeaf)
            return;
        foreach (var child in node.Children!)
        {
            if (visited.Contains(child))
                throw new InvalidDataException($"Tag {Name} reaches the index page at offset {child} twice; the index is corrupt.");
            CollectPages(ReadNode(child), visited, pages, leavesOnly, depth + 1);
        }
    }

    private void GuardDepth(int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"Tag {Name} is deeper than {MaxDepth} levels; the index is probably corrupt.");
    }

    private void SetRoot(long offset)
    {
        var header = _index.Read(HeaderOffset, CdxIndex.DefaultPageLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)offset));
        _index.Write(HeaderOffset, header);
    }

    private static int CompareEntries(byte[] key, int record, byte[] otherKey, int otherRecord)
    {
        var order = key.AsSpan().SequenceCompareTo(otherKey);
        return order != 0 ? order : record.CompareTo(otherRecord);
    }

    private void CheckKey(byte[] key)
    {
        if (key.Length != KeyLength)
            throw new ArgumentException($"Tag {Name} has {KeyLength}-byte keys, not {key.Length}.", nameof(key));
    }

    // ---- insert -----------------------------------------------------------------------------

    /// <summary>Adds (key, record number) in (key, record) order, splitting pages as needed.</summary>
    internal void Insert(byte[] key, int recordNumber)
    {
        CheckKey(key);
        var path = new List<(Node Node, int Slot)>();
        var node = ReadNode(RootOffset(), inByteOrder: true);
        var visited = new HashSet<long> { node.Offset };
        while (!node.IsLeaf)
        {
            GuardDepth(path.Count);
            // The first child whose highest entry is at or past the new one; past them all, the last.
            var slot = 0;
            while (slot < node.Count - 1 && CompareEntries(node.Keys[slot], node.Records[slot], key, recordNumber) < 0)
                slot++;
            path.Add((node, slot));
            node = ReadChild(node.Children![slot], visited);
        }

        var at = node.Count;
        while (at > 0 && CompareEntries(node.Keys[at - 1], node.Records[at - 1], key, recordNumber) > 0)
            at--;
        node.Keys.Insert(at, key);
        node.Records.Insert(at, recordNumber);
        Store(node, path, appendedAtEnd: at == node.Count - 1 && node.Right == NoPage);
    }

    /// <summary>
    /// Writes a changed node, splitting it when it no longer fits, and carries the change up: each
    /// parent's entry for a child is that child's last key, and a split adds an entry.
    /// </summary>
    private void Store(Node node, List<(Node Node, int Slot)> path, bool appendedAtEnd)
    {
        for (var level = 0; ; level++)
        {
            GuardDepth(level);
            var page = node.IsLeaf ? EncodeLeaf(node) : EncodeInterior(node);
            if (page is not null)
            {
                _index.Write(node.Offset, page);
                if (path.Count == 0 || node.Count == 0)
                    return;
                var (parent, slot) = path[^1];
                path.RemoveAt(path.Count - 1);
                if (parent.Keys[slot].AsSpan().SequenceEqual(node.Keys[^1]) && parent.Records[slot] == node.Records[^1])
                    return;
                parent.Keys[slot] = node.Keys[^1];
                parent.Records[slot] = node.Records[^1];
                node = parent;
                appendedAtEnd = false;
                continue;
            }

            var isRoot = path.Count == 0;
            if (isRoot)
                node.Attributes &= unchecked((ushort)~RootAttribute);
            var right = Split(node, appendedAtEnd);
            WriteNode(node);
            WriteNode(right);
            if (right.Right != NoPage)
                PatchSibling(right.Right, leftLink: true, (uint)right.Offset);

            if (isRoot)
            {
                var root = new Node
                {
                    Offset = _index.AllocatePage(),
                    Attributes = RootAttribute,
                    Keys = [node.Keys[^1], right.Keys[^1]],
                    Records = [node.Records[^1], right.Records[^1]],
                    Children = [(uint)node.Offset, (uint)right.Offset],
                };
                WriteNode(root);
                SetRoot(root.Offset);
                return;
            }

            var (up, at) = path[^1];
            path.RemoveAt(path.Count - 1);
            up.Keys[at] = node.Keys[^1];
            up.Records[at] = node.Records[^1];
            up.Keys.Insert(at + 1, right.Keys[^1]);
            up.Records.Insert(at + 1, right.Records[^1]);
            up.Children!.Insert(at + 1, (uint)right.Offset);
            appendedAtEnd = appendedAtEnd && at + 1 == up.Count - 1 && up.Right == NoPage;
            node = up;
        }
    }

    // Splits a node that doesn't fit into itself (left) and a new right sibling. Usually in half;
    // when keys arrive in order at the right edge, the left keeps all it held before, so tables
    // filled in key order (autoincrement IDs) get full pages rather than half-empty ones.
    private Node Split(Node node, bool appendedAtEnd)
    {
        var right = new Node
        {
            Offset = _index.AllocatePage(),
            Attributes = node.Attributes,
            Left = (uint)node.Offset,
            Right = node.Right,
            Children = node.IsLeaf ? null : [],
        };

        var n = node.Count;
        var cuts = appendedAtEnd
            ? new[] { n - 1 }.Concat(Enumerable.Range(1, n - 1).OrderBy(c => Math.Abs(c - n / 2)))
            : Enumerable.Range(1, n - 1).OrderBy(c => Math.Abs(c - n / 2));
        foreach (var cut in cuts)
        {
            var left = Slice(node, 0, cut);
            var rest = Slice(node, cut, n - cut);
            if ((node.IsLeaf ? EncodeLeaf(left) : EncodeInterior(left)) is null
                || (node.IsLeaf ? EncodeLeaf(rest) : EncodeInterior(rest)) is null)
                continue;

            right.Keys = rest.Keys;
            right.Records = rest.Records;
            right.Children = rest.Children;
            node.Keys = left.Keys;
            node.Records = left.Records;
            node.Children = left.Children;
            node.Right = (uint)right.Offset;
            return right;
        }
        throw new InvalidDataException($"Tag {Name}'s page at offset {node.Offset} can't be split so both halves fit.");
    }

    private static Node Slice(Node node, int start, int count) => new()
    {
        Attributes = node.Attributes,
        Left = node.Left,
        Right = node.Right,
        Keys = node.Keys.GetRange(start, count),
        Records = node.Records.GetRange(start, count),
        Children = node.Children?.GetRange(start, count),
    };

    // Points a neighbour's left (or right) link at a new page.
    private void PatchSibling(uint offset, bool leftLink, uint target)
    {
        var page = _index.Read(offset, PageLength);
        if (BinaryPrimitives.ReadUInt16LittleEndian(page) > 7)
            throw new InvalidDataException($"Tag {Name}'s sibling link leads to offset {offset}, which isn't an index node.");
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(leftLink ? 4 : 8), target);
        _index.Write(offset, page);
    }

    // ---- remove -----------------------------------------------------------------------------

    private enum Search { Found, KeepLooking, Stop }

    /// <summary>Removes the entry (key, record number). Returns false when there isn't one.</summary>
    internal bool Remove(byte[] key, int recordNumber)
    {
        CheckKey(key);
        var path = new List<(Node Node, int Slot)>();
        var root = ReadNode(RootOffset(), inByteOrder: true);
        if (Find(root, key, recordNumber, path, depth: 0, visited: [root.Offset]) != Search.Found)
            return false;

        var (leaf, slot) = path[^1];
        path.RemoveAt(path.Count - 1);
        leaf.Keys.RemoveAt(slot);
        leaf.Records.RemoveAt(slot);
        if (leaf.Count > 0 || path.Count == 0)
            Store(leaf, path, appendedAtEnd: false);
        else
            DropEmpty(leaf, path);
        CollapseRoot();
        return true;
    }

    // Searches equal keys in order (they may run across several leaves) for this record. The path
    // ends with the leaf and the entry's slot in it.
    private Search Find(Node node, byte[] key, int recordNumber, List<(Node, int)> path, int depth, HashSet<long> visited)
    {
        GuardDepth(depth);
        var start = 0;
        while (start < node.Count && node.Keys[start].AsSpan().SequenceCompareTo(key) < 0)
            start++;

        if (node.IsLeaf)
        {
            for (var i = start; i < node.Count; i++)
            {
                if (!node.Keys[i].AsSpan().SequenceEqual(key))
                    return Search.Stop;
                if (node.Records[i] == recordNumber)
                {
                    path.Add((node, i));
                    return Search.Found;
                }
            }
            return Search.KeepLooking;
        }

        for (var i = start; i < node.Count; i++)
        {
            path.Add((node, i));
            var result = Find(ReadChild(node.Children![i], visited), key, recordNumber, path, depth + 1, visited);
            if (result == Search.Found)
                return result;
            path.RemoveAt(path.Count - 1);
            if (result == Search.Stop)
                return result;
        }
        return Search.KeepLooking;
    }

    // An emptied page leaves the tree: its neighbours link past it, it goes on the free list, and its
    // parent loses the entry for it (and leaves too, if that empties it). An emptied root becomes an
    // empty leaf, which is how FoxPro stores a tag with no keys.
    private void DropEmpty(Node node, List<(Node Node, int Slot)> path)
    {
        var leafAttributes = (ushort)(node.Attributes & ~RootAttribute);
        for (var level = 0; ; level++)
        {
            GuardDepth(level);
            if (node.Left != NoPage)
                PatchSibling(node.Left, leftLink: false, node.Right);
            if (node.Right != NoPage)
                PatchSibling(node.Right, leftLink: true, node.Left);
            _index.FreePage(node.Offset);

            var (parent, slot) = path[^1];
            path.RemoveAt(path.Count - 1);
            parent.Keys.RemoveAt(slot);
            parent.Records.RemoveAt(slot);
            parent.Children!.RemoveAt(slot);

            if (parent.Count > 0)
            {
                Store(parent, path, appendedAtEnd: false);
                return;
            }
            if (path.Count == 0)
            {
                WriteNode(new Node { Offset = parent.Offset, Attributes = (ushort)(leafAttributes | RootAttribute) });
                return;
            }
            node = parent;
        }
    }

    // A root with a single child hands the root role to that child, so the tree stays as shallow as
    // its keys need.
    private void CollapseRoot()
    {
        for (var depth = 0; ; depth++)
        {
            GuardDepth(depth);
            var root = ReadNode(RootOffset());
            if (root.IsLeaf || root.Count > 1)
                return;
            var child = ReadNode(root.Children![0]);
            child.Attributes |= RootAttribute;
            child.Left = child.Right = NoPage;
            WriteNode(child);
            SetRoot(child.Offset);
            _index.FreePage(root.Offset);
        }
    }

    // ---- rebuild ----------------------------------------------------------------------------

    /// <summary>Empties the tag: every page but the root goes on the free list, and the root becomes an empty leaf.</summary>
    internal void Clear()
    {
        var root = ReadNode(RootOffset());
        var pages = new List<long>();
        CollectPages(root, [], pages, leavesOnly: false, depth: 0);
        var leafAttributes = (ushort)(LeafAttribute | (ReadNode(LeafOffsets()[0]).Attributes & 0x04));
        foreach (var page in pages.Where(p => p != root.Offset))
            _index.FreePage(page);
        WriteNode(new Node { Offset = root.Offset, Attributes = (ushort)(leafAttributes | RootAttribute) });
    }
}
