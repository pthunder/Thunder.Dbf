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
//   ^ deletion flag: ' ' live, '*' deleted
//
// After the last record comes 0x1A, the old DOS end-of-file marker.

using System.Text;

namespace Thunder.Dbf;

/// <summary>An open DBF table. Not thread-safe; record locking for shared files comes later.</summary>
public sealed class DbfTable : IDisposable
{
    private const byte LiveFlag = (byte)' ';
    private const byte DeletedFlag = (byte)'*';
    private const byte EndOfFile = 0x1A;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _buffer;

    private DbfTable(Stream stream, DbfHeader header, Encoding encoding, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Header = header;
        Encoding = encoding;
        _buffer = new byte[header.RecordLength];
    }

    public DbfHeader Header { get; }
    public Encoding Encoding { get; }
    public IReadOnlyList<DbfField> Fields => Header.Fields;
    public int RecordCount => checked((int)Header.RecordCount);
    public bool CanWrite => _stream.CanWrite;

    /// <summary>Opens a .dbf file. Read-only opens let other programs keep writing; writable opens only allow readers.</summary>
    public static DbfTable Open(string path, bool writable = false, Encoding? encoding = null)
    {
        var stream = writable
            ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return Open(stream, encoding ?? DbfEncoding.FromCpgFile(Path.ChangeExtension(path, ".cpg")));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens a table from a seekable stream. Text uses <paramref name="encoding"/>, else the header's language driver.</summary>
    public static DbfTable Open(Stream stream, Encoding? encoding = null, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("DBF tables need a seekable stream.", nameof(stream));

        stream.Position = 0;
        var header = DbfHeader.Read(stream, encoding);
        return new DbfTable(stream, header, encoding ?? DbfEncoding.For(header.LanguageDriver), leaveOpen);
    }

    /// <summary>Creates a new, empty .dbf file. Fails if the file already exists.</summary>
    public static DbfTable Create(string path, IReadOnlyList<DbfFieldDefinition> fields, byte languageDriver = 0x57)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            return Create(stream, fields, languageDriver);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Writes a new, empty table into <paramref name="stream"/>, replacing anything already there.</summary>
    public static DbfTable Create(Stream stream, IReadOnlyList<DbfFieldDefinition> fields, byte languageDriver = 0x57, bool leaveOpen = false)
    {
        var header = DbfHeader.Create(fields, languageDriver);

        stream.Position = 0;
        stream.SetLength(0);
        header.WriteFull(stream);
        stream.WriteByte(EndOfFile);
        stream.Flush();

        return new DbfTable(stream, header, DbfEncoding.For(languageDriver), leaveOpen);
    }

    public DbfRecord Read(int index)
    {
        Seek(index);
        _stream.ReadExactly(_buffer);

        var values = new object?[Fields.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var field = Fields[i];
            values[i] = DbfValueCodec.Decode(field, _buffer.AsSpan(field.Offset, field.Length), Encoding);
        }

        return new DbfRecord(Header, index, _buffer[0] == DeletedFlag, values);
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

        EncodeRecord(values, LiveFlag);
        Seek(index, forAppend: true);
        _stream.Write(_buffer);
        _stream.WriteByte(EndOfFile);

        Header.RecordCount++;
        CommitHeader();
        return index;
    }

    /// <summary>Replaces every field of an existing record. Its deletion flag is kept.</summary>
    public void Update(int index, params object?[] values)
    {
        EnsureWritable();
        Seek(index);
        var flag = (byte)_stream.ReadByte();

        EncodeRecord(values, flag);
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
        if (!_leaveOpen)
            _stream.Dispose();
    }

    private void SetDeletionFlag(int index, byte flag)
    {
        EnsureWritable();
        Seek(index);
        _stream.WriteByte(flag);
        CommitHeader();
    }

    private void EncodeRecord(object?[] values, byte flag)
    {
        if (values.Length != Fields.Count)
            throw new ArgumentException($"The table has {Fields.Count} fields but {values.Length} values were given.", nameof(values));

        _buffer[0] = flag;
        for (var i = 0; i < values.Length; i++)
        {
            var field = Fields[i];
            DbfValueCodec.Encode(field, values[i], _buffer.AsSpan(field.Offset, field.Length), Encoding);
        }
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
