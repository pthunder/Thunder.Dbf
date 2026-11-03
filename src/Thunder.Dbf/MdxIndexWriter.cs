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
using System.Text;

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
        WriteRaw((long)page * PageSize, block);
    }

    // ---- deferred batch -------------------------------------------------------------------

    // A batch (DbfTable.DeferIndexWrites under LockTable): the table lock is held throughout, and dBASE
    // and Thunder.Dbf both lock the table to write, so nobody else changes the .mdx meanwhile. Every
    // page read or written is kept here, 512 bytes each (a block may span several, and tag headers sit
    // between blocks), and each changed page is written once, when the batch ends. A process killed
    // during a batch leaves the .mdx as it was before it, and the batch's note in the table's journal
    // says so.
    private sealed class Batch(long length)
    {
        public readonly Dictionary<int, byte[]> Pages = [];
        public readonly HashSet<int> Changed = [];
        public readonly HashSet<int> Freed = []; // first pages of blocks the batch put on the free list
        public readonly long StartLength = length;
        public long Length = length;
    }

    private Batch? _batch;
    internal static int MaxUnchangedPages = 16_384; // 8 MB of pages kept only because they were read (tests lower it)
    private const int MaxRunPages = 256; // a contiguous run is written 128 KB at a time, not copied whole

    bool IMaintainedIndex.BeginDeferredWrites()
    {
        if (_batch is not null || _stream is not FileStream { CanWrite: true })
            return false;
        // Under the table lock now, so nothing changes from here: the tags are reloaded first, as the
        // batch's first write would without it (another writer may have changed them since this process
        // last looked), and pages are read from the file from here on, not from what was read before.
        DbfTable.DiscardReadBuffer(_stream);
        ReloadTags();
        _batch = new Batch(_stream.Length);
        return true;
    }

    bool IMaintainedIndex.FinishDeferredWrites(bool toDisk)
    {
        if (_batch is not { } batch)
            return true;
        _batch = null; // a write that throws below ends the batch too: the table keeps its note on the exception

        // New blocks first (nothing on disk points at them yet), then, once they're out, the blocks that
        // existed (some now pointing at new ones), blocks put on the free list once nothing points at
        // them, and the file header (page count, free list, date) last. Contiguous pages go in one write.
        var firstNew = (int)((batch.StartLength + PageSize - 1) / PageSize);
        var headerPages = HeaderAreaSize / PageSize;
        var freed = batch.Freed.SelectMany(p => Enumerable.Range(p, PagesPerBlock)).ToHashSet();
        WriteRuns(batch, p => p >= firstNew && !freed.Contains(p));
        if (batch.Changed.Any(p => p >= firstNew))
            DbfTable.FlushToDisk(_stream, toDisk);
        WriteRuns(batch, p => p >= headerPages && p < firstNew && !freed.Contains(p));
        WriteRuns(batch, p => p >= headerPages && freed.Contains(p));
        WriteRuns(batch, p => p < headerPages);
        DbfTable.FlushToDisk(_stream, toDisk);
        return true;
    }

    private void WriteRuns(Batch batch, Func<int, bool> which)
    {
        var pages = batch.Changed.Where(which).Order().ToList();
        for (var start = 0; start < pages.Count;)
        {
            var end = start + 1;
            while (end < pages.Count && pages[end] == pages[end - 1] + 1 && end - start < MaxRunPages)
                end++;
            var run = new byte[(end - start) * PageSize];
            for (var i = start; i < end; i++)
                batch.Pages[pages[i]].CopyTo(run, (i - start) * PageSize);
            _stream.Position = (long)pages[start] * PageSize;
            _stream.Write(run);
            start = end;
        }
    }

    /// <summary>The file's length, counting what a batch has added but not yet written.</summary>
    private long Length => _batch?.Length ?? _stream.Length;

    /// <summary>Reads from the file, or during a batch from its pages. Past the end: EndOfStreamException.</summary>
    private void ReadRaw(long offset, Span<byte> bytes)
    {
        if (_batch is not { } batch)
        {
            DbfTable.DiscardReadBuffer(_stream); // other programs change indexes too
            _stream.Position = offset;
            _stream.ReadExactly(bytes);
            return;
        }
        if (offset < 0 || offset + bytes.Length > batch.Length)
            throw new EndOfStreamException();
        var first = (int)(offset / PageSize);
        var last = (int)((offset + bytes.Length - 1) / PageSize);
        for (var p = first; p <= last; p++)
        {
            if (!batch.Pages.TryGetValue(p, out var page))
            {
                LoadPages(batch, p, last); // the rest of the range in the same read
                page = batch.Pages[p];
            }
            var from = Math.Max(offset, (long)p * PageSize);
            var to = Math.Min(offset + bytes.Length, (long)(p + 1) * PageSize);
            page.AsSpan((int)(from - (long)p * PageSize), (int)(to - from)).CopyTo(bytes[(int)(from - offset)..]);
        }
    }

    /// <summary>Writes to the file, or during a batch to its pages, to be written when it ends.</summary>
    private void WriteRaw(long offset, ReadOnlySpan<byte> bytes)
    {
        if (_batch is not { } batch)
        {
            _stream.Position = offset;
            _stream.Write(bytes);
            return;
        }
        var first = (int)(offset / PageSize);
        var last = (int)((offset + bytes.Length - 1) / PageSize);
        for (var p = first; p <= last; p++)
        {
            var from = Math.Max(offset, (long)p * PageSize);
            var to = Math.Min(offset + bytes.Length, (long)(p + 1) * PageSize);
            if (!batch.Pages.TryGetValue(p, out var page))
            {
                // A page written only in part keeps the rest of what's on disk.
                if (to - from < PageSize && (long)p * PageSize < batch.StartLength)
                    LoadPages(batch, p, p);
                else
                    batch.Pages[p] = new byte[PageSize];
                page = batch.Pages[p];
            }
            bytes.Slice((int)(from - offset), (int)(to - from)).CopyTo(page.AsSpan((int)(from - (long)p * PageSize)));
            batch.Changed.Add(p);
        }
        batch.Length = Math.Max(batch.Length, offset + bytes.Length);
    }

    // Reads pages first..last from the file in one go, keeping those not already held. Pages past the
    // file's length when the batch began (gaps before a new block) are zeros.
    private void LoadPages(Batch batch, int first, int last)
    {
        if (batch.Pages.Count - batch.Changed.Count >= MaxUnchangedPages)
        {
            foreach (var p in batch.Pages.Keys.Where(p => !batch.Changed.Contains(p)).ToList())
                batch.Pages.Remove(p);
        }
        var span = new byte[(last - first + 1) * PageSize];
        var onDisk = (int)Math.Clamp(batch.StartLength - (long)first * PageSize, 0, span.Length);
        if (onDisk > 0)
        {
            DbfTable.DiscardReadBuffer(_stream); // what's on disk, never read-ahead from before the batch
            _stream.Position = (long)first * PageSize;
            _stream.ReadExactly(span.AsSpan(0, onDisk));
        }
        for (var p = first; p <= last; p++)
        {
            if (!batch.Pages.ContainsKey(p))
                batch.Pages[p] = span.AsSpan((p - first) * PageSize, PageSize).ToArray();
        }
    }

    /// <summary>
    /// Tag creation writes the tag table directly, past the batch's pages. The table ends a batch before
    /// creating a tag, so this only stops a caller that doesn't.
    /// </summary>
    internal void ThrowIfDeferring()
    {
        if (_batch is not null)
            throw new InvalidOperationException("An .mdx tag can't be created while a deferred batch is open.");
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
        var page = (int)((Length + PageSize - 1) / PageSize);
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
        _batch?.Freed.Add(page);
        WriteHeaderInt(FreePageOffset, page);
    }

    /// <summary>Stamps today's date as the last update and pushes everything to disk.</summary>
    internal void Commit(bool toDisk = true)
    {
        var today = DateTime.Today;
        WriteRaw(UpdatedOffset, [(byte)(today.Year - 1900), (byte)today.Month, (byte)today.Day]);
        if (_batch is null)
            DbfTable.FlushToDisk(_stream, toDisk); // a batch writes and flushes when it ends
    }

    private int ReadHeaderInt(int offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadRaw(offset, bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private void WriteHeaderInt(int offset, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        WriteRaw(offset, bytes);
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
        return EncodeKey(KeyType, KeyLength, _index.Encoding, _keyExpression!.Evaluate(record), Name, Expression);
    }

    /// <summary>
    /// The stored bytes for one key value. Shared with INDEX ON, which encodes every key before a new
    /// tag exists, so a value that can't be stored stops the command before the file changes.
    /// </summary>
    internal static byte[] EncodeKey(char keyType, int keyLength, Encoding encoding, object? value, string tag, string expression)
    {
        switch (keyType)
        {
            case 'C':
            {
                var text = value as string ?? throw new InvalidDataException($"Tag {tag}'s key expression '{expression}' didn't produce text.");
                var key = new byte[keyLength];
                key.AsSpan().Fill((byte)' ');
                var encoded = encoding.GetBytes(text);
                encoded.AsSpan(0, Math.Min(encoded.Length, keyLength)).CopyTo(key);
                return key;
            }
            case 'N':
                return value switch
                {
                    decimal d => MdxKeys.EncodeNumeric(d),
                    double d => MdxKeys.EncodeNumeric(d),
                    int or long => MdxKeys.EncodeNumeric(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)),
                    null => MdxKeys.EncodeNumeric(0m),
                    _ => throw new InvalidDataException($"Tag {tag}'s key expression '{expression}' didn't produce a number."),
                };
            default:
            {
                if (value is not (DateOnly or DateTime or null))
                    throw new InvalidDataException($"Tag {tag}'s key expression '{expression}' didn't produce a date.");
                var key = new byte[8];
                DateOnly? date = value switch { DateOnly d => d, DateTime t => DateOnly.FromDateTime(t), _ => null };
                BinaryPrimitives.WriteDoubleLittleEndian(key, date is { } day ? MdxKeys.JulianDay(day) : 0d);
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
        // Usually the first key at or after this one is in the leaf the search lands on. When the
        // leaf runs out first, the answer is in a later leaf, and the full walk finds it, unless the
        // leaf is the last one (keys added in order always land there): then there's none.
        var (_, block, slot, last) = FindLeaf(key, upper: false);
        if (slot < NodeCount(block))
            return CompareKeys(NodeKey(block, slot), key) == 0 ? BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8 + slot * ItemLength)) : null;
        if (last)
            return null;
        foreach (var hit in Walk(new RawProbe(key, this)))
            return hit.Matches ? hit.Entry.RecordNumber : null;
        return null;
    }

    // ---- insert ---------------------------------------------------------------------------

    private readonly record struct Item(int Pointer, byte[] Key);

    /// <summary>Adds (key, record number), after any equal keys, splitting nodes as needed.</summary>
    internal void Insert(byte[] key, int recordNumber)
    {
        // Most inserts land in a leaf with room: that leaf alone changes, in place, in a reused
        // buffer. A full leaf takes the general path below, which splits it.
        var (page, block, slot, _) = FindLeaf(key, upper: true);
        if (NodeCount(block) < MaxKeys)
        {
            InsertInPlace(page, block, slot, key, recordNumber);
            MarkHasKeys();
            return;
        }

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

    /// <summary>True when the tag holds exactly this key for this record.</summary>
    internal bool Contains(byte[] key, int recordNumber) =>
        Find(RootPage(), key, recordNumber, [], depth: 0, visited: []) == Search.Found;

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

    // ---- raw nodes, for the common insert and lookup -------------------------------------

    private byte[][]? _levels; // one reused block buffer per tree level

    /// <summary>
    /// Follows the key from the root to its leaf, the way InsertInto (upper: after equal keys) or
    /// the walk (upper: false, at the first equal key) would, without decoding any node. Returns the
    /// leaf's page, its block (a reused buffer), the key's slot in it, and whether it's the last leaf
    /// (every node on the way followed its last pointer). The block is only good until the next call on
    /// this tag, which reuses the buffer; like the rest of the writer, one thread per tag.
    /// </summary>
    private (int Page, byte[] Block, int Slot, bool Last) FindLeaf(byte[] key, bool upper)
    {
        _levels ??= new byte[MaxDepth + 1][];
        var page = RootPage();
        var last = true;
        for (var depth = 0; ; depth++)
        {
            GuardDepth(depth);
            var block = _levels[depth] ??= new byte[_index.BlockSize];
            _index.ReadBlock(page, block);
            var count = NodeCount(block);
            if (count < 0 || count > MaxKeys)
                throw new InvalidDataException($"Index page {page} claims {count} keys; tag {Name} holds at most {MaxKeys} per node.");

            int lo = 0, hi = count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                var order = CompareKeys(NodeKey(block, mid), key);
                if (order < 0 || (upper && order == 0))
                    lo = mid + 1;
                else
                    hi = mid;
            }
            var lastPointer = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8 + count * ItemLength));
            if (lastPointer == 0)
                return (page, block, lo, last);
            last &= lo == count;
            page = lo < count ? BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8 + lo * ItemLength)) : lastPointer;
        }
    }

    private static int NodeCount(byte[] block) => BinaryPrimitives.ReadInt32LittleEndian(block);

    private ReadOnlySpan<byte> NodeKey(byte[] block, int slot) => block.AsSpan(8 + slot * ItemLength + 4, KeyLength);

    // Leaves the block exactly as WriteNode would write the leaf with the item added: bytes 4-7, the
    // padding after each key and everything after the items zeroed.
    private void InsertInPlace(int page, byte[] block, int slot, byte[] key, int recordNumber)
    {
        var count = NodeCount(block);
        var at = 8 + slot * ItemLength;
        block.AsSpan(at, (count - slot) * ItemLength).CopyTo(block.AsSpan(at + ItemLength));
        var item = block.AsSpan(at, ItemLength);
        item.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(item, recordNumber);
        key.CopyTo(item[4..]);

        count++;
        BinaryPrimitives.WriteInt32LittleEndian(block, count);
        block.AsSpan(4, 4).Clear();
        if (ItemLength > KeyLength + 4)
        {
            for (var i = 0; i < count; i++)
                block.AsSpan(8 + i * ItemLength + 4 + KeyLength, ItemLength - 4 - KeyLength).Clear();
        }
        block.AsSpan(8 + count * ItemLength).Clear(); // a leaf's last pointer is 0
        _index.WriteBlock(page, block);
    }

    private int RootPage()
    {
        Span<byte> root = stackalloc byte[4];
        _index.ReadAt(_headerPage, 0, root);
        return BinaryPrimitives.ReadInt32LittleEndian(root);
    }

    private void SetRoot(int page)
    {
        var header = _index.ReadBlock(_headerPage);
        BinaryPrimitives.WriteInt32LittleEndian(header, page);
        _index.WriteBlock(_headerPage, header);
    }

    private void MarkHasKeys()
    {
        Span<byte> flag = stackalloc byte[1];
        _index.ReadAt(_headerPage, 246, flag);
        if (flag[0] == 1)
            return;
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
