//      page 0-3                 page 4-5          page 6-7         ProjectThunder :: Thunder.Dbf
//     +----------------------+ +---------------+ +------------+    INDEX ON for dBASE IV: a new .mdx,
//     | header | tag table   | | tag header    | | empty root |    or one more tag in an existing one.
//     |        | NAME -> 4   | | root -> 6     | | 0 keys     |
//     +----------------------+ | "UPPER(LAST)" | +------------+    Byte layouts match what dBASE IV and
//                              +---------------+                   SIMATIC Manager write (docs/formats/mdx.md).
//
//  The tag table also carries a binary search tree over tag names (each entry's left, right and
//  parent slots). Readers here ignore it, but dBASE walks it to find a tag by name, so it's rebuilt
//  every time a tag is added.

using System.Buffers.Binary;
using System.Text;

namespace Thunder.Dbf;

public sealed partial class MdxIndex
{
    private const int NewBlockSize = 1024; // what dBASE IV and every real file in tests/fixtures use
    private const byte KeyFormatBase = 0x10;
    private const byte KeyFormatDescending = 0x08;
    private const byte KeyFormatUnique = 0x40;

    /// <summary>Writes an empty production .mdx (header and tag table, no tags) to <paramref name="stream"/>.</summary>
    internal static void WriteEmpty(Stream stream, string tableBaseName)
    {
        var header = new byte[HeaderAreaSize];
        var today = DateTime.Today;
        header[0] = 0x02;
        WriteDate(header.AsSpan(1, 3), today);
        var name = Encoding.ASCII.GetBytes(tableBaseName.ToUpperInvariant());
        name.AsSpan(0, Math.Min(name.Length, 15)).CopyTo(header.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), NewBlockSize / PageSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), NewBlockSize);
        header[24] = 0x01; // production: dBASE opens it with the table
        header[25] = 0x30; // tag table slots
        header[26] = 0x20; // tag entry length
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), HeaderAreaSize / PageSize);
        WriteDate(header.AsSpan(44, 3), today);

        stream.SetLength(0);
        stream.Position = 0;
        stream.Write(header);
        DbfTable.FlushToDisk(stream);
    }

    private static void WriteDate(Span<byte> target, DateTime date)
    {
        target[0] = (byte)(date.Year - 1900);
        target[1] = (byte)date.Month;
        target[2] = (byte)date.Day;
    }

    /// <summary>
    /// Adds an empty tag: a tag header block, an empty root node, and an entry in the tag table.
    /// The caller fills it with keys and commits.
    /// </summary>
    internal MdxTag AddTag(string name, string expression, string? forExpression, char keyType, int keyLength, bool unique, bool descending)
    {
        if (Tags.Count >= MaxTags)
            throw new InvalidOperationException($"An .mdx holds at most {MaxTags} tags.");
        if (Tags.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The index already has a tag named {name}.");

        var headerArea = ReadHeaderArea(_stream);
        var entryLength = headerArea[26] == 0 ? 32 : headerArea[26];
        var count = Tags.Count;

        var itemLength = (keyLength + 4 + 3) / 4 * 4;
        var maxKeys = (BlockSize - 12) / itemLength;

        // Allocate the root first and the header second, so the header can point at the root and
        // no half-made tag is ever listed: the tag table entry is written last.
        var root = AllocatePage();
        var headerPage = AllocatePage();

        var block = new byte[BlockSize];
        BinaryPrimitives.WriteInt32LittleEndian(block, root);
        block[8] = (byte)(KeyFormatBase | (descending ? KeyFormatDescending : 0) | (unique ? KeyFormatUnique : 0));
        block[9] = (byte)keyType;
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(12), (ushort)keyLength);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(14), (ushort)maxKeys);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(18), (ushort)itemLength);
        block[20] = 0x01; // change counter; dBASE bumps it, nothing reads it
        block[23] = unique ? KeyFormatUnique : (byte)0;
        WriteText(block.AsSpan(24, 221), expression);
        if (forExpression is not null)
        {
            block[245] = 0x01;
            WriteText(block.AsSpan(762, 220), forExpression);
        }
        WriteBlock(headerPage, block);

        // The entry: header page, name, key format 0x10, threads (filled in below), 0x02, key type.
        var entry = new byte[entryLength];
        BinaryPrimitives.WriteInt32LittleEndian(entry, headerPage);
        Encoding.ASCII.GetBytes(name).AsSpan(0, Math.Min(name.Length, 10)).CopyTo(entry.AsSpan(4, 11));
        entry[15] = KeyFormatBase;
        entry[19] = 0x02;
        entry[20] = (byte)keyType;
        _stream.Position = TagTableOffset + (long)entryLength * count;
        _stream.Write(entry);

        WriteNameTree([.. Tags.Select(t => t.Name), name], entryLength);
        _stream.Position = 28;
        Span<byte> tagCount = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tagCount, (ushort)(count + 1));
        _stream.Write(tagCount);

        ReloadTags();
        return Tags[^1];
    }

    // Rebuilds the left / right / parent slots (1-based, 0 = none) as a binary search tree over the
    // tag names, inserted in table order: the same tree dBASE IV builds.
    private void WriteNameTree(IReadOnlyList<string> names, int entryLength)
    {
        var left = new byte[names.Count];
        var right = new byte[names.Count];
        var parent = new byte[names.Count];
        for (var i = 1; i < names.Count; i++)
        {
            var at = 0;
            while (true)
            {
                var goLeft = string.CompareOrdinal(names[i].ToUpperInvariant(), names[at].ToUpperInvariant()) < 0;
                var next = goLeft ? left[at] : right[at];
                if (next == 0)
                {
                    (goLeft ? left : right)[at] = (byte)(i + 1);
                    parent[i] = (byte)(at + 1);
                    break;
                }
                at = next - 1;
            }
        }

        for (var i = 0; i < names.Count; i++)
        {
            _stream.Position = TagTableOffset + (long)entryLength * i + 16;
            _stream.Write([left[i], right[i], parent[i]]);
        }
    }

    private void WriteText(Span<byte> target, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        if (bytes.Length >= target.Length)
            throw new ArgumentException($"The expression is {bytes.Length} characters; an .mdx tag holds at most {target.Length - 1}.");
        bytes.CopyTo(target);
    }
}
