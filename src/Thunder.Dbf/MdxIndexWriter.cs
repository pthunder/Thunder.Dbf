//          insert 42                       ProjectThunder :: Thunder.Dbf
//              |                           Keeping a dBASE IV .mdx right while the table
//      [ 7 | 40 | 88 ]  full!              changes underneath it.
//              |
//      [ 7 | 40 ] [ 42 | 88 ]   split      A full node splits in half; the parent gains a
//         \         /                      separator (the left half's last key). A full
//          [ 40 ]          new root        root splits too, and a new root grows above it.
//
// Removing a key never merges nodes: an emptied node goes back on the free list and its
// pointer leaves the parent. A separator that's larger than its child's real last key still
// routes searches correctly, so nothing else needs touching.

using System.Buffers.Binary;

namespace Thunder.Dbf;

public sealed partial class MdxIndex
{
    private const int PageCountOffset = 32;
    private const int FreePageOffset = 36;
    private const int UpdatedOffset = 44;

    internal bool CanWrite => _stream.CanWrite;

    private int PagesPerBlock => BlockSize / PageSize;

    internal void WriteBlock(int page, byte[] block)
    {
        if (page <= 0 || block.Length != BlockSize)
            throw new InvalidOperationException($"Can't write index page {page}.");
        _stream.Position = (long)page * PageSize;
        _stream.Write(block);
    }

    /// <summary>The largest index file dBASE handles: 2 GB.</summary>
    private const long MaxIndexBytes = int.MaxValue;

    /// <summary>
    /// A zeroed block for a new node, always at the physical end of the file. The free list isn't
    /// reused: its pointers come from the file, and following a damaged one would overwrite a live
    /// node (dBASE 7 and Microsoft's ODBC driver don't reuse it either). Freed nodes are still put
    /// on the list for programs that do.
    /// </summary>
    internal int AllocatePage()
    {
        // The stored page count is only advice; the file's real length decides.
        var page = (int)((_stream.Length + PageSize - 1) / PageSize);
        page = (page + PagesPerBlock - 1) / PagesPerBlock * PagesPerBlock;
        if ((long)(page + PagesPerBlock) * PageSize > MaxIndexBytes)
            throw new InvalidDataException("The .mdx index has reached the 2 GB dBASE can handle.");
        WriteBlock(page, new byte[BlockSize]);
        WriteHeaderInt(PageCountOffset, page + PagesPerBlock);
        return page;
    }

    /// <summary>Puts a node back on the free list (its bytes 4-7 link to the next free node).</summary>
    internal void FreePage(int page)
    {
        var block = new byte[BlockSize];
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), ReadHeaderInt(FreePageOffset));
        WriteBlock(page, block);
        WriteHeaderInt(FreePageOffset, page);
    }

    /// <summary>Stamps today's date as the last update and pushes everything to disk.</summary>
    internal void Commit()
    {
        var today = DateTime.Today;
        _stream.Position = UpdatedOffset;
        _stream.Write([(byte)(today.Year - 1900), (byte)today.Month, (byte)today.Day]);
        DbfTable.FlushToDisk(_stream);
    }

    private int ReadHeaderInt(int offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        _stream.Position = offset;
        _stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private void WriteHeaderInt(int offset, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _stream.Position = offset;
        _stream.Write(bytes);
    }
}

public sealed partial class MdxTag
{
    private DbfExpression? _keyExpression;
    private DbfExpression? _forExpression;
    private bool _expressionsParsed;

    // ---- keys for records -----------------------------------------------------------------

    /// <summary>True when the tag's FOR filter (if any) lets <paramref name="record"/> into the index.</summary>
    internal bool Includes(DbfRecord record)
    {
        ParseExpressions();
        return _forExpression is null || _forExpression.Evaluate(record) is true;
    }

    /// <summary>The stored key bytes for <paramref name="record"/>, built from the tag's key expression.</summary>
    internal byte[] KeyFor(DbfRecord record)
    {
        ParseExpressions();
        var value = _keyExpression!.Evaluate(record);
        switch (KeyType)
        {
            case 'C':
            {
                var text = value as string ?? throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce text.");
                var key = new byte[KeyLength];
                key.AsSpan().Fill((byte)' ');
                var encoded = _index.Encoding.GetBytes(text);
                encoded.AsSpan(0, Math.Min(encoded.Length, KeyLength)).CopyTo(key);
                return key;
            }
            case 'N':
                return value switch
                {
                    decimal d => MdxKeys.EncodeNumeric(d),
                    double d => MdxKeys.EncodeNumeric(d),
                    null => MdxKeys.EncodeNumeric(0m),
                    _ => throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce a number."),
                };
            default:
            {
                var key = new byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(key, value is DateOnly date ? MdxKeys.JulianDay(date) : 0d);
                return key;
            }
        }
    }

    private void ParseExpressions()
    {
        if (_expressionsParsed)
            return;
        _keyExpression = DbfExpression.Parse(Expression);
        _forExpression = string.IsNullOrWhiteSpace(ForExpression) ? null : DbfExpression.Parse(ForExpression);
        _expressionsParsed = true;
    }

    /// <summary>Orders two stored keys the way this tag sorts them (descending tags reversed).</summary>
    internal int CompareKeys(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => Order(CompareAscending(a, b));

    private int CompareAscending(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => KeyType switch
    {
        'C' => a.SequenceCompareTo(b),
        'N' => MdxKeys.CompareNumeric(a, b),
        _ => BinaryPrimitives.ReadDoubleLittleEndian(a).CompareTo(BinaryPrimitives.ReadDoubleLittleEndian(b)),
    };

    /// <summary>True when some entry has exactly this key (the first entry at or after it matches).</summary>
    internal bool ContainsKey(byte[] key) => FirstRecordFor(key) is not null;

    /// <summary>The record number of the first entry with exactly this key, or null.</summary>
    internal int? FirstRecordFor(byte[] key)
    {
        foreach (var hit in Walk(new RawProbe(key, this)))
            return hit.Matches ? hit.Entry.RecordNumber : null;
        return null;
    }

    // ---- insert ---------------------------------------------------------------------------

    private readonly record struct Item(int Pointer, byte[] Key);

    /// <summary>Adds (key, record number), after any equal keys, splitting nodes as needed.</summary>
    internal void Insert(byte[] key, int recordNumber)
    {
        var root = RootPage();
        if (InsertInto(root, key, recordNumber, depth: 0) is { } split)
        {
            var newRoot = _index.AllocatePage();
            WriteNode(newRoot, [new Item(root, split.LeftLast)], lastPointer: split.Right);
            SetRoot(newRoot);
        }
        MarkHasKeys();
    }

    private (byte[] LeftLast, int Right)? InsertInto(int page, byte[] key, int recordNumber, int depth)
    {
        GuardDepth(depth);
        var (items, lastPointer, isLeaf) = ReadNode(page);

        if (isLeaf)
        {
            items.Insert(UpperBound(items, key), new Item(recordNumber, key));
        }
        else
        {
            var slot = UpperBound(items, key);
            var child = slot < items.Count ? items[slot].Pointer : lastPointer;
            if (InsertInto(child, key, recordNumber, depth + 1) is not { } split)
                return null;

            // The child split into itself (left) and a new right node: the left keeps this slot with
            // its new last key as separator, and the right takes the slot after it.
            if (slot < items.Count)
            {
                var oldKey = items[slot].Key;
                items[slot] = new Item(child, split.LeftLast);
                items.Insert(slot + 1, new Item(split.Right, oldKey));
            }
            else
            {
                items.Add(new Item(child, split.LeftLast));
                lastPointer = split.Right;
            }
        }

        if (items.Count <= MaxKeys)
        {
            WriteNode(page, items, isLeaf ? 0 : lastPointer);
            return null;
        }
        return isLeaf ? SplitLeaf(page, items) : SplitInterior(page, items, lastPointer);
    }

    private (byte[], int) SplitLeaf(int page, List<Item> items)
    {
        var mid = items.Count / 2;
        var right = _index.AllocatePage();
        WriteNode(page, items.GetRange(0, mid), 0);
        WriteNode(right, items.GetRange(mid, items.Count - mid), 0);
        return (items[mid - 1].Key, right);
    }

    // Interior key i is the last key under child i. The left half keeps children 0..mid, so its last
    // child's key (items[mid].Key) becomes the separator and leaves both halves.
    private (byte[], int) SplitInterior(int page, List<Item> items, int lastPointer)
    {
        var mid = items.Count / 2;
        var right = _index.AllocatePage();
        WriteNode(page, items.GetRange(0, mid), items[mid].Pointer);
        WriteNode(right, items.GetRange(mid + 1, items.Count - mid - 1), lastPointer);
        return (items[mid].Key, right);
    }

    private int UpperBound(List<Item> items, byte[] key)
    {
        int lo = 0, hi = items.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (CompareKeys(items[mid].Key, key) <= 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // ---- remove ---------------------------------------------------------------------------

    private enum Search { Found, KeepLooking, Stop }

    /// <summary>Removes the entry (key, record number). Returns false when there isn't one.</summary>
    internal bool Remove(byte[] key, int recordNumber)
    {
        var path = new List<(int Page, int Slot)>();
        if (Find(RootPage(), key, recordNumber, path, depth: 0, visited: []) != Search.Found)
            return false;

        // Remove the item from its leaf; if that empties a node (other than the root), drop the node
        // and its pointer from the parent, and carry on upward while parents empty out too.
        for (var level = path.Count - 1; level >= 0; level--)
        {
            var (page, slot) = path[level];
            var (items, lastPointer, isLeaf) = ReadNode(page);
            if (isLeaf)
                items.RemoveAt(slot);
            else if (slot < items.Count)
                items.RemoveAt(slot);
            else if (items.Count > 0)
            {
                // The last child went; the one before it becomes last, and keys for the last child aren't stored.
                lastPointer = items[^1].Pointer;
                items.RemoveAt(items.Count - 1);
            }
            else
                lastPointer = 0;

            var empty = isLeaf ? items.Count == 0 : lastPointer == 0;
            if (!empty || level == 0)
            {
                WriteNode(page, items, isLeaf ? 0 : lastPointer);
                if (level == 0)
                    CollapseRoot();
                return true;
            }
            _index.FreePage(page);
        }
        return true;
    }

    // Each page is searched at most once: a damaged index whose nodes share children can't make this
    // repeat the same subtree over and over.
    private Search Find(int page, byte[] key, int recordNumber, List<(int, int)> path, int depth, HashSet<int> visited)
    {
        GuardDepth(depth);
        if (!visited.Add(page))
            throw new InvalidDataException($"Tag {Name} reaches index page {page} twice; the index is corrupt.");
        var (items, lastPointer, isLeaf) = ReadNode(page);
        var start = LowerBound(items, key);

        if (isLeaf)
        {
            for (var i = start; i < items.Count; i++)
            {
                if (CompareKeys(items[i].Key, key) != 0)
                    return Search.Stop;
                if (items[i].Pointer == recordNumber)
                {
                    path.Add((page, i));
                    return Search.Found;
                }
            }
            return Search.KeepLooking; // equal keys may continue in the next leaf
        }

        for (var i = start; i <= items.Count; i++)
        {
            path.Add((page, i));
            var result = Find(i < items.Count ? items[i].Pointer : lastPointer, key, recordNumber, path, depth + 1, visited);
            if (result == Search.Found)
                return result;
            path.RemoveAt(path.Count - 1);
            if (result == Search.Stop)
                return result;
        }
        return Search.KeepLooking;
    }

    private int LowerBound(List<Item> items, byte[] key)
    {
        int lo = 0, hi = items.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (CompareKeys(items[mid].Key, key) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // An interior root left with a single child hands the root role to that child.
    private void CollapseRoot()
    {
        while (true)
        {
            var root = RootPage();
            var (items, lastPointer, isLeaf) = ReadNode(root);
            if (isLeaf || items.Count > 0)
                return;
            SetRoot(lastPointer);
            _index.FreePage(root);
        }
    }

    // ---- rebuild --------------------------------------------------------------------------

    /// <summary>Empties the tag: frees every node and starts again with one empty leaf as root.</summary>
    internal void Clear()
    {
        var pages = new List<int>();
        Collect(RootPage(), pages, [], depth: 0);
        foreach (var page in pages)
            _index.FreePage(page);

        var root = _index.AllocatePage();
        WriteNode(root, [], 0);
        SetRoot(root);
    }

    private void Collect(int page, List<int> pages, HashSet<int> visited, int depth)
    {
        GuardDepth(depth);
        if (!visited.Add(page))
            throw new InvalidDataException($"Tag {Name} reaches index page {page} twice; the index is corrupt.");
        pages.Add(page);
        var (items, lastPointer, isLeaf) = ReadNode(page);
        if (isLeaf)
            return;
        foreach (var item in items)
            Collect(item.Pointer, pages, visited, depth + 1);
        Collect(lastPointer, pages, visited, depth + 1);
    }

    // ---- nodes ----------------------------------------------------------------------------

    private (List<Item> Items, int LastPointer, bool IsLeaf) ReadNode(int page)
    {
        var node = _index.ReadBlock(page);
        var count = BinaryPrimitives.ReadInt32LittleEndian(node);
        if (count < 0 || count > MaxKeys)
            throw new InvalidDataException($"Index page {page} claims {count} keys; tag {Name} holds at most {MaxKeys} per node.");

        var items = new List<Item>(count + 1);
        for (var i = 0; i < count; i++)
        {
            var at = 8 + i * ItemLength;
            items.Add(new Item(BinaryPrimitives.ReadInt32LittleEndian(node.AsSpan(at)), node.AsSpan(at + 4, KeyLength).ToArray()));
        }
        var lastPointer = BinaryPrimitives.ReadInt32LittleEndian(node.AsSpan(8 + count * ItemLength));
        return (items, lastPointer, lastPointer == 0);
    }

    private void WriteNode(int page, List<Item> items, int lastPointer)
    {
        var node = new byte[_index.BlockSize];
        BinaryPrimitives.WriteInt32LittleEndian(node, items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var at = 8 + i * ItemLength;
            BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(at), items[i].Pointer);
            items[i].Key.CopyTo(node.AsSpan(at + 4));
        }
        BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(8 + items.Count * ItemLength), lastPointer);
        _index.WriteBlock(page, node);
    }

    private int RootPage() => BinaryPrimitives.ReadInt32LittleEndian(_index.ReadBlock(_headerPage));

    private void SetRoot(int page)
    {
        var header = _index.ReadBlock(_headerPage);
        BinaryPrimitives.WriteInt32LittleEndian(header, page);
        _index.WriteBlock(_headerPage, header);
    }

    private void MarkHasKeys()
    {
        var header = _index.ReadBlock(_headerPage);
        if (header[246] == 1)
            return;
        header[246] = 1;
        _index.WriteBlock(_headerPage, header);
    }

    private void GuardDepth(int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"Tag {Name} is deeper than {MaxDepth} levels; the index is probably corrupt.");
    }

    /// <summary>A probe that compares whole stored keys, for exact-key checks during maintenance.</summary>
    private sealed record RawProbe(byte[] Key, MdxTag Tag) : KeyProbe;

    private static int CompareRaw(ReadOnlySpan<byte> key, RawProbe probe) => probe.Tag.CompareAscending(key, probe.Key);
}
