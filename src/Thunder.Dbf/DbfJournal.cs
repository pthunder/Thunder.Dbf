//      ________
//     | NOTE:  |      ProjectThunder :: Thunder.Dbf
//     | rec 42 |      A journal of one: before a write moves a record's keys, it notes which
//     | A -> B |      keys it's moving, and clears the note once they're all in place. Cut off
//     |________|      in between, the next writer reads the note and finishes the job.
//
//   TABLE.DBF.journal, empty when nothing's in flight:
//
//   +------+------+--------+-------+--------------------------------------------+----------+
//   | TDJ3 | kind | record | count | per tag: index file, tag, old key, new key  | SHA-256  |
//   +------+------+--------+-------+--------------------------------------------+----------+
//     4      1      4        4       1 + file, 1 + tag, 2 + old (-1: none), 2 + new 32
//
//   kind 'U': one record's key change. kind 'A': appending from that record on; the "tags" are
//   just the index files the writer kept (no keys), which a replay must keep too.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Thunder.Dbf;

/// <summary>One record's key changes, noted before they're made.</summary>
internal sealed record DbfJournalEntry(int RecordNumber, IReadOnlyList<DbfJournalChange> Changes, bool IsAppend = false);

/// <summary>One tag's part of a key change. The index file's name and the key length pin down which tag.</summary>
internal sealed record DbfJournalChange(string IndexFile, string Tag, byte[]? OldKey, byte[]? NewKey);

/// <summary>
/// The note next to a table that makes a cut-off key change recoverable. It's written, and forced to
/// disk, before the first index change of a write, and emptied once every change is on disk. Every
/// such write holds the table's header lock, so one note is enough. A note that's torn (its checksum
/// doesn't match) was cut off before any index change was made, so it's simply discarded.
/// </summary>
internal static class DbfJournal
{
    private static readonly byte[] Magic = "TDJ3"u8.ToArray();
    private static readonly byte[] Family = "TDJ"u8.ToArray();
    private const int HashLength = 32;

    public static string PathFor(string tablePath) => tablePath + ".journal";

    public static void Write(string tablePath, DbfJournalEntry entry)
    {
        using var body = new MemoryStream();
        body.Write(Magic);
        body.WriteByte(entry.IsAppend ? (byte)'A' : (byte)'U');
        WriteInt(body, entry.RecordNumber);
        WriteInt(body, entry.Changes.Count);
        foreach (var change in entry.Changes)
        {
            WriteName(body, change.IndexFile);
            WriteName(body, change.Tag);
            WriteKey(body, change.OldKey);
            WriteKey(body, change.NewKey);
        }
        var bytes = body.ToArray();

        using var file = new FileStream(PathFor(tablePath), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        file.SetLength(0);
        file.Write(bytes);
        file.Write(SHA256.HashData(bytes));
        file.Flush(flushToDisk: true);
    }

    /// <summary>
    /// The note, or null when there's none (no file, an empty one, or one torn before any index change).
    /// Throws <see cref="InvalidDataException"/> for a whole note in another version of the format, or
    /// one whose counts and lengths don't fit inside it (damaged, or not written by Thunder.Dbf).
    /// </summary>
    public static DbfJournalEntry? Read(string tablePath)
    {
        var path = PathFor(tablePath);
        if (!File.Exists(path))
            return null;
        byte[] bytes;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (file.Length is 0 or > 1 << 20)
                return null;
            bytes = new byte[file.Length];
            file.ReadExactly(bytes);
        }
        var hashed = bytes.Length >= Magic.Length + 9 + HashLength
            && SHA256.HashData(bytes.AsSpan(0, bytes.Length - HashLength)).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - HashLength));
        // A whole note in another version of the format can't be read safely, nor dropped as torn.
        if (hashed && bytes.AsSpan(0, Family.Length).SequenceEqual(Family) && !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException($"The journal note is in format {Encoding.ASCII.GetString(bytes, 0, Magic.Length)}, not {Encoding.ASCII.GetString(Magic)}.");
        if (!hashed || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return null;

        // Every count and length is held to the bytes the note really has: a hash says the note is
        // whole, not that what's in it is sane.
        var body = bytes.AsSpan(0, bytes.Length - HashLength);
        var at = Magic.Length;
        var kind = body[at++];
        if (kind is not ((byte)'A' or (byte)'U'))
            throw Malformed("its kind");
        var record = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(body[(at + 4)..]);
        at += 8;
        if (record < 0 || count < 0 || count > (body.Length - at) / MinChangeLength)
            throw Malformed("its record number or count");
        var changes = new List<DbfJournalChange>(count);
        for (var i = 0; i < count; i++)
        {
            var file = ReadName(body, ref at);
            var tag = ReadName(body, ref at);
            var oldKey = ReadKey(body, ref at);
            var newKey = ReadKey(body, ref at);
            changes.Add(new DbfJournalChange(file, tag, oldKey, newKey));
        }
        if (at != body.Length)
            throw Malformed("its length");
        return new DbfJournalEntry(record, changes, kind == (byte)'A');
    }

    /// <summary>True when a note is waiting (a write was cut off, or one is in flight in another program).</summary>
    public static bool IsPending(string tablePath)
    {
        var path = PathFor(tablePath);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    public static void Clear(string tablePath)
    {
        var path = PathFor(tablePath);
        if (!File.Exists(path))
            return;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        if (file.Length == 0)
            return;
        file.SetLength(0);
        file.Flush(flushToDisk: true);
    }

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private const int MinChangeLength = 1 + 1 + 2 + 2; // two empty names, two absent keys
    private const int MaxKeyLength = 256;                 // the longest key any of the index formats holds (Clipper's .ntx)

    private static InvalidDataException Malformed(string what) =>
        new($"The journal note is damaged or not Thunder.Dbf's: {what} doesn't fit inside it.");

    private static void WriteName(Stream stream, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > byte.MaxValue)
            throw new ArgumentException($"An index or tag name of {bytes.Length} bytes is too long for the journal (at most {byte.MaxValue}).", nameof(name));
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }

    private static string ReadName(ReadOnlySpan<byte> body, ref int at)
    {
        if (at >= body.Length || body[at] > body.Length - at - 1)
            throw Malformed("a name");
        int length = body[at++];
        var name = Encoding.UTF8.GetString(body.Slice(at, length));
        at += length;
        return name;
    }

    private static void WriteKey(Stream stream, byte[]? key)
    {
        if (key is not null && key.Length > MaxKeyLength)
            throw new ArgumentException($"A key of {key.Length} bytes is too long for the journal (at most {MaxKeyLength}).", nameof(key));
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(length, key is null ? (short)-1 : checked((short)key.Length));
        stream.Write(length);
        if (key is not null)
            stream.Write(key);
    }

    private static byte[]? ReadKey(ReadOnlySpan<byte> body, ref int at)
    {
        if (at > body.Length - 2)
            throw Malformed("a key's length");
        var length = BinaryPrimitives.ReadInt16LittleEndian(body[at..]);
        at += 2;
        if (length == -1)
            return null;
        if (length < 0 || length > MaxKeyLength || length > body.Length - at)
            throw Malformed("a key");
        var key = body.Slice(at, length).ToArray();
        at += length;
        return key;
    }
}
