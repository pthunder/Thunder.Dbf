//      ____                          ProjectThunder :: Thunder.Dbf
//     |    |  record 3 is mine       Byte-range locks far past the end of the file, at the
//     | () |  ...wait your turn      offsets each family of xBase apps agreed on, so Thunder.Dbf
//     |____|                         and a running legacy app see each other's locks.
//
//   scheme          header/append     record n                       whole file
//   dBASE           4,026,531,838     4,026,531,838 - n              3,026,531,838 for 1e9, + header/append
//   Clipper         1,000,000,000     1,000,000,000 + n              1,000,000,001 for 1e9
//   VFP (no .cdx)   0x40000000        0x40000000 + record's offset   0x40000001 for 0x3FFFFFFD
//   VFP (.cdx)      0x7FFFFFFE        0x7FFFFFFE - n                 0x77FFFFFF for 0x07FFFFFF
//
// Clipper and Visual FoxPro offsets follow Harbour's include/hbrdddbf.h and src/rdd/dbf1.c
// (read, not copied). Locks only work when every program agrees to take them, and only where
// the operating system passes them through: Windows (with opportunistic locking off on shares)
// and Linux do; .NET can't lock part of a file on macOS. See docs/locking.md.

using System.Diagnostics;

namespace Thunder.Dbf;

/// <summary>Which family of xBase applications' lock positions to use.</summary>
public enum DbfLockScheme
{
    /// <summary>Pick from the table: FoxPro and Visual FoxPro tables use <see cref="VisualFoxPro"/>, everything else <see cref="Dbase"/>.</summary>
    Auto,

    /// <summary>dBASE IV and the Borland Database Engine (and so GoldMine).</summary>
    Dbase,

    /// <summary>Clipper (and Harbour's default). Clipper tables look like dBASE ones, so choose this explicitly.</summary>
    Clipper,

    /// <summary>FoxPro and Visual FoxPro. Positions differ with and without a structural .cdx.</summary>
    VisualFoxPro,
}

/// <summary>A held lock. Dispose it to release the lock.</summary>
public sealed class DbfLock : IDisposable
{
    private Action? _release;

    internal DbfLock(Action release) => _release = release;

    /// <inheritdoc />
    public void Dispose()
    {
        _release?.Invoke();
        _release = null;
    }
}

/// <summary>Takes and releases legacy-compatible byte-range locks on one table file.</summary>
internal sealed class DbfLocks
{
    internal const long HeaderByte = 4_026_531_838;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly FileStream? _file;
    private readonly string? _path;
    private readonly FileRegistry? _registry;
    private readonly bool _withCdx;
    private readonly int _recordLength;
    private readonly int _headerLength;
    private bool _unsupported;

    public DbfLocks(FileStream? file, string? path, DbfLockScheme scheme, DbfHeader header)
    {
        _file = file;
        _path = path;
        Scheme = Resolve(scheme, header);
        _withCdx = header.HasStructuralCdx;
        _recordLength = header.RecordLength;
        _headerLength = header.HeaderLength;
        if (file is not null)
            _registry = FileRegistry.For(file.Name);
    }

    /// <summary>
    /// Locks for a file that isn't a table (an index), where the caller names every range itself
    /// through <see cref="Range"/>. Same registry, timeout and RequireLocks rules as a table's.
    /// </summary>
    public DbfLocks(FileStream? file, string? path)
    {
        _file = file;
        _path = path;
        Scheme = DbfLockScheme.Clipper;
        if (file is not null)
            _registry = FileRegistry.For(file.Name);
    }

    public DbfLockScheme Scheme { get; }

    /// <summary>The scheme <see cref="DbfLockScheme.Auto"/> stands for on this table.</summary>
    internal static DbfLockScheme Resolve(DbfLockScheme scheme, DbfHeader header) =>
        scheme == DbfLockScheme.Auto ? (header.IsFoxPro || header.IsFoxProByCompanion ? DbfLockScheme.VisualFoxPro : DbfLockScheme.Dbase) : scheme;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Refuse to write when locks can't be taken, instead of writing uncoordinated.</summary>
    public bool RequireLocks { get; set; }

    /// <summary>False on platforms where .NET can't lock part of a file (macOS).</summary>
    public static bool PlatformSupportsLocks => !OperatingSystem.IsMacOS();

    /// <summary>
    /// True when locks really reach the operating system. False for memory streams, and on platforms
    /// that can't lock part of a file (macOS), where locks only coordinate tables in this process.
    /// </summary>
    public bool Enforced => _file is not null && !_unsupported && PlatformSupportsLocks;

    // ---- positions ---------------------------------------------------------------------

    private (long Offset, long Length) HeaderRange => Scheme switch
    {
        DbfLockScheme.Clipper => (1_000_000_000, 1),
        DbfLockScheme.VisualFoxPro => (_withCdx ? 0x7FFFFFFEL : 0x40000000L, 1),
        _ => (HeaderByte, 1),
    };

    // Harbour's schemes take the header byte for appends too; dBASE uses the byte after it.
    private (long Offset, long Length) AppendRange => Scheme == DbfLockScheme.Dbase ? (HeaderByte + 1, 1) : HeaderRange;

    internal (long Offset, long Length) RecordRange(int recordNumber) => Scheme switch
    {
        DbfLockScheme.Clipper => (1_000_000_000L + recordNumber, 1),
        DbfLockScheme.VisualFoxPro when _withCdx => (0x7FFFFFFEL - recordNumber, 1),
        DbfLockScheme.VisualFoxPro => (0x40000000L + (long)(recordNumber - 1) * _recordLength + _headerLength, 1),
        _ => (HeaderByte - recordNumber, 1),
    };

    private (long Offset, long Length)[] TableRanges => Scheme switch
    {
        DbfLockScheme.Clipper => [(1_000_000_001, 1_000_000_000)],
        DbfLockScheme.VisualFoxPro when _withCdx => [(0x7FFFFFFEL - 0x07FFFFFFL, 0x07FFFFFF)],
        DbfLockScheme.VisualFoxPro => [(0x40000001L, 0x3FFFFFFD)],
        _ => [(3_026_531_838, 1_000_000_000), (HeaderByte, 2)],
    };

    // ---- the kinds of lock --------------------------------------------------------------

    public DbfLock Record(int recordNumber, string purpose) =>
        Take(RecordRange(recordNumber), DbfErrorCode.RecordLocked,
            $"Record {recordNumber} is locked by another program, so it can't be {purpose} right now.");

    public DbfLock Header() =>
        Take(HeaderRange, DbfErrorCode.TableLocked, "Another program is changing this table's header (adding records or locking the whole table).");

    public DbfLock Append() =>
        Take(AppendRange, DbfErrorCode.TableLocked, "Another program is adding records to this table.");

    /// <summary>Every record lock plus the header and append locks: nobody else can change the table.</summary>
    public DbfLock Table()
    {
        var taken = new List<DbfLock>();
        try
        {
            foreach (var range in TableRanges)
                taken.Add(Take(range, DbfErrorCode.TableLocked, "Another program has this table, or some of its records, locked."));
            taken.Add(Header());
            return new DbfLock(() =>
            {
                for (var i = taken.Count - 1; i >= 0; i--)
                    taken[i].Dispose();
            });
        }
        catch
        {
            for (var i = taken.Count - 1; i >= 0; i--)
                taken[i].Dispose();
            throw;
        }
    }

    /// <summary>An exclusive lock on any range of the file, for formats with their own lock positions.</summary>
    public DbfLock Range((long Offset, long Length) range, DbfErrorCode code, string message) => Take(range, code, message);

    // ---- taking and releasing -----------------------------------------------------------

    private DbfLock Take((long Offset, long Length) range, DbfErrorCode code, string message)
    {
        if (_file is null || _registry is null)
            return new DbfLock(() => { });
        if (RequireLocks && !Enforced)
            throw Unsupported(null);

        var timer = Stopwatch.StartNew();
        while (true)
        {
            // Other tables in this process count as other programs: operating-system locks can't
            // tell them apart on Linux, so the registry does.
            if (_registry.TryClaim(this, range, out var needsOsLock))
            {
                if (!needsOsLock || !Enforced)
                    return new DbfLock(() => Release(range));
                if (TryOsLock(range))
                {
                    _registry.MarkOsLock(this, range);
                    return new DbfLock(() => Release(range));
                }
                _registry.Release(this, range);
            }

            if (timer.Elapsed >= Timeout)
                throw new DbfException(code, message,
                    $"Waited {Timeout.TotalSeconds:0.#} s. Try again in a moment, or raise LockTimeout if the other program holds locks for long.",
                    _path);
            Thread.Sleep(RetryDelay);
        }
    }

    private bool TryOsLock((long Offset, long Length) range)
    {
        try
        {
#pragma warning disable CA1416 // only reached when Enforced, which is false on macOS
            _file!.Lock(range.Offset, range.Length);
#pragma warning restore CA1416
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (PlatformNotSupportedException ex)
        {
            _unsupported = true;
            if (RequireLocks)
                throw Unsupported(ex);
            return true; // no OS locks here; the registry still coordinates this process
        }
    }

    private void Release((long Offset, long Length) range)
    {
        if (_registry!.Release(this, range) && Enforced)
        {
            try
            {
#pragma warning disable CA1416 // only reached for locks the OS granted, so never on macOS
                _file!.Unlock(range.Offset, range.Length);
#pragma warning restore CA1416
            }
            catch (IOException)
            {
                // Already released (the handle may be closing); nothing more to do.
            }
        }
        _registry.CloseDeferredIfIdle();
    }

    private DbfException Unsupported(Exception? inner) =>
        new(DbfErrorCode.LockingUnsupported, "This platform or file system can't lock part of a file, and RequireLocks is on.",
            "Writing safely next to another program needs byte-range locks: run on Windows (against the share) or Linux.", _path, inner);

    /// <summary>
    /// Closes a stream to a table file. On Linux, closing any handle to a file drops every lock this
    /// process holds on it, so while locks are held the close waits until they're all released.
    /// </summary>
    internal static void CloseWhenSafe(FileStream stream)
    {
        if (FileRegistry.TryGet(stream.Name) is { } registry && registry.DeferIfLocked(stream))
            return;
        stream.Dispose();
    }

    /// <summary>Process-wide bookkeeping for one file: who holds which range, and handles waiting to close.</summary>
    private sealed class FileRegistry
    {
        private static readonly Dictionary<string, FileRegistry> All = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private readonly List<Holding> _held = [];
        private readonly List<FileStream> _deferred = [];

        private sealed class Holding(DbfLocks owner, long offset, long length)
        {
            public DbfLocks Owner { get; } = owner;
            public long Offset { get; } = offset;
            public long Length { get; } = length;
            public int Count { get; set; } = 1;
            public bool OsLock { get; set; }

            public bool Overlaps((long Offset, long Length) r) => Offset < r.Offset + r.Length && r.Offset < Offset + Length;
            public bool Contains((long Offset, long Length) r) => Offset <= r.Offset && r.Offset + r.Length <= Offset + Length;
            public bool Is((long Offset, long Length) r) => Offset == r.Offset && Length == r.Length;
        }

        public static FileRegistry For(string path)
        {
            var key = Key(path);
            lock (All)
            {
                if (!All.TryGetValue(key, out var registry))
                    All[key] = registry = new FileRegistry();
                return registry;
            }
        }

        public static FileRegistry? TryGet(string path)
        {
            lock (All)
                return All.GetValueOrDefault(Key(path));
        }

        private static string Key(string path)
        {
            var full = Path.GetFullPath(path);
            return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToUpperInvariant() : full;
        }

        // Claims a range for owner. False when another owner in this process holds an overlapping
        // range. needsOsLock is false when owner already holds this range, or an OS lock covering it.
        public bool TryClaim(DbfLocks owner, (long Offset, long Length) range, out bool needsOsLock)
        {
            lock (_gate)
            {
                needsOsLock = true;
                if (_held.Any(h => !ReferenceEquals(h.Owner, owner) && h.Overlaps(range)))
                    return false;

                var same = _held.FirstOrDefault(h => ReferenceEquals(h.Owner, owner) && h.Is(range));
                if (same is not null)
                {
                    same.Count++;
                    needsOsLock = false;
                    return true;
                }

                needsOsLock = !_held.Any(h => ReferenceEquals(h.Owner, owner) && h.OsLock && h.Contains(range));
                _held.Add(new Holding(owner, range.Offset, range.Length));
                return true;
            }
        }

        public void MarkOsLock(DbfLocks owner, (long Offset, long Length) range)
        {
            lock (_gate)
            {
                var holding = _held.LastOrDefault(h => ReferenceEquals(h.Owner, owner) && h.Is(range));
                if (holding is not null)
                    holding.OsLock = true;
            }
        }

        // Returns true when the caller should release the operating-system lock for this range.
        public bool Release(DbfLocks owner, (long Offset, long Length) range)
        {
            lock (_gate)
            {
                var holding = _held.FirstOrDefault(h => ReferenceEquals(h.Owner, owner) && h.Is(range));
                if (holding is null)
                    return false;
                if (--holding.Count > 0)
                    return false;
                _held.Remove(holding);
                return holding.OsLock;
            }
        }

        public bool DeferIfLocked(FileStream stream)
        {
            lock (_gate)
            {
                if (_held.Count == 0)
                    return false;
                _deferred.Add(stream);
                return true;
            }
        }

        public void CloseDeferredIfIdle()
        {
            List<FileStream>? closing = null;
            lock (_gate)
            {
                if (_held.Count == 0 && _deferred.Count > 0)
                {
                    closing = [.. _deferred];
                    _deferred.Clear();
                }
            }
            closing?.ForEach(s => s.Dispose());
        }
    }
}
