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
//
//   Every change runs under Visual FoxPro's index lock, one byte at 0x7FFFFFFE of the index file:
//
//   lock -> re-check the tag's header, re-read every page -> change -> bump the counter -> flush -> unlock
//
//   and writes its pages so a crash in the middle loses no key:
//
//   new pages  -->  the page (or header) that makes them reachable  -->  pages they replace or shrink
//                                                                        --> free what nothing uses

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;

namespace Thunder.Dbf;

public sealed partial class CdxIndex : IMaintainedIndex
{
    private const int FreeListOffset = 4;
    private const int CounterOffset = 8;
    private const uint NoPage = 0xFFFFFFFF;

    /// <summary>The largest index file FoxPro handles: 2 GB.</summary>
    private const long MaxIndexBytes = int.MaxValue;

    /// <summary>
    /// Visual FoxPro's index lock: one byte at 0x7FFFFFFE of the .cdx or .idx itself, taken exclusive
    /// for reading and writing alike. From Harbour (read, not copied): IDX_LOCKPOS_VFP is 0x7ffffffe
    /// and IDX_LOCKPOOL_VFP 0 in include/hbrdddbf.h, so hb_dbfLockIdxFile in src/rdd/dbf1.c locks
    /// that one byte; doc/locks.txt lists the [V]FP CDX read and write locks at @0x7ffffffe : 1.
    /// </summary>
    internal static readonly (long Offset, long Length) FoxProLock = (0x7FFF_FFFE, 1);

    private DbfTable? _table;
    private bool _changed;
    private DbfLocks? _fileLocks;

    // The file's size and update counter when its pages were last checked to belong to one tree each
    // (or after a change made here). Anything else means another program changed it: check again.
    private (long Length, uint Counter)? _checkedState;

    // Tags whose stored keys were already compared with the table, so reloading before every
    // write doesn't read the same records again. Keyed by everything that decides a tag's keys.
    private readonly HashSet<string> _verifiedTags = [];

    // The file's size and update counter as this process last left it, with its tags loaded and
    // checked for writing. While the file still says exactly that, no other program has changed it
    // (FoxPro, Harbour and this library bump the counter on every change, a REINDEX included), so a
    // write can skip reloading the tags, re-reading their headers and checking their layout.
    private (long Length, uint Counter)? _ourState;

    /// <summary>True during a locked change that began with the file exactly as this process left it.</summary>
    internal bool Unchanged { get; private set; }

    // One record's changes to every tag of this file, under one lock: see Hold. A batch holds it too.
    private DbfLock? _held;
    private bool _holdFailed;

    // A batch (DbfTable.DeferIndexWrites under LockTable): the index lock is held throughout, nobody else
    // can read or write the file meanwhile, and page writes collect here, by page, to be written once
    // each when it ends. Reads see them. A process killed during a batch leaves the index without the
    // batch's changes, which the open-time count check reports.
    private Dictionary<long, byte[]>? _deferred;

    // Inside a batch the cache shares its nodes instead of copying them in and out (the copies were
    // most of a batch's time). Every change that edits a node in place writes it, and a write puts
    // that node back in the cache; a change that fails partway can leave a cached node edited but
    // unwritten, which is why that keeps the batch's journal note and stops writes until a rebuild.
    internal bool Deferring => _deferred is not null;
    private long _deferredFrom; // the file's length when the batch began: pages past it are new
    private readonly HashSet<long> _deferredFreed = []; // pages the batch put on the free list

    bool IMaintainedIndex.BeginDeferredWrites()
    {
        if (!OpenedForWriting || _held is not null)
            return false;
        var held = TakeIndexLock();
        try
        {
            // Another program may have changed the file since this process last wrote: the tags are
            // reloaded first, and the pages checked against those, as the batch's first write would.
            var state = CurrentState();
            _lockedCounter = state.Counter;
            ReloadTagsUnderLock(state);
            if (MaintainRefusal is not null)
            {
                _lockedCounter = null;
                held.Dispose();
                return false; // the batch's first write refuses, with the reason, as usual
            }
            Unchanged = _ourState == state;
            _deferredFrom = FileLength();
        }
        catch
        {
            Unchanged = false;
            _lockedCounter = null;
            _nodes.Clear();
            held.Dispose();
            throw;
        }
        _held = held;
        _holdFailed = false;
        _deferred = [];
        _deferredFreed.Clear();
        return true;
    }

    private void WriteRuns(Dictionary<long, byte[]> pages, IEnumerable<long> which)
    {
        var offsets = which.Order().ToList();
        for (var start = 0; start < offsets.Count;)
        {
            var end = start + 1;
            while (end < offsets.Count && offsets[end] == offsets[end - 1] + DefaultPageLength)
                end++;
            var run = new byte[(end - start) * DefaultPageLength];
            for (var i = start; i < end; i++)
                pages[offsets[i]].CopyTo(run, (i - start) * DefaultPageLength);
            _stream.Position = offsets[start];
            _stream.Write(run);
            start = end;
        }
    }

    bool IMaintainedIndex.FinishDeferredWrites(bool toDisk)
    {
        if (_deferred is not { } pages)
            return true;
        _deferred = null;
        var held = _held!;
        _held = null;
        try
        {
            // New pages first (nothing on disk points at them yet), then, once they're out, the pages
            // that existed (some now pointing at new ones), and the file header (the free list) last.
            // Contiguous pages go in one write. A batch cut off in the middle is still reported by
            // its note in the table's journal; this order keeps what's on disk a tree meanwhile.
            var freed = _deferredFreed;
            WriteRuns(pages, pages.Keys.Where(o => o >= _deferredFrom && !freed.Contains(o)));
            if (pages.Keys.Any(o => o >= _deferredFrom))
                DbfTable.FlushToDisk(_stream, toDisk);
            WriteRuns(pages, pages.Keys.Where(o => o < _deferredFrom && o >= DefaultPageLength * 2 && !freed.Contains(o)));
            WriteRuns(pages, pages.Keys.Where(o => freed.Contains(o) && o >= DefaultPageLength * 2)); // freed once nothing points at them
            WriteRuns(pages, pages.Keys.Where(o => o < DefaultPageLength * 2));
            var clean = !_holdFailed;
            Publish(succeeded: clean);
            DbfTable.FlushToDisk(_stream, toDisk);
            return clean;
        }
        catch
        {
            _ourState = null;
            _nodes.Clear();
            throw;
        }
        finally
        {
            Unchanged = false;
            _lockedCounter = null;
            held.Dispose();
        }
    }

    // Tree nodes as this process last read or wrote them, by page offset, with the tag they belong to.
    // Used only while Unchanged: inside a locked change that began with the file exactly as this
    // process left it, so no other program has written a page since they were cached. Every page
    // write here drops the copy of that page (WriteNode then caches what it wrote); anything else
    // (another program's change, a failed change, a reload, a change outside a locked one) empties it.
    private readonly Dictionary<long, (CdxTag Owner, object Node)> _nodes = [];
    private const int MaxCachedNodes = 2048;

    internal object? CachedNode(long offset, CdxTag owner) =>
        Unchanged && _nodes.TryGetValue(offset, out var hit) && hit.Owner == owner ? hit.Node : null;

    internal void CacheNode(long offset, CdxTag owner, object node)
    {
        if (!Unchanged)
            return;
        if (_nodes.Count >= MaxCachedNodes)
            _nodes.Clear();
        _nodes[offset] = (owner, node);
    }

    // The update counter while this process holds the index lock: nobody else can change it then,
    // so it's bumped from here rather than read back first.
    private uint? _lockedCounter;

    // Taking the index lock: the file's state, and whether it's exactly as this process left it.
    private (long Length, uint Counter) BeginLockedChange()
    {
        var state = CurrentState();
        _lockedCounter = state.Counter;
        if (_checkedState != state)
            CheckPageOwnership();
        Unchanged = _ourState == state;
        if (!Unchanged)
            _nodes.Clear();
        return state;
    }

    /// <summary>The table the index was opened with, whose records its keys are built from.</summary>
    internal DbfTable? Table => _table;

    // A writable table opens its index files by path, read-write, and passes itself along. Other
    // callers (a MemoryStream to read, say) aren't held to the checks writing needs; if they do
    // write through the index, each tag is still checked before its first key.
    private bool OpenedForWriting => _stream is FileStream { CanWrite: true } && _table?.FilePath is not null;

    IEnumerable<IMaintainedTag> IMaintainedIndex.MaintainedTags => Tags;
    bool IMaintainedIndex.CanMaintain => MaintainRefusal is null;
    bool IMaintainedIndex.CanMaintainAfterRebuild => MaintainRefusal is null || _refusedOnlyForOrder;
    string? IMaintainedIndex.CannotMaintainReason => MaintainRefusal;
    // Refused only for what a rebuild in byte order replaces anyway: keys out of order, or out of step
    // with the table. MaintainIndex(path, rebuild: true) takes such an index over.
    private bool _refusedOnlyForOrder;
    private bool _rebuildExpected;

    void IMaintainedIndex.ExpectRebuild() => _rebuildExpected = true;
    void IMaintainedIndex.RebuildAbandoned() => _rebuildExpected = false;
    void IMaintainedIndex.ReloadTags() => ReloadTags();
    void IMaintainedIndex.Commit(bool toDisk) => Commit(toDisk);

    /// <summary>
    /// Takes the index lock for all of one record's tag changes in this file, and checks the file once:
    /// its pages (if another program changed it) and whether it's as this process left it. The counter
    /// is bumped and the changes pushed out once, when the hold is disposed.
    /// </summary>
    IDisposable? IMaintainedIndex.Hold()
    {
        if (!OpenedForWriting || _held is not null)
            return null;
        var held = TakeIndexLock();
        try
        {
            BeginLockedChange();
        }
        catch
        {
            Unchanged = false;
            _lockedCounter = null;
            _nodes.Clear();
            held.Dispose();
            throw;
        }
        _held = held;
        _holdFailed = false;
        return new Hold(this);
    }

    private sealed class Hold(CdxIndex index) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;
            _released = true;
            index.Release();
        }
    }

    private void Release()
    {
        var held = _held!;
        _held = null;
        try
        {
            Publish(succeeded: !_holdFailed);
        }
        finally
        {
            held.Dispose();
        }
    }

    // After a locked change: bump the counter again (it moved before the first page too, see Write)
    // and push the change out. The second bump is for readers that walk without the lock, this
    // library's own open-time check among them: a walk that met the change half done sees the
    // counter move again, and walks once more. Half a change (a damaged page met midway) is still published, but then neither the
    // pages nor the layout count as checked any more.
    private void Publish(bool succeeded)
    {
        try
        {
            if (_changed)
                BumpCounter();
            _stream.Flush();
            if (succeeded)
            {
                // Under the lock the length and counter are this process's own: no need to read them back.
                _checkedState = _lockedCounter is { } counter ? (FileLength(), counter) : CurrentState(); // this change left every page with one owner
                _ourState = Unchanged ? _checkedState : null;
            }
            else
            {
                _ourState = null;
                _nodes.Clear();
            }
        }
        catch
        {
            _ourState = null;
            _nodes.Clear();
            throw;
        }
        finally
        {
            Unchanged = false;
            _lockedCounter = null;
        }
    }

    /// <summary>
    /// Why this index can't be kept exactly as FoxPro would keep it, or null when it can (or wasn't
    /// opened for writing). The table then refuses writes rather than attach it.
    /// </summary>
    internal string? MaintainRefusal { get; private set; }

    /// <summary>
    /// Opens the index read-only and works out whether a writable table could keep it up to date:
    /// the reason it couldn't, or null. For <see cref="DbfDatabase.Check"/>, which mustn't promise
    /// what writes would refuse.
    /// </summary>
    internal static string? WhyNotMaintainable(string path, DbfTable table)
    {
        using var index = Open(path, table.Encoding, table);
        index.PrepareForWriting();
        return index.MaintainRefusal;
    }

    // Opening for writing: an index that can't be kept exactly as FoxPro would keep it is refused as
    // a whole, so the table treats it like any other index it can't maintain (writes are blocked,
    // unless the caller allowed unmaintained indexes and will REINDEX in FoxPro). Pages shared by two
    // trees, or free yet still in a tree, make it damaged: InvalidDataException, so it isn't attached.
    private void PrepareForWriting()
    {
        _refusedOnlyForOrder = false;
        if (OwnedByAnotherTable())
        {
            MaintainRefusal = "This index is named after another table next to it, so it isn't this table's to update.";
            return;
        }
        Check();
    }

    // The check can do without a lock it can't get quickly (the first write checks again), so it
    // waits at most this long for each, whatever the table's lock timeout.
    private TimeSpan CheckLockWait => _table is { } table && table.Locks.Timeout < MaxCheckLockWait ? table.Locks.Timeout : MaxCheckLockWait;
    private static readonly TimeSpan MaxCheckLockWait = TimeSpan.FromMilliseconds(250);

    // The whole tree is first walked without holding Visual FoxPro's index lock, which would stop
    // every VFP user for as long as a big index takes to read. Then, briefly under the table's
    // header lock (VFP appends under it, record, count and keys together) and the index lock, the
    // update counter and the record count must be what they were before the walk; otherwise
    // another program changed something meanwhile. On a table that's never quiet that could go on
    // for ever, so after two such tries the walk itself runs under both locks: other users wait
    // for it, as they wait for any write. Keys out of step with the table get a moment to settle.
    private void Check()
    {
        for (var attempt = 1; ; attempt++)
        {
            _outOfStep = false;
            var underLock = attempt > OptimisticAttempts && OpenedForWriting;
            string? refusal = null;
            ExceptionDispatchInfo? damage = null;
            bool settled, locked;

            void Walk()
            {
                try
                {
                    CheckPageOwnership();
                    refusal = FirstRefusal();
                }
                catch (InvalidDataException ex)
                {
                    damage = ExceptionDispatchInfo.Capture(ex);
                }
            }

            if (underLock)
            {
                using var header = TryTakeTableHeaderLock(out var headerBusy);
                using var held = headerBusy ? null : TryTakeIndexLock();
                settled = !headerBusy && held is not null;
                locked = settled;
                if (settled)
                {
                    ReloadRecordCount();
                    Walk();
                }
            }
            else
            {
                var before = CurrentState();
                var records = ReloadRecordCount();
                Walk();
                using var header = TryTakeTableHeaderLock(out var headerBusy);
                using var held = headerBusy ? null : TryTakeIndexLock();
                locked = held is not null && (header is not null || !OpenedForWriting);
                settled = !headerBusy && CurrentState() == before && ReloadRecordCount() == records;
            }

            if (!settled)
            {
                _verifiedTags.Clear();
                if (attempt < OutOfStepAttempts)
                    continue;
                // Never quiet, and the locks never free for long enough: other users keep them busy.
                // A table opened for writing is taken on trust for now; its first write, which must
                // hold the header and index locks anyway, checks every tag in full first (nothing is
                // in _verifiedTags), and refuses then if anything's wrong.
                MaintainRefusal = OpenedForWriting
                    ? null
                    : "the index kept changing while it was being checked (another program is busy writing to the table); check again when it's quieter.";
                return;
            }
            damage?.Throw();
            // Without the lock, a change in progress at both readings goes unseen, so a refusal may
            // be that change rather than the index (Check's read-only probe, where a lock isn't allowed).
            MaintainRefusal = refusal is not null && !locked
                ? refusal + " (checked without Visual FoxPro's index lock, so another program's write in progress could also explain it)"
                : refusal;
            if (refusal is null || !_outOfStep || attempt == OutOfStepAttempts)
                return;
            Thread.Sleep(OutOfStepPause);
        }
    }

    private const int OptimisticAttempts = 2;

    private int ReloadRecordCount()
    {
        _table?.ReloadHeader();
        return _table?.RecordCount ?? 0;
    }

    // The table's header lock, for the moment the check confirms its result. Busy (another program
    // appending, or holding the whole table) means try again; a handle that can't lock at all (Check's
    // read-only probe on some platforms) goes without.
    private DbfLock? TryTakeTableHeaderLock(out bool busy)
    {
        busy = false;
        if (_table is null || _table.FilePath is null)
            return null;
        try
        {
            return _table.Locks.Header(CheckLockWait);
        }
        catch (DbfException ex) when (ex.Code is DbfErrorCode.TableLocked)
        {
            busy = true;
            return null;
        }
        catch (Exception ex) when (!OpenedForWriting && ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or DbfException { Code: DbfErrorCode.LockingUnsupported })
        {
            return null;
        }
    }

    // The index lock where this handle can take it; a read-only handle (Check's probe) may not be
    // allowed a lock on every platform, and then relies on the counter alone.
    private DbfLock? TryTakeIndexLock()
    {
        try
        {
            return TakeIndexLock(CheckLockWait);
        }
        catch (Exception ex) when (!OpenedForWriting && ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or DbfException { Code: DbfErrorCode.LockingUnsupported })
        {
            return null;
        }
    }

    private const int OutOfStepAttempts = 5;
    private static readonly TimeSpan OutOfStepPause = TimeSpan.FromMilliseconds(200);
    private bool _outOfStep; // the last refusal was keys out of step with the table, which a write in progress can explain

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
            if (tag.CheckWholeOrder(out var entries) is { } disorder)
            {
                // Remember whether order is the only problem: MaintainIndex(path, rebuild: true) can fix that.
                _refusedOnlyForOrder = Tags.All(t => t.PrepareForWriting(_table!) is null);
                return $"Tag {tag.Name} can't be kept up to date: {disorder}.";
            }
            // A tag with no FOR filter and no UNIQUE holds one key per record, deleted ones included.
            // Any other count is a write that reached the table and not the index (a writer killed
            // between the two), which a sample of the first and last keys can miss.
            var mismatch = tag.ForExpression is null && !tag.IsUnique && entries != _table!.RecordCount
                ? $"it holds {entries:N0} keys for {_table.RecordCount:N0} records, so it isn't in step with the table (a write reached the table but not the index)"
                : tag.MissingLastKeys(_table!) ?? tag.CompareSampleWithTable(_table!);
            if (mismatch is not null)
            {
                _outOfStep = true;
                // Keys out of step with the table (another program wrote without updating the index, or
                // an update failed) are what a rebuild replaces, so a takeover with a rebuild can fix it too.
                _refusedOnlyForOrder = Tags.All(t => t.PrepareForWriting(_table!) is null);
                return $"Tag {tag.Name} can't be kept up to date: {mismatch}.";
            }
            _verifiedTags.Add(identity);
        }
        return null;
    }

    /// <summary>Re-reads the tag directory and headers, in case FoxPro changed them (a REINDEX, a new tag).</summary>
    internal void ReloadTags()
    {
        if (!OpenedForWriting)
        {
            Tags = LoadTags();
            return;
        }

        // Inside a batch (deferred writes) this process holds the index lock: nobody else can have changed it.
        if (_held is not null)
            return;

        using (TakeIndexLock())
            ReloadTagsUnderLock(CurrentState());
        if (MaintainRefusal is { } reason && !(_rebuildExpected && _refusedOnlyForOrder))
            throw new DbfException(DbfErrorCode.IndexWouldGoStale, $"{reason} Writing would leave the FoxPro index out of date.",
                "Rebuild the tag in FoxPro with MACHINE collation, or open with allowUnmaintainedIndexes: true only if it will be rebuilt (REINDEX) before it's used.",
                _table?.FilePath);
    }

    private void ReloadTagsUnderLock((long Length, uint Counter) state)
    {
        // Nobody changed the file since this process loaded and checked its tags: nothing to reload.
        if (_ourState == state && MaintainRefusal is null)
            return;
        _ourState = null;
        _nodes.Clear();
        try
        {
            Tags = LoadTags();
            if (_checkedState != state)
                CheckPageOwnership();
        }
        catch (InvalidDataException ex)
        {
            throw new DbfException(DbfErrorCode.IndexDamaged, $"The FoxPro index is damaged: {ex.Message}",
                "REINDEX it in FoxPro (or restore it from a backup) before writing to this table again.", _table?.FilePath, ex);
        }

        // Recomputed on every reload, so an index rebuilt into byte order is writable again, and one
        // FoxPro rebuilt with a collation stops writes.
        _refusedOnlyForOrder = false;
        MaintainRefusal = FirstRefusal();
        if (MaintainRefusal is null)
            _ourState = state;
    }

    private DbfLock TakeIndexLock(TimeSpan? timeout = null)
    {
        var locks = _fileLocks ??= new DbfLocks(_stream as FileStream, (_stream as FileStream)?.Name);
        if (_table is not null)
        {
            locks.Timeout = _table.Locks.Timeout;
            locks.RequireLocks = _table.Locks.RequireLocks;
        }
        return locks.Range(FoxProLock, DbfErrorCode.TableLocked, "Another program is updating this table's FoxPro index.", timeout);
    }

    /// <summary>
    /// Runs one tree operation under Visual FoxPro's index lock: the tag's header is re-checked and
    /// every page re-read inside it (nothing read before the lock is trusted after it), and the update
    /// counter is bumped and the change flushed before it's released, so another program sees either
    /// none of it or all of it.
    /// </summary>
    internal T Update<T>(CdxTag tag, Func<T> change)
    {
        if (_held is not null)
        {
            // Inside a hold: the lock is taken and the file checked; Release publishes.
            try
            {
                if (!Unchanged)
                    tag.CheckLayoutUnchanged();
                return change();
            }
            catch
            {
                _holdFailed = true;
                throw;
            }
        }

        using var held = TakeIndexLock();
        var succeeded = false;
        try
        {
            BeginLockedChange();
            if (!Unchanged)
                tag.CheckLayoutUnchanged();
            var result = change();
            succeeded = true;
            return result;
        }
        finally
        {
            Publish(succeeded);
        }
    }

    /// <summary>Bumps the update counter if anything changed (so other programs drop their cached pages) and pushes everything to disk.</summary>
    internal void Commit(bool toDisk = true)
    {
        if (_deferred is not null)
            return; // a batch publishes when it ends
        _rebuildExpected = false; // a commit follows the rebuild; from here on order is checked again
        if (_changed)
        {
            // Changes made outside a locked change (creating a tag): the tags are reloaded and checked before the next write.
            _ourState = null;
            _nodes.Clear();
            BumpCounter();
        }
        DbfTable.FlushToDisk(_stream, toDisk);
    }

    // Harbour keeps this big-endian counter in the file's first header and compares it to spot
    // changes made by other processes; FoxPro's documentation calls it reserved.
    private void BumpCounter()
    {
        Span<byte> counter = stackalloc byte[4];
        uint next;
        if (_lockedCounter is { } known)
        {
            next = unchecked(known + 1);
        }
        else
        {
            DbfTable.DiscardReadBuffer(_stream);
            _stream.Position = CounterOffset;
            _stream.ReadExactly(counter);
            next = unchecked(BinaryPrimitives.ReadUInt32BigEndian(counter) + 1);
        }
        if (_lockedCounter is not null)
            _lockedCounter = next;
        if (_deferred is not null && _deferred.TryGetValue(0, out var first))
            BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(CounterOffset), next); // so writing it back keeps the new counter
        BinaryPrimitives.WriteUInt32BigEndian(counter, next);
        _stream.Position = CounterOffset;
        _stream.Write(counter);
        _changed = false;
    }

    private (long Length, uint Counter) CurrentState() =>
        (FileLength(fresh: true), BinaryPrimitives.ReadUInt32BigEndian(Read(0, DefaultPageLength).AsSpan(CounterOffset)));

    /// <summary>
    /// Checks that every page has one owner: the file's headers, the tag directory, one tag's tree,
    /// or the free list. A page two trees share, a sibling link that doesn't point back, or a free
    /// page a tree still uses would make the next change overwrite somebody else's keys, so any of
    /// them makes the index damaged rather than maintainable.
    /// </summary>
    internal void CheckPageOwnership()
    {
        FileLength(fresh: true); // another program may have grown the file and freed pages past where it ended
        var owners = new Dictionary<long, string>();
        void Own(long page, string owner)
        {
            if (!owners.TryAdd(page, owner))
                throw new InvalidDataException($"The page at offset {page} belongs to both {owners[page]} and {owner}; the index is damaged.");
        }

        Own(0, "the file header");
        Own(DefaultPageLength, "the file header");
        if (IsCompound)
        {
            CdxTag.LoadDirectory(this).ClaimPages(Own, "the tag directory");
            foreach (var tag in Tags)
            {
                Own(tag.HeaderOffset, $"tag {tag.Name}'s header");
                Own(tag.HeaderOffset + DefaultPageLength, $"tag {tag.Name}'s header");
            }
        }
        foreach (var tag in Tags)
            tag.ClaimPages(Own, $"tag {tag.Name}");

        var free = BinaryPrimitives.ReadUInt32LittleEndian(Read(0, DefaultPageLength).AsSpan(FreeListOffset));
        var listed = new HashSet<long>();
        while (free is not 0 and not NoPage)
        {
            if (free % DefaultPageLength != 0 || free + PageLength > FileLength() || !listed.Add(free))
                throw new InvalidDataException($"The free page list leads to offset {free}, which isn't a page of the file it can hold; the index is damaged.");
            if (owners.TryGetValue(free, out var owner))
                throw new InvalidDataException($"The free page list holds the page at offset {free}, which {owner} still uses; the index is damaged.");
            free = BinaryPrimitives.ReadUInt32LittleEndian(Read(free, 4));
        }
        _checkedState = CurrentState();
    }

    /// <summary>
    /// Makes every page written so far reach the disk before the next write: new pages before the
    /// page that starts pointing at them, and a page's replacement before its old copy is freed.
    /// </summary>
    internal void Barrier() => DbfTable.FlushToDisk(_stream, _table?.ForcesWrites ?? true);

    internal void Write(long offset, ReadOnlySpan<byte> bytes)
    {
        if (offset < 0 || offset % DefaultPageLength != 0 || (offset + bytes.Length > FileLength() && offset + bytes.Length > FileLength(fresh: true)))
            throw new InvalidOperationException($"Can't write the .cdx page at offset {offset}.");
        if (!_changed)
        {
            // The counter moves before the first page does, and again when the change is published:
            // a writer killed in between still leaves other programs a counter that says "reload".
            BumpCounter();
            _stream.Flush();
        }
        for (var page = offset - offset % DefaultPageLength; page < offset + bytes.Length; page += DefaultPageLength)
            _nodes.Remove(page);
        if (_deferred is { } deferred)
        {
            // Writes are whole pages (a tag header is two), so each is kept page by page.
            if (offset % DefaultPageLength != 0 || bytes.Length % DefaultPageLength != 0)
                throw new InvalidOperationException($"A batch keeps whole .cdx pages; a {bytes.Length}-byte write at offset {offset} isn't one.");
            for (var at = 0; at < bytes.Length; at += DefaultPageLength)
                deferred[offset + at] = bytes.Slice(at, DefaultPageLength).ToArray();
            _changed = true;
            return;
        }
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
        var offset = (FileLength(fresh: true) + PageLength - 1) / PageLength * PageLength;
        if (offset + PageLength > MaxIndexBytes)
            throw new InvalidDataException("The .cdx index has reached the 2 GB FoxPro can handle.");
        _stream.SetLength(offset + PageLength);
        _knownLength = offset + PageLength;
        Write(offset, new byte[PageLength]);
        return offset;
    }

    /// <summary>Puts a page at the head of the free list, whose head lives at offset 4 of the file's first header.</summary>
    internal void FreePage(long offset)
    {
        // Like Harbour: a free page holds only the link to the next one, in its first four bytes.
        if (_deferred is not null)
            _deferredFreed.Add(offset);
        var page = new byte[PageLength];
        Read(0, DefaultPageLength).AsSpan(FreeListOffset, 4).CopyTo(page);
        Write(offset, page);

        var header = Read(0, DefaultPageLength); // read after that write, which may have moved the counter
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(FreeListOffset), checked((uint)offset));
        Write(0, header);
    }
}

public sealed partial class CdxTag : IMaintainedTag
{
    private const int MaxWritableKeyLength = 240; // FoxPro's limit, and what keeps the count fields in 8 bits
    private const int SampleSize = 32;

    private enum KeyKind { Character, Double, Date, Logical, Integer, Currency, DateTime }

    // What a character key is made of: bare character fields, UPPER() of one, DTOS() of a date field
    // and STR() of a whole-number field, joined with +. Each is built from the record's stored bytes,
    // exactly as FoxPro does; anything else isn't reproduced byte for byte, so it's refused.
    private enum PartKind { Field, Upper, Dtos, Str }

    private readonly record struct KeyPart(PartKind Kind, DbfField Field, int FieldIndex, int Length);

    private static readonly Regex PartPattern = new(
        @"^\s*(?:(?<function>UPPER|DTOS|STR)\s*\(\s*(?<field>[A-Z_][A-Z0-9_]*)\s*(?:,\s*(?<length>\d+)\s*(?:,\s*(?<decimals>\d+)\s*)?)?\)|(?<field>[A-Z_][A-Z0-9_]*))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private DbfExpression? _keyExpression;
    private DbfExpression? _forExpression;
    private KeyKind _kind;
    private List<KeyPart>? _parts;
    private bool _prepared;

    bool IMaintainedTag.IsUnique => IsUnique;
    bool IMaintainedTag.RefusesDuplicates => IsCandidate;
    bool IMaintainedTag.UniqueKeepsFirstIndexed => true; // Visual FoxPro: only REINDEX picks the lowest record
    bool IMaintainedTag.Includes(DbfRecord record) => Includes(record);
    byte[] IMaintainedTag.KeyFor(DbfRecord record) => KeyBytes(record);
    void IMaintainedTag.Insert(byte[] key, int recordNumber) => _index.Update(this, () => { Insert(key, recordNumber); return true; });
    bool IMaintainedTag.Remove(byte[] key, int recordNumber) => _index.Update(this, () => Remove(key, recordNumber));
    bool IMaintainedTag.Contains(byte[] key, int recordNumber) => _index.Update(this, () =>
    {
        var root = ReadNode(RootOffset(), inByteOrder: true);
        return Find(root, key, recordNumber, [], depth: 0, visited: [root.Offset]) == Search.Found;
    });
    int? IMaintainedTag.FirstRecordFor(byte[] key) => _index.Update(this, () => FirstRecordFor(key));
    void IMaintainedTag.Clear() => _index.Update(this, () => { Clear(); return true; });
    void IMaintainedTag.ReplaceAll(IReadOnlyList<(byte[] Key, int Record)> keys) => _index.Update(this, () => { ReplaceAll(keys); return true; });

    /// <summary>The header bytes that decide how the tag's keys are built and sorted, to spot a REINDEX by another program.</summary>
    internal byte[] Layout { get; private init; } = [];

    internal static byte[] LayoutOf(byte[] header) =>
        [.. header.AsSpan(12, 4), .. header.AsSpan(92, 26), .. header.AsSpan(494, 6), .. header.AsSpan(502, 10), .. header.AsSpan(512, 512)];

    // Under the index lock, before a change: the tag must still be the one its keys were planned for.
    internal void CheckLayoutUnchanged()
    {
        if (_index.Unchanged)
            return;
        if (!LayoutOf(_index.Read(HeaderOffset, CdxIndex.HeaderLength)).AsSpan().SequenceEqual(Layout))
            throw new InvalidDataException($"Tag {Name} was rebuilt by another program while this write was under way.");
    }

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
                // Visual FoxPro's keys over a currency or datetime field, and the null marker byte in front of
                // a key over a nullable field, are reproduced for a bare field (checked against tables VFP 9
                // wrote); in anything more, guessing would mislead FoxPro.
                var bare = BareField(table) is { } only && only.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase);
                if (field.IsNullable && !(bare && field.Type == 'C'))
                    return $"its expression uses the nullable field {field.Name} in a way whose key (with its null marker) isn't reproduced here; only a bare nullable character field is";
                if (field.Type is 'T' or 'Y' && !bare)
                    return $"its expression uses the {(field.Type == 'T' ? "datetime" : "currency")} field {field.Name} in more than a bare field, whose key isn't reproduced here";
            }

            // In a double-byte code page FoxPro's string functions count bytes, and .NET's count
            // characters, so a filter over text could let different records in.
            if (!_index.Encoding.IsSingleByte && ForExpression is not null
                && DbfExpression.FieldNamesIn(ForExpression).Any(n => table.Fields.First(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Type is 'C' or 'V' or 'M'))
                return $"its FOR filter works on text in the double-byte code page {_index.Encoding.CodePage}, where FoxPro's byte-counting string functions aren't reproduced";
            blank = _keyExpression.Evaluate(IndexKeys.BlankRecord(table));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or KeyNotFoundException
                                       or FormatException or ArgumentException or OverflowException)
        {
            return $"its expression can't be evaluated here ({ex.Message})";
        }

        var bareType = BareField(table)?.Type;
        KeyKind? kind = blank switch
        {
            _ when bareType == 'Y' && KeyLength == 8 => KeyKind.Currency,
            _ when bareType == 'T' && KeyLength == 8 => KeyKind.DateTime,
            string when KeyType == 'C' => KeyKind.Character,
            decimal or double when KeyLength == 8 => KeyKind.Double,
            decimal or double when KeyLength == 4 && BareField(table) is { Type: 'I' } => KeyKind.Integer,
            DateOnly or null when KeyLength == 8 => KeyKind.Date,
            bool when KeyLength == 1 => KeyKind.Logical,
            _ => null,
        };
        if (kind is null)
            return $"its {KeyLength}-byte keys don't match what its expression returns ({blank?.GetType().Name ?? "a date"})";

        // FoxPro works numeric expressions out in binary floating point and this library in decimal,
        // so anything past reading one field (a sum, a VAL(), a ROUND()) can come out a different
        // double. A bare field is read from its text once, correctly rounded, by both.
        if (kind is KeyKind.Double or KeyKind.Integer && BareField(table) is not { Type: 'N' or 'F' or 'B' or 'I' or 'Y' })
            return $"its numeric key '{Expression}' is more than a bare numeric field, and FoxPro works such expressions out in binary floating point, which isn't reproduced exactly here";

        _parts = null;
        if (kind == KeyKind.Character)
        {
            var (parts, reason) = CharacterParts(table);
            if (reason is not null)
                return reason;
            _parts = parts;
        }
        _kind = kind.Value;
        _prepared = true;
        return null;
    }

    private DbfField? BareField(DbfTable table) =>
        table.Fields.FirstOrDefault(f => string.Equals(f.Name, Expression.Trim(), StringComparison.OrdinalIgnoreCase));

    // Splits a character key expression into parts built from stored bytes, or says why it can't be.
    private (List<KeyPart>? Parts, string? Reason) CharacterParts(DbfTable table)
    {
        const string Unsupported = "its key expression '{0}' does more than join character fields, UPPER(), DTOS() and STR() of fields with +, and FoxPro's byte-level results for the rest aren't reproduced exactly";
        var parts = new List<KeyPart>();
        foreach (var piece in Expression.Split('+'))
        {
            var match = PartPattern.Match(piece);
            if (!match.Success)
                return (null, string.Format(CultureInfo.InvariantCulture, Unsupported, Expression));
            var fieldIndex = -1;
            for (var i = 0; i < table.Fields.Count && fieldIndex < 0; i++)
            {
                if (table.Fields[i].Name.Equals(match.Groups["field"].Value, StringComparison.OrdinalIgnoreCase))
                    fieldIndex = i;
            }
            if (fieldIndex < 0)
                return (null, $"its expression uses '{match.Groups["field"].Value}', which isn't a field of the table");
            var field = table.Fields[fieldIndex];
            var function = match.Groups["function"].Success ? match.Groups["function"].Value.ToUpperInvariant() : null;
            var hasLength = match.Groups["length"].Success;

            KeyPart? part = function switch
            {
                null when field.Type == 'C' => new(PartKind.Field, field, fieldIndex, field.Length),
                // FoxPro upper-cases by its own tables for each code page; only ASCII letters are
                // certain, and in a double-byte code page a trailing byte can look like one.
                "UPPER" when field.Type == 'C' && !hasLength && _index.Encoding.IsSingleByte => new(PartKind.Upper, field, fieldIndex, field.Length),
                "DTOS" when field.Type == 'D' && !hasLength => new(PartKind.Dtos, field, fieldIndex, 8),
                // A whole number can't round: STR() of one is its digits, right-aligned, as long as
                // the width holds every value the field can.
                "STR" when (field.Type == 'I' || (field.Type is 'N' or 'F' && field.DecimalCount == 0))
                           && (!match.Groups["decimals"].Success || match.Groups["decimals"].Value.TrimStart('0').Length == 0)
                           && (hasLength ? int.Parse(match.Groups["length"].Value, CultureInfo.InvariantCulture) : 10) is var width
                           && width >= (field.Type == 'I' ? 11 : field.Length) && width <= MaxWritableKeyLength
                    => new(PartKind.Str, field, fieldIndex, width),
                _ => null,
            };
            if (part is null)
                return (null, string.Format(CultureInfo.InvariantCulture, Unsupported, Expression)
                    + (_index.Encoding.IsSingleByte ? "" : $" (the table's code page {_index.Encoding.CodePage} is double-byte)"));
            parts.Add(part.Value);
        }
        // A key over a nullable field starts with Visual FoxPro's null marker byte.
        var total = parts.Sum(p => p.Length) + (parts is [{ Field.IsNullable: true }] ? 1 : 0);
        if (total != KeyLength)
            return (null, $"its {KeyLength}-byte keys don't match the {total} bytes its expression gives");
        return (parts, null);
    }

    /// <summary>
    /// Appends only ever add records at the end, so an append cut off between the record and its keys
    /// leaves the last records without them, which a count can't show in a FOR tag. UNIQUE tags are
    /// left out: Visual FoxPro itself can drop a key from one between rebuilds.
    /// </summary>
    internal string? MissingLastKeys(DbfTable table)
    {
        if (IsUnique)
            return null;
        for (var index = Math.Max(0, table.RecordCount - 2); index < table.RecordCount; index++)
        {
            var row = table.Read(index);
            if (!Includes(row))
                continue;
            var root = ReadNode(RootOffset(), inByteOrder: true);
            if (Find(root, KeyBytes(row), index + 1, [], depth: 0, visited: [root.Offset]) != Search.Found)
                return $"record {index + 1}, near the end of the table, has no key in it, so a write reached the table but not the index";
        }
        return null;
    }

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

    /// <summary>
    /// Walks the whole tree, root to leaves, and checks every page's keys are in FoxPro's MACHINE
    /// (byte) order, and the leaves' keys across pages too. A tag sorted by a collation (X# sorts some
    /// case-blind without saying so) would be scrambled by byte-order changes, so it's refused before
    /// the first write. Disorder anywhere, the root included, is reported here as a sort order a
    /// rebuild fixes, not as damage.
    /// </summary>
    internal string? CheckWholeOrder() => CheckWholeOrder(out _);

    /// <summary>The same walk, also counting the entries in the leaves.</summary>
    internal string? CheckWholeOrder(out long entries)
    {
        long counted = 0;
        const string Disorder = "its keys aren't in FoxPro's MACHINE byte order, so it was built with a collation this code doesn't reproduce (rebuild it with RebuildIndex, or REINDEX with SET COLLATE TO \"MACHINE\" in FoxPro)";
        byte[]? previous = null;
        var ordered = true;
        void Visit(long offset, int depth, HashSet<long> visited)
        {
            GuardDepth(depth);
            if (!visited.Add(offset))
                throw new InvalidDataException($"Tag {Name} reaches the index page at offset {offset} twice; the index is corrupt.");
            var node = ReadNode(offset);
            for (var i = 1; i < node.Count && ordered; i++)
                ordered = node.Keys[i - 1].AsSpan().SequenceCompareTo(node.Keys[i]) <= 0;
            if (node.IsLeaf)
            {
                counted += node.Count;
                if (node.Count > 0)
                {
                    ordered &= previous is null || previous.AsSpan().SequenceCompareTo(node.Keys[0]) <= 0;
                    previous = node.Keys[^1];
                }
                return;
            }
            foreach (var child in node.Children!)
            {
                if (!ordered)
                    return;
                Visit(child, depth + 1, visited);
            }
        }
        Visit(RootOffset(), 0, []);
        entries = counted;
        return ordered ? null : Disorder;
    }

    /// <summary>
    /// Hands every page of the tree to <paramref name="own"/> (which refuses a page someone else owns)
    /// and checks each level's pages link to their neighbours both ways, the first with no left
    /// neighbour and the last with no right one.
    /// </summary>
    internal void ClaimPages(Action<long, string> own, string owner)
    {
        var previous = new List<(long Offset, uint Right)>(); // the last page met on each level
        void Visit(long offset, int depth)
        {
            GuardDepth(depth);
            own(offset, owner);
            var node = ReadNode(offset);
            if (depth == previous.Count)
            {
                if (node.Left != NoPage)
                    throw new InvalidDataException($"{Label}'s first page on level {depth} (offset {offset}) links left to {node.Left}; the index is damaged.");
                previous.Add((offset, node.Right));
            }
            else
            {
                var (left, right) = previous[depth];
                if (right != offset || node.Left != left)
                    throw new InvalidDataException($"{Label}'s pages at offsets {left} and {offset} sit side by side but don't link to each other both ways; the index is damaged.");
                previous[depth] = (offset, node.Right);
            }
            if (!node.IsLeaf)
            {
                foreach (var child in node.Children!)
                    Visit(child, depth + 1);
            }
        }
        Visit(RootOffset(), 0);
        for (var depth = 0; depth < previous.Count; depth++)
        {
            if (previous[depth].Right != NoPage)
                throw new InvalidDataException($"{Label}'s last page on level {depth} (offset {previous[depth].Offset}) links right to {previous[depth].Right}; the index is damaged.");
        }
    }

    private string Label => Name.Length == 0 ? "The tag directory" : $"Tag {Name}";

    private Node EdgeLeaf(bool last)
    {
        var node = ReadNode(RootOffset());
        var visited = new HashSet<long> { node.Offset };
        for (var depth = 0; !node.IsLeaf; depth++)
        {
            GuardDepth(depth);
            var child = node.Children![last ? ^1 : 0];
            if (!visited.Add(child))
                throw new InvalidDataException($"Tag {Name} reaches the index page at offset {child} twice; the index is corrupt.");
            node = ReadNode(child);
        }
        return node;
    }

    // ---- keys for records -------------------------------------------------------------------

    internal bool Includes(DbfRecord record)
    {
        EnsurePrepared();
        return _forExpression is null || _forExpression.Evaluate(record) is true;
    }

    /// <summary>The key FoxPro stores for <paramref name="record"/>.</summary>
    internal byte[] KeyBytes(DbfRecord record)
    {
        EnsurePrepared();
        if (_kind == KeyKind.Character)
            return CharacterKey(record);

        var value = _keyExpression!.Evaluate(record);
        switch (_kind)
        {
            case KeyKind.Double:
                // Through text, so the double is the correctly rounded one FoxPro reads from the field.
                return IndexKeys.WriteOrderedDouble(value switch
                {
                    decimal d => double.Parse(d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                    double d => d,
                    int i => i,
                    _ => throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce a number."),
                });
            case KeyKind.Integer:
            {
                var number = value switch
                {
                    decimal d => decimal.ToInt32(d),
                    int i => i,
                    _ => throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce a number."),
                };
                var key = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(key, unchecked((uint)number + 0x80000000));
                return key;
            }
            case KeyKind.Date:
                return IndexKeys.WriteOrderedDouble(value switch
                {
                    DateOnly date => IndexKeys.JulianDay(date),
                    null => 0d,
                    _ => throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce a date."),
                });
            case KeyKind.DateTime:
                // Julian days with the time as a fraction of the day, worked out the way Visual FoxPro 9
                // does it: whole seconds since day 0 (the stored milliseconds rounded), times 1/86400 as a
                // double. (Adding seconds / 86400 to the day comes out one step off for about a quarter of
                // real values.) An empty one keys as 0.
                return IndexKeys.WriteOrderedDouble(value switch
                {
                    DateTime moment => (IndexKeys.JulianDay(DateOnly.FromDateTime(moment)) * 86_400d
                        + Math.Round(moment.TimeOfDay.Ticks / (double)TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero)) * (1d / 86_400d),
                    null => 0d,
                    _ => throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce a datetime."),
                });
            case KeyKind.Currency:
            {
                // The stored ten-thousandths, big-endian, with the sign bit flipped so negatives sort first.
                var scaled = value switch
                {
                    decimal d => decimal.ToInt64(decimal.Round(d * 10_000m)),
                    null => 0L,
                    _ => throw new InvalidDataException($"Tag {Name}'s key expression '{Expression}' didn't produce a number."),
                };
                var key = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(key, unchecked((ulong)scaled ^ 0x8000_0000_0000_0000));
                return key;
            }
            default:
                return [(byte)(value is true ? 'T' : 'F')];
        }
    }

    // Built from the record's stored bytes: decoding drops NULs and trailing bytes that FoxPro keeps
    // (a field holding "A\0" keys as 41 00 20 20, not 41 20 20 20), and re-encoding the decoded text
    // needn't give back the bytes that were there.
    private byte[] CharacterKey(DbfRecord record)
    {
        var key = new byte[KeyLength];
        if (_parts is [{ Field.IsNullable: true } only])
        {
            // Visual FoxPro: 0x80 then the value for a value; all zeros for .NULL. (VFP 9's own keys).
            if (record.IsNull(only.FieldIndex))
                return key;
            key[0] = 0x80;
            key.AsSpan(1).Fill((byte)' ');
            StoredBytes(record, only).CopyTo(key.AsSpan(1, only.Length));
            return key;
        }
        key.AsSpan().Fill((byte)' ');
        var at = 0;
        foreach (var part in _parts!)
        {
            var target = key.AsSpan(at, part.Length);
            at += part.Length;
            switch (part.Kind)
            {
                case PartKind.Field:
                    StoredBytes(record, part).CopyTo(target);
                    break;
                case PartKind.Upper:
                    StoredBytes(record, part).CopyTo(target);
                    foreach (ref var b in target)
                    {
                        if (b >= 0x80)
                            throw new DbfException(DbfErrorCode.IndexWouldGoStale,
                                $"Tag {Name}'s key UPPER({part.Field.Name}) can't be built exactly for record {record.RecordNumber}: the value has characters outside ASCII, which FoxPro upper-cases by its own code page tables.",
                                "Keep that field to ASCII, or open with allowUnmaintainedIndexes: true and REINDEX in FoxPro afterwards.", _index.Table?.FilePath);
                        if (b is >= (byte)'a' and <= (byte)'z')
                            b -= 0x20;
                    }
                    break;
                case PartKind.Dtos:
                    // DTOS() of a blank date is eight spaces, which the key already holds.
                    if (record[part.FieldIndex] is DateOnly date)
                        System.Text.Encoding.ASCII.GetBytes(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), target);
                    break;
                default:
                {
                    var digits = record[part.FieldIndex] switch
                    {
                        int i => i.ToString(CultureInfo.InvariantCulture),
                        decimal d => decimal.Truncate(d).ToString(CultureInfo.InvariantCulture),
                        _ => "0",
                    };
                    if (digits.Length > target.Length)
                        target.Fill((byte)'*'); // FoxPro's overflow; the width check makes it unreachable for stored values
                    else
                        System.Text.Encoding.ASCII.GetBytes(digits, target[(target.Length - digits.Length)..]);
                    break;
                }
            }
        }
        return key;
    }

    private ReadOnlySpan<byte> StoredBytes(DbfRecord record, KeyPart part)
    {
        if (record.Raw is { } raw && raw.Length >= part.Field.Offset + part.Field.Length)
            return raw.AsSpan(part.Field.Offset, part.Field.Length);
        // A record that didn't come from the table (a blank one, say): its text, space padded.
        var bytes = new byte[part.Field.Length];
        bytes.AsSpan().Fill((byte)' ');
        var text = _index.Encoding.GetBytes(record[part.FieldIndex] as string ?? "");
        text.AsSpan(0, Math.Min(text.Length, bytes.Length)).CopyTo(bytes);
        return bytes;
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
        public bool InByteOrder;     // its keys were checked to be in byte order, or written here in it

        public bool IsLeaf => Children is null;
        public int Count => Keys.Count;

        // Callers change a node's lists in place, so the cache hands out and keeps copies. The key
        // arrays themselves are never changed, only replaced, so they're shared.
        public Node Copy() => new()
        {
            Offset = Offset,
            Attributes = Attributes,
            Left = Left,
            Right = Right,
            Keys = new List<byte[]>(Keys),
            Records = new List<int>(Records),
            Children = Children is null ? null : new List<uint>(Children),
            InByteOrder = InByteOrder,
        };
    }

    private const ushort RootAttribute = 0x01;

    private int MaxInteriorKeys => (PageLength - InteriorHeaderLength) / (KeyLength + 8);

    private Node ReadNode(long offset, bool inByteOrder = false)
    {
        if (_index.CachedNode(offset, this) is Node cached)
        {
            if (inByteOrder && !cached.InByteOrder)
            {
                CheckByteOrder(cached);
                cached.InByteOrder = true;
            }
            return _index.Deferring ? cached : cached.Copy();
        }

        var node = DecodeNode(offset);
        if (inByteOrder)
        {
            CheckByteOrder(node);
            node.InByteOrder = true;
        }
        _index.CacheNode(offset, this, _index.Deferring ? node : node.Copy());
        return node;
    }

    private Node DecodeNode(long offset)
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
            node.Keys = new List<byte[]>(keys);
            node.Records = new List<int>(records);
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

        return node;
    }

    private static bool IsInByteOrder(Node node)
    {
        for (var i = 1; i < node.Count; i++)
        {
            if (node.Keys[i - 1].AsSpan().SequenceCompareTo(node.Keys[i]) > 0)
                return false;
        }
        return true;
    }

    // Changes are placed by byte order, so changing a node in any other order (X# sorts some tags
    // case-blind without saying so) would scramble it. Reading for a rebuild needs no order.
    private void CheckByteOrder(Node node)
    {
        for (var i = 1; i < node.Count; i++)
        {
            if (node.Keys[i - 1].AsSpan().SequenceCompareTo(node.Keys[i]) > 0)
                throw new InvalidDataException($"Tag {Name}'s keys at offset {node.Offset} aren't in byte order; it was sorted by a collation this code doesn't reproduce. Rebuild it with RebuildIndex (or REINDEX with MACHINE collation in FoxPro).");
        }
    }

    private Node ReadChild(uint offset, HashSet<long> visited)
    {
        if (!visited.Add(offset))
            throw new InvalidDataException($"Tag {Name} reaches the index page at offset {offset} twice; the index is corrupt.");
        return ReadNode(offset, inByteOrder: true);
    }

    /// <summary>Writes an empty root leaf over the tag's root page, for a tag just created.</summary>
    internal void WriteEmptyRoot() => WriteNode(new Node { Offset = RootOffset(), Attributes = (ushort)(RootAttribute | LeafAttribute) });

    private void WriteNode(Node node, byte[]? encoded = null)
    {
        var page = encoded ?? (node.IsLeaf ? EncodeLeaf(node) : EncodeInterior(node));
        _index.Write(node.Offset, page ?? throw new InvalidOperationException($"Tag {Name}'s page at offset {node.Offset} doesn't fit."));
        // What was just written, as a reader would decode it. Byte order is only vouched for when it
        // was checked (or the change began from checked nodes): a rebuild's reads skip the check.
        var written = _index.Deferring ? node : node.Copy();
        written.InByteOrder = IsInByteOrder(written);
        _index.CacheNode(node.Offset, this, written);
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
        // As in Harbour's dbfcdx1.c: the record field is everything below the two counts, which sit
        // in the entry's top bits, even past 32 bits (record 67,108,864 with 4-byte keys needs 5-byte
        // entries and 34 record bits). The mask field is only 4 bytes, so it stops at 32 bits.
        var recordBits = entryLength * 8 - 2 * countBits;

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
                // A shared prefix never overlaps the dropped trailing bytes, this key's or the previous
                // one's: it's counted against the bytes the previous key keeps (Visual FoxPro 9's own
                // leaves; a key that adds a date after blanks repeats the blanks).
                var previous = node.Keys[i - 1];
                var previousKept = KeyLength - (KeyLength - previous.AsSpan().TrimEnd(trail).Length);
                while (duplicates < Math.Min(KeyLength - trailing, previousKept) && key[duplicates] == previous[duplicates])
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
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), recordBits >= 32 ? uint.MaxValue : (1u << recordBits) - 1);
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

    /// <summary>Every page the tag's tree uses.</summary>
    internal List<long> Pages()
    {
        var pages = new List<long>();
        CollectPages(ReadNode(RootOffset()), [], pages, leavesOnly: false, depth: 0);
        return pages;
    }

    /// <summary>Where the tag's root page is, for the tests: it never moves (Visual FoxPro holds on to it).</summary>
    internal long RootPage => RootOffset();

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

    // ---- writing a change in a crash-safe order -----------------------------------------------

    /// <summary>
    /// Every page one tree operation writes, held back so they go to disk in an order a crash can't
    /// turn into lost keys: new pages first (nothing points at them yet), then the pages that already
    /// exist, then sibling links, then pages for the free list, which nothing references by then.
    /// </summary>
    private sealed class Plan
    {
        public readonly List<Node> Fresh = [];
        // Page: the node already encoded, when Store's fit test did it and nothing changed it since.
        public readonly List<(int Level, Node Node, byte[]? Page)> Changed = [];
        public readonly List<(uint Offset, bool LeftLink, uint Target)> Links = [];
        public readonly List<long> Freed = [];
    }

    // Growing (an insert, a split) writes the existing pages from the top down: a parent learns of a
    // new page, or of a higher last key, before the page below changes, so until the last write a
    // crash leaves keys listed twice or a parent's key too high, never a key a search can't reach.
    // Shrinking (a removal) goes from the bottom up, for the same reason the other way round.
    private void Apply(Plan plan, bool topDown)
    {
        foreach (var node in plan.Fresh)
            WriteNode(node);
        if (plan.Fresh.Count > 0)
            _index.Barrier(); // a split's new pages (a grown root's children among them) are on disk before anything points at them
        // A stable sort by level, highest first when growing, in place: the list holds a few entries.
        var changed = plan.Changed;
        for (var i = 1; i < changed.Count; i++)
        {
            var item = changed[i];
            var j = i - 1;
            for (; j >= 0 && (topDown ? changed[j].Level < item.Level : changed[j].Level > item.Level); j--)
                changed[j + 1] = changed[j];
            changed[j + 1] = item;
        }
        foreach (var (_, node, page) in changed)
            WriteNode(node, page);
        foreach (var (offset, leftLink, target) in plan.Links)
            PatchSibling(offset, leftLink, target);
        if (plan.Freed.Count > 0)
            _index.Barrier();
        foreach (var page in plan.Freed)
            _index.FreePage(page);
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
        var plan = new Plan();
        Store(node, path, appendedAtEnd: at == node.Count - 1 && node.Right == NoPage, plan);
        Apply(plan, topDown: true);
    }

    /// <summary>
    /// Plans the writes for a changed node, splitting it when it no longer fits, and carries the
    /// change up: each parent's entry for a child is that child's last key, and a split adds an entry.
    /// </summary>
    private void Store(Node node, List<(Node Node, int Slot)> path, bool appendedAtEnd, Plan plan)
    {
        for (var level = 0; ; level++)
        {
            GuardDepth(level);
            if ((node.IsLeaf ? EncodeLeaf(node) : EncodeInterior(node)) is { } encoded)
            {
                plan.Changed.Add((level, node, encoded)); // nothing below changes this node again
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

            // Visual FoxPro reads a tag's root address once, when it opens the table, and keeps using
            // it, so the root never moves: a full root's keys go down into two new pages and the root
            // page is rewritten in place above them, which is also the one write that switches over.
            var isRoot = path.Count == 0;
            var rootOffset = node.Offset;
            if (isRoot)
            {
                node.Attributes &= unchecked((ushort)~RootAttribute);
                node.Offset = _index.AllocatePage();
            }
            var right = Split(node, appendedAtEnd);
            plan.Fresh.Add(right);
            if (isRoot)
                plan.Fresh.Add(node);
            else
                plan.Changed.Add((level, node, null));
            if (right.Right != NoPage)
                plan.Links.Add((right.Right, true, (uint)right.Offset));

            if (isRoot)
            {
                plan.Changed.Add((level + 1, new Node
                {
                    Offset = rootOffset,
                    Attributes = RootAttribute,
                    Keys = [node.Keys[^1], right.Keys[^1]],
                    Records = [node.Records[^1], right.Records[^1]],
                    Children = [(uint)node.Offset, (uint)right.Offset],
                }, null));
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

            var right = new Node
            {
                Offset = _index.AllocatePage(),
                Attributes = node.Attributes,
                Left = (uint)node.Offset,
                Right = node.Right,
                Keys = rest.Keys,
                Records = rest.Records,
                Children = rest.Children,
            };
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
        var plan = new Plan();
        if (leaf.Count > 0 || path.Count == 0)
            Store(leaf, path, appendedAtEnd: false, plan);
        else
            DropEmpty(leaf, path, plan);
        // A leaf another writer packed tighter (Harbour, X#) can need more room once re-packed the
        // way Visual FoxPro packs, so even a removal can split it: then the split's order applies.
        Apply(plan, topDown: plan.Fresh.Count > 0);
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

    // An emptied page leaves the tree: its parent loses the entry for it first (and leaves too, if
    // that empties it), then its neighbours link past it, and only then, with nothing pointing at
    // it, does it go on the free list. An emptied root becomes an empty leaf, which is how FoxPro
    // stores a tag with no keys.
    private void DropEmpty(Node node, List<(Node Node, int Slot)> path, Plan plan)
    {
        var leafAttributes = (ushort)(node.Attributes & ~RootAttribute);
        for (var level = 0; ; level++)
        {
            GuardDepth(level);
            if (node.Left != NoPage)
                plan.Links.Add((node.Left, false, node.Right));
            if (node.Right != NoPage)
                plan.Links.Add((node.Right, true, node.Left));
            plan.Freed.Add(node.Offset);

            var (parent, slot) = path[^1];
            path.RemoveAt(path.Count - 1);
            parent.Keys.RemoveAt(slot);
            parent.Records.RemoveAt(slot);
            parent.Children!.RemoveAt(slot);

            if (parent.Count > 0)
            {
                Store(parent, path, appendedAtEnd: false, plan);
                return;
            }
            if (path.Count == 0)
            {
                plan.Changed.Add((level + 1, new Node { Offset = parent.Offset, Attributes = (ushort)(leafAttributes | RootAttribute) }, null));
                return;
            }
            node = parent;
        }
    }

    // A root with a single child takes that child's entries, so the tree stays as shallow as its keys
    // need. The root keeps its page (Visual FoxPro holds on to its address): the child's contents are
    // written over it in one page write, and only then is the child's page freed.
    private void CollapseRoot()
    {
        for (var depth = 0; ; depth++)
        {
            GuardDepth(depth);
            var root = ReadNode(RootOffset());
            if (root.IsLeaf || root.Count > 1)
                return;
            var child = ReadNode(root.Children![0]);
            var childPage = child.Offset;
            child.Offset = root.Offset;
            child.Attributes |= RootAttribute;
            child.Left = child.Right = NoPage;
            WriteNode(child);
            _index.Barrier(); // the root holds the child's entries on disk before the child's page is freed
            _index.FreePage(childPage);
        }
    }

    // ---- rebuild ----------------------------------------------------------------------------

    private static readonly Comparer<byte[]> ByteOrder = Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));

    /// <summary>Empties the tag (a rebuild with no keys).</summary>
    internal void Clear() => ReplaceAll([]);

    /// <summary>
    /// Rebuilds the tag from <paramref name="keys"/>, like FoxPro's REINDEX: a UNIQUE tag keeps the
    /// lowest record number per key. The new tree is built in new pages and its root is written over
    /// the old root last, in place (Visual FoxPro keeps the root's address), and only then do the old
    /// tree's other pages go on the free list, so a crash leaves either the old tree or the new one,
    /// never half of each.
    /// </summary>
    internal void ReplaceAll(IReadOnlyList<(byte[] Key, int Record)> keys)
    {
        foreach (var (key, _) in keys)
            CheckKey(key);
        var entries = keys.OrderBy(k => k.Key, ByteOrder).ThenBy(k => k.Record).ToList();
        if (IsUnique)
            entries = entries.Where((e, i) => i == 0 || !entries[i - 1].Key.AsSpan().SequenceEqual(e.Key)).ToList();

        // Read the old tree first: a damaged one stops the rebuild before anything is written.
        var oldPages = Pages();
        var leafAttributes = (ushort)(LeafAttribute | (EdgeLeaf(last: false).Attributes & 0x04));

        // Leaves packed as full as they go, then each level above, until one page holds them all.
        var level = new List<Node>();
        var leaf = new Node { Attributes = leafAttributes };
        foreach (var (key, record) in entries)
        {
            leaf.Keys.Add(key);
            leaf.Records.Add(record);
            if (leaf.Count > 1 && EncodeLeaf(leaf) is null)
            {
                leaf.Keys.RemoveAt(leaf.Count - 1);
                leaf.Records.RemoveAt(leaf.Records.Count - 1);
                level.Add(leaf);
                leaf = new Node { Attributes = leafAttributes, Keys = [key], Records = [record] };
            }
        }
        level.Add(leaf);

        var built = new List<Node>();
        while (level.Count > 1)
        {
            Place(level);
            built.AddRange(level);
            var parents = new List<Node>();
            var parent = new Node { Children = [] };
            foreach (var child in level)
            {
                if (parent.Count == MaxInteriorKeys)
                {
                    parents.Add(parent);
                    parent = new Node { Children = [] };
                }
                parent.Keys.Add(child.Keys[^1]);
                parent.Records.Add(child.Records[^1]);
                parent.Children!.Add((uint)child.Offset);
            }
            parents.Add(parent);
            level = parents;
        }
        var root = level[0];
        root.Attributes |= RootAttribute;
        root.Offset = RootOffset();
        root.Left = root.Right = NoPage;

        foreach (var node in built)
            WriteNode(node);
        _index.Barrier();
        WriteNode(root);
        _index.Barrier();
        foreach (var page in oldPages.Where(p => p != root.Offset))
            _index.FreePage(page);
    }

    // New pages for one level, linked left to right.
    private void Place(List<Node> level)
    {
        foreach (var node in level)
            node.Offset = _index.AllocatePage();
        for (var i = 0; i < level.Count; i++)
        {
            level[i].Left = i == 0 ? NoPage : (uint)level[i - 1].Offset;
            level[i].Right = i == level.Count - 1 ? NoPage : (uint)level[i + 1].Offset;
        }
    }
}
