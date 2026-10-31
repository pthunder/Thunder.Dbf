//    page 0 (header)                               ProjectThunder :: Thunder.Dbf
//    +--------+--------+-----+--------+-----+---+  Keeping a dBASE III .ndx right while
//    | root   | pages  | ... | keylen | ... | U |  the table changes underneath it.
//    +--------+--------+-----+--------+-----+---+
//      0        4              12             23   A write touches only the root, the
//                                                   page count and the pages on one path.
//    leaf  [ n | -- | 0,rec,key | 0,rec,key | ... ]
//    inner [ n | -- | pg,0,key  | pg,0,key  | pg ]  <- the last child has no key
//
//          insert 42
//      [ 7 | 40 | 88 ]  full!       A full page splits in half; the parent gains the left
//      [ 7 | 40 ] [ 42 | 88 ]       half's last key. A full root splits too, and a new
//          [ 40 | -> ]              root grows above it.
//
// Removing a key never merges pages. dBASE III doesn't either: a page that empties leaves the
// tree and stays in the file, unused. A separator larger than its child's real last key still
// routes searches correctly, so nothing else needs touching.
//
// dBASE III PLUS takes no lock on the .ndx itself: its multi-user commands update indexes under the
// table's own record and file locks, so this writer relies on the table's locks too.

using System.Buffers.Binary;

namespace Thunder.Dbf;

public sealed partial class NdxIndex : IMaintainedIndex
{
    private const int PageCountOffset = 4;

    /// <summary>The largest index file the 32-bit offsets dBASE uses can address safely: 2 GB.</summary>
    private const long MaxIndexBytes = int.MaxValue;

    IEnumerable<IMaintainedTag> IMaintainedIndex.MaintainedTags => CannotMaintainReason is null ? [Tag] : [];
    bool IMaintainedIndex.CanMaintain => CannotMaintainReason is null;
    string? IMaintainedIndex.CannotMaintainReason => CannotMaintainReason;
    void IMaintainedIndex.ReloadTags() => ReloadTags();
    void IMaintainedIndex.Commit() => Commit();

    /// <summary>
    /// Why this file can't be kept as dBASE III would keep it (keys out of the byte or number order
    /// it compares by: a localized sort order), or null when it can. Checked once per header layout
    /// with one walk that stops at the first pair out of order.
    /// </summary>
    internal string? CannotMaintainReason => Tag.MaintenanceProblem();

    /// <summary>
    /// Re-reads the header, in case the legacy app rebuilt or rewrote the file. The tag object stays
    /// the same unless its layout changed, so its key-order check isn't repeated on every write.
    /// </summary>
    internal void ReloadTags()
    {
        var fresh = NdxTag.Load(this, Tag.Name, _table);
        if (!fresh.SameLayoutAs(Tag))
            Tag = fresh;
    }

    /// <summary>Records the file's real length as its page count and pushes everything to disk.</summary>
    internal void Commit()
    {
        // dBASE III allocates its next page from this count, so it must never lag behind the file.
        WriteHeaderUInt(PageCountOffset, (uint)(_stream.Length / PageSize));
        DbfTable.FlushToDisk(_stream);
    }

    internal void WritePage(uint page, byte[] block)
    {
        if (page == 0 || block.Length != PageSize)
            throw new InvalidOperationException($"Can't write index page {page}.");
        _stream.Position = (long)page * PageSize;
        _stream.Write(block);
    }

    /// <summary>
    /// A zeroed page for a new node, always at the physical end of the file, as dBASE III does:
    /// it never reuses pages that deletes emptied.
    /// </summary>
    internal uint AllocatePage()
    {
        // The stored page count is only advice; the file's real length decides, so a count that a
        // damaged header set too low can't make a new page overwrite a live one.
        var page = (_stream.Length + PageSize - 1) / PageSize;
        if ((page + 1) * PageSize > MaxIndexBytes)
            throw new InvalidDataException("The .ndx index has reached the 2 GB dBASE can handle.");
        WritePage((uint)page, new byte[PageSize]);
        WriteHeaderUInt(PageCountOffset, (uint)page + 1);
        return (uint)page;
    }

    /// <summary>Drops every page but the header, as REINDEX does before it fills the file again.</summary>
    internal void Truncate() => _stream.SetLength(PageSize);

    internal uint ReadHeaderUInt(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(ReadPage(0).AsSpan(offset));

    internal void WriteHeaderUInt(int offset, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _stream.Position = offset;
        _stream.Write(bytes);
    }
}

public sealed partial class NdxTag : IMaintainedTag
{
    private DbfExpression? _keyExpression;

    // A dBASE III index has no FOR filter, and keeps deleted records too: every record is in it.
    bool IMaintainedTag.Includes(DbfRecord record) => true;
    byte[] IMaintainedTag.KeyFor(DbfRecord record) => KeyFor(record);
    void IMaintainedTag.Insert(byte[] key, int recordNumber) => Insert(key, recordNumber);
    bool IMaintainedTag.Remove(byte[] key, int recordNumber) => Remove(key, recordNumber);
    bool IMaintainedTag.Contains(byte[] key, int recordNumber) =>
        Find(RootPage(), key, (uint)recordNumber, [], depth: 0, visited: []) == Search.Found;
    int? IMaintainedTag.FirstRecordFor(byte[] key) => FirstRecordFor(key);
    void IMaintainedTag.Clear() => Clear();

    private bool _problemChecked;
    private string? _problem;

    internal bool SameLayoutAs(NdxTag other) =>
        Expression == other.Expression && KeyType == other.KeyType && KeyLength == other.KeyLength
        && ItemLength == other.ItemLength && MaxKeys == other.MaxKeys && IsUnique == other.IsUnique;

    // ---- what can be maintained -----------------------------------------------------------

    /// <inheritdoc cref="NdxIndex.CannotMaintainReason"/>
    internal string? MaintenanceProblem()
    {
        if (!_problemChecked)
        {
            _problem = KeyOrderProblem();
            _problemChecked = true;
        }
        return _problem;
    }

    // The file doesn't record a sort order. A localized dBASE that ordered text by a collation leaves
    // a pair out of byte order; inserting by byte order would then file keys where dBASE can't find
    // them. A damaged tree isn't judged here: the write that meets the damage reports it.
    private string? KeyOrderProblem()
    {
        try
        {
            byte[]? previous = null;
            return InKeyOrder(RootPage(), depth: 0, visited: [], ref previous)
                ? null
                : $"Index {Name}'s keys aren't in {(KeyType == 'C' ? "byte" : "number")} order, so it was probably built with another sort order.";
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private bool InKeyOrder(uint page, int depth, HashSet<uint> visited, ref byte[]? previous)
    {
        GuardDepth(depth);
        if (!visited.Add(page))
            throw new InvalidDataException($"Index {Name} reaches page {page} twice; the index is corrupt.");
        var (items, lastPointer, isLeaf) = ReadNode(page);
        if (!isLeaf)
        {
            for (var i = 0; i <= items.Count; i++)
            {
                if (!InKeyOrder(i < items.Count ? items[i].Pointer : lastPointer, depth + 1, visited, ref previous))
                    return false;
            }
            return true;
        }
        foreach (var item in items)
        {
            if (previous is not null && CompareKeys(previous, item.Key) > 0)
                return false;
            previous = item.Key;
        }
        return true;
    }

    // ---- keys for records -----------------------------------------------------------------

    /// <summary>
    /// The stored key bytes for <paramref name="record"/>, as dBASE III writes them: character keys
    /// space padded (or cut) to the key length in the table's code page; numbers and dates as a
    /// little-endian IEEE double, dates as their Julian day number and a blank date as 0.
    /// </summary>
    internal byte[] KeyFor(DbfRecord record)
    {
        _keyExpression ??= DbfExpression.Parse(Expression);
        var value = _keyExpression.Evaluate(record);
        if (KeyType == 'C')
        {
            var text = value as string ?? throw new InvalidDataException($"Index {Name}'s key expression '{Expression}' didn't produce text.");
            var key = new byte[KeyLength];
            key.AsSpan().Fill((byte)' ');
            var encoded = _index.Encoding.GetBytes(text);
            encoded.AsSpan(0, Math.Min(encoded.Length, KeyLength)).CopyTo(key);
            return key;
        }

        var number = value switch
        {
            decimal d => (double)d,
            double d => d,
            int i => i,
            DateOnly date => IndexKeys.JulianDay(date),
            null => 0d, // a blank date (or number)
            _ => throw new InvalidDataException($"Index {Name}'s key expression '{Expression}' didn't produce a number or a date."),
        };
        var bytes = new byte[NumericKeyLength];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, number == 0 ? 0d : number); // -0.0 is stored as 0.0
        return bytes;
    }

    /// <summary>Orders two stored keys the way dBASE III does: bytes for text, values for numbers.</summary>
    internal int CompareKeys(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => KeyType == 'C'
        ? a.SequenceCompareTo(b)
        : BinaryPrimitives.ReadDoubleLittleEndian(a).CompareTo(BinaryPrimitives.ReadDoubleLittleEndian(b));

    /// <summary>The record number of the first entry with exactly this key, or null.</summary>
    internal int? FirstRecordFor(byte[] key)
    {
        CheckKey(key);
        // A full-length character probe compares the whole key, so a prefix can't match here.
        KeyProbe probe = KeyType == 'C' ? new CharacterProbe(key) : new NumberProbe(BinaryPrimitives.ReadDoubleLittleEndian(key));
        foreach (var hit in Walk(probe))
            return hit.Matches ? hit.Entry.RecordNumber : null;
        return null;
    }

    private void CheckKey(byte[] key)
    {
        if (key.Length != KeyLength)
            throw new InvalidOperationException($"Index {Name} has {KeyLength}-byte keys; got {key.Length} bytes.");
    }

    // ---- insert ---------------------------------------------------------------------------

    // Pointer is the record number in a leaf and the child page in an inner page.
    private readonly record struct Item(uint Pointer, byte[] Key);

    /// <summary>Adds (key, record number), after any equal keys, splitting pages as needed.</summary>
    internal void Insert(byte[] key, int recordNumber)
    {
        CheckKey(key);
        if (recordNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(recordNumber));

        var root = RootPage();
        if (InsertInto(root, key, (uint)recordNumber, depth: 0, path: []) is { } split)
        {
            var newRoot = _index.AllocatePage();
            WriteNode(newRoot, [new Item(root, split.LeftLast)], split.Right, isLeaf: false);
            SetRoot(newRoot);
        }
    }

    private (byte[] LeftLast, uint Right)? InsertInto(uint page, byte[] key, uint recordNumber, int depth, HashSet<uint> path)
    {
        GuardDepth(depth);
        if (!path.Add(page))
            throw new InvalidDataException($"Index {Name} reaches page {page} twice; the index is corrupt.");
        var (items, lastPointer, isLeaf) = ReadNode(page);
        if (!isLeaf)
            CheckChildren(items, lastPointer, page, path);

        if (isLeaf)
        {
            items.Insert(UpperBound(items, key), new Item(recordNumber, key));
        }
        else
        {
            var slot = UpperBound(items, key);
            var child = slot < items.Count ? items[slot].Pointer : lastPointer;
            if (InsertInto(child, key, recordNumber, depth + 1, path) is not { } split)
                return null;

            // The child split into itself (left) and a new right page: the left keeps this slot with
            // its new last key as separator, and the right takes the slot after it with the old one.
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
            WriteNode(page, items, lastPointer, isLeaf);
            return null;
        }
        return isLeaf ? SplitLeaf(page, items) : SplitInner(page, items, lastPointer);
    }

    private (byte[], uint) SplitLeaf(uint page, List<Item> items)
    {
        var mid = items.Count / 2;
        var right = _index.AllocatePage();
        WriteNode(right, items.GetRange(mid, items.Count - mid), 0, isLeaf: true);
        WriteNode(page, items.GetRange(0, mid), 0, isLeaf: true);
        return (items[mid - 1].Key, right);
    }

    // Inner key i is the highest key under child i. The left half keeps children 0..mid, so its last
    // child's key (items[mid].Key) becomes the separator and leaves both halves.
    private (byte[], uint) SplitInner(uint page, List<Item> items, uint lastPointer)
    {
        var mid = items.Count / 2;
        var right = _index.AllocatePage();
        WriteNode(right, items.GetRange(mid + 1, items.Count - mid - 1), lastPointer, isLeaf: false);
        WriteNode(page, items.GetRange(0, mid), items[mid].Pointer, isLeaf: false);
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

    // ---- remove ---------------------------------------------------------------------------

    private enum Search { Found, KeepLooking, Stop }

    /// <summary>Removes the entry (key, record number). Returns false when there isn't one.</summary>
    internal bool Remove(byte[] key, int recordNumber)
    {
        CheckKey(key);
        var path = new List<(uint Page, int Slot)>();
        if (Find(RootPage(), key, (uint)recordNumber, path, depth: 0, visited: []) != Search.Found)
            return false;

        // Remove the item from its leaf; if that empties a page (other than the root), drop the
        // page's pointer from its parent and carry on upward while parents empty out too. The
        // emptied page itself stays in the file, out of the tree, like dBASE III leaves it.
        for (var level = path.Count - 1; level >= 0; level--)
        {
            var (page, slot) = path[level];
            var (items, lastPointer, isLeaf) = ReadNode(page);
            if (isLeaf || slot < items.Count)
                items.RemoveAt(slot);
            else if (items.Count > 0)
            {
                // The last child went; the one before it becomes last, and the last child has no key.
                lastPointer = items[^1].Pointer;
                items.RemoveAt(items.Count - 1);
            }
            else
                lastPointer = 0;

            var empty = isLeaf ? items.Count == 0 : lastPointer == 0;
            if (!empty || level == 0)
            {
                // An inner root that lost its only child is an empty tree: write it as an empty leaf.
                WriteNode(page, items, lastPointer, isLeaf || empty);
                if (level == 0)
                    CollapseRoot();
                return true;
            }
        }
        return true;
    }

    // Each page is searched at most once: a damaged index whose pages share children can't make this
    // repeat the same subtree over and over.
    private Search Find(uint page, byte[] key, uint recordNumber, List<(uint, int)> path, int depth, HashSet<uint> visited)
    {
        GuardDepth(depth);
        if (!visited.Add(page))
            throw new InvalidDataException($"Index {Name} reaches page {page} twice; the index is corrupt.");
        var (items, lastPointer, isLeaf) = ReadNode(page);
        if (!isLeaf)
            CheckChildren(items, lastPointer, page, path.Select(p => p.Item1).Append(page).ToHashSet());
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

    // An inner root left with a single child (no keys) hands the root role to that child. dBASE III
    // would leave it in place; both shapes read the same, and the shorter tree is quicker to search.
    private void CollapseRoot()
    {
        for (var depth = 0; ; depth++)
        {
            GuardDepth(depth);
            var root = RootPage();
            var (items, lastPointer, isLeaf) = ReadNode(root);
            if (isLeaf || items.Count > 0)
                return;
            SetRoot(lastPointer);
        }
    }

    // ---- rebuild --------------------------------------------------------------------------

    /// <summary>
    /// Empties the index: like dBASE's REINDEX, the file shrinks to its header and one empty leaf,
    /// which becomes the root. The header's key expression, key layout and unique flag are kept.
    /// </summary>
    internal void Clear()
    {
        _index.Truncate();
        var root = _index.AllocatePage();
        WriteNode(root, [], 0, isLeaf: true);
        SetRoot(root);
    }

    // ---- pages ----------------------------------------------------------------------------

    private (List<Item> Items, uint LastPointer, bool IsLeaf) ReadNode(uint page)
    {
        if (page == 0)
            throw new InvalidDataException($"Index {Name} has an inner page pointing at page 0, the header; the index is corrupt.");

        var node = _index.ReadPage(page);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(node);
        if (count > MaxKeys)
            throw new InvalidDataException($"Index page {page} claims {count} keys; index {Name} holds at most {MaxKeys} per page.");

        // Same rule as the reader: a leaf's first item has no lower page.
        var isLeaf = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(4)) == 0;
        var items = new List<Item>(count + 1);
        for (var i = 0; i < count; i++)
        {
            var at = 4 + i * ItemLength;
            var pointer = isLeaf
                ? (uint)IndexKeys.RecordNumber(BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(at + 4)), Name)
                : BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(at));
            items.Add(new Item(pointer, node.AsSpan(at + 8, KeyLength).ToArray()));
        }
        var lastPointer = isLeaf ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(4 + count * ItemLength));
        return (items, lastPointer, isLeaf);
    }

    /// <summary>
    /// An inner page's children must be distinct pages, none of them the page or one above it: a page
    /// referenced twice is damage, and changing it would change two branches at once.
    /// </summary>
    private void CheckChildren(List<Item> items, uint lastPointer, uint page, HashSet<uint> above)
    {
        var seen = new HashSet<uint>();
        for (var i = 0; i <= items.Count; i++)
        {
            var child = i < items.Count ? items[i].Pointer : lastPointer;
            if (!seen.Add(child) || above.Contains(child))
                throw new InvalidDataException($"Index page {page} of {Name} points at page {child} twice, or back up the tree; the index is corrupt.");
        }
    }

    //  leaf item:  [ 0 0 0 0 | record | key ... pad ]
    //  inner item: [ child   | 0 0 0 0 | key ... pad ]   then the last child's 4 bytes alone
    private void WriteNode(uint page, List<Item> items, uint lastPointer, bool isLeaf)
    {
        var node = new byte[NdxIndex.PageSize];
        BinaryPrimitives.WriteUInt16LittleEndian(node, (ushort)items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var at = 4 + i * ItemLength;
            BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(isLeaf ? at + 4 : at), items[i].Pointer);
            items[i].Key.CopyTo(node.AsSpan(at + 8));
        }
        if (!isLeaf)
            BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4 + items.Count * ItemLength), lastPointer);
        _index.WritePage(page, node);
    }

    private uint RootPage()
    {
        var root = _index.ReadHeaderUInt(0);
        return root != 0 ? root : throw new InvalidDataException("The .ndx header has no root page.");
    }

    private void SetRoot(uint page) => _index.WriteHeaderUInt(0, page);

    private void GuardDepth(int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"Index {Name} is deeper than {MaxDepth} levels; the index is probably corrupt.");
    }
}
