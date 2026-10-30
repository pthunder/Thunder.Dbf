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
/// (or <see cref="byte"/>[] for binary, picture and OLE memos). Not thread-safe; record locking comes later.
/// </summary>
public sealed class DbfTable : IDisposable
{
    private const byte LiveFlag = (byte)' ';
    private const byte DeletedFlag = (byte)'*';
    private const byte EndOfFile = 0x1A;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly DbfMemoFile? _memo;
    private readonly bool _allowUnmaintainedIndexes;
    private readonly byte[] _buffer;

    private DbfTable(Stream stream, DbfHeader header, Encoding encoding, bool leaveOpen, DbfMemoFile? memo,
        bool ignoreMissingMemo, bool allowUnmaintainedIndexes)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _memo = memo;
        _allowUnmaintainedIndexes = allowUnmaintainedIndexes;
        Header = header;
        Encoding = encoding;
        _buffer = new byte[header.RecordLength];

        if (memo is null && !ignoreMissingMemo && Fields.Any(DbfValueCodec.IsMemoField))
            throw new FileNotFoundException("The table has memo fields but no memo file was found. Pass ignoreMissingMemo to read it anyway.");
    }

    public DbfHeader Header { get; }
    public Encoding Encoding { get; }
    public IReadOnlyList<DbfField> Fields => Header.Fields;
    public int RecordCount => checked((int)Header.RecordCount);
    public bool CanWrite => _stream.CanWrite;

    /// <summary>The memo file's format, or null when the table has none (or it was missing and ignored).</summary>
    public DbfMemoFormat? MemoFormat => _memo?.Format;

    /// <summary>
    /// Opens a .dbf file and its .dbt/.fpt memo file. Read-only opens let other programs keep writing;
    /// writable opens only allow readers.
    /// </summary>
    /// <param name="ignoreMissingMemo">Read memo fields as null when the memo file is missing, instead of failing.
    /// Updates then leave those memo pointers untouched.</param>
    /// <param name="allowUnmaintainedIndexes">Allow writes to a table with a production .mdx or structural .cdx.
    /// Thunder.Dbf doesn't update those yet, so they go stale until something rebuilds them.</param>
    public static DbfTable Open(string path, bool writable = false, Encoding? encoding = null, bool ignoreMissingMemo = false,
        bool allowUnmaintainedIndexes = false)
    {
        var access = writable ? FileAccess.ReadWrite : FileAccess.Read;
        var share = writable ? FileShare.Read : FileShare.ReadWrite;
        var stream = new FileStream(path, FileMode.Open, access, share);
        FileStream? memoStream = null;
        try
        {
            stream.Position = 0;
            var header = DbfHeader.Read(stream);
            if (header.Fields.Any(DbfValueCodec.IsMemoField) && DbfMemoFile.FindFor(path, header) is { } memoPath)
                memoStream = new FileStream(memoPath, FileMode.Open, access, share);

            return Open(stream, encoding ?? DbfEncoding.FromCpgFile(Path.ChangeExtension(path, ".cpg")), leaveOpen: false,
                memoStream, ignoreMissingMemo, allowUnmaintainedIndexes);
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
        bool ignoreMissingMemo = false, bool allowUnmaintainedIndexes = false)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("DBF tables need a seekable stream.", nameof(stream));

        stream.Position = 0;
        var header = DbfHeader.Read(stream, encoding);
        var memo = memoStream is null ? null : DbfMemoFile.Open(memoStream, header, leaveOpen);
        return new DbfTable(stream, header, encoding ?? DbfEncoding.For(header.LanguageDriver), leaveOpen, memo,
            ignoreMissingMemo, allowUnmaintainedIndexes);
    }

    /// <summary>Creates a new, empty .dbf file, plus a dBASE IV .dbt when there are memo fields. Fails if either exists.</summary>
    public static DbfTable Create(string path, IReadOnlyList<DbfFieldDefinition> fields, byte languageDriver = 0x57)
    {
        DbfHeader.Create(fields, languageDriver); // validate before touching the disk
        var memoPath = fields.Any(f => char.ToUpperInvariant(f.Type) == 'M') ? Path.ChangeExtension(path, ".dbt") : null;
        if (memoPath is not null && File.Exists(memoPath))
            throw new IOException($"The memo file '{memoPath}' already exists.");

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

    public DbfRecord Read(int index)
    {
        Seek(index);
        _stream.ReadExactly(_buffer);
        return Decode(index, _buffer);
    }

    /// <summary>Reads every record in order, deleted ones included (check <see cref="DbfRecord.IsDeleted"/>).</summary>
    public IEnumerable<DbfRecord> ReadAll()
    {
        for (var i = 0; i < RecordCount; i++)
            yield return Read(i);
    }

    /// <summary>
    /// Adds a record with values in field order and returns its zero-based index. A null value in a
    /// Visual FoxPro autoincrement field takes the table's next number.
    /// </summary>
    public int Append(params object?[] values)
    {
        EnsureWritable();
        CheckValueCount(values);
        var index = RecordCount;

        values = (object?[])values.Clone();
        var counters = AssignAutoIncrements(values);

        _buffer[0] = LiveFlag;
        EncodeFields(values, changed: null);

        Seek(index, forAppend: true);
        _stream.Write(_buffer);
        _stream.WriteByte(EndOfFile);

        Header.RecordCount++;
        CommitCounters(counters);
        CommitHeader();
        return index;
    }

    /// <summary>
    /// Replaces the values of an existing record. Its deletion flag is kept, and fields (and memos)
    /// whose values didn't change keep their original bytes, so other apps' formatting survives.
    /// </summary>
    public void Update(int index, params object?[] values)
    {
        EnsureWritable();
        CheckValueCount(values);

        var current = Read(index); // also leaves the original record bytes in _buffer
        var changed = new bool[Fields.Count];
        for (var i = 0; i < Fields.Count; i++)
            changed[i] = !SameValue(current.Values[i], values[i]);

        values = (object?[])values.Clone();
        var counters = AssignAutoIncrements(values, changed);
        EncodeFields(values, changed);

        Seek(index);
        _stream.Write(_buffer);
        CommitCounters(counters);
        CommitHeader();
    }

    /// <summary>Marks a record deleted, like dBASE's DELETE. The row stays in the file until a pack.</summary>
    public void Delete(int index) => SetDeletionFlag(index, DeletedFlag);

    /// <summary>Clears the deletion mark, like dBASE's RECALL.</summary>
    public void Recall(int index) => SetDeletionFlag(index, LiveFlag);

    public void Dispose()
    {
        _memo?.Dispose();
        if (!_leaveOpen)
            _stream.Dispose();
    }

    private DbfRecord Decode(int index, byte[] record)
    {
        var nullFlags = Header.NullFlagsFieldIndex is { } n
            ? record.AsSpan(Fields[n].Offset, Fields[n].Length)
            : Span<byte>.Empty;

        var values = new object?[Fields.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var field = Fields[i];
            if (field.NullBit is { } bit && IsBitSet(nullFlags, bit))
                continue;

            var value = DbfValueCodec.Decode(field, record.AsSpan(field.Offset, field.Length), Encoding);
            values[i] = DbfValueCodec.IsMemoField(field) ? ReadMemo(field, value as int?) : value;
        }

        return new DbfRecord(Header, index, record[0] == DeletedFlag, values);
    }

    private object? ReadMemo(DbfField field, int? block)
    {
        if (block is null || _memo is null)
            return null;

        var memo = _memo.Read(block.Value);
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
                DbfValueCodec.Encode(field, values[i], _buffer.AsSpan(field.Offset, field.Length), Encoding);
        }

        for (var i = 0; i < Fields.Count; i++)
        {
            var field = Fields[i];
            if ((changed is null || changed[i]) && DbfValueCodec.IsMemoField(field))
                DbfValueCodec.Encode(field, StoreMemo(field, values[i]), _buffer.AsSpan(field.Offset, field.Length), Encoding);
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

    // Turns a memo value into a block number by writing it to the memo file.
    private object? StoreMemo(DbfField field, object? value)
    {
        if (value is int or long)
            return DbfValueCodec.CheckedMemoBlock(field, value); // a raw pointer, for callers managing the memo file themselves

        var data = value switch
        {
            null => null,
            string text => Encoding.GetBytes(text),
            byte[] bytes => bytes,
            _ => throw new ArgumentException($"Memo field {field.Name} needs a string or byte[], not {value.GetType().Name}."),
        };
        if (data is null || data.Length == 0)
            return null;

        if (_memo is null)
            throw new InvalidOperationException($"Can't write memo field {field.Name}: the table's memo file isn't open.");

        var blockType = field.Type switch
        {
            'G' => DbfMemoFile.ObjectBlock,
            'P' => DbfMemoFile.PictureBlock,
            _ => DbfMemoFile.TextBlock,
        };
        return _memo.Write(data, blockType);
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
                values[i] = next;
                counters.Add((i, next + step));
            }
            else if (values[i] is not null && Convert.ToInt64(values[i]) >= next)
            {
                // An explicit value at or past the counter moves it, so VFP won't hand the same number out later.
                counters.Add((i, checked((int)Convert.ToInt64(values[i]) + step)));
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
        Seek(index);
        _stream.WriteByte(flag);
        CommitHeader();
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
            throw new InvalidOperationException("The table was opened read-only.");
        if ((Header.HasProductionMdx || Header.HasStructuralCdx) && !_allowUnmaintainedIndexes)
            throw new InvalidOperationException(
                "The table has a production index (.mdx or .cdx) that Thunder.Dbf can't update yet, so writing would leave it stale. " +
                "Open with allowUnmaintainedIndexes: true if the index will be rebuilt.");
    }

    private void CommitHeader()
    {
        Header.LastUpdate = DateOnly.FromDateTime(DateTime.Today);
        Header.WriteFixed(_stream);
        _stream.Flush();
    }
}
