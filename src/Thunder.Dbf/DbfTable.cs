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
/// (or <see cref="byte"/>[] for picture/OLE/binary memos). Not thread-safe; record locking comes later.
/// </summary>
public sealed class DbfTable : IDisposable
{
    private const byte LiveFlag = (byte)' ';
    private const byte DeletedFlag = (byte)'*';
    private const byte EndOfFile = 0x1A;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly DbfMemoFile? _memo;
    private readonly bool _ignoreMissingMemo;
    private readonly byte[] _buffer;

    private DbfTable(Stream stream, DbfHeader header, Encoding encoding, bool leaveOpen, DbfMemoFile? memo, bool ignoreMissingMemo)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _memo = memo;
        _ignoreMissingMemo = ignoreMissingMemo;
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
    /// writable opens only allow readers. With <paramref name="ignoreMissingMemo"/>, a missing memo file
    /// makes memo fields read as null instead of failing.
    /// </summary>
    public static DbfTable Open(string path, bool writable = false, Encoding? encoding = null, bool ignoreMissingMemo = false)
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

            return Open(stream, encoding ?? DbfEncoding.FromCpgFile(Path.ChangeExtension(path, ".cpg")), leaveOpen: false, memoStream, ignoreMissingMemo);
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
    public static DbfTable Open(Stream stream, Encoding? encoding = null, bool leaveOpen = false, Stream? memoStream = null, bool ignoreMissingMemo = false)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("DBF tables need a seekable stream.", nameof(stream));

        stream.Position = 0;
        var header = DbfHeader.Read(stream, encoding);
        var memo = memoStream is null ? null : DbfMemoFile.Open(memoStream, header, leaveOpen);
        return new DbfTable(stream, header, encoding ?? DbfEncoding.For(header.LanguageDriver), leaveOpen, memo, ignoreMissingMemo);
    }

    /// <summary>Creates a new, empty .dbf file, plus a dBASE IV .dbt when there are memo fields. Fails if either exists.</summary>
    public static DbfTable Create(string path, IReadOnlyList<DbfFieldDefinition> fields, byte languageDriver = 0x57)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        FileStream? memoStream = null;
        try
        {
            if (fields.Any(f => char.ToUpperInvariant(f.Type) == 'M'))
                memoStream = new FileStream(Path.ChangeExtension(path, ".dbt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);

            return Create(stream, fields, languageDriver, leaveOpen: false, memoStream, Path.GetFileName(path));
        }
        catch
        {
            memoStream?.Dispose();
            stream.Dispose();
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
        return new DbfTable(stream, header, DbfEncoding.For(languageDriver), leaveOpen, memo, ignoreMissingMemo: false);
    }

    public DbfRecord Read(int index)
    {
        Seek(index);
        _stream.ReadExactly(_buffer);
        var isDeleted = _buffer[0] == DeletedFlag;

        var values = new object?[Fields.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var field = Fields[i];
            var value = DbfValueCodec.Decode(field, _buffer.AsSpan(field.Offset, field.Length), Encoding);
            values[i] = DbfValueCodec.IsMemoField(field) ? ReadMemo(field, value as int?) : value;
        }

        return new DbfRecord(Header, index, isDeleted, values);
    }

    /// <summary>Reads every record in order, deleted ones included (check <see cref="DbfRecord.IsDeleted"/>).</summary>
    public IEnumerable<DbfRecord> ReadAll()
    {
        for (var i = 0; i < RecordCount; i++)
            yield return Read(i);
    }

    /// <summary>Adds a record with values in field order and returns its zero-based index.</summary>
    public int Append(params object?[] values)
    {
        EnsureWritable();
        var index = RecordCount;

        EncodeRecord(values, LiveFlag, existingMemoBlocks: null);
        Seek(index, forAppend: true);
        _stream.Write(_buffer);
        _stream.WriteByte(EndOfFile);

        Header.RecordCount++;
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
        Seek(index);
        _stream.ReadExactly(_buffer);
        var original = _buffer.ToArray();
        var existingMemoBlocks = new int?[Fields.Count];
        for (var i = 0; i < Fields.Count; i++)
        {
            var field = Fields[i];
            if (DbfValueCodec.IsMemoField(field))
                existingMemoBlocks[i] = DbfValueCodec.Decode(field, original.AsSpan(field.Offset, field.Length), Encoding) as int?;
        }

        var encoded = EncodeRecord(values, original[0], existingMemoBlocks);
        for (var i = 0; i < Fields.Count; i++)
        {
            var field = Fields[i];
            var span = original.AsSpan(field.Offset, field.Length);
            var unchanged = DbfValueCodec.IsMemoField(field)
                ? Equals(encoded[i], existingMemoBlocks[i])
                : SameValue(DbfValueCodec.Decode(field, span, Encoding), values[i]);
            if (unchanged)
                span.CopyTo(_buffer.AsSpan(field.Offset, field.Length));
        }

        Seek(index);
        _stream.Write(_buffer);
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

    private object? ReadMemo(DbfField field, int? block)
    {
        if (block is null || _memo is null)
            return null;

        var data = _memo.Read(block.Value);
        return field.Type == 'M' ? Encoding.GetString(data) : data;
    }

    // Turns a memo value into a block number, writing a new memo only when the content changed.
    private object? StoreMemo(DbfField field, object? value, int? existingBlock)
    {
        if (value is int or long)
            return value; // a raw block pointer, for callers managing the memo file themselves

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

        if (existingBlock is { } block && _memo.Read(block).AsSpan().SequenceEqual(data))
            return block;

        return _memo.Write(data, isText: value is string);
    }

    private void SetDeletionFlag(int index, byte flag)
    {
        EnsureWritable();
        Seek(index);
        _stream.WriteByte(flag);
        CommitHeader();
    }

    private static bool SameValue(object? stored, object? incoming) =>
        stored is byte[] a && incoming is byte[] b ? a.AsSpan().SequenceEqual(b) : Equals(stored, incoming);

    // Returns the values as encoded, with memo values replaced by their block numbers.
    private object?[] EncodeRecord(object?[] values, byte flag, int?[]? existingMemoBlocks)
    {
        if (values.Length != Fields.Count)
            throw new ArgumentException($"The table has {Fields.Count} fields but {values.Length} values were given.", nameof(values));

        // Memo values become block numbers first; the codec then writes those like any other field.
        var encoded = new object?[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            encoded[i] = DbfValueCodec.IsMemoField(Fields[i])
                ? StoreMemo(Fields[i], values[i], existingMemoBlocks?[i])
                : values[i];
        }

        _buffer[0] = flag;
        for (var i = 0; i < encoded.Length; i++)
        {
            var field = Fields[i];
            DbfValueCodec.Encode(field, encoded[i], _buffer.AsSpan(field.Offset, field.Length), Encoding);
        }

        return encoded;
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
    }

    private void CommitHeader()
    {
        Header.LastUpdate = DateOnly.FromDateTime(DateTime.Today);
        Header.WriteFixed(_stream);
        _stream.Flush();
    }
}
