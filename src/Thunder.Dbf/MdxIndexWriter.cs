//          insert 42                       ProjectThunder :: Thunder.Dbf
//              |                           Keeping a dBASE IV .mdx right while the table
//      [ 7 | 40 | 88 ]  full!              changes underneath it.
//              |
//      [ 7 | 40 ] [ 42 | 88 ]   split      A full node splits in half; the parent gains a
//         \         /                      separator (the left half's last key). A full
//          [ 40 ]          new root        root splits too, and a new root grows above it.
//
// Removing a key never merges nodes: an emptied node goes back on the free list and its
// pointer leaves the parent. Each interior key stays equal to its child's last key, as dBASE and
// Jet keep it: Jet goes down by those keys and never on to the next leaf, so a key left above its
// child's real last key hides the keys that come after it (found by reading Thunder.Dbf's indexes with Jet on Windows).

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

    /// <summary>
    /// Why the tags aren't in step with <paramref name="table"/>, or null when they are. A tag with no
    /// FOR filter and no UNIQUE holds one key per record, deleted ones included, and every tag holds the
    /// last two records' keys: a writer killed between the table and the index (an append that counted
    /// its record but added no keys, an update with its new keys in and its old ones not yet out), or
    /// Jet writing beside another writer, leaves a tag that fails one of them. The same checks a .cdx gets.
    /// </summary>
    /// <param name="table">The table the index belongs to.</param>
    /// <param name="checkUnique">Also check each UNIQUE tag holds one key per distinct value: a read of every record, which Check() does.</param>
    internal string? OutOfStep(DbfTable table, bool checkUnique = false)
    {
        var records = table.RecordCount;
        foreach (var tag in Tags)
        {
            if (tag.ForExpression is null && !tag.IsUnique && tag.CountKeys() is var keys && keys != records)
                return $"Tag {tag.Name} holds {keys:N0} keys for {records:N0} records, so it isn't in step with the table (a write reached the table but not the index, or the index but not the table)";
        }
        // A UNIQUE tag holds one key for each value the records have (deleted ones included, as dBASE
        // keeps them). Kills that hand a key on between records can leave one short or over.
        foreach (var tag in checkUnique ? Tags.Where(t => t.IsUnique && t.ForExpression is null) : [])
        {
            var values = new HashSet<string>();
            foreach (var record in table.ReadAll())
                values.Add(Convert.ToHexString(tag.KeyFor(record)));
            if (tag.CountKeys() is var keys && keys != values.Count)
                return $"UNIQUE tag {tag.Name} holds {keys:N0} keys for {values.Count:N0} distinct values, so it isn't in step with the table";
        }
        for (var index = Math.Max(0, records - 2); index < records; index++)
        {
            var record = table.Read(index);
            // A UNIQUE tag keeps one record per key, so a record may rightly have none (as for a .cdx).
            foreach (var tag in Tags.Where(t => !t.IsUnique))
            {
                if (tag.Includes(record) && !tag.Contains(tag.KeyFor(record), record.RecordNumber))
                    return $"Tag {tag.Name} has no key for record {record.RecordNumber:N0}, so it isn't in step with the table (a write reached the table but not the index)";
            }
        }
        return null;
    }

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

        // New blocks first (nothing on disk points at them yet), then, once they're out, the tree blocks
        // that existed (some now pointing at new ones), then the tag headers that point at each tag's
        // root, then blocks put on the free list (the old roots may point at them until the headers are
        // out), and the file header (page count, free list, date) last. Contiguous pages go in one write.
        // A batch cut off between these can leave a tag missing keys, never reaching a page twice through
        // a new root over an old one; either way its journal note stays, and RebuildIndex puts it right.
        var firstNew = (int)((batch.StartLength + PageSize - 1) / PageSize);
        var headerPages = HeaderAreaSize / PageSize;
        var freed = batch.Freed.SelectMany(p => Enumerable.Range(p, PagesPerBlock)).ToHashSet();
        var tagHeaders = Tags.SelectMany(t => Enumerable.Range(t.HeaderPage, PagesPerBlock)).ToHashSet();
        WriteRuns(batch, p => p >= firstNew && !freed.Contains(p) && !tagHeaders.Contains(p));
        if (batch.Changed.Any(p => p >= firstNew))
            DbfTable.FlushToDisk(_stream, toDisk);
        WriteRuns(batch, p => p >= headerPages && p < firstNew && !freed.Contains(p) && !tagHeaders.Contains(p));
        WriteRuns(batch, p => tagHeaders.Contains(p) && !freed.Contains(p));
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

    /// <summary>
    /// Whether the table forces writes to disk (FlushWritesToDisk, or a step that always does), asked at
    /// each split; null for an index opened on its own, which never forces.
    /// </summary>
    internal Func<bool>? ForcesWrites { get; set; }

    /// <summary>
    /// Called after each key a rebuild goes through, whether the tag took it or (a unique tag, a key
    /// it already holds) left it out: where a test cuts a rebuild off partway.
    /// </summary>
    internal Action? RebuildPoint { get; set; }

    /// <summary>Forces what's written so far to disk before a write that depends on it, when the table forces writes.</summary>
    internal void Barrier()
    {
        if (_batch is null && ForcesWrites?.Invoke() == true)
            DbfTable.FlushToDisk(_stream, toDisk: true);
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
        // A rebuild hands over the pages of the tree it is replacing (see MdxTag.Refill).
        if (_reusable is { Count: > 0 } reusable)
        {
            var reused = reusable.Dequeue();
            WriteBlock(reused, new byte[BlockSize]);
            return reused;
        }

        // The stored page count is only advice; the file's real length decides.
        var page = (int)((Length + PageSize - 1) / PageSize);
        page = (page + PagesPerBlock - 1) / PagesPerBlock * PagesPerBlock;
        if ((long)(page + PagesPerBlock) * PageSize > MaxIndexBytes)
            throw new InvalidDataException("The .mdx index has reached the 2 GB dBASE can handle.");
        WriteBlock(page, new byte[BlockSize]);
        WriteHeaderInt(PageCountOffset, page + PagesPerBlock);
        return page;
    }

    // Pages a rebuild may take before the file grows: the nodes of the tree being replaced. That isn't
    // the free list, whose pointers come from the file and aren't followed.
    private Queue<int>? _reusable;

    internal void OfferPages(IEnumerable<int> pages) => _reusable = new Queue<int>(pages);

    // Which pages each tag may reuse, by tag header page: worked out for the whole index before a
    // rebuild changes any tag, and only when the file proves each page is one tag's alone.
    private Dictionary<int, List<int>>? _rebuildPages;
    private bool _rebuildPlanned;

    /// <summary>
    /// The nodes of <paramref name="tag"/>'s tree, for its rebuild to build the new tree in; null when
    /// the rebuild must use new pages. A page is reused only if nothing else could own it: every
    /// tag's tree was walked whole, no page is reached twice (in one tree or in two), none is a tag
    /// header or lies outside the file, and none is on the free list. An index that fails any of
    /// that (a cut-off write can leave one so) is rebuilt as before, in new pages.
    /// </summary>
    internal List<int>? PagesToReuse(MdxTag tag)
    {
        if (!_rebuildPlanned)
        {
            _rebuildPages = PlanReuse();
            _rebuildPlanned = true;
        }
        return _rebuildPages is { } plan && plan.Remove(tag.HeaderPage, out var pages) ? pages : null;
    }

    /// <summary>The plan covers one rebuild: it's dropped when that ends, in a commit or a failure.</summary>
    internal void ForgetRebuildPlan()
    {
        _rebuildPages = null;
        _rebuildPlanned = false;
    }

    private Dictionary<int, List<int>>? PlanReuse()
    {
        if (_batch is not null)
            return null; // a batch orders its writes by which pages are new
        // Every page of every block is claimed once: a tag header, a node or a free block may not
        // even partly cover another, and each must lie whole inside the file, past the header area.
        // A tag header counts as a block, which is how dBASE, Jet and this library lay them out and
        // how they're read and written here. A file that packs them closer isn't damaged; it only
        // fails this and is rebuilt in new pages, as every index was before pages were reused.
        var firstNode = HeaderAreaSize / PageSize;
        var claimed = new HashSet<int>();
        bool Claim(int block)
        {
            if (block < firstNode || (long)(block + PagesPerBlock) * PageSize > Length)
                return false;
            for (var page = block; page < block + PagesPerBlock; page++)
            {
                if (!claimed.Add(page))
                    return false;
            }
            return true;
        }

        var plan = new Dictionary<int, List<int>>();
        try
        {
            if (!Tags.All(t => Claim(t.HeaderPage)))
                return null;
            foreach (var tag in Tags)
            {
                var pages = tag.OwnPages();
                if (!pages.All(Claim))
                    return null;
                plan[tag.HeaderPage] = pages;
            }
            for (var block = ReadHeaderInt(FreePageOffset); block != 0; block = BinaryPrimitives.ReadInt32LittleEndian(ReadBlock(block).AsSpan(4)))
            {
                if (!Claim(block))
                    return null;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
        return plan;
    }

    /// <summary>Ends <see cref="OfferPages"/> and returns the pages nothing took.</summary>
    internal IReadOnlyList<int> TakeBackPages()
    {
        var unused = _reusable?.ToList() ?? [];
        _reusable = null;
        return unused;
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
        ForgetRebuildPlan();
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
        return EncodeKey(KeyType, KeyLength, _index.Encoding, _keyExpression!.Evaluate(record), Name, Expression, NumericStyle, BlankDateKey);
    }

    /// <summary>
    /// The stored bytes for one key value. Shared with INDEX ON, which encodes every key before a new
    /// tag exists, so a value that can't be stored stops the command before the file changes. A new
    /// tag counts digits as Jet does (see <see cref="MdxTag.NumericStyle"/>) and keys a blank date as 0
    /// (see <see cref="MdxTag.BlankDateKey"/>).
    /// </summary>
    internal static byte[] EncodeKey(char keyType, int keyLength, Encoding encoding, object? value, string tag, string expression, NumericKeyStyle style = NumericKeyStyle.Jet, double blankDate = 0d)
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
                    decimal d => MdxKeys.EncodeNumeric(d, style),
                    double d => MdxKeys.EncodeNumeric(d, style),
                    int or long => MdxKeys.EncodeNumeric(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture), style),
                    null => MdxKeys.EncodeNumeric(0m, style),
                    _ => throw new InvalidDataException($"Tag {tag}'s key expression '{expression}' didn't produce a number."),
                };
            default:
            {
                if (value is not (DateOnly or DateTime or null))
                    throw new InvalidDataException($"Tag {tag}'s key expression '{expression}' didn't produce a date.");
                var key = new byte[8];
                DateOnly? date = value switch { DateOnly d => d, DateTime t => DateOnly.FromDateTime(t), _ => null };
                BinaryPrimitives.WriteDoubleLittleEndian(key, date is { } day ? MdxKeys.JulianDay(day) : blankDate);
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
        // buffer. A full leaf splits, using the nodes the search already read on its way down.
        var (page, block, slot, _) = FindLeaf(key, upper: true);
        if (NodeCount(block) < MaxKeys)
            InsertInPlace(page, block, slot, key, recordNumber);
        else
            InsertSplitting(page, block, slot, key, recordNumber);
        MarkHasKeys();
    }

    // A full leaf splits in half, the left half keeping its page and the right going to a new one; the
    // parent gains the right half after the left (keyed by the left's new last key), and splits too if
    // it's full, up to a new root. Pages are allocated, halved and keyed exactly as dBASE's own split
    // does, so the bytes match a node-by-node rebuild.
    //
    // The writes go in an order that never loses a key to a writer killed partway: new nodes first
    // (nothing points at them yet), then the tag header if the root moved, then the changed nodes from
    // the top down, so each parent points at the new right half before its child gives those keys up.
    // A cut can leave keys in both an old node and the new right half (a scan sees them twice), never
    // in neither. Only the key being added can go missing, as with any cut-off write.
    //
    // Only valid straight after Insert's FindLeaf: it works on the path that search left behind
    // (_levels, _pathPages, _pathSlots, _leafDepth), so nothing may search or read into those buffers first.
    private void InsertSplitting(int leafPage, byte[] leaf, int slot, byte[] key, int recordNumber)
    {
        var fresh = new List<(int Page, byte[] Block)>();
        var changed = new List<(int Page, byte[] Block)>(); // bottom-up; written top-down

        var wide = new byte[(MaxKeys + 1) * ItemLength];
        var count = NodeCount(leaf);
        leaf.AsSpan(8, slot * ItemLength).CopyTo(wide);
        BinaryPrimitives.WriteInt32LittleEndian(wide.AsSpan(slot * ItemLength), recordNumber);
        key.CopyTo(wide.AsSpan(slot * ItemLength + 4, ItemLength - 4));
        leaf.AsSpan(8 + slot * ItemLength, (count - slot) * ItemLength).CopyTo(wide.AsSpan((slot + 1) * ItemLength));
        var n = count + 1;
        var mid = n / 2;
        var right = _index.AllocatePage();
        fresh.Add((right, Node(wide, mid, n - mid, lastPointer: 0)));
        changed.Add((leafPage, Node(wide, 0, mid, lastPointer: 0)));
        var separator = wide.AsSpan((mid - 1) * ItemLength + 4, KeyLength).ToArray();
        var child = leafPage;

        for (var level = _leafDepth - 1; level >= 0; level--)
        {
            var page = _pathPages![level];
            var node = _levels![level];
            count = NodeCount(node);
            var at = _pathSlots![level];
            var lastPointer = BinaryPrimitives.ReadInt32LittleEndian(node.AsSpan(8 + count * ItemLength));

            // The child keeps its slot with its new last key; the right half takes the next slot, with
            // the key the child had (or, past the last key, becomes the last pointer).
            wide = new byte[(MaxKeys + 1) * ItemLength];
            node.AsSpan(8, count * ItemLength).CopyTo(wide);
            if (at < count)
            {
                var item = wide.AsSpan(at * ItemLength, ItemLength);
                var oldKey = item.Slice(4, KeyLength).ToArray();
                wide.AsSpan(at * ItemLength, (count - at) * ItemLength).CopyTo(wide.AsSpan((at + 1) * ItemLength));
                separator.CopyTo(item[4..]);
                var next = wide.AsSpan((at + 1) * ItemLength, ItemLength);
                BinaryPrimitives.WriteInt32LittleEndian(next, right);
                oldKey.CopyTo(next[4..]);
            }
            else
            {
                var item = wide.AsSpan(count * ItemLength, ItemLength);
                BinaryPrimitives.WriteInt32LittleEndian(item, child);
                separator.CopyTo(item[4..]);
                lastPointer = right;
            }
            n = count + 1;
            if (n <= MaxKeys)
            {
                changed.Add((page, Node(wide, 0, n, lastPointer)));
                Publish(fresh, changed, newRoot: null);
                return;
            }

            // Interior key i is the last key under child i: the left half keeps children 0..mid, so
            // its last child's key becomes the separator and leaves both halves.
            mid = n / 2;
            var newRight = _index.AllocatePage();
            fresh.Add((newRight, Node(wide, mid + 1, n - mid - 1, lastPointer)));
            changed.Add((page, Node(wide, 0, mid, BinaryPrimitives.ReadInt32LittleEndian(wide.AsSpan(mid * ItemLength)))));
            separator = wide.AsSpan(mid * ItemLength + 4, KeyLength).ToArray();
            right = newRight;
            child = page;
        }

        // The root split: a new root holds the two halves.
        var root = _index.AllocatePage();
        var rootItem = new byte[ItemLength];
        BinaryPrimitives.WriteInt32LittleEndian(rootItem, child);
        separator.CopyTo(rootItem.AsSpan(4));
        fresh.Add((root, Node(rootItem, 0, 1, right)));
        Publish(fresh, changed, root);
    }

    // A node of items [from, from + n) of a run of items, laid out as WriteNode lays one out.
    private byte[] Node(byte[] items, int from, int n, int lastPointer)
    {
        var block = new byte[_index.BlockSize];
        BinaryPrimitives.WriteInt32LittleEndian(block, n);
        items.AsSpan(from * ItemLength, n * ItemLength).CopyTo(block.AsSpan(8));
        if (ItemLength > KeyLength + 4)
        {
            for (var i = 0; i < n; i++)
                block.AsSpan(8 + i * ItemLength + 4 + KeyLength, ItemLength - 4 - KeyLength).Clear();
        }
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8 + n * ItemLength), lastPointer);
        return block;
    }

    // With writes forced, each step is on disk before the next one depends on it, so a power cut keeps
    // the order a killed process does: a disk may otherwise put a shortened node down before the parent
    // that points past it. A batch writes everything at its end, in its own order, so needs none.
    private void Publish(List<(int Page, byte[] Block)> fresh, List<(int Page, byte[] Block)> changed, int? newRoot)
    {
        foreach (var (page, block) in fresh)
            _index.WriteBlock(page, block);
        _index.Barrier();
        if (newRoot is { } root)
        {
            SetRoot(root);
            _index.Barrier();
        }
        for (var i = changed.Count - 1; i >= 0; i--)
        {
            _index.WriteBlock(changed[i].Page, changed[i].Block);
            if (i > 0)
                _index.Barrier();
        }
    }

    // ---- remove ---------------------------------------------------------------------------

    private enum Search { Found, KeepLooking, Stop }

    /// <summary>True when the tag holds exactly this key for this record.</summary>
    internal bool Contains(byte[] key, int recordNumber) =>
        Find(RootPage(), key, recordNumber, [], depth: 0, visited: []) == Search.Found
        || (OtherBlankDate(key) is { } other && Find(RootPage(), other, recordNumber, [], depth: 0, visited: []) == Search.Found);

    // A blank date may be keyed either way in one tag: 0 by dBASE and Thunder.Dbf, 1E100 by Jet.
    // Finding or removing a blank record's key looks for the other form when its own isn't there.
    private byte[]? OtherBlankDate(byte[] key)
    {
        if (KeyType != 'D')
            return null;
        var zero = MdxKeys.IsZeroBlank(key);
        if (!zero && !MdxKeys.IsJetBlank(key))
            return null;
        var other = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(other, zero ? MdxKeys.JetBlankDate : 0d);
        return other;
    }

    /// <summary>
    /// How many keys the tag holds: the leaves' counts added up, without reading a key (a million
    /// keys in three tags took 1.25 s decoded). Every page is read at most once.
    /// </summary>
    internal long CountKeys()
    {
        long keys = 0;
        var visited = new HashSet<int>();
        var pages = new Stack<(int Page, int Depth)>([(RootPage(), 0)]);
        var block = new byte[_index.BlockSize];
        while (pages.TryPop(out var at))
        {
            GuardDepth(at.Depth);
            if (!visited.Add(at.Page))
                throw new InvalidDataException($"Tag {Name} reaches index page {at.Page} twice; the index is corrupt.");
            _index.ReadBlock(at.Page, block);
            var count = NodeCount(block);
            if (count < 0 || count > MaxKeys)
                throw new InvalidDataException($"Index page {at.Page} claims {count} keys; tag {Name} holds at most {MaxKeys} per node.");
            if (BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8 + count * ItemLength)) == 0)
            {
                keys += count;
                continue;
            }
            for (var i = 0; i <= count; i++)
                pages.Push((BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8 + i * ItemLength)), at.Depth + 1));
        }
        return keys;
    }

    /// <summary>Removes the entry (key, record number). Returns false when there isn't one.</summary>
    internal bool Remove(byte[] key, int recordNumber) =>
        RemoveExact(key, recordNumber) || (OtherBlankDate(key) is { } other && RemoveExact(other, recordNumber));

    private bool RemoveExact(byte[] key, int recordNumber)
    {
        var path = new List<(int Page, int Slot)>();
        if (Find(RootPage(), key, recordNumber, path, depth: 0, visited: []) != Search.Found)
            return false;

        // Remove the item from its leaf; if that empties a node (other than the root), drop the node
        // and its pointer from the parent, and carry on upward while parents empty out too. When the
        // node that's left has a new last key, the keys above it that named the old one follow it.
        for (var level = path.Count - 1; level >= 0; level--)
        {
            var (page, slot) = path[level];
            var (items, lastPointer, isLeaf) = ReadNode(page);
            byte[]? newLast = null; // the node's new last key, when its old one left
            if (isLeaf)
            {
                items.RemoveAt(slot);
                if (slot == items.Count && items.Count > 0)
                    newLast = items[^1].Key;
            }
            else if (slot < items.Count)
                items.RemoveAt(slot);
            else if (items.Count > 0)
            {
                // The last child went; the one before it becomes last, and keys for the last child aren't
                // stored. Its key was that child's last key, now the last key under this node.
                lastPointer = items[^1].Pointer;
                newLast = items[^1].Key;
                items.RemoveAt(items.Count - 1);
            }
            else
                lastPointer = 0;

            var empty = isLeaf ? items.Count == 0 : lastPointer == 0;
            if (!empty || level == 0)
            {
                WriteNode(page, items, isLeaf ? 0 : lastPointer);
                if (newLast is not null)
                    LowerKeysAbove(path, level, newLast);
                if (level == 0)
                    CollapseRoot();
                return true;
            }
            _index.FreePage(page);
        }
        return true;
    }

    // The node at path[level] now ends at newLast: the parent's key for it becomes newLast. Under a
    // parent's last pointer there's no key, but the parent's own last key changed, so on up the path.
    private void LowerKeysAbove(List<(int Page, int Slot)> path, int level, byte[] newLast)
    {
        for (var parent = level - 1; parent >= 0; parent--)
        {
            var (page, slot) = path[parent];
            var (items, lastPointer, _) = ReadNode(page);
            if (slot < items.Count)
            {
                if (items[slot].Key.AsSpan().SequenceEqual(newLast))
                    return; // a duplicate of the key that left: nothing above it changes
                items[slot] = items[slot] with { Key = newLast };
                WriteNode(page, items, lastPointer);
                return;
            }
        }
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

    /// <summary>Empties the tag: a rebuild to no keys (see <see cref="Refill"/>).</summary>
    internal void Clear() => Refill([]);

    /// <summary>
    /// Replaces every key, building the new tree in the old one's nodes. Freeing those instead puts
    /// them on a list that's never read back, so each rebuild or PACK added the tag's whole size to
    /// the file, up to the 2 GB limit. Nodes left over (fewer keys than before) are freed for
    /// programs that reuse them. When the index can't prove whose each page is, the new tree starts
    /// in new pages and the old ones are left alone.
    /// <para>
    /// A rebuild cut off partway leaves the tag half filled, in either case and as it always did (the
    /// tag was emptied first before, too): the table is held exclusively meanwhile, the next writable
    /// open finds the tag's key count out of step with the records and refuses writes, and running
    /// the rebuild again puts it right. Pages the cut-off rebuild hadn't got to yet are left unused.
    /// </para>
    /// </summary>
    internal void Refill(IReadOnlyList<(byte[] Key, int Record)> keys)
    {
        try
        {
            if (_index.PagesToReuse(this) is { } pages)
            {
                _index.OfferPages(pages); // the old root first, so the root stays where it is
                var root = _index.AllocatePage();
                WriteNode(root, [], 0);
                SetRoot(root);
            }
            else
            {
                // Pages that can't be proven this tag's own are neither reused nor freed: freeing one
                // that's already on the free list, or that another tag reaches, would loop the list
                // or hand a live node to a program that reuses it. They're left as unused space.
                var root = _index.AllocatePage();
                WriteNode(root, [], 0);
                SetRoot(root);
            }
            foreach (var (key, record) in keys)
            {
                // Judged by the tag's own comparison, so 1E2 and 100 count as one key.
                if (!IsUnique || FirstRecordFor(key) is null)
                    Insert(key, record);
                _index.RebuildPoint?.Invoke();
            }
        }
        catch
        {
            _index.TakeBackPages(); // nothing else may take these pages
            _index.ForgetRebuildPlan();
            throw;
        }
        foreach (var page in _index.TakeBackPages())
            _index.FreePage(page);
    }

    /// <summary>Every node of the tag's tree, the root first. Throws <see cref="InvalidDataException"/> for a tree that reaches a page twice.</summary>
    internal List<int> OwnPages()
    {
        var pages = new List<int>();
        Collect(RootPage(), pages, [], depth: 0);
        return pages;
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
    private int[]? _pathPages, _pathSlots; // the last search's page and child slot at each level
    private int _leafDepth; // the last search's leaf level

    /// <summary>
    /// Follows the key from the root to its leaf, the way an insert (upper: after equal keys) or
    /// the walk (upper: false, at the first equal key) would, without decoding any node. Returns the
    /// leaf's page, its block (a reused buffer), the key's slot in it, and whether it's the last leaf
    /// (every node on the way followed its last pointer). The block is only good until the next call on
    /// this tag, which reuses the buffer; like the rest of the writer, one thread per tag.
    /// </summary>
    private (int Page, byte[] Block, int Slot, bool Last) FindLeaf(byte[] key, bool upper)
    {
        _levels ??= new byte[MaxDepth + 1][];
        _pathPages ??= new int[MaxDepth + 1];
        _pathSlots ??= new int[MaxDepth + 1];
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
            // An insert equal to an interior key goes into the child that key covers, at its end, as Jet
            // finds it, unless the next child starts with the same key (equal keys stay in record order).
            if (upper && lastPointer != 0 && lo > 0 && CompareKeys(NodeKey(block, lo - 1), key) == 0
                && !StartsWith(lo < count ? BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8 + lo * ItemLength)) : lastPointer, key))
                lo--;
            _pathPages[depth] = page;
            _pathSlots[depth] = lo;
            if (lastPointer == 0)
            {
                _leafDepth = depth;
                return (page, block, lo, last);
            }
            last &= lo == count;
            page = lo < count ? BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8 + lo * ItemLength)) : lastPointer;
        }
    }

    private static int NodeCount(byte[] block) => BinaryPrimitives.ReadInt32LittleEndian(block);

    // Whether the subtree at page starts with key: down its first children to the first leaf key.
    private bool StartsWith(int page, byte[] key)
    {
        for (var depth = 0; ; depth++)
        {
            GuardDepth(depth);
            var node = _index.ReadBlock(page);
            var count = NodeCount(node);
            if (count < 0 || count > MaxKeys)
                throw new InvalidDataException($"Index page {page} claims {count} keys; tag {Name} holds at most {MaxKeys} per node.");
            var lastPointer = BinaryPrimitives.ReadInt32LittleEndian(node.AsSpan(8 + count * ItemLength));
            if (lastPointer == 0)
                return count > 0 && CompareKeys(NodeKey(node, 0), key) == 0;
            page = count > 0 ? BinaryPrimitives.ReadInt32LittleEndian(node.AsSpan(8)) : lastPointer;
        }
    }

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
