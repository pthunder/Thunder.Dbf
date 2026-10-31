//   page (1,024 bytes)                       ProjectThunder :: Thunder.Dbf
//   +-------+---------------------+--------  Keeping a Clipper .ntx right while the table
//   | count | offset 0 .. offset m| item 0   changes underneath it.
//   +-------+---------------------+--------
//   item = [ child page | record no | key ]  A full page splits around its middle item, which
//                                            moves up into the parent; a full root grows a new
//          [ 7 19 | 42 | 57 88 ]  full!      root above it. A page a delete empties borrows an
//                   |                        item from a sibling through the parent, or merges
//          [ 7 19 ] [ 57 88 ]                with it, so no page but the root is ever left empty.
//
// The page layout, split rule and free list follow Harbour's src/rdd/dbfntx (read, not copied:
// Harbour is GPL with a linking exception) and the files X#'s DBFNTX driver writes.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

public sealed partial class NtxIndex : IMaintainedIndex
{
    // Header offsets (see docs/formats/ntx.md).
    private const int VersionOffset = 2;
    internal const int RootOffset = 4;
    internal const int FreePageOffset = 8;

    private bool _changed;

    IEnumerable<IMaintainedTag> IMaintainedIndex.MaintainedTags => Tags;
    void IMaintainedIndex.ReloadTags() => ReloadTags();
    void IMaintainedIndex.Commit() => Commit();

    /// <summary>
    /// Re-reads the header, in case another program rebuilt the index. The tag object stays the same
    /// unless its layout changed, so changes a table planned against it still apply.
    /// </summary>
    internal void ReloadTags()
    {
        var fresh = NtxTag.Load(this, _fileName, _table);
        if (!fresh.SameLayoutAs(Tag))
            Tag = fresh;
    }

    /// <summary>
    /// Bumps the header's update counter when anything changed and pushes everything to disk. Clipper
    /// and Harbour compare that counter to the one they last saw to know their cached pages are stale.
    /// </summary>
    internal void Commit()
    {
        if (_changed)
        {
            Span<byte> version = stackalloc byte[2];
            DbfTable.DiscardReadBuffer(_stream);
            _stream.Position = VersionOffset;
            _stream.ReadExactly(version);
            BinaryPrimitives.WriteUInt16LittleEndian(version, (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(version) + 1));
            _stream.Position = VersionOffset;
            _stream.Write(version);
            _changed = false;
        }
        DbfTable.FlushToDisk(_stream);
    }

    internal void MarkChanged() => _changed = true;

    internal void WritePage(long offset, byte[] page)
    {
        if (offset < PageSize || offset % PageSize != 0 || page.Length != PageSize)
            throw new InvalidOperationException($"Can't write an index page at offset {offset}.");
        _stream.Position = offset;
        _stream.Write(page);
    }

    /// <summary>
    /// A new page, always at the physical end of the file. The free list isn't reused: its links come
    /// from the file, and following a damaged one would overwrite a live page. Pages this writer frees
    /// still go on the list, in Harbour's layout, for Clipper and Harbour to reuse.
    /// </summary>
    internal long AllocatePage(long maxOffset)
    {
        var offset = (_stream.Length + PageSize - 1) / PageSize * PageSize;
        if (offset > maxOffset)
            throw new InvalidDataException("The .ntx index has reached the largest size its page pointers can address.");
        WritePage(offset, new byte[PageSize]);
        return offset;
    }

    /// <summary>Cuts the file back to its header, for a rebuild. The free list goes with the pages it listed.</summary>
    internal void TruncateToHeader()
    {
        _stream.SetLength(PageSize);
        WriteHeaderUInt32(FreePageOffset, 0);
    }

    internal uint ReadHeaderUInt32(int offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        DbfTable.DiscardReadBuffer(_stream);
        _stream.Position = offset;
        _stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    internal void WriteHeaderUInt32(int offset, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _stream.Position = offset;
        _stream.Write(bytes);
    }
}

public sealed partial class NtxTag : IMaintainedTag
{
    private DbfExpression? _keyExpression;
    private DbfExpression? _forExpression;
    private bool _expressionsParsed;

    bool IMaintainedTag.Includes(DbfRecord record) => Includes(record);
    byte[] IMaintainedTag.KeyFor(DbfRecord record) => KeyFor(record);
    void IMaintainedTag.Insert(byte[] key, int recordNumber) => Insert(key, recordNumber);
    bool IMaintainedTag.Remove(byte[] key, int recordNumber) => Remove(key, recordNumber);
    int? IMaintainedTag.FirstRecordFor(byte[] key) => FirstRecordFor(key);
    void IMaintainedTag.Clear() => Clear();

    internal bool SameLayoutAs(NtxTag other) =>
        Expression == other.Expression && ForExpression == other.ForExpression && KeyType == other.KeyType
        && KeyLength == other.KeyLength && KeyDecimals == other.KeyDecimals && ItemLength == other.ItemLength
        && MaxKeys == other.MaxKeys && IsUnique == other.IsUnique && IsDescending == other.IsDescending
        && _largeFile == other._largeFile;

    // ---- keys for records -----------------------------------------------------------------

    /// <summary>True when the tag's FOR filter (Clipper 5.3, Harbour), if any, lets <paramref name="record"/> in.</summary>
    internal bool Includes(DbfRecord record)
    {
        ParseExpressions();
        return _forExpression is null || _forExpression.Evaluate(record) is true;
    }

    /// <summary>
    /// The key Clipper stores for <paramref name="record"/>: always text. Character keys are space
    /// padded, numbers are STR()-like digits (see EncodeNumber), dates DTOS() text, logicals T or F.
    /// </summary>
    internal byte[] KeyFor(DbfRecord record)
    {
        ParseExpressions();
        var value = _keyExpression!.Evaluate(record);
        return value switch
        {
            string text => Padded(_index.Encoding.GetBytes(text)),
            decimal number => EncodeNumber(number),
            double number => double.IsFinite(number) && Math.Abs(number) < 7.9e28 ? EncodeNumber((decimal)number) : Overflow(),
            int or long or short or byte or float => EncodeNumber(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
            DateOnly date => Padded(Encoding.ASCII.GetBytes(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture))),
            bool flag => Padded([(byte)(flag ? 'T' : 'F')]),
            null when KeyType == 'N' => EncodeNumber(0m),
            null => Padded([]), // a blank date: DTOS() gives spaces
            _ => throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' produced a {value.GetType().Name}, which Clipper can't store as a key."),
        };
    }

    private byte[] Padded(byte[] text)
    {
        var key = new byte[KeyLength];
        key.AsSpan().Fill((byte)' ');
        text.AsSpan(0, Math.Min(text.Length, KeyLength)).CopyTo(key);
        return key;
    }

    private byte[] Overflow() => Encoding.ASCII.GetBytes(new string('*', KeyLength));

    private void ParseExpressions()
    {
        if (_expressionsParsed)
            return;
        _keyExpression = DbfExpression.Parse(Expression);
        _forExpression = string.IsNullOrWhiteSpace(ForExpression) ? null : DbfExpression.Parse(ForExpression);
        _expressionsParsed = true;
    }

    // Keys compare as bytes (memcmp), the whole tree reversed for a descending index. Equal keys are
    // kept in record order, as Clipper's own INDEX ON leaves them, so a rebuild and a run of single
    // inserts give the same file order.
    private int CompareKeys(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => Order(a.SequenceCompareTo(b));

    private int CompareItems(ReadOnlySpan<byte> key, int record, Item item)
    {
        var order = CompareKeys(key, item.Key);
        return order != 0 ? order : record.CompareTo(item.Record);
    }

    /// <summary>The record number of the first entry with exactly this key, or null.</summary>
    internal int? FirstRecordFor(byte[] key)
    {
        CheckKey(key);
        foreach (var hit in Walk(key))
            return hit.Matches ? hit.Entry.RecordNumber : null;
        return null;
    }

    // ---- insert ---------------------------------------------------------------------------

    private readonly record struct Item(long Child, int Record, byte[] Key);

    private sealed class Node(List<Item> items, long lastChild)
    {
        public List<Item> Items { get; } = items;

        /// <summary>The child after the last key (slot count's pointer). 0 on a leaf.</summary>
        public long LastChild { get; set; } = lastChild;

        public bool IsLeaf => LastChild == 0;

        public long ChildAt(int slot) => slot < Items.Count ? Items[slot].Child : LastChild;

        public void SetChild(int slot, long child)
        {
            if (slot < Items.Count)
                Items[slot] = Items[slot] with { Child = child };
            else
                LastChild = child;
        }
    }

    /// <summary>Adds (key, record number) in key order, splitting full pages on the way back up.</summary>
    internal void Insert(byte[] key, int recordNumber)
    {
        CheckKey(key);
        CheckWritable();
        var root = CurrentRoot();
        if (InsertInto(root, key, recordNumber, depth: 0) is { } split)
        {
            var newRoot = AllocatePage();
            WriteNode(newRoot, new Node([split.Middle with { Child = root }], split.Right));
            SetRoot(newRoot);
        }
        _index.MarkChanged();
    }

    private (Item Middle, long Right)? InsertInto(long page, byte[] key, int recordNumber, int depth)
    {
        GuardDepth(depth);
        var node = ReadNode(page);
        var slot = UpperBound(node.Items, key, recordNumber);

        if (node.IsLeaf)
        {
            node.Items.Insert(slot, new Item(0, recordNumber, key));
        }
        else
        {
            var child = node.ChildAt(slot);
            if (InsertInto(child, key, recordNumber, depth + 1) is not { } split)
                return null;

            // The child kept the items before the middle one; the middle item comes up to sit in front
            // of it, and the pointer that led to the child now leads to its new right half.
            node.Items.Insert(slot, split.Middle with { Child = child });
            node.SetChild(slot + 1, split.Right);
        }

        if (node.Items.Count <= MaxKeys)
        {
            WriteNode(page, node);
            return null;
        }

        // Harbour splits at the middle too: with MaxKeys + 1 items, each half gets about MaxKeys / 2,
        // the half-page figure in the header.
        var mid = node.Items.Count / 2;
        var middle = node.Items[mid];
        var right = AllocatePage();
        WriteNode(page, new Node(node.Items.GetRange(0, mid), middle.Child));
        WriteNode(right, new Node(node.Items.GetRange(mid + 1, node.Items.Count - mid - 1), node.LastChild));
        return (middle, right);
    }

    private int UpperBound(List<Item> items, byte[] key, int recordNumber)
    {
        int lo = 0, hi = items.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (CompareItems(key, recordNumber, items[mid]) >= 0)
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
        CheckWritable();

        // path[i] is (page, the slot taken there); the last entry's slot is the item itself.
        var path = new List<(long Page, int Slot)>();
        if (Find(CurrentRoot(), key, recordNumber, path, depth: 0, visited: []) != Search.Found)
            return false;

        var (page, slot) = path[^1];
        var node = ReadNode(page);
        if (node.IsLeaf)
        {
            node.Items.RemoveAt(slot);
            WriteNode(page, node);
        }
        else
        {
            // A key in an inner page is replaced by its predecessor, the last key of the rightmost leaf
            // under its left child; that leaf then loses one key instead.
            var leaf = node.Items[slot].Child;
            for (var depth = path.Count; ; depth++)
            {
                GuardDepth(depth);
                var below = ReadNode(leaf);
                if (below.IsLeaf)
                {
                    if (below.Items.Count == 0)
                        throw new InvalidDataException($"Tag {Name} has an empty page at offset {leaf} below a key; the index is corrupt.");
                    path.Add((leaf, below.Items.Count - 1));
                    var predecessor = below.Items[^1];
                    below.Items.RemoveAt(below.Items.Count - 1);
                    WriteNode(leaf, below);
                    node.Items[slot] = node.Items[slot] with { Record = predecessor.Record, Key = predecessor.Key };
                    WriteNode(page, node);
                    break;
                }
                path.Add((leaf, below.Items.Count));
                leaf = below.LastChild;
            }
        }

        Rebalance(path);
        _index.MarkChanged();
        return true;
    }

    // Each page is searched at most once: a damaged index whose pages share children can't make this
    // repeat the same subtree over and over. Equal keys may sit in any record order in a file Clipper
    // wrote, so every entry with the key is a candidate.
    private Search Find(long page, byte[] key, int recordNumber, List<(long, int)> path, int depth, HashSet<long> visited)
    {
        GuardDepth(depth);
        if (!visited.Add(page))
            throw new InvalidDataException($"Tag {Name} reaches the index page at offset {page} twice; the index is corrupt.");
        var node = ReadNode(page);

        for (var i = LowerBound(node.Items, key); i <= node.Items.Count; i++)
        {
            if (!node.IsLeaf)
            {
                path.Add((page, i));
                var result = Find(node.ChildAt(i), key, recordNumber, path, depth + 1, visited);
                if (result == Search.Found)
                    return result;
                path.RemoveAt(path.Count - 1);
                if (result == Search.Stop)
                    return result;
            }

            if (i == node.Items.Count)
                break;
            if (CompareKeys(node.Items[i].Key, key) != 0)
                return Search.Stop;
            if (node.Items[i].Record == recordNumber)
            {
                path.Add((page, i));
                return Search.Found;
            }
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

    // Pages may run below half full (Clipper reads them fine), but never empty: an empty page that
    // isn't the root borrows an item through its parent from a sibling with two or more, or else
    // merges with that sibling, which can empty the parent in turn. An inner root left with no keys
    // hands the root role to its one child.
    private void Rebalance(List<(long Page, int Slot)> path)
    {
        for (var level = path.Count - 1; level >= 0; level--)
        {
            var page = path[level].Page;
            var node = ReadNode(page);
            if (node.Items.Count > 0)
                return;

            if (level == 0)
            {
                if (!node.IsLeaf)
                {
                    SetRoot(node.LastChild);
                    FreePage(page);
                }
                return;
            }

            var (parentPage, slot) = path[level - 1];
            var parent = ReadNode(parentPage);
            if (slot > parent.Items.Count || parent.ChildAt(slot) != page)
                throw new InvalidDataException($"Tag {Name}'s page at offset {parentPage} no longer points where it did; the index is corrupt.");
            if (parent.Items.Count == 0)
                return; // a keyless inner page from a damaged file has no sibling to lend; it still reads correctly

            if (slot > 0)
            {
                var leftPage = parent.ChildAt(slot - 1);
                var left = ReadSibling(leftPage, node);
                if (left.Items.Count >= 2)
                {
                    // Rotate right: the separator comes down to the front of this page, and the left
                    // sibling's last item goes up to replace it.
                    var separator = parent.Items[slot - 1];
                    node.Items.Insert(0, separator with { Child = left.LastChild });
                    var last = left.Items[^1];
                    left.Items.RemoveAt(left.Items.Count - 1);
                    left.LastChild = last.Child;
                    parent.Items[slot - 1] = last with { Child = leftPage };
                    WriteNode(leftPage, left);
                    WriteNode(page, node);
                    WriteNode(parentPage, parent);
                    return;
                }
            }

            if (slot < parent.Items.Count)
            {
                var rightPage = parent.ChildAt(slot + 1);
                var right = ReadSibling(rightPage, node);
                if (right.Items.Count >= 2)
                {
                    // Rotate left: the separator comes down to the end of this page, and the right
                    // sibling's first item goes up to replace it.
                    var separator = parent.Items[slot];
                    node.Items.Add(separator with { Child = node.LastChild });
                    var first = right.Items[0];
                    right.Items.RemoveAt(0);
                    node.LastChild = first.Child;
                    parent.Items[slot] = first with { Child = page };
                    WriteNode(rightPage, right);
                    WriteNode(page, node);
                    WriteNode(parentPage, parent);
                    return;
                }

                if (slot == 0)
                {
                    // Merge the right sibling (one item at most) into this page, around the separator.
                    node.Items.Add(parent.Items[slot] with { Child = node.LastChild });
                    node.Items.AddRange(right.Items);
                    node.LastChild = right.LastChild;
                    parent.Items.RemoveAt(slot);
                    parent.SetChild(slot, page);
                    WriteNode(page, node);
                    WriteNode(parentPage, parent);
                    FreePage(rightPage);
                    continue;
                }
            }

            // Merge this empty page into its left sibling (one item at most), around the separator.
            {
                var leftPage = parent.ChildAt(slot - 1);
                var left = ReadSibling(leftPage, node);
                left.Items.Add(parent.Items[slot - 1] with { Child = left.LastChild });
                left.LastChild = node.LastChild;
                parent.Items.RemoveAt(slot - 1);
                parent.SetChild(slot - 1, leftPage);
                WriteNode(leftPage, left);
                WriteNode(parentPage, parent);
                FreePage(page);
            }
        }
    }

    private Node ReadSibling(long page, Node of)
    {
        var sibling = ReadNode(page);
        if (sibling.IsLeaf != of.IsLeaf)
            throw new InvalidDataException($"Tag {Name} has a leaf and an inner page side by side at offset {page}; the index is corrupt.");
        return sibling;
    }

    // ---- rebuild --------------------------------------------------------------------------

    /// <summary>Empties the tag: the file is cut back to its header and one empty leaf becomes the root.</summary>
    internal void Clear()
    {
        CheckWritable();
        _index.TruncateToHeader();
        var root = AllocatePage();
        WriteNode(root, new Node([], 0));
        SetRoot(root);
        _index.MarkChanged();
    }

    // ---- pages ----------------------------------------------------------------------------

    private Node ReadNode(long offset)
    {
        if (offset == 0)
            throw new InvalidDataException($"Tag {Name} points at offset 0, the header; the index is corrupt.");
        var page = _index.ReadPage(offset);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(page);
        if (count > MaxKeys)
            throw new InvalidDataException($"The index page at offset {offset} claims {count} keys; tag {Name} holds at most {MaxKeys} per page.");

        var items = new List<Item>(count + 2);
        for (var i = 0; i < count; i++)
        {
            var at = ItemOffset(page, i, offset, withKey: true);
            var record = IndexKeys.RecordNumber(BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(at + 4)), Name);
            items.Add(new Item(ChildOffset(page, at), record, page.AsSpan(at + 8, KeyLength).ToArray()));
        }
        var node = new Node(items, ChildOffset(page, ItemOffset(page, count, offset, withKey: false)));

        // A page is a leaf when all its child pointers are 0, and an inner page when none are.
        if (items.Any(item => (item.Child == 0) != node.IsLeaf))
            throw new InvalidDataException($"The index page at offset {offset} mixes empty and non-empty child pointers; the index is corrupt.");
        return node;
    }

    private long ChildOffset(byte[] page, int at)
    {
        var pointer = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(at));
        return _largeFile ? pointer * (long)NtxIndex.PageSize : pointer;
    }

    // Pages are written the way Harbour lays out a fresh one: the offset table lists every slot, the
    // ones past the count too, each pointing at its own item area in order. Clipper takes a free
    // slot's offset as the place to write the next key, so none may be shared or out of the page.
    private void WriteNode(long offset, Node node)
    {
        if (node.Items.Count > MaxKeys)
            throw new InvalidOperationException($"Tag {Name} can't hold {node.Items.Count} keys on one page.");

        var page = new byte[NtxIndex.PageSize];
        BinaryPrimitives.WriteUInt16LittleEndian(page, (ushort)node.Items.Count);
        var firstItem = 2 + (MaxKeys + 1) * 2;
        for (var slot = 0; slot <= MaxKeys; slot++)
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2 + slot * 2), (ushort)(firstItem + slot * ItemLength));

        for (var slot = 0; slot < node.Items.Count; slot++)
        {
            var at = firstItem + slot * ItemLength;
            var item = node.Items[slot];
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(at), Pointer(item.Child));
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(at + 4), (uint)item.Record);
            item.Key.CopyTo(page.AsSpan(at + 8));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(firstItem + node.Items.Count * ItemLength), Pointer(node.LastChild));
        _index.WritePage(offset, page);
    }

    private uint Pointer(long offset) => (uint)(_largeFile ? offset / NtxIndex.PageSize : offset);

    // Plain files hold byte offsets in 32 bits; large ones (Harbour) hold page numbers.
    private long AllocatePage() =>
        _index.AllocatePage(_largeFile ? uint.MaxValue * (long)NtxIndex.PageSize : uint.MaxValue - NtxIndex.PageSize + 1);

    /// <summary>Puts a page on the free list the way Harbour does: no keys, slot 0's child links to the next free page.</summary>
    private void FreePage(long offset)
    {
        var next = _index.ReadHeaderUInt32(NtxIndex.FreePageOffset);
        WriteNode(offset, new Node([], _largeFile ? next * (long)NtxIndex.PageSize : next));
        _index.WriteHeaderUInt32(NtxIndex.FreePageOffset, Pointer(offset));
    }

    private void SetRoot(long offset) => _index.WriteHeaderUInt32(NtxIndex.RootOffset, Pointer(offset));

    private void GuardDepth(int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"Tag {Name} is deeper than {MaxDepth} levels; the index is probably corrupt.");
    }

    private void CheckKey(byte[] key)
    {
        if (key.Length != KeyLength)
            throw new ArgumentException($"Tag {Name} has {KeyLength}-byte keys, not {key.Length}.", nameof(key));
    }

    // A split leaves MaxKeys / 2 items on each side, so pages that hold fewer than two keys can't split.
    private void CheckWritable()
    {
        if (MaxKeys < 2)
            throw new InvalidDataException($"Tag {Name} holds only {MaxKeys} key per page, too few to keep up to date.");
    }
}
