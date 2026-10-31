//    _
//   / |__     ProjectThunder :: Thunder.Dbf
//  /_   /     Records: read, append, update, delete.
//    | /
//    |/
//
// A record is one fixed-width row. Fields sit side by side with no separators:
//
// +---+----------------------+------------+----------+---+------------+
// | * | NAME (C 20)          | BALANCE    | HIRED    | A | NOTES      |
// +---+----------------------+------------+----------+---+------------+
// |   | Smith                |    1234.50 | 19970412 | T |         42 |
// +---+----------------------+------------+----------+---+------------+
//   ^ deletion flag: ' ' live, '*' deleted                     |
//                                                              v
//                                          block 42 of people.dbt holds the notes
//
// After the last record comes 0x1A, the old DOS end-of-file marker.

using System.Text;

namespace Thunder.Dbf;

/// <summary>
/// An open DBF table and its memo file, if it has one. Memo fields read as <see cref="string"/>
/// (or <see cref="byte"/>[] for binary, picture and OLE memos). Writes take legacy-compatible locks
/// (see <see cref="LockScheme"/>). An instance isn't thread-safe: give each thread its own.
/// </summary>
public sealed partial class DbfTable : IDisposable
{
    private const byte LiveFlag = (byte)' ';
    private const byte DeletedFlag = (byte)'*';
    private const byte EndOfFile = 0x1A;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly DbfMemoFile? _memo;
    private readonly bool _allowUnmaintainedIndexes;

    // Writes use an encoder that throws instead of quietly turning unknown characters into '?'.
    private readonly Encoding _strictEncoding;

    /// <summary>Set when a <see cref="DbfDatabase"/> opened the table: companions found later must not be links.</summary>
    internal bool RefuseLinks { get; set; }
    private readonly byte[] _buffer;
    private string? _path;
    private MdxIndex? _index; // the production .mdx, kept in step with every write
    private readonly List<IMaintainedIndex> _otherIndexes = []; // a structural .cdx, and .ndx/.ntx/.idx files that belong to the table

    // Every index this table keeps in step with its writes.
    private IEnumerable<IMaintainedIndex> MaintainedIndexes => _index is null ? _otherIndexes : _otherIndexes.Prepend(_index);
    private readonly HashSet<int> _dirtyCounters = [];
    private readonly List<(List<TagChange> Changes, int RecordNumber)> _pendingIndexChanges = [];
    private IReadOnlyList<string> _unmaintainedIndexes = [];

    /// <summary>The largest file dBASE and FoxPro handle: 2 GB.</summary>
    private const long MaxTableBytes = int.MaxValue;

    private DbfTable(Stream stream, DbfHeader header, Encoding encoding, bool leaveOpen, DbfMemoFile? memo,
        bool ignoreMissingMemo, bool allowUnmaintainedIndexes)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _memo = memo;
        _allowUnmaintainedIndexes = allowUnmaintainedIndexes;
        Header = header;
        Encoding = encoding;
        _strictEncoding = DbfEncoding.Strict(encoding);
        _buffer = new byte[header.RecordLength];

        if (header.IsEncrypted)
            throw new DbfException(DbfErrorCode.EncryptedTable, "dBASE IV encrypted this table with PROTECT, so its records are scrambled.",
                "Decrypt it in dBASE (or the legacy app) and save an unencrypted copy; Thunder.Dbf can't read PROTECT encryption.");

        if (memo is null && !ignoreMissingMemo && Fields.Any(DbfValueCodec.IsMemoField))
            throw new DbfException(DbfErrorCode.MemoFileMissing, "The table has memo fields but its .dbt/.fpt memo file wasn't found.",
                "Copy the memo file next to the table (same name), or open with ignoreMissingMemo: true to read memo fields as null.");
    }

    /// <summary>The table's header: version, record count and field descriptors.</summary>
    public DbfHeader Header { get; }
    /// <summary>The encoding text fields are read and written with.</summary>
    public Encoding Encoding { get; }
    /// <summary>The fields, in table order.</summary>
    public IReadOnlyList<DbfField> Fields => Header.Fields;
    /// <summary>The number of records, including those marked deleted.</summary>
    public int RecordCount => checked((int)Header.RecordCount);
    /// <summary>True when the table was opened for writing.</summary>
    public bool CanWrite => _stream.CanWrite;

    /// <summary>The memo file's format, or null when the table has none (or it was missing and ignored).</summary>
    public DbfMemoFormat? MemoFormat => _memo?.Format;

    /// <summary>
    /// Opens a .dbf file and its .dbt/.fpt memo file. Files are opened shared, like dBASE does in
    /// multi-user mode, so the legacy app keeps working; writes coordinate with it through
    /// dBASE-compatible record and table locks.
    /// </summary>
    /// <param name="ignoreMissingMemo">Read memo fields as null when the memo file is missing, instead of failing.
    /// Updates then leave those memo pointers untouched.</param>
    /// <param name="allowUnmaintainedIndexes">Allow writes to a table with an index Thunder.Dbf can't keep up to
    /// date (a missing .mdx, a FoxPro .cdx without <paramref name="maintainFoxProIndexes"/>, an index in a sort
    /// order or mode it doesn't write). Those indexes go stale until something rebuilds them.</param>
    /// <param name="maintainFoxProIndexes">Preview: keep FoxPro and Visual FoxPro .cdx and .idx indexes up to date on
    /// writes. Off by default until it has been checked against Visual FoxPro itself (see docs/testing.md);
    /// without it, a table with a .cdx refuses writes.</param>
    /// <param name="path">The .dbf file.</param>
    /// <param name="writable">Open for writing. Read-only by default.</param>
    /// <param name="encoding">Text encoding, overriding what the header or a .cpg file says.</param>
    /// <param name="exclusive">Open the .dbf so no other program can open it, like dBASE and FoxPro's
    /// <c>USE ... EXCLUSIVE</c>; fails with <see cref="DbfErrorCode.FileInUse"/> while another program has it
    /// open. <see cref="Pack"/>, <see cref="RebuildIndex"/>, <see cref="CreateIndexTag"/> and
    /// <c>MaintainIndex(path, rebuild: true)</c> need it.</param>
    public static DbfTable Open(string path, bool writable = false, Encoding? encoding = null, bool ignoreMissingMemo = false,
        bool allowUnmaintainedIndexes = false, bool maintainFoxProIndexes = false, bool exclusive = false)
        => OpenPath(path, writable, encoding, ignoreMissingMemo, allowUnmaintainedIndexes, maintainFoxProIndexes, exclusive);

    /// <summary>Opens a table shared: the 0.1.0 signature, kept so code compiled against it still runs.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static DbfTable Open(string path, bool writable, Encoding? encoding, bool ignoreMissingMemo,
        bool allowUnmaintainedIndexes, bool maintainFoxProIndexes)
        => OpenPath(path, writable, encoding, ignoreMissingMemo, allowUnmaintainedIndexes, maintainFoxProIndexes, exclusive: false);

    private static DbfTable OpenPath(string path, bool writable, Encoding? encoding, bool ignoreMissingMemo,
        bool allowUnmaintainedIndexes, bool maintainFoxProIndexes, bool exclusive)
    {
        var access = writable ? FileAccess.ReadWrite : FileAccess.Read;
        var share = FileShare.ReadWrite;
        // Every program has to open the .dbf to use the table, so holding it unshared is enough.
        var stream = DbfException.OpenFile(path, FileMode.Open, access, exclusive ? FileShare.None : share);
        FileStream? memoStream = null;
        try
        {
            stream.Position = 0;
            var header = ReadHeader(stream, encoding: null, path);
            if (header.Fields.Any(DbfValueCodec.IsMemoField) && DbfMemoFile.FindFor(path, header) is { } memoPath)
                memoStream = DbfException.OpenFile(memoPath, FileMode.Open, access, share);

            var cpg = FindCompanion(path, ".cpg");
            var table = Open(stream, encoding ?? (cpg is null ? null : DbfEncoding.FromCpgFile(cpg)), leaveOpen: false,
                memoStream, ignoreMissingMemo, allowUnmaintainedIndexes);
            table._path = path;
            table._exclusive = exclusive;
            table._maintainFoxProIndexes = maintainFoxProIndexes;
            if (table.Header.HasProductionMdx && MdxIndex.FindFor(path) is null && FindCompanion(path, ".cdx") is { } cdx
                && IsIndexOver(cdx, table.Header))
                table.Header.TreatIndexFlagAsCdx(); // a FoxPro 2 table without memos: it says 0x03, like dBASE
            if (writable)
            {
                try
                {
                    if (table.Header.HasProductionMdx && MdxIndex.FindFor(path) is { } indexPath)
                        table.AttachIndex(DbfException.OpenFile(indexPath, FileMode.Open, FileAccess.ReadWrite, share));
                    table._journalPending = DbfJournal.IsPending(path);
                    table.AttachOtherIndexes(path, table.Header);
                    table.RepairUnfinishedAppends();
                }
                catch
                {
                    table.Dispose();
                    throw;
                }
            }
            return table;
        }
        catch
        {
            memoStream?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a table from a seekable stream, with its memo file in <paramref name="memoStream"/> if it has memo fields.
    /// Text uses <paramref name="encoding"/>, else the header's language driver. PACK, rebuilds and INDEX ON don't
    /// check for exclusive use on a stream: when it's a file other programs could have open, keeping them out
    /// (opening it with <see cref="FileShare.None"/>) is the caller's job.
    /// </summary>
    public static DbfTable Open(Stream stream, Encoding? encoding = null, bool leaveOpen = false, Stream? memoStream = null,
        bool ignoreMissingMemo = false, bool allowUnmaintainedIndexes = false, Stream? indexStream = null)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("DBF tables need a seekable stream.", nameof(stream));

        stream.Position = 0;
        var header = ReadHeader(stream, encoding, path: null);
        DbfMemoFile? memo;
        try
        {
            memo = memoStream is null ? null : DbfMemoFile.Open(memoStream, header, leaveOpen);
        }
        catch (InvalidDataException ex)
        {
            throw new DbfException(DbfErrorCode.MemoFileDamaged, $"The memo file can't be read: {ex.Message}",
                "Restore the .dbt/.fpt from a backup, or open with ignoreMissingMemo: true to read the table without it.", inner: ex);
        }
        var table = new DbfTable(stream, header, encoding ?? DbfEncoding.For(header.LanguageDriver), leaveOpen, memo,
            ignoreMissingMemo, allowUnmaintainedIndexes);
        if (indexStream is not null && header.HasProductionMdx && stream.CanWrite)
            table.AttachIndex(indexStream, leaveOpen);
        return table;
    }

    // Opens every other index that belongs to the table for writing. Each one whose format can be
    // kept up to date is attached; the rest are remembered, and block writes (see WriteBlocker).
    private void AttachOtherIndexes(string path, DbfHeader header)
    {
        var unmaintained = new List<string>();
        _indexFilesAtOpen = IndexFileNames(path);
        foreach (var file in FindCompanionIndexes(path, header))
        {
            // .ndx, .ntx and .idx files don't record which table they belong to, and a field name like
            // ID is in many tables, so only an index named after the table is taken to be its own.
            // Any other that looks like it belongs blocks writes until MaintainIndex names it.
            if (!Path.GetFileNameWithoutExtension(file).Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
            {
                unmaintained.Add(Path.GetFileName(file));
                continue;
            }
            if (!TryAttachIndex(file, forJournal: _journalPending))
                unmaintained.Add(Path.GetFileName(file));
        }
        _unmaintainedIndexes = unmaintained;
    }

    // A dBASE IV table whose .mdx went missing looks just like a FoxPro 2 table, so a .cdx of the same
    // name only says "FoxPro 2" when it opens and every tag's expressions name nothing but this
    // table's fields. A stray .cdx (another table's, renamed or copied in) leaves dBASE's rules in
    // force: the missing .mdx keeps blocking writes, and dBASE's lock positions stay in use.
    private static bool IsIndexOver(string cdxPath, DbfHeader header)
    {
        var fields = header.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var index = CdxIndex.Open(cdxPath);
            return index.Tags.Count > 0 && index.Tags.All(tag =>
            {
                var names = DbfExpression.FieldNamesIn(tag.Expression);
                var filter = tag.ForExpression is null ? (IReadOnlyCollection<string>)[] : DbfExpression.FieldNamesIn(tag.ForExpression);
                return names.Count > 0 && names.Concat(filter).All(fields.Contains);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException
                                       or FormatException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps the index at <paramref name="path"/> up to date from now on, for an index whose name
    /// doesn't match the table's (dBASE III and Clipper indexes record nothing that ties them to a
    /// table, so Thunder.Dbf won't guess). Use it for every index the legacy app opens with this table,
    /// such as <c>USE CUSTOMER INDEX CUSTNAME, CUSTZIP</c>.
    /// </summary>
    /// <param name="path">The index file.</param>
    /// <param name="rebuild">
    /// Also take over an index whose keys are in a sort order Thunder.Dbf doesn't reproduce (X#, for
    /// one, sorts some tags case-blind), and rebuild every index in byte order straight away, like
    /// FoxPro's REINDEX with SET COLLATE TO "MACHINE". Any other reason to refuse still applies.
    /// </param>
    /// <exception cref="DbfException">The index can't be read, or its format can't be kept up to date.</exception>
    public void MaintainIndex(string path, bool rebuild = false)
    {
        EnsureWritable();
        if (rebuild)
            RequireExclusive("Rebuilding an index");
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        if (_attachedIndexes.ContainsKey(full))
        {
            // Already kept: asking for a rebuild still rebuilds.
            if (rebuild)
                RebuildIndex();
            return;
        }
        if (!File.Exists(full))
            throw new DbfException(DbfErrorCode.TableNotFound, "There's no index file at that path.", "Check the name and folder.", diagnostic: full);
        var hadStructuralCdx = HasMaintainedStructuralCdx;
        if (!TryAttachIndex(full, acceptAfterRebuild: rebuild))
            throw new DbfException(DbfErrorCode.IndexWouldGoStale, $"The index {name} can't be kept up to date (unreadable, or a format or sort order Thunder.Dbf doesn't write).",
                rebuild
                    ? "Rebuild it in the legacy app, or open with allowUnmaintainedIndexes: true if it will be rebuilt before use."
                    : "If it's only the sort order, MaintainIndex(path, rebuild: true) takes it over and rebuilds it in byte order. Otherwise rebuild it in the legacy app.", _path);
        var unmaintainedBefore = _unmaintainedIndexes;
        _unmaintainedIndexes = _unmaintainedIndexes.Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!rebuild)
            return;
        try
        {
            RebuildIndex();
        }
        catch
        {
            // A takeover whose rebuild failed leaves the index as it was (in an order or state these
            // writes can't keep), so it's let go again and blocks writes as it did before.
            var index = _attachedIndexes[full];
            _attachedIndexes.Remove(full);
            _otherIndexes.Remove(index);
            index.RebuildAbandoned();
            index.Dispose();
            HasMaintainedStructuralCdx = hadStructuralCdx;
            _unmaintainedIndexes = unmaintainedBefore;
            throw;
        }
    }

    private bool _maintainFoxProIndexes;
    private string? _structuralCdxRefusal; // why the structural .cdx wasn't attached, for the write refusal
    private bool _structuralCdxRebuildable; // and whether a takeover with a rebuild would take it

    private bool IsStructuralCdx(string file) =>
        Path.GetExtension(file).Equals(".cdx", StringComparison.OrdinalIgnoreCase) && _path is not null
        && Path.GetFileNameWithoutExtension(file).Equals(Path.GetFileNameWithoutExtension(_path), StringComparison.OrdinalIgnoreCase);

    // forJournal: a journal note is waiting, and a cut-off key change leaves an index out of step
    // with the table in just the way the note will put right. Such an index is attached on
    // probation: writes wait for the replay, which then checks it again.
    private bool TryAttachIndex(string file, bool acceptAfterRebuild = false, bool forJournal = false)
    {
        // FoxPro index writing is a preview, so it's only used when asked for.
        if (!_maintainFoxProIndexes && Path.GetExtension(file).ToLowerInvariant() is ".cdx" or ".idx")
            return false;
        {
            Stream? stream = null;
            try
            {
                stream = DbfException.OpenFile(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                IDbfIndex index = OpenAnyIndex(file, stream, Encoding, this);
                if (index is IMaintainedIndex maintained
                    && (maintained.CanMaintain || ((acceptAfterRebuild || forJournal) && maintained.CanMaintainAfterRebuild)))
                {
                    if (!maintained.CanMaintain)
                    {
                        maintained.ExpectRebuild();
                        if (!acceptAfterRebuild)
                            _awaitingReplay.Add(maintained);
                    }
                    _otherIndexes.Add(maintained);
                    _attachedIndexes[Path.GetFullPath(file)] = maintained;
                    if (IsStructuralCdx(file))
                    {
                        HasMaintainedStructuralCdx = true;
                        _structuralCdxRefusal = null;
                    }
                    stream = null; // the index owns it now
                    return true;
                }
                if (index is IMaintainedIndex refused && IsStructuralCdx(file))
                {
                    _structuralCdxRefusal = refused.CannotMaintainReason;
                    _structuralCdxRebuildable = refused.CanMaintainAfterRebuild;
                }
                index.Dispose();
                stream = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or FormatException)
            {
                // Unreadable: nobody can tell it doesn't belong, so it blocks writes like an unsupported one.
            }
            finally
            {
                stream?.Dispose();
            }
            return false;
        }
    }

    // ---- indexes that appear after opening ------------------------------------------------

    private HashSet<string> _indexFilesAtOpen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IMaintainedIndex> _attachedIndexes = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> IndexFileNames(string tablePath) =>
        Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(tablePath))!)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ndx" or ".ntx" or ".idx" or ".cdx" or ".mdx")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    // An index created next to the table after it was opened (by the legacy app's INDEX ON, say)
    // isn't attached, so writing would leave it stale. Only a new file name triggers the slower check.
    private void ThrowIfIndexAppeared()
    {
        if (_path is null || !_stream.CanWrite || _allowUnmaintainedIndexes)
            return;
        var now = IndexFileNames(_path);
        if (now.IsSubsetOf(_indexFilesAtOpen))
            return;
        var fresh = FindCompanionIndexes(_path, Header).Select(Path.GetFileName).Where(n => !_indexFilesAtOpen.Contains(n!)).ToList();
        if (fresh.Count > 0)
            throw new DbfException(DbfErrorCode.IndexWouldGoStale,
                $"An index that belongs to this table appeared after it was opened ({string.Join(", ", fresh)}), so writing would leave it out of date.",
                "Close and reopen the table so it can be kept up to date.", _path);
        _indexFilesAtOpen = now; // new files that don't belong to this table
    }

    // ---- failed index updates -------------------------------------------------------------

    private bool _indexesNeedRebuild;

    // After a failed index update the index may hold half a change, so no write may build on it.
    private void ThrowIfIndexesNeedRebuild()
    {
        if (_indexesNeedRebuild)
            throw new DbfException(DbfErrorCode.IndexDamaged, "An earlier write couldn't update this table's indexes, so they may be out of step.",
                "Close the legacy app, open the table with exclusive: true and call RebuildIndex() (or REINDEX in the legacy app) before writing again.", _path);
    }

    // An attached index another program has since rebuilt into something Thunder.Dbf can't keep (a
    // custom .ntx, say) stops writes, like one that was that way at open. Its tags are left alone.
    private void ThrowIfIndexCantBeMaintained()
    {
        if (_allowUnmaintainedIndexes || MaintainedIndexes.FirstOrDefault(i => !i.CanMaintain && !_awaitingReplay.Contains(i)) is not { } index)
            return;
        throw new DbfException(DbfErrorCode.IndexWouldGoStale,
            "One of this table's indexes was changed by another program into a kind Thunder.Dbf can't keep up to date.",
            "Rebuild it in the legacy app, or open with allowUnmaintainedIndexes: true if it will be rebuilt before use.", _path,
            diagnostic: index.CannotMaintainReason);
    }

    private static IDbfIndex OpenAnyIndexReadOnly(string path)
    {
        var stream = DbfException.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return OpenAnyIndex(path, stream, null, null);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static IDbfIndex OpenAnyIndex(string path, Stream stream, Encoding? encoding, DbfTable? table) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ndx" => NdxIndex.Open(stream, encoding, leaveOpen: false, table),
            ".ntx" => NtxIndex.Open(stream, encoding, leaveOpen: false, table),
            _ => CdxIndex.Open(stream, encoding, leaveOpen: false, table),
        };

    private void AttachIndex(Stream indexStream, bool leaveOpen = false)
    {
        try
        {
            _index = MdxIndex.Open(indexStream, Encoding, leaveOpen);
        }
        catch (InvalidDataException ex)
        {
            if (!leaveOpen)
                indexStream.Dispose();
            throw new DbfException(DbfErrorCode.IndexDamaged, $"The production .mdx index can't be read: {ex.Message}",
                "Rebuild it in the legacy app (REINDEX) or restore it from a backup before writing to this table.", _path, ex);
        }
    }

    // ---- locking ---------------------------------------------------------------------------

    private DbfLocks? _locks;
    private bool _exclusive;

    /// <summary>True when this table holds its .dbf so no other program can open it (see the <c>exclusive</c> parameter of Open).</summary>
    public bool IsExclusive => _exclusive;

    // PACK, REINDEX and INDEX ON rewrite pages and renumber records other programs may hold in their
    // caches, so dBASE and FoxPro only run them on a table opened EXCLUSIVE; so does Thunder.Dbf.
    // A table opened from a stream is the caller's to share or not.
    private void RequireExclusive(string operation)
    {
        if (_path is null || _exclusive)
            return;
        throw new DbfException(DbfErrorCode.ExclusiveUseRequired,
            $"{operation} needs the table opened exclusively, as dBASE and FoxPro require: other programs could be holding its records or index pages.",
            "Close the legacy app, then open the table with exclusive: true (Exclusive=true in a connection string).", _path);
    }
    internal DbfLocks Locks => _locks ??= new DbfLocks(_stream as FileStream, _path, _lockScheme, Header) { Timeout = _lockTimeout, RequireLocks = _requireLocks };
    private TimeSpan _lockTimeout = TimeSpan.FromSeconds(3);
    private bool _requireLocks;
    private DbfLockScheme _lockScheme = DbfLockScheme.Auto;

    /// <summary>
    /// Whose lock positions to use, so the legacy app sees this table's locks: dBASE (and GoldMine),
    /// Clipper, or Visual FoxPro. <see cref="DbfLockScheme.Auto"/> (the default) picks Visual FoxPro for
    /// FoxPro tables and dBASE otherwise; Clipper tables look like dBASE ones, so set Clipper
    /// explicitly. Set it before the first write or lock.
    /// </summary>
    public DbfLockScheme LockScheme
    {
        get => _locks?.Scheme ?? DbfLocks.Resolve(_lockScheme, Header);
        set
        {
            if (_locks is not null && _locks.Scheme != DbfLocks.Resolve(value, Header))
            {
                if (!_locks.HoldsNothing)
                    throw new InvalidOperationException("Set LockScheme while the table holds no lock.");
                _locks = null; // made again, with the new positions, by the next lock
            }
            _lockScheme = value;
        }
    }

    /// <summary>
    /// True when this table's writes take real dBASE-compatible locks that other programs see.
    /// False on macOS (.NET can't lock part of a file there) and for tables opened from memory streams.
    /// </summary>
    public bool LocksEnforced => Locks.Enforced;

    /// <summary>Refuse writes when locks can't be enforced, instead of writing without them. Off by default.</summary>
    public bool RequireLocks
    {
        get => _requireLocks;
        set
        {
            _requireLocks = value;
            if (_locks is not null)
                _locks.RequireLocks = value;
        }
    }

    /// <summary>
    /// How long a write waits for another program's lock on the same record or table before giving
    /// up with <see cref="DbfErrorCode.RecordLocked"/> or <see cref="DbfErrorCode.TableLocked"/>. Three seconds by default.
    /// </summary>
    public TimeSpan LockTimeout
    {
        get => _lockTimeout;
        set
        {
            _lockTimeout = value;
            if (_locks is not null)
                _locks.Timeout = value;
        }
    }

    /// <summary>
    /// Locks a record the way dBASE's RLOCK() does, so other dBASE-compatible programs can't change
    /// it until the returned lock is disposed. Writes lock records on their own; use this to keep a
    /// record yours across several steps (read, decide, update).
    /// </summary>
    public DbfLock LockRecord(int index)
    {
        Seek(index);
        return Locks.Record(index + 1, "locked");
    }

    /// <summary>Locks the whole table the way dBASE's FLOCK() does, until the returned lock is disposed.</summary>
    public DbfLock LockTable() => Locks.Table();

    /// <summary>Re-reads only the header's record count and counters, for an index checking itself against the table.</summary>
    internal void ReloadHeader()
    {
        if (_stream is not FileStream)
            return;
        DiscardReadBuffer(_stream);
        Header.Reload(_stream);
    }

    /// <summary>
    /// Re-reads the record count (and autoincrement counters) from disk, to see records other
    /// programs have added since the table was opened. Writes do this on their own, under lock.
    /// </summary>
    public void Refresh()
    {
        if (_stream is not FileStream)
            return;
        DiscardReadBuffer(_stream);
        Header.Reload(_stream);
        if (Header.RecordLength != _buffer.Length)
            throw new DbfException(DbfErrorCode.NotADbfFile, "Another program changed this table's structure while it was open.",
                "Close and reopen the table.", _path);
        foreach (var index in MaintainedIndexes)
            index.ReloadTags();
    }

    // Everything a write must re-check after taking its locks, because another program may have
    // changed the table since it was opened: header, index tags, blockers and the file's real size.
    private void PrepareWrite(bool appending)
    {
        Refresh();
        ThrowIfPackUnfinished();
        ThrowIfIndexesNeedRebuild();
        ThrowIfIndexAppeared();
        ThrowIfIndexCantBeMaintained();
        if (WriteBlocker(Header, _allowUnmaintainedIndexes, indexMaintained: _index is not null, _path, _unmaintainedIndexes,
                structuralCdxMaintained: HasMaintainedStructuralCdx, structuralCdxRefusal: _maintainFoxProIndexes ? _structuralCdxRefusal : null) is { } blocker)
            throw blocker;
        ReplayJournal(); // a key change cut off in this or another process; every caller holds the header lock

        var dataEnd = Header.HeaderLength + (long)Header.RecordCount * Header.RecordLength;
        if (dataEnd > _stream.Length)
            throw new DbfException(DbfErrorCode.NotADbfFile,
                $"The table's header counts {Header.RecordCount:N0} records, but the file is too short to hold them.",
                "The file was probably truncated by an interrupted copy. Restore it from a backup before writing.", _path);
        if (appending && dataEnd + Header.RecordLength + 1 > MaxTableBytes)
            throw new DbfException(DbfErrorCode.InvalidValue, "The table has reached the 2 GB size dBASE and FoxPro can handle.",
                "Archive or pack older records before adding more.", _path);
    }

    // An append writes the record, then the header's count, then the keys. Cut off after the first,
    // it leaves whole records past the count, and Visual FoxPro calls the table corrupted (error
    // 2091). Bytes past the count can also be another program's append in progress (Harbour, for
    // one, can publish the count later, after releasing its lock), so only a table opened
    // exclusively, where nobody else can be writing, takes them off. Only bytes that look like
    // records (each starting with a delete flag, then at most the 0x1A end mark) count.
    private long UnfinishedAppends(long dataEnd)
    {
        if (_stream is not FileStream)
            return 0;
        var extra = _stream.Length - dataEnd;
        if (extra < Header.RecordLength)
            return 0;
        var whole = extra / Header.RecordLength;
        var tail = extra - whole * Header.RecordLength;
        if (tail > 1)
            return 0;
        Span<byte> one = stackalloc byte[1];
        for (var i = 0L; i < whole; i++)
        {
            _stream.Position = dataEnd + i * Header.RecordLength;
            _stream.ReadExactly(one);
            if (one[0] is not ((byte)' ' or (byte)'*'))
                return 0;
        }
        if (tail == 1)
        {
            _stream.Position = _stream.Length - 1;
            _stream.ReadExactly(one);
            if (one[0] != EndOfFile)
                return 0;
        }
        return whole;
    }

    /// <summary>How many whole records sit past the header's count, read fresh from disk: an append cut off, or one in progress.</summary>
    internal long RecordsPastCount()
    {
        ReloadHeader();
        DiscardReadBuffer(_stream);
        return UnfinishedAppends(Header.HeaderLength + (long)Header.RecordCount * Header.RecordLength);
    }

    // On a writable exclusive open: takes records left past the count back off and rebuilds every
    // index, since a writer cut off may already have added keys for them, and a key left behind would
    // point at whatever record is appended there next. So nothing is taken off unless every index
    // next to the table is kept here or can be taken over with a rebuild; otherwise the records stay,
    // Check() keeps reporting them, and the indexes that can't be rebuilt keep blocking writes.
    private void RepairUnfinishedAppends()
    {
        if (!_exclusive || !_stream.CanWrite || RecordsPastCount() == 0)
            return;
        var takeOverCdx = Header.HasStructuralCdx && !HasMaintainedStructuralCdx;
        if (_unmaintainedIndexes.Any(name => !IsStructuralCdx(name))
            || (Header.HasProductionMdx && _index is null)
            || (takeOverCdx && !(_maintainFoxProIndexes && _structuralCdxRebuildable && FindCompanion(_path!, ".cdx") is not null)))
            return;

        var dataEnd = Header.HeaderLength + (long)Header.RecordCount * Header.RecordLength;
        _stream.SetLength(dataEnd);
        _stream.Position = dataEnd;
        _stream.WriteByte(EndOfFile);
        FlushToDisk(_stream);
        if (takeOverCdx)
            MaintainIndex(FindCompanion(_path!, ".cdx")!, rebuild: true); // rebuilds every index the table keeps
        else if (MaintainsIndex)
            RebuildIndex();
    }

    /// <summary>The table's file path, or null for a stream. For plans and logs, never for messages.</summary>
    internal string? FilePath => _path;

    /// <summary>True when writes keep at least one index up to date (a production .mdx, a structural .cdx, or an attached .ndx/.ntx/.idx).</summary>
    public bool MaintainsIndex => MaintainedIndexes.Any();

    private bool HasMaintainedStructuralCdx { get; set; }

    /// <summary>Creates a new, empty .dbf file, plus a dBASE IV .dbt when there are memo fields. Fails if either exists.</summary>
    public static DbfTable Create(string path, IReadOnlyList<DbfFieldDefinition> fields, byte languageDriver = 0x57)
    {
        DbfHeader.Create(fields, languageDriver); // validate before touching the disk
        var memoPath = fields.Any(f => char.ToUpperInvariant(f.Type) == 'M') ? Path.ChangeExtension(path, ".dbt") : null;

        // Opening finds companions case-insensitively, so creating must refuse them the same way:
        // a stray PEOPLE.DBT or PEOPLE.MDX would otherwise be picked up as this table's.
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var baseName = Path.GetFileNameWithoutExtension(path);
        var existing = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).FirstOrDefault(p =>
                Path.GetFileNameWithoutExtension(p).Equals(baseName, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(p).ToLowerInvariant() is ".dbf" or ".dbt" or ".fpt" or ".mdx" or ".cdx" or ".cpg")
            : null;
        if (existing is not null)
            throw new DbfException(DbfErrorCode.TableAlreadyExists, $"'{Path.GetFileName(existing)}' already exists next to the new table.",
                "Pick another table name, or move the old files away first; Thunder.Dbf won't adopt or overwrite another table's files.", existing);

        // Unshared, like a table opened EXCLUSIVE, so INDEX ON and PACK can follow straight away.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        FileStream? memoStream = null;
        try
        {
            if (memoPath is not null)
                memoStream = new FileStream(memoPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);

            var table = Create(stream, fields, languageDriver, leaveOpen: false, memoStream, Path.GetFileName(path));
            table._path = path; // so locks, INDEX ON and PACK know where the table lives
            table._exclusive = true; // brand new, and held unshared
            return table;
        }
        catch
        {
            memoStream?.Dispose();
            stream.Dispose();
            File.Delete(path);
            if (memoStream is not null)
                File.Delete(memoPath!);
            throw;
        }
    }

    /// <summary>
    /// Writes a new, empty table into <paramref name="stream"/>, replacing anything already there.
    /// Tables with memo fields need a <paramref name="memoStream"/>, which gets an empty dBASE IV .dbt.
    /// </summary>
    public static DbfTable Create(Stream stream, IReadOnlyList<DbfFieldDefinition> fields, byte languageDriver = 0x57,
        bool leaveOpen = false, Stream? memoStream = null, string tableName = "TABLE")
    {
        var header = DbfHeader.Create(fields, languageDriver);
        if (header.HasMemo && memoStream is null)
            throw new ArgumentException("Tables with memo fields need a memo stream.", nameof(memoStream));

        stream.Position = 0;
        stream.SetLength(0);
        header.WriteFull(stream);
        stream.WriteByte(EndOfFile);
        stream.Flush();

        var memo = header.HasMemo ? DbfMemoFile.CreateDbaseIV(memoStream!, tableName, leaveOpen) : null;
        return new DbfTable(stream, header, DbfEncoding.For(languageDriver), leaveOpen, memo,
            ignoreMissingMemo: false, allowUnmaintainedIndexes: false);
    }

    /// <summary>Reads one record, fresh from disk (another program may have just changed it).</summary>
    public DbfRecord Read(int index)
    {
        DiscardReadBuffer(_stream);
        return ReadBuffered(index);
    }

    // Reads through FileStream's buffer: fast for scans, but only fresh as of the last discard.
    private DbfRecord ReadBuffered(int index)
    {
        Seek(index);
        _stream.ReadExactly(_buffer);
        return Decode(index, _buffer);
    }

    /// <summary>
    /// FileStream keeps what it last read in a buffer and serves later reads from it, which would
    /// hide changes other programs make. Flushing a stream that's been reading drops that buffer.
    /// </summary>
    internal static void DiscardReadBuffer(Stream stream)
    {
        if (stream is FileStream)
            stream.Flush();
    }

    /// <summary>Reads every record in order, deleted ones included (check <see cref="DbfRecord.IsDeleted"/>).</summary>
    public IEnumerable<DbfRecord> ReadAll()
    {
        DiscardReadBuffer(_stream);
        for (var i = 0; i < RecordCount; i++)
            yield return ReadBuffered(i);
    }

    /// <summary>
    /// Adds a record with values in field order and returns its zero-based index. A null value in a
    /// Visual FoxPro autoincrement field takes the table's next number.
    /// </summary>
    public int Append(params object?[] values)
    {
        EnsureWritable();
        using var header = Locks.Header();
        using var append = Locks.Append();
        PrepareWrite(appending: true);
        var index = AppendOne(values);
        Commit();
        return index;
    }

    /// <summary>
    /// Adds many records quickly: rows are written in sequence and the header is committed every
    /// <paramref name="commitEvery"/> rows and at the end, instead of after each one. If the process
    /// dies midway, the table holds every row up to the last commit. Returns the number of rows added.
    /// </summary>
    public int AppendRange(IEnumerable<object?[]> rows, int commitEvery = 10_000)
    {
        EnsureWritable();
        ArgumentOutOfRangeException.ThrowIfLessThan(commitEvery, 1);
        using var header = Locks.Header();
        using var append = Locks.Append();
        PrepareWrite(appending: true);

        var added = 0;
        try
        {
            foreach (var values in rows)
            {
                if (Header.HeaderLength + (long)(Header.RecordCount + 1) * Header.RecordLength + 1 > MaxTableBytes)
                    throw new DbfException(DbfErrorCode.InvalidValue, "The table has reached the 2 GB size dBASE and FoxPro can handle.",
                        "Archive or pack older records before adding more.", _path);
                AppendOne(values);
                if (++added % commitEvery == 0)
                    Commit();
            }
        }
        catch
        {
            // Rows before the failing one stay: commit them, but never let a failed commit hide the
            // original error. RecordCount then says how many rows made it. The failing row was never
            // counted: AppendOne counts a row only once everything about it has been written.
            try
            {
                Commit();
            }
            catch (Exception commitFailure) when (commitFailure is IOException or UnauthorizedAccessException)
            {
                // The original exception is what the caller needs to see.
            }
            throw;
        }

        if (added % commitEvery != 0)
            Commit();
        return added;
    }

    // Writes one record after the last, without committing the header.
    private int AppendOne(object?[] values)
    {
        CheckValueCount(values);
        var index = RecordCount;

        values = (object?[])values.Clone();
        var counters = AssignAutoIncrements(values);

        _buffer[0] = LiveFlag;
        EncodeFields(values, changed: null);
        var keys = PlanIndexChanges(before: null, after: !MaintainedIndexes.Any() ? null : Decode(index, _buffer.ToArray()));

        // The first row of a batch notes where the batch starts, before any of it is written.
        if (_useJournal && _path is not null && !_appendNoted)
        {
            DbfJournal.Write(_path, new DbfJournalEntry(index + 1,
                TagsWithFiles().Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase).Select(f => new DbfJournalChange(f, "", null, null)).ToList(),
                IsAppend: true));
            _appendNoted = true;
        }

        Seek(index, forAppend: true);
        _stream.Write(_buffer);
        CrashPoint?.Invoke("appended record");

        // The row's index entries wait for the commit, after the header counts the row: a crash in
        // between leaves keys missing (RebuildIndex repairs that), never keys for an uncounted row.
        Header.RecordCount++;
        if (keys is not null)
            _pendingIndexChanges.Add((keys, index + 1));
        foreach (var (field, next) in counters)
        {
            Header.SetNextAutoIncrement(field, next);
            _dirtyCounters.Add(field);
        }
        return index;
    }

    // Makes everything appended so far durable: end-of-file marker, counters and the header (which
    // makes the rows visible), then the index entries for those rows.
    private void Commit()
    {
        Seek(RecordCount, forAppend: true);
        _stream.WriteByte(EndOfFile);
        foreach (var field in _dirtyCounters)
            Header.WriteAutoIncrement(_stream, field);
        _dirtyCounters.Clear();
        CommitHeader();
        CrashPoint?.Invoke("counted");

        var pending = _pendingIndexChanges.ToList();
        _pendingIndexChanges.Clear();
        foreach (var (changes, recordNumber) in pending)
            ApplyIndexChanges(changes, recordNumber);
        CommitIndexes();
        if (_appendNoted)
        {
            DbfJournal.Clear(_path!);
            _appendNoted = false;
        }
    }

    private bool _appendNoted; // the current batch of appends has its note

    private void CommitIndexes()
    {
        foreach (var index in MaintainedIndexes)
            index.Commit();
    }

    /// <summary>
    /// Replaces the values of an existing record. Its deletion flag is kept, and fields (and memos)
    /// whose values didn't change keep their original bytes, so other apps' formatting survives.
    /// </summary>
    /// <remarks>
    /// Fields are compared with the record as it is on disk when the lock is taken. If you read the
    /// record earlier and another program changed it since, its change is overwritten for every
    /// field in <paramref name="values"/>: use <see cref="UpdateFields"/> to change only some fields.
    /// </remarks>
    public void Update(int index, params object?[] values)
    {
        EnsureWritable();
        CheckValueCount(values);
        using var record = Locks.Record(index + 1, "changed");
        using var header = Locks.Header();
        PrepareWrite(appending: false);
        UpdateCore(index, Read(index), values);
    }

    /// <summary>
    /// Changes only the named fields of a record, leaving every other field as it is on disk at that
    /// moment, even if another program changed it after you last read it. The record is locked,
    /// re-read, changed and written in one step. Returns the record before and after the change.
    /// </summary>
    public (DbfRecord Before, DbfRecord After) UpdateFields(int index, IReadOnlyDictionary<string, object?> changes)
    {
        EnsureWritable();
        using var record = Locks.Record(index + 1, "changed");
        using var header = Locks.Header();
        PrepareWrite(appending: false);

        var current = Read(index);
        var values = current.Values.ToArray();
        foreach (var (name, value) in changes)
        {
            var i = FieldIndex(name) ?? throw new ArgumentException($"The table has no field named '{name}'.", nameof(changes));
            values[i] = value;
        }
        UpdateCore(index, current, values);
        return (current, Read(index));
    }

    private int? FieldIndex(string name)
    {
        for (var i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return null;
    }

    // Writes a record's new values under the caller's locks. Order: memos (inside EncodeFields), the
    // new index entries, the record, then the old entries go; each reaches the disk before the next.
    private void UpdateCore(int index, DbfRecord current, object?[] values)
    {
        Seek(index);
        _stream.ReadExactly(_buffer); // the on-disk bytes, which unchanged fields keep
        var changed = new bool[Fields.Count];
        for (var i = 0; i < Fields.Count; i++)
            changed[i] = !SameValue(current.Values[i], values[i]);

        values = (object?[])values.Clone();
        var counters = AssignAutoIncrements(values, changed);
        EncodeFields(values, changed);
        var keys = PlanIndexChanges(current, !MaintainedIndexes.Any() ? null : Decode(index, _buffer.ToArray()));

        _memo?.FlushToDisk();
        var journaled = ChangeRecordAndKeys(keys, index + 1, () =>
        {
            Seek(index);
            _stream.Write(_buffer);
            FlushToDisk(_stream);
        });
        CommitCounters(counters);
        CommitAfterKeyChange(journaled);
    }

    /// <summary>
    /// Opens the production .mdx that sits next to the table, or returns null when the table has
    /// none (or was opened from a stream; use <see cref="MdxIndex.Open(Stream, Encoding?, bool)"/> then).
    /// </summary>
    public MdxIndex? OpenProductionIndex()
    {
        if (!Header.HasProductionMdx || _path is null || MdxIndex.FindFor(_path) is not { } indexPath)
            return null;
        if (RefuseLinks)
            DbfDatabase.EnsureNotLink(indexPath);
        return MdxIndex.Open(indexPath, Encoding);
    }

    /// <summary>
    /// Opens the FoxPro structural .cdx that sits next to the table (read-only), or returns null
    /// when the table has none or was opened from a stream.
    /// </summary>
    public CdxIndex? OpenStructuralIndex()
    {
        if (!Header.HasStructuralCdx || _path is null || CdxIndex.FindFor(_path) is not { } indexPath)
            return null;
        if (RefuseLinks)
            DbfDatabase.EnsureNotLink(indexPath);
        return CdxIndex.Open(indexPath, Encoding, this);
    }

    /// <summary>
    /// Opens any index for this table by its file type: dBASE IV .mdx, dBASE III .ndx, Clipper .ntx,
    /// or FoxPro .cdx/.idx. A relative path is taken from the table's folder. The table is passed
    /// along, so key types and text encoding come out right.
    /// </summary>
    public IDbfIndex OpenIndex(string path)
    {
        if (!Path.IsPathRooted(path) && _path is not null)
            path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!, path);
        if (RefuseLinks)
            DbfDatabase.EnsureNotLink(path);

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mdx" => MdxIndex.Open(path, Encoding),
            ".ndx" => NdxIndex.Open(path, Encoding, this),
            ".ntx" => NtxIndex.Open(path, Encoding, this),
            ".cdx" or ".idx" => CdxIndex.Open(path, Encoding, this),
            var other => throw new DbfException(DbfErrorCode.UnsupportedFormat, $"'{other}' isn't an index format Thunder.Dbf reads.",
                "Index files end in .mdx (dBASE IV), .ndx (dBASE III), .ntx (Clipper), or .cdx/.idx (FoxPro).", path),
        };
    }

    /// <summary>Reads records in the tag's key order (any index format).</summary>
    public IEnumerable<DbfRecord> ReadInOrder(IDbfIndexTag tag) => tag.Entries().Select(e => Read(e.RecordNumber - 1));

    /// <summary>The first record whose key equals <paramref name="key"/>, through any index format.</summary>
    public DbfRecord? Seek(IDbfIndexTag tag, object? key)
    {
        foreach (var entry in tag.SeekExact(key))
            return Read(entry.RecordNumber - 1);
        return null;
    }

    /// <summary>Reads records in the tag's key order, skipping any the tag's FOR filter left out.</summary>
    public IEnumerable<DbfRecord> ReadInOrder(MdxTag tag) => tag.Entries().Select(e => Read(e.RecordNumber - 1));

    /// <summary>The first record whose key equals <paramref name="key"/> (prefix match for character keys), like dBASE's SEEK.</summary>
    public DbfRecord? Seek(MdxTag tag, object key)
    {
        foreach (var entry in tag.SeekExact(key))
            return Read(entry.RecordNumber - 1);
        return null;
    }

    /// <summary>Marks a record deleted, like dBASE's DELETE. The row stays in the file until a pack.</summary>
    public void Delete(int index) => SetDeletionFlag(index, DeletedFlag);

    /// <summary>Clears the deletion mark, like dBASE's RECALL.</summary>
    public void Recall(int index) => SetDeletionFlag(index, LiveFlag);

    /// <inheritdoc />
    public void Dispose()
    {
        _memo?.Dispose();
        _index?.Dispose();
        foreach (var index in _otherIndexes)
            index.Dispose();
        if (_leaveOpen)
            return;
        if (_stream is FileStream file)
            DbfLocks.CloseWhenSafe(file); // on Linux, closing early would drop this process's locks
        else
            _stream.Dispose();
    }

    private DbfRecord Decode(int index, byte[] record)
    {
        var nullFlags = Header.NullFlagsFieldIndex is { } n
            ? record.AsSpan(Fields[n].Offset, Fields[n].Length)
            : Span<byte>.Empty;

        var values = new object?[Fields.Count];
        bool[]? nulls = null;
        for (var i = 0; i < values.Length; i++)
        {
            var field = Fields[i];
            if (field.NullBit is { } bit && IsBitSet(nullFlags, bit))
            {
                (nulls ??= new bool[values.Length])[i] = true;
                continue;
            }

            object? value;
            try
            {
                value = DbfValueCodec.Decode(field, record.AsSpan(field.Offset, field.Length), Encoding);
            }
            catch (FormatException ex)
            {
                throw new DbfException(DbfErrorCode.NotADbfFile,
                    $"Record {index + 1}, field {field.Name} holds bytes that aren't a valid {field.Type} value.",
                    "The record is damaged. Fix it in the legacy app or restore the table from a backup.", _path, ex,
                    diagnostic: $"Raw value: {Convert.ToHexString(record.AsSpan(field.Offset, field.Length))}.");
            }
            values[i] = DbfValueCodec.IsMemoField(field) ? ReadMemo(field, value as int?) : value;
        }

        return new DbfRecord(Header, index, record[0] == DeletedFlag, values, Encoding)
        {
            NullFlags = nulls,
            Raw = ReferenceEquals(record, _buffer) ? record.ToArray() : record, // _buffer is reused by the next read
        };
    }

    private object? ReadMemo(DbfField field, int? block)
    {
        if (block is null || _memo is null)
            return null;

        DbfMemo memo;
        try
        {
            memo = _memo.Read(block.Value);
        }
        catch (InvalidDataException ex)
        {
            throw new DbfException(DbfErrorCode.MemoFileDamaged, $"Memo field {field.Name} points at a damaged memo: {ex.Message}",
                "The memo file is corrupt or doesn't belong to this table. Restore it from a backup.", inner: ex);
        }
        var isText = field.Type == 'M' && !field.IsBinary && memo.BlockType == DbfMemoFile.TextBlock;
        return isText ? Encoding.GetString(memo.Data) : memo.Data;
    }

    // Encodes the changed fields (all of them when changed is null) into _buffer. Scalar fields go
    // first so a bad value throws before any memo is written; memos follow; null bits come last.
    private void EncodeFields(object?[] values, bool[]? changed)
    {
        for (var i = 0; i < Fields.Count; i++)
        {
            var field = Fields[i];
            if ((changed is null || changed[i]) && !DbfValueCodec.IsMemoField(field) && i != Header.NullFlagsFieldIndex)
                DbfValueCodec.Encode(field, values[i], _buffer.AsSpan(field.Offset, field.Length), _strictEncoding);
        }

        // Validate every memo before writing any, so a bad second memo can't leave the first one orphaned.
        var pending = new List<(int Index, PendingMemo Memo)>();
        for (var i = 0; i < Fields.Count; i++)
        {
            if ((changed is null || changed[i]) && DbfValueCodec.IsMemoField(Fields[i]))
                pending.Add((i, PrepareMemo(Fields[i], values[i])));
        }
        foreach (var (i, memo) in pending)
        {
            var field = Fields[i];
            object? pointer = memo.Data is null ? memo.Pointer : _memo!.Write(memo.Data, memo.BlockType);
            DbfValueCodec.Encode(field, pointer, _buffer.AsSpan(field.Offset, field.Length), Encoding);
        }

        if (Header.NullFlagsFieldIndex is not { } n)
            return;

        var flagsField = Fields[n];
        var nullFlags = _buffer.AsSpan(flagsField.Offset, flagsField.Length);
        if (changed is null)
            DbfValueCodec.Encode(flagsField, values[n], nullFlags, Encoding);
        for (var i = 0; i < Fields.Count; i++)
        {
            if (Fields[i].NullBit is { } bit && (changed is null || changed[i]))
                SetBit(nullFlags, bit, values[i] is null);
        }
    }

    private readonly record struct PendingMemo(int? Pointer, byte[]? Data, int BlockType);

    // Works out what a memo value will store, checking everything that could fail before any write.
    private PendingMemo PrepareMemo(DbfField field, object? value)
    {
        if (value is int or long)
            return new(DbfValueCodec.CheckedMemoBlock(field, value), null, 0); // a raw pointer, for callers managing the memo file themselves

        var data = value switch
        {
            null => null,
            string text => DbfValueCodec.StrictBytes(field, text, _strictEncoding),
            byte[] bytes => bytes,
            _ => throw new ArgumentException($"Memo field {field.Name} needs a string or byte[], not {value.GetType().Name}."),
        };
        if (data is null || data.Length == 0)
            return new(null, null, 0);

        if (_memo is null)
            throw new DbfException(DbfErrorCode.MemoFileMissing, $"Can't write memo field {field.Name}: the table's memo file isn't open.",
                "Open the table with its .dbt/.fpt next to it (not with ignoreMissingMemo) to write memos.", _path);

        var blockType = field.Type switch
        {
            'G' => DbfMemoFile.ObjectBlock,
            'P' => DbfMemoFile.PictureBlock,
            _ => DbfMemoFile.TextBlock,
        };
        _memo.Validate(data, blockType);
        return new(null, data, blockType);
    }

    // Fills null autoincrement values from the header's counters and returns the counters to save.
    private List<(int Field, int Next)> AssignAutoIncrements(object?[] values, bool[]? changed = null)
    {
        var counters = new List<(int, int)>();
        for (var i = 0; i < Fields.Count; i++)
        {
            var field = Fields[i];
            if (!field.IsAutoIncrement || (changed is not null && !changed[i]))
                continue;

            var next = Header.NextAutoIncrement(i);
            var step = Math.Max(1, field.AutoIncrementStep);
            if (values[i] is null && changed is null)
            {
                if ((long)next + step > int.MaxValue)
                    throw new InvalidOperationException($"Autoincrement field {field.Name} has run out of numbers (next is {next}, step {step}).");
                values[i] = next;
                counters.Add((i, next + step));
            }
            else if (values[i] is not null && Convert.ToInt64(values[i]) >= next)
            {
                // An explicit value at or past the counter moves it, so VFP won't hand the same number out later.
                var explicitValue = Convert.ToInt64(values[i]);
                if (explicitValue + step > int.MaxValue)
                    throw new InvalidOperationException($"Autoincrement field {field.Name} can't advance past {explicitValue}; the counter would overflow.");
                counters.Add((i, (int)(explicitValue + step)));
            }
        }
        return counters;
    }

    private void CommitCounters(List<(int Field, int Next)> counters)
    {
        foreach (var (field, next) in counters)
        {
            Header.SetNextAutoIncrement(field, next);
            Header.WriteAutoIncrement(_stream, field);
        }
    }

    private static bool SameValue(object? stored, object? incoming) =>
        stored is byte[] a && incoming is byte[] b ? a.AsSpan().SequenceEqual(b) : Equals(stored, incoming);

    private static bool IsBitSet(ReadOnlySpan<byte> bits, int bit) =>
        bit / 8 < bits.Length && (bits[bit / 8] & (1 << (bit % 8))) != 0;

    private static void SetBit(Span<byte> bits, int bit, bool on)
    {
        if (bit / 8 >= bits.Length)
            return;
        if (on)
            bits[bit / 8] |= (byte)(1 << (bit % 8));
        else
            bits[bit / 8] &= (byte)~(1 << (bit % 8));
    }

    private void SetDeletionFlag(int index, byte flag)
    {
        EnsureWritable();
        using var record = Locks.Record(index + 1, flag == DeletedFlag ? "deleted" : "recalled");
        using var header = Locks.Header();
        PrepareWrite(appending: false);
        var before = Read(index); // leaves the record's bytes in _buffer
        _buffer[0] = flag;
        var keys = PlanIndexChanges(before, !MaintainedIndexes.Any() ? null : Decode(index, _buffer.ToArray()));

        var journaled = ChangeRecordAndKeys(keys, index + 1, () =>
        {
            Seek(index);
            _stream.WriteByte(flag);
            FlushToDisk(_stream);
        });
        CommitAfterKeyChange(journaled);
    }

    // ---- index maintenance ----------------------------------------------------------------

    private readonly record struct TagChange(IMaintainedTag Tag, byte[]? Remove, byte[]? Insert);

    // Works out every tag's change before anything is written, so a key expression that fails
    // (a type mismatch, say) leaves the table and its index untouched.
    private List<TagChange>? PlanIndexChanges(DbfRecord? before, DbfRecord? after)
    {
        if (after is null || !MaintainedIndexes.Any())
            return null;

        var changes = new List<TagChange>();
        foreach (var tag in MaintainedIndexes.SelectMany(i => i.MaintainedTags))
        {
            var wasIn = before is not null && tag.Includes(before);
            var isIn = tag.Includes(after);
            var oldKey = wasIn ? tag.KeyFor(before!) : null;
            var newKey = isIn ? tag.KeyFor(after) : null;
            if (oldKey is not null && newKey is not null && oldKey.AsSpan().SequenceEqual(newKey))
                continue;
            if (newKey is not null && tag.RefusesDuplicates)
                ThrowIfKeyTaken(tag, newKey, after.RecordNumber);
            if (oldKey is not null || newKey is not null)
                changes.Add(new TagChange(tag, oldKey, newKey));
        }
        return changes;
    }

    // A candidate key is checked against what the tag will hold once this batch's planned changes
    // are applied, not only what's in it now: AppendRange applies its rows' keys at the commit, so
    // two rows of one batch with the same key would otherwise both get in.
    private void ThrowIfKeyTaken(IMaintainedTag tag, byte[] key, int recordNumber)
    {
        var holders = new HashSet<int>();
        if (tag.FirstRecordFor(key) is { } holder)
            holders.Add(holder);
        foreach (var (changes, pendingRecord) in _pendingIndexChanges)
        {
            foreach (var change in changes)
            {
                if (!ReferenceEquals(change.Tag, tag))
                    continue;
                if (change.Remove is { } removed && removed.AsSpan().SequenceEqual(key))
                    holders.Remove(pendingRecord);
                if (change.Insert is { } inserted && inserted.AsSpan().SequenceEqual(key))
                    holders.Add(pendingRecord);
            }
        }
        holders.Remove(recordNumber);
        if (holders.Count > 0)
            throw DuplicateKey(tag, holders.Min());
    }

    private static DbfException DuplicateKey(IMaintainedTag tag, int holder) =>
        new(DbfErrorCode.InvalidValue, $"Uniqueness of index {tag.Name} would be violated: record {holder} already has this key.",
            $"Give the record a {tag.Name} value no other record has; FoxPro refuses duplicates in a candidate or primary key the same way.");

    // A rebuild's keys are checked on their own, before the index is touched: the old tree says
    // nothing about which records share a key now.
    private static void ThrowIfRebuiltKeysRepeat(Dictionary<IMaintainedTag, List<(byte[] Key, int Record)>> keysByTag)
    {
        foreach (var (tag, keys) in keysByTag)
        {
            if (!tag.RefusesDuplicates)
                continue;
            var seen = new Dictionary<string, int>();
            foreach (var (key, record) in keys)
            {
                if (!seen.TryAdd(Convert.ToHexString(key), record))
                    throw DuplicateKey(tag, seen[Convert.ToHexString(key)]);
            }
        }
    }

    private enum KeyPhase { All, NewKeys, OldKeys }

    // Changing a record's keys goes new keys, record, old keys: each step reaches the disk before the
    // next, so a crash in between leaves the record with an extra key (still found by its new key and,
    // until the old one goes, its old key too), never without one. UNIQUE tags are the exception:
    // there a key belongs to one record, and adding it first would hand it over before the record
    // says so, so they change after the record, old key then new, as before. Appends write the record
    // first too, since a key for a record past the count would send other programs past the end of
    // the table. Returns whether a journal note was written, for the caller to clear once committed.
    private bool ChangeRecordAndKeys(List<TagChange>? keys, int recordNumber, Action writeRecord)
    {
        var journaled = _useJournal && _path is not null && keys is { Count: > 0 };
        if (journaled)
            DbfJournal.Write(_path!, new DbfJournalEntry(recordNumber,
                keys!.Select(k => new DbfJournalChange(IndexFileOf(k.Tag), k.Tag.Name, k.Remove, k.Insert)).ToList()));

        ApplyIndexChanges(keys, recordNumber, KeyPhase.NewKeys);
        try
        {
            if (keys is not null && keys.Any(k => k.Insert is not null && !k.Tag.IsUnique))
                CommitIndexes();
            CrashPoint?.Invoke("new keys");
            writeRecord();
            CrashPoint?.Invoke("record");
        }
        catch (IOException ex)
        {
            // New keys may be on disk for a record that isn't: the index no longer matches the table.
            _indexesNeedRebuild = true;
            throw new DbfException(DbfErrorCode.IndexDamaged, $"Record {recordNumber} couldn't be written after its new index entries were.",
                "Close the legacy app, open the table with exclusive: true and run RebuildIndex() before writing again.", _path, ex);
        }
        ApplyIndexChanges(keys, recordNumber, KeyPhase.OldKeys);
        return journaled;
    }

    // A failed commit leaves the index's state on disk unknown, so writes wait for a rebuild; the note
    // stays, and is cleared only once everything it covers is committed.
    private void CommitAfterKeyChange(bool journaled)
    {
        try
        {
            CommitHeader();
        }
        catch (IOException)
        {
            _indexesNeedRebuild = true;
            throw;
        }
        if (journaled)
            DbfJournal.Clear(_path!);
    }

    // The index file a tag lives in, by name, so a journal note finds the same tag in another process.
    private string IndexFileOf(IMaintainedTag tag)
    {
        foreach (var (file, index) in _attachedIndexes)
        {
            if (index.MaintainedTags.Contains(tag))
                return Path.GetFileName(file);
        }
        return _index is not null && _path is not null && MdxIndex.FindFor(_path) is { } mdx ? Path.GetFileName(mdx) : "";
    }

    private IEnumerable<(string File, IMaintainedTag Tag)> TagsWithFiles() =>
        MaintainedIndexes.SelectMany(i => i.MaintainedTags).Select(t => (IndexFileOf(t), t));

    private bool _useJournal;
    private bool _journalPending; // a note was waiting when the table opened
    private readonly HashSet<IMaintainedIndex> _awaitingReplay = [];

    /// <summary>For the tests: called between a key change's steps, where throwing stands in for a killed process.</summary>
    internal Action<string>? CrashPoint { get; set; }

    /// <summary>
    /// Opt-in: note each record's key changes, and each batch of appends, in <c>TABLE.DBF.journal</c> before making
    /// them, so an update, delete, recall or append cut off partway (a crash, a killed process) is finished by the next
    /// write, here or in another Thunder.Dbf process keeping the same indexes. Costs two extra forced disk writes (the
    /// note and its clearing) per such write or batch. Without it, a cut-off key change leaves the record with an
    /// extra key (never a missing one, UNIQUE tags aside) until a rebuild, and an append cut off before its count
    /// waits for an exclusive open.
    /// </summary>
    public bool UseJournal
    {
        get => _useJournal;
        set
        {
            if (value && _path is null)
                throw new InvalidOperationException("Only a table opened by path can keep a journal next to it.");
            _useJournal = value;
        }
    }

    /// <summary>True when the table's journal holds a note: a write was cut off, or another Thunder.Dbf process is mid-write.</summary>
    public bool HasPendingJournal => _path is not null && DbfJournal.IsPending(_path);

    // Finishes a key change a writer was cut off in the middle of. Every journaled write holds the
    // header lock, which the caller holds now; the record is locked too, against legacy programs
    // editing it meanwhile (without waiting: whoever holds it may be waiting for this header lock,
    // and will finish the note itself). Each noted change is matched to exactly one kept tag (index
    // file, tag name, key length); a note this table can't match stops writes, rather than being
    // written over, until the table is opened with those indexes or they're all rebuilt. Keys the
    // record has are added and forced to disk before stale ones go, so none goes missing on the way.
    private void ReplayJournal()
    {
        if (_path is null || !_stream.CanWrite)
            return;
        if (!DbfJournal.IsPending(_path))
        {
            RecheckAfterReplay();
            return;
        }

        DbfJournalEntry? entry;
        try
        {
            entry = DbfJournal.Read(_path);
        }
        catch (InvalidDataException ex)
        {
            throw new DbfException(DbfErrorCode.IndexWouldGoStale, "The table's journal holds a note in a format this version doesn't read.",
                "Close the legacy app, open the table with exclusive: true and run RebuildIndex(), which clears it.", _path, ex);
        }
        if (entry is null)
        {
            DbfJournal.Clear(_path); // torn: cut off while noting, before any index change
            RecheckAfterReplay();
            return;
        }
        if (entry.IsAppend)
        {
            ReplayAppend(entry);
            DbfJournal.Clear(_path);
            RecheckAfterReplay();
            return;
        }

        var record = entry.RecordNumber >= 1 && entry.RecordNumber <= RecordCount ? Read(entry.RecordNumber - 1) : null;
        var tags = TagsWithFiles().ToList();
        var matched = new List<(DbfJournalChange Change, IMaintainedTag Tag)>();
        foreach (var change in entry.Changes)
        {
            var candidates = tags.Where(t => string.Equals(t.File, change.IndexFile, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Tag.Name, change.Tag, StringComparison.OrdinalIgnoreCase)).ToList();
            var noted = (change.OldKey ?? change.NewKey)?.Length;
            if (candidates.Count != 1 || (record is not null && noted is { } length && candidates[0].Tag.KeyFor(record).Length != length))
                throw new DbfException(DbfErrorCode.IndexWouldGoStale,
                    $"A Thunder.Dbf write to record {entry.RecordNumber} was cut off while changing keys in {change.IndexFile} (tag {change.Tag}), which this table doesn't keep, so it can't be finished here.",
                    "Open the table with the same indexes kept (MaintainIndex for an index the legacy app names), or open it exclusively and rebuild every index, then write again.", _path);
            matched.Add((change, candidates[0].Tag));
        }

        using var recordLock = entry.RecordNumber >= 1 ? TryLockRecordNow(entry.RecordNumber) : null;
        try
        {
            // Keys the record has go in first, and reach the disk, before anything is taken out.
            foreach (var (change, tag) in matched)
                Reconcile(tag, change, record, entry.RecordNumber, adding: true);
            CommitIndexes();
            foreach (var (change, tag) in matched)
                Reconcile(tag, change, record, entry.RecordNumber, adding: false);
            CommitIndexes();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _indexesNeedRebuild = true;
            throw new DbfException(DbfErrorCode.IndexDamaged, $"A cut-off write to record {entry.RecordNumber} couldn't be finished in the index.",
                "Close the legacy app, open the table with exclusive: true and run RebuildIndex().", _path, ex);
        }
        DbfJournal.Clear(_path);
        RecheckAfterReplay();
    }

    // Finishes a batch of appends that was cut off. The note says where the batch started (F). If the
    // header's count never reached F, nothing of it was counted, and the records past the count are
    // that batch's, so they come off (under the append lock too, taken without waiting: a dBASE
    // writer holding it may be waiting for the header lock held here). If the count got to F or past
    // it, those records were counted and only their keys may be missing: every kept tag gets the key
    // of each record from F on that it doesn't hold yet.
    private void ReplayAppend(DbfJournalEntry entry)
    {
        var files = TagsWithFiles().Select(t => t.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entry.Changes.FirstOrDefault(c => !files.Contains(c.IndexFile)) is { } missing)
            throw new DbfException(DbfErrorCode.IndexWouldGoStale,
                $"A Thunder.Dbf append from record {entry.RecordNumber} was cut off while keeping {missing.IndexFile}, which this table doesn't keep, so it can't be finished here.",
                "Open the table with the same indexes kept (MaintainIndex for an index the legacy app names), or open it exclusively and rebuild every index, then write again.", _path);

        if (RecordCount < entry.RecordNumber)
        {
            if (RecordsPastCount() == 0)
                return;
            var timeout = Locks.Timeout;
            Locks.Timeout = TimeSpan.Zero;
            DbfLock append;
            try
            {
                append = Locks.Append();
            }
            finally
            {
                Locks.Timeout = timeout;
            }
            using (append)
            {
                var dataEnd = Header.HeaderLength + (long)Header.RecordCount * Header.RecordLength;
                _stream.SetLength(dataEnd);
                _stream.Position = dataEnd;
                _stream.WriteByte(EndOfFile);
                FlushToDisk(_stream);
            }
            return;
        }

        var tags = MaintainedIndexes.SelectMany(i => i.MaintainedTags).ToList();
        var done = new List<Action>();
        try
        {
            for (var number = entry.RecordNumber; number <= RecordCount; number++)
            {
                var record = Read(number - 1);
                foreach (var tag in tags)
                {
                    if (!tag.Includes(record))
                        continue;
                    var key = tag.KeyFor(record);
                    if (!tag.Contains(key, number))
                        InsertKey(tag, key, number, done);
                }
            }
            CommitIndexes();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _indexesNeedRebuild = true;
            throw new DbfException(DbfErrorCode.IndexDamaged, $"A cut-off append from record {entry.RecordNumber} couldn't be finished in the index.",
                "Close the legacy app, open the table with exclusive: true and run RebuildIndex().", _path, ex);
        }
    }

    private DbfLock TryLockRecordNow(int recordNumber)
    {
        var timeout = Locks.Timeout;
        Locks.Timeout = TimeSpan.Zero;
        try
        {
            return Locks.Record(recordNumber, "finished after a cut-off write");
        }
        finally
        {
            Locks.Timeout = timeout;
        }
    }

    // An index attached on probation (out of step with the table when it opened, while a note was
    // waiting) is checked again once the note is finished: if it still doesn't match, writes stop
    // until a rebuild.
    private void RecheckAfterReplay()
    {
        foreach (var index in _awaitingReplay.ToList())
        {
            _awaitingReplay.Remove(index);
            index.RebuildAbandoned();
            try
            {
                index.ReloadTags();
            }
            catch (DbfException)
            {
                _indexesNeedRebuild = true;
                throw;
            }
        }
    }

    // Makes one tag hold what the record has now, in two passes: adding (the record's key, if it's
    // missing) and removing (noted keys that are now stale). A UNIQUE tag changed only after the
    // record was written: if the record has its new key, or has left the tag, that change is finished
    // the way the write would have (the key goes to its holder by the tag's rules, the old one is let
    // go); if it still has its old key, nothing in the tag had changed yet, and only a noted new key
    // pointing at it is taken back, never the old key added (a Visual FoxPro UNIQUE tag can rightly
    // lack it).
    private void Reconcile(IMaintainedTag tag, DbfJournalChange change, DbfRecord? record, int recordNumber, bool adding)
    {
        var current = record is not null && tag.Includes(record) ? tag.KeyFor(record) : null;
        var done = new List<Action>();
        static bool Same(byte[]? a, byte[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);

        if (!tag.IsUnique)
        {
            if (adding && current is not null && !tag.Contains(current, recordNumber))
                tag.Insert(current, recordNumber);
            if (!adding)
            {
                foreach (var stale in new[] { change.OldKey, change.NewKey }.OfType<byte[]>().Where(k => !Same(k, current)))
                    tag.Remove(stale, recordNumber);
            }
            return;
        }

        // Out of the tag now: written if the change was the record leaving (or moving within) the tag;
        // if it was the record coming in, the record wasn't written yet. In the tag: written if it has
        // the new key.
        var written = current is null ? change.OldKey is not null : Same(current, change.NewKey) && !Same(current, change.OldKey);
        if (written)
        {
            if (adding && current is not null && !tag.Contains(current, recordNumber))
                InsertKey(tag, current, recordNumber, done);
            if (!adding)
            {
                foreach (var stale in new[] { change.OldKey, change.NewKey }.OfType<byte[]>().Where(k => !Same(k, current)))
                    RemoveKey(tag, stale, recordNumber, done, mayBeMissing: true);
            }
        }
        else if (!adding && change.NewKey is { } added && !Same(added, current))
        {
            RemoveKey(tag, added, recordNumber, done, mayBeMissing: true);
        }
    }

    // Applies one record's index changes (or one phase of them). If one fails partway (a damaged
    // node, a full disk), the ones already made are undone, so the index never holds half of a change.
    private void ApplyIndexChanges(List<TagChange>? changes, int recordNumber, KeyPhase phase = KeyPhase.All)
    {
        if (changes is null)
            return;

        var done = new List<Action>();
        try
        {
            foreach (var (tag, remove, insert) in changes)
            {
                // NewKeys: only tags without UNIQUE add keys ahead of the record. OldKeys: those tags drop
                // their old keys, and UNIQUE tags make their whole change, old key out, new key in.
                var addNow = phase switch { KeyPhase.NewKeys => !tag.IsUnique, KeyPhase.OldKeys => tag.IsUnique, _ => true };
                var removeNow = phase != KeyPhase.NewKeys;
                if (remove is not null && removeNow)
                    RemoveKey(tag, remove, recordNumber, done);
                if (insert is not null && addNow)
                    InsertKey(tag, insert, recordNumber, done);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Undo what can be undone, but don't trust it: a tree operation that failed halfway can't be
            // undone key by key, and the record itself is already written. Writes stop until a rebuild.
            _indexesNeedRebuild = true;
            for (var i = done.Count - 1; i >= 0; i--)
            {
                try
                {
                    done[i]();
                }
                catch (Exception undoFailure) when (undoFailure is IOException or InvalidDataException)
                {
                    break; // the index is damaged beyond undoing here; the error below says what to do
                }
            }
            throw new DbfException(DbfErrorCode.IndexDamaged,
                phase == KeyPhase.NewKeys
                    ? $"Record {recordNumber} wasn't changed: its new index entries couldn't be added."
                    : $"Record {recordNumber} was written, but its index entries couldn't be updated.",
                "Close the legacy app, open the table with exclusive: true and run RebuildIndex() (or REINDEX in the legacy app) before relying on the index again.", _path, ex);
        }
    }

    // A unique tag keeps one record per key: the lowest record number, as a rebuild would choose; or,
    // for Visual FoxPro, whichever record got there first.
    private void InsertKey(IMaintainedTag tag, byte[] key, int recordNumber, List<Action> done)
    {
        if (tag.IsUnique && tag.FirstRecordFor(key) is { } holder)
        {
            if (holder <= recordNumber || tag.UniqueKeepsFirstIndexed)
                return;
            tag.Remove(key, holder);
            done.Add(() => tag.Insert(key, holder));
        }
        tag.Insert(key, recordNumber);
        done.Add(() => tag.Remove(key, recordNumber));
    }

    private void RemoveKey(IMaintainedTag tag, byte[] key, int recordNumber, List<Action> done, bool mayBeMissing = false)
    {
        if (!tag.Remove(key, recordNumber))
        {
            // A unique tag may hold another record under this key. Any other tag must have had this
            // entry: if it's missing, the index is already out of step with the table. (A journal
            // replay doesn't know how far the cut-off write got, so there it may be missing.)
            if (!tag.IsUnique && !mayBeMissing)
                throw new InvalidDataException($"Index tag {tag.Name} has no entry for record {recordNumber}, which it should.");
            return;
        }
        done.Add(() => tag.Insert(key, recordNumber));
        if (tag.IsUnique && !tag.UniqueKeepsFirstIndexed && LowestOtherRecordWithKey(tag, key, recordNumber) is { } next)
        {
            tag.Insert(key, next);
            done.Add(() => tag.Remove(key, next));
        }
    }

    // When a unique tag's record leaves, the lowest-numbered other record with the same key takes its place.
    private int? LowestOtherRecordWithKey(IMaintainedTag tag, byte[] key, int leaving)
    {
        foreach (var record in ReadAll())
        {
            if (record.RecordNumber != leaving && tag.Includes(record) && tag.KeyFor(record).AsSpan().SequenceEqual(key))
                return record.RecordNumber;
        }
        return null;
    }

    /// <summary>
    /// Rebuilds every tag of every index the table keeps (the production .mdx, a structural .cdx, and
    /// attached .ndx/.ntx/.idx files) from the table, like dBASE's REINDEX. Use it after writing with
    /// <c>allowUnmaintainedIndexes</c>, or when an index has gone out of step.
    /// </summary>
    public void RebuildIndex()
    {
        if (!_stream.CanWrite)
            throw new DbfException(DbfErrorCode.ReadOnlyConnection, "The table was opened read-only.",
                "Open it with writable: true to rebuild its index.", _path);
        if (!MaintainedIndexes.Any())
            throw new DbfException(DbfErrorCode.IndexDamaged, "The table has no index open that Thunder.Dbf can rebuild.",
                "Open the table by path, writable, with its index files next to it.", _path);

        RequireExclusive("Rebuilding the indexes");
        using var tableLock = Locks.Table();
        Refresh();
        if (Header.HasIncompleteTransaction)
            throw WriteBlocker(Header, allowUnmaintainedIndexes: true, indexMaintained: true, _path)!;

        // Work out every tag's keys first. Any record or expression that fails stops the rebuild here,
        // before the existing index is touched.
        var keysByTag = MaintainedIndexes.SelectMany(i => i.MaintainedTags).ToDictionary(tag => tag, _ => new List<(byte[] Key, int Record)>());
        foreach (var record in ReadAll())
        {
            foreach (var (tag, keys) in keysByTag)
            {
                if (tag.Includes(record))
                    keys.Add((tag.KeyFor(record), record.RecordNumber));
            }
        }

        ReplaceAllKeys(keysByTag);
    }

    // Swaps every tag's keys for the given ones (in record order) and commits. A unique tag keeps the
    // lowest record number per key (see IMaintainedTag.ReplaceAll).
    private void ReplaceAllKeys(Dictionary<IMaintainedTag, List<(byte[] Key, int Record)>> keysByTag)
    {
        ThrowIfRebuiltKeysRepeat(keysByTag);
        // Until every tag is refilled and on disk, the indexes are half rebuilt.
        _indexesNeedRebuild = true;
        foreach (var (tag, keys) in keysByTag)
            tag.ReplaceAll(keys);
        CommitIndexes();
        _indexesNeedRebuild = false;
        // Rebuilt from the table, so a note about these indexes is moot; one naming an index this
        // table doesn't keep stays for a process that does.
        if (_path is not null)
        {
            DbfJournalEntry? note;
            try
            {
                note = DbfJournal.Read(_path);
            }
            catch (InvalidDataException)
            {
                note = null; // a format this version can't read: the rebuild is the way past it
            }
            var files = TagsWithFiles().Select(t => t.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (note is null || note.Changes.All(c => files.Contains(c.IndexFile)))
                DbfJournal.Clear(_path);
        }
    }

    private void CheckValueCount(object?[] values)
    {
        if (values.Length != Fields.Count)
            throw new ArgumentException($"The table has {Fields.Count} fields but {values.Length} values were given.", nameof(values));
    }

    private void Seek(int index, bool forAppend = false)
    {
        var last = forAppend ? RecordCount : RecordCount - 1;
        if (index < 0 || index > last)
            throw new ArgumentOutOfRangeException(nameof(index), $"Record {index} is outside 0..{RecordCount - 1}.");
        _stream.Position = Header.HeaderLength + (long)index * Header.RecordLength;
    }

    private void EnsureWritable()
    {
        if (!_stream.CanWrite)
            throw new DbfException(DbfErrorCode.ReadOnlyConnection, "The table was opened read-only.",
                "Open it with writable: true (or a database with ReadOnly = false) to change it.", _path);
    }

    /// <summary>
    /// Why the library would refuse to write this table even with write access, or null when it wouldn't.
    /// Shared by writes and <see cref="DbfDatabase.Check"/> so the handshake never promises what writes refuse.
    /// </summary>
    internal static DbfException? WriteBlocker(DbfHeader header, bool allowUnmaintainedIndexes, bool indexMaintained, string? path,
        IReadOnlyList<string>? unmaintainedIndexes = null, bool structuralCdxMaintained = false, string? structuralCdxRefusal = null)
    {
        if (header.HasIncompleteTransaction)
            return new DbfException(DbfErrorCode.IncompleteTransaction, "dBASE IV marked a transaction on this table as unfinished.",
                "Open the table in the legacy app so it can roll the transaction back, then try again. Reading is still safe.", path);
        if (allowUnmaintainedIndexes)
            return null;
        if (header.HasProductionMdx && !indexMaintained)
            return new DbfException(DbfErrorCode.IndexWouldGoStale,
                "The table says it has a production .mdx index, but it isn't open (missing, or the table was opened from a stream without it), so writing would leave it out of date.",
                "Put the .mdx next to the table and open it by path, pass indexStream, or open with allowUnmaintainedIndexes: true and rebuild it afterwards (exclusive: true, RebuildIndex).", path);
        if (header.HasStructuralCdx && !structuralCdxMaintained && structuralCdxRefusal is not null)
            return new DbfException(DbfErrorCode.IndexWouldGoStale,
                "The table's FoxPro .cdx index can't be kept up to date as it stands, so writing would leave it further out of date.",
                "If it's out of step with the table (a write that reached the table but not the index), close the legacy app, open the table with exclusive: true and call MaintainIndex(path, rebuild: true), or REINDEX in FoxPro.",
                path, diagnostic: structuralCdxRefusal);
        if (header.HasStructuralCdx && !structuralCdxMaintained)
            return new DbfException(DbfErrorCode.IndexWouldGoStale,
                "The table has a FoxPro .cdx index that isn't open for updating (missing, unreadable, or opened from a stream), so writing would leave it out of date.",
                "Keeping .cdx indexes up to date is a preview: open with maintainFoxProIndexes: true (Maintain FoxPro Indexes=true) to use it. Or open with allowUnmaintainedIndexes: true only if the index will be rebuilt (REINDEX in FoxPro) before it's used.", path);
        if (unmaintainedIndexes is { Count: > 0 })
            return new DbfException(DbfErrorCode.IndexWouldGoStale,
                $"An index next to the table ({string.Join(", ", unmaintainedIndexes)}) looks like it belongs to it, and Thunder.Dbf can't keep it up to date (its format isn't supported for writing, or it can't be read).",
                "Open with allowUnmaintainedIndexes: true only if those indexes will be rebuilt in the legacy app before they're used, or move indexes that belong to other tables elsewhere.", path);
        return null;
    }

    /// <summary>
    /// Index files next to a table that belong to it, other than its production .mdx: its structural
    /// .cdx (when the header says it has one), and any dBASE III .ndx, Clipper .ntx, FoxPro .idx or
    /// other .cdx whose key expressions name only this table's fields. An index that can't be read
    /// counts too, because nobody can tell it doesn't belong. Full paths.
    /// </summary>
    internal static IReadOnlyList<string> FindCompanionIndexes(string tablePath, DbfHeader header)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(tablePath))!;
        var baseName = Path.GetFileNameWithoutExtension(tablePath);
        var fieldNames = header.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".ndx" or ".ntx" or ".idx" or ".cdx"))
                continue;
            if (extension == ".cdx" && Path.GetFileNameWithoutExtension(file).Equals(baseName, StringComparison.OrdinalIgnoreCase))
            {
                if (header.HasStructuralCdx)
                    found.Add(file); // the table's own: it belongs by name
                continue;
            }

            try
            {
                using var index = OpenAnyIndexReadOnly(file);
                var belongs = index.Tags.Any(tag =>
                {
                    var names = DbfExpression.FieldNamesIn(tag.Expression);
                    return names.Count > 0 && names.All(fieldNames.Contains);
                });
                if (belongs)
                    found.Add(file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or FormatException)
            {
                found.Add(file);
            }
        }
        return found;
    }

    /// <summary>
    /// The companion indexes (see <see cref="FindCompanionIndexes"/>) a write couldn't keep up to date:
    /// unreadable ones, and formats Thunder.Dbf can't write. File names only.
    /// </summary>
    internal static IReadOnlyList<string> FindUnmaintainedIndexes(string tablePath, DbfHeader header)
    {
        var found = new List<string>();
        foreach (var file in FindCompanionIndexes(tablePath, header))
        {
            try
            {
                using var index = OpenAnyIndexReadOnly(file);
                if (index is IMaintainedIndex)
                    continue;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or FormatException)
            {
                // unreadable: can't be kept up to date
            }
            found.Add(Path.GetFileName(file));
        }
        return found;
    }

    /// <summary>A file next to the table with the same name and this extension, matched in any letter case.</summary>
    internal static string? FindCompanion(string tablePath, string extension)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(tablePath))!;
        var wanted = Path.GetFileNameWithoutExtension(tablePath) + extension;
        return Directory.EnumerateFiles(directory)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static DbfHeader ReadHeader(Stream stream, Encoding? encoding, string? path)
    {
        try
        {
            return DbfHeader.Read(stream, encoding);
        }
        catch (InvalidDataException ex)
        {
            throw new DbfException(DbfErrorCode.NotADbfFile, $"This isn't a readable DBF table: {ex.Message}",
                "Check it's a table (.dbf) and not an index, memo or other file, and that it wasn't truncated by an interrupted copy.", path, ex);
        }
        catch (NotSupportedException ex)
        {
            throw new DbfException(DbfErrorCode.UnsupportedFormat, ex.Message,
                "Save the table as dBASE III/IV or FoxPro from the legacy app, or ask for this format on the Thunder.Dbf roadmap.", path, ex);
        }
    }

    // Order matters for power loss: memos, then records, then the header that counts them, each on
    // disk before the next starts, so a surviving count never points at missing data. Index entries
    // follow (see Commit and UpdateCore). This orders the writes; it isn't a transaction: see
    // docs/durability.md for what a crash can leave behind.
    private void CommitHeader()
    {
        _memo?.FlushToDisk();
        FlushToDisk(_stream);
        CommitIndexes();
        Header.LastUpdate = DateOnly.FromDateTime(DateTime.Today);
        Header.WriteFixed(_stream);
        FlushToDisk(_stream);
    }

    internal static void FlushToDisk(Stream stream)
    {
        if (stream is FileStream file)
            file.Flush(flushToDisk: true);
        else
            stream.Flush();
    }
}
