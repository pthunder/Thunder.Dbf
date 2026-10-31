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

    public DbfHeader Header { get; }
    public Encoding Encoding { get; }
    public IReadOnlyList<DbfField> Fields => Header.Fields;
    public int RecordCount => checked((int)Header.RecordCount);
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
    /// <param name="allowUnmaintainedIndexes">Allow writes to a table with a production .mdx or structural .cdx.
    /// Thunder.Dbf doesn't update those yet, so they go stale until something rebuilds them.</param>
    public static DbfTable Open(string path, bool writable = false, Encoding? encoding = null, bool ignoreMissingMemo = false,
        bool allowUnmaintainedIndexes = false)
    {
        var access = writable ? FileAccess.ReadWrite : FileAccess.Read;
        var share = FileShare.ReadWrite;
        var stream = DbfException.OpenFile(path, FileMode.Open, access, share);
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
            if (writable)
            {
                if (header.HasProductionMdx && MdxIndex.FindFor(path) is { } indexPath)
                    table.AttachIndex(DbfException.OpenFile(indexPath, FileMode.Open, FileAccess.ReadWrite, share));
                table._unmaintainedIndexes = FindUnmaintainedIndexes(path, header);
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
    /// Text uses <paramref name="encoding"/>, else the header's language driver.
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
            if (_locks is not null && _locks.Scheme != value && value != DbfLockScheme.Auto)
                throw new InvalidOperationException("Set LockScheme before the table's first write or lock.");
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
        _index?.ReloadTags();
    }

    // Everything a write must re-check after taking its locks, because another program may have
    // changed the table since it was opened: header, index tags, blockers and the file's real size.
    private void PrepareWrite(bool appending)
    {
        Refresh();
        if (WriteBlocker(Header, _allowUnmaintainedIndexes, indexMaintained: _index is not null, _path, _unmaintainedIndexes) is { } blocker)
            throw blocker;

        var dataEnd = Header.HeaderLength + (long)Header.RecordCount * Header.RecordLength;
        if (dataEnd > _stream.Length)
            throw new DbfException(DbfErrorCode.NotADbfFile,
                $"The table's header counts {Header.RecordCount:N0} records, but the file is too short to hold them.",
                "The file was probably truncated by an interrupted copy. Restore it from a backup before writing.", _path);
        if (appending && dataEnd + Header.RecordLength + 1 > MaxTableBytes)
            throw new DbfException(DbfErrorCode.InvalidValue, "The table has reached the 2 GB size dBASE and FoxPro can handle.",
                "Archive or pack older records before adding more.", _path);
    }

    /// <summary>The table's file path, or null for a stream. For plans and logs, never for messages.</summary>
    internal string? FilePath => _path;

    /// <summary>True when writes keep a production .mdx index up to date.</summary>
    public bool MaintainsIndex => _index is not null;

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

        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        FileStream? memoStream = null;
        try
        {
            if (memoPath is not null)
                memoStream = new FileStream(memoPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);

            return Create(stream, fields, languageDriver, leaveOpen: false, memoStream, Path.GetFileName(path));
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
        var keys = PlanIndexChanges(before: null, after: _index is null ? null : Decode(index, _buffer.ToArray()));

        Seek(index, forAppend: true);
        _stream.Write(_buffer);

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

        var pending = _pendingIndexChanges.ToList();
        _pendingIndexChanges.Clear();
        foreach (var (changes, recordNumber) in pending)
            ApplyIndexChanges(changes, recordNumber);
        _index?.Commit();
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
    // record, then index entries; each reaches the disk before the next starts.
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
        var keys = PlanIndexChanges(current, _index is null ? null : Decode(index, _buffer.ToArray()));

        _memo?.FlushToDisk();
        Seek(index);
        _stream.Write(_buffer);
        FlushToDisk(_stream);
        ApplyIndexChanges(keys, index + 1);
        CommitCounters(counters);
        CommitHeader();
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

    public void Dispose()
    {
        _memo?.Dispose();
        _index?.Dispose();
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

        return new DbfRecord(Header, index, record[0] == DeletedFlag, values, Encoding) { NullFlags = nulls };
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
        var keys = PlanIndexChanges(before, _index is null ? null : Decode(index, _buffer.ToArray()));

        Seek(index);
        _stream.WriteByte(flag);
        FlushToDisk(_stream);
        ApplyIndexChanges(keys, index + 1);
        CommitHeader();
    }

    // ---- index maintenance ----------------------------------------------------------------

    private readonly record struct TagChange(MdxTag Tag, byte[]? Remove, byte[]? Insert);

    // Works out every tag's change before anything is written, so a key expression that fails
    // (a type mismatch, say) leaves the table and its index untouched.
    private List<TagChange>? PlanIndexChanges(DbfRecord? before, DbfRecord? after)
    {
        if (_index is null || after is null)
            return null;

        var changes = new List<TagChange>();
        foreach (var tag in _index.Tags)
        {
            var wasIn = before is not null && tag.Includes(before);
            var isIn = tag.Includes(after);
            var oldKey = wasIn ? tag.KeyFor(before!) : null;
            var newKey = isIn ? tag.KeyFor(after) : null;
            if (oldKey is not null && newKey is not null && oldKey.AsSpan().SequenceEqual(newKey))
                continue;
            if (oldKey is not null || newKey is not null)
                changes.Add(new TagChange(tag, oldKey, newKey));
        }
        return changes;
    }

    // Applies one record's index changes. If one fails partway (a damaged node, a full disk), the
    // ones already made are undone, so the index never holds half of a record's change.
    private void ApplyIndexChanges(List<TagChange>? changes, int recordNumber)
    {
        if (changes is null)
            return;

        var done = new List<Action>();
        try
        {
            foreach (var (tag, remove, insert) in changes)
            {
                if (remove is not null)
                    RemoveKey(tag, remove, recordNumber, done);
                if (insert is not null)
                    InsertKey(tag, insert, recordNumber, done);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
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
            throw new DbfException(DbfErrorCode.IndexDamaged, $"Record {recordNumber} was written, but its index entries couldn't be updated.",
                "Run RebuildIndex() (or REINDEX in the legacy app) before relying on the index again.", _path, ex);
        }
    }

    // A unique tag keeps one record per key: the lowest record number, as a rebuild would choose.
    private void InsertKey(MdxTag tag, byte[] key, int recordNumber, List<Action> done)
    {
        if (tag.IsUnique && tag.FirstRecordFor(key) is { } holder)
        {
            if (holder <= recordNumber)
                return;
            tag.Remove(key, holder);
            done.Add(() => tag.Insert(key, holder));
        }
        tag.Insert(key, recordNumber);
        done.Add(() => tag.Remove(key, recordNumber));
    }

    private void RemoveKey(MdxTag tag, byte[] key, int recordNumber, List<Action> done)
    {
        if (!tag.Remove(key, recordNumber))
            return;
        done.Add(() => tag.Insert(key, recordNumber));
        if (tag.IsUnique && LowestOtherRecordWithKey(tag, key, recordNumber) is { } next)
        {
            tag.Insert(key, next);
            done.Add(() => tag.Remove(key, next));
        }
    }

    // When a unique tag's record leaves, the lowest-numbered other record with the same key takes its place.
    private int? LowestOtherRecordWithKey(MdxTag tag, byte[] key, int leaving)
    {
        foreach (var record in ReadAll())
        {
            if (record.RecordNumber != leaving && tag.Includes(record) && tag.KeyFor(record).AsSpan().SequenceEqual(key))
                return record.RecordNumber;
        }
        return null;
    }

    /// <summary>
    /// Rebuilds every tag of the production .mdx from the table, like dBASE's REINDEX. Use it after
    /// writing with <c>allowUnmaintainedIndexes</c>, or when an index has gone out of step.
    /// </summary>
    public void RebuildIndex()
    {
        if (!_stream.CanWrite)
            throw new DbfException(DbfErrorCode.ReadOnlyConnection, "The table was opened read-only.",
                "Open it with writable: true to rebuild its index.", _path);
        if (_index is null)
            throw new DbfException(DbfErrorCode.IndexDamaged, "The table has no production .mdx index open to rebuild.",
                "Open the table by path, writable, with its .mdx next to it.", _path);

        using var tableLock = Locks.Table();
        Refresh();
        if (Header.HasIncompleteTransaction)
            throw WriteBlocker(Header, allowUnmaintainedIndexes: true, indexMaintained: true, _path)!;

        // Work out every tag's keys first. Any record or expression that fails stops the rebuild here,
        // before the existing index is touched.
        var keysByTag = _index.Tags.ToDictionary(tag => tag, _ => new List<(byte[] Key, int Record)>());
        foreach (var record in ReadAll())
        {
            foreach (var (tag, keys) in keysByTag)
            {
                if (tag.Includes(record))
                    keys.Add((tag.KeyFor(record), record.RecordNumber));
            }
        }

        foreach (var (tag, keys) in keysByTag)
        {
            tag.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, record) in keys)
            {
                // Records arrive in record order, so a unique tag keeps the lowest number per key.
                if (!tag.IsUnique || seen.Add(Convert.ToHexString(key)))
                    tag.Insert(key, record);
            }
        }
        _index.Commit();
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
        IReadOnlyList<string>? unmaintainedIndexes = null)
    {
        if (header.HasIncompleteTransaction)
            return new DbfException(DbfErrorCode.IncompleteTransaction, "dBASE IV marked a transaction on this table as unfinished.",
                "Open the table in the legacy app so it can roll the transaction back, then try again. Reading is still safe.", path);
        if (allowUnmaintainedIndexes)
            return null;
        if (header.HasProductionMdx && !indexMaintained)
            return new DbfException(DbfErrorCode.IndexWouldGoStale,
                "The table says it has a production .mdx index, but it isn't open (missing, or the table was opened from a stream without it), so writing would leave it out of date.",
                "Put the .mdx next to the table and open it by path, pass indexStream, or open with allowUnmaintainedIndexes: true and call RebuildIndex() afterwards.", path);
        if (header.HasStructuralCdx)
            return new DbfException(DbfErrorCode.IndexWouldGoStale,
                "The table has a FoxPro .cdx index, which Thunder.Dbf can read but not yet update, so writing would leave it out of date.",
                "Open with allowUnmaintainedIndexes: true only if the index will be rebuilt (REINDEX in FoxPro) before it's used.", path);
        if (unmaintainedIndexes is { Count: > 0 })
            return new DbfException(DbfErrorCode.IndexWouldGoStale,
                $"An index next to the table ({string.Join(", ", unmaintainedIndexes)}) looks like it belongs to it, and Thunder.Dbf can't keep .ndx, .ntx or .idx indexes up to date yet.",
                "Open with allowUnmaintainedIndexes: true only if those indexes will be rebuilt in the legacy app before they're used, or move indexes that belong to other tables elsewhere.", path);
        return null;
    }

    /// <summary>
    /// Index files next to a table that it can't keep up to date: dBASE III .ndx, Clipper .ntx,
    /// FoxPro .idx, and .cdx files other than its own structural one. An index counts as the table's
    /// when every field its key expression names is one of the table's fields; an index that can't
    /// be read counts too, because nobody can tell it isn't.
    /// </summary>
    internal static IReadOnlyList<string> FindUnmaintainedIndexes(string tablePath, DbfHeader header)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(tablePath))!;
        var baseName = Path.GetFileNameWithoutExtension(tablePath);
        var fieldNames = header.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var isStructuralCdx = extension == ".cdx" && Path.GetFileNameWithoutExtension(file).Equals(baseName, StringComparison.OrdinalIgnoreCase);
            if (extension is not (".ndx" or ".ntx" or ".idx" or ".cdx") || isStructuralCdx)
                continue;

            try
            {
                using IDbfIndex index = extension switch
                {
                    ".ndx" => NdxIndex.Open(file),
                    ".ntx" => NtxIndex.Open(file),
                    _ => CdxIndex.Open(file),
                };
                var belongs = index.Tags.Any(tag =>
                {
                    var names = DbfExpression.FieldNamesIn(tag.Expression);
                    return names.Count > 0 && names.All(fieldNames.Contains);
                });
                if (belongs)
                    found.Add(Path.GetFileName(file));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or FormatException)
            {
                found.Add(Path.GetFileName(file));
            }
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
        _index?.Commit();
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
