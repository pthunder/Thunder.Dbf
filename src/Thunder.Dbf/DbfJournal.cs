//      ________
//     | NOTE:  |      ProjectThunder :: Thunder.Dbf
//     | rec 42 |      A journal of one: before a write moves a record's keys, it notes which
//     | A -> B |      keys it's moving, and clears the note once they're all in place. Cut off
//     |________|      in between, the next writer reads the note and finishes the job.
//
//   TABLE.DBF.journal, empty when nothing's in flight:
//
//   +------+--------+-------+---------------------------------------------------+----------+
//   | TDJ2 | record | count | per tag: index file, tag, old key, new key        | SHA-256  |
//   +------+--------+-------+---------------------------------------------------+----------+
//     4      4        4       1 + file, 1 + tag, 2 + old (-1: none), 2 + new       32

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Thunder.Dbf;

/// <summary>One record's key changes, noted before they're made.</summary>
internal sealed record DbfJournalEntry(int RecordNumber, IReadOnlyList<DbfJournalChange> Changes);

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
    private static readonly byte[] Magic = "TDJ2"u8.ToArray();
    private static readonly byte[] Family = "TDJ"u8.ToArray();
    private const int HashLength = 32;

    public static string PathFor(string tablePath) => tablePath + ".journal";

    public static void Write(string tablePath, DbfJournalEntry entry)
    {
        using var body = new MemoryStream();
        body.Write(Magic);
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
    /// Throws <see cref="InvalidDataException"/> for a whole note in another version of the format.
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
        var hashed = bytes.Length >= Magic.Length + 8 + HashLength
            && SHA256.HashData(bytes.AsSpan(0, bytes.Length - HashLength)).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - HashLength));
        // A whole note in another version of the format can't be read safely, nor dropped as torn.
        if (hashed && bytes.AsSpan(0, Family.Length).SequenceEqual(Family) && !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException($"The journal note is in format {Encoding.ASCII.GetString(bytes, 0, Magic.Length)}, not {Encoding.ASCII.GetString(Magic)}.");
        if (!hashed || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return null;

        var at = Magic.Length;
        var record = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
        at += 8;
        var changes = new List<DbfJournalChange>();
        for (var i = 0; i < count; i++)
        {
            var file = ReadName(bytes, ref at);
            var tag = ReadName(bytes, ref at);
            var oldKey = ReadKey(bytes, ref at);
            var newKey = ReadKey(bytes, ref at);
            changes.Add(new DbfJournalChange(file, tag, oldKey, newKey));
        }
        return new DbfJournalEntry(record, changes);
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

    private static void WriteName(Stream stream, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        stream.WriteByte(checked((byte)bytes.Length));
        stream.Write(bytes);
    }

    private static string ReadName(byte[] bytes, ref int at)
    {
        int length = bytes[at++];
        var name = Encoding.UTF8.GetString(bytes, at, length);
        at += length;
        return name;
    }

    private static void WriteKey(Stream stream, byte[]? key)
    {
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(length, key is null ? (short)-1 : checked((short)key.Length));
        stream.Write(length);
        if (key is not null)
            stream.Write(key);
    }

    private static byte[]? ReadKey(byte[] bytes, ref int at)
    {
        var length = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at));
        at += 2;
        if (length < 0)
            return null;
        var key = bytes.AsSpan(at, length).ToArray();
        at += length;
        return key;
    }
}
