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
    /// Writes a new tag's header block and empty root, without listing it in the tag table yet: until
    /// <see cref="PublishTag"/> runs, no reader (dBASE or this one) can see it, so a crash while it's
    /// being filled leaves only unused pages behind.
    /// </summary>
    internal MdxTag PrepareTag(string name, string expression, string? forExpression, char keyType, int keyLength, bool unique, bool descending)
    {
        if (Tags.Count >= MaxTags)
            throw new InvalidOperationException($"An .mdx holds at most {MaxTags} tags.");
        if (Tags.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The index already has a tag named {name}.");

        // Encode the text first: a character the code page can't hold must stop this before any page is written.
        var expressionBytes = TextBytes(expression, 221);
        var forBytes = forExpression is null ? null : TextBytes(forExpression, 220);

        var itemLength = (keyLength + 4 + 3) / 4 * 4;
        var maxKeys = (BlockSize - 12) / itemLength;
        var root = AllocatePage();
        var headerPage = AllocatePage();

        var block = new byte[BlockSize];
        BinaryPrimitives.WriteInt32LittleEndian(block, root);
        block[8] = (byte)(KeyFormatBase | (descending ? KeyFormatDescending : 0) | (unique ? KeyFormatUnique : 0));
        block[9] = (byte)keyType;
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(12), (ushort)keyLength);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(14), (ushort)maxKeys);
        // The secondary key type: 1 for date keys, which dBASE IV checks when it seeks a date tag.
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(16), (ushort)(keyType == 'D' ? 1 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(18), (ushort)itemLength);
        block[20] = 0x01; // change counter; dBASE bumps it, nothing reads it
        block[23] = unique ? KeyFormatUnique : (byte)0;
        expressionBytes.CopyTo(block.AsSpan(24));
        if (forBytes is not null)
        {
            block[245] = 0x01;
            forBytes.CopyTo(block.AsSpan(762));
        }
        WriteBlock(headerPage, block);
        return MdxTag.Load(this, name, headerPage, Encoding);
    }

    /// <summary>
    /// Lists a filled tag in the tag table. The entry goes in first (past the count, so still unseen),
    /// then the count that makes it visible, then the name tree, which only ever points at listed tags.
    /// </summary>
    internal void PublishTag(MdxTag tag)
    {
        var headerArea = ReadHeaderArea(_stream);
        var entryLength = headerArea[26] == 0 ? 32 : headerArea[26];
        var count = Tags.Count;

        var entry = new byte[entryLength];
        BinaryPrimitives.WriteInt32LittleEndian(entry, tag.HeaderPage);
        Encoding.ASCII.GetBytes(tag.Name).AsSpan(0, Math.Min(tag.Name.Length, 10)).CopyTo(entry.AsSpan(4, 11));
        entry[15] = KeyFormatBase;
        entry[19] = 0x02;
        entry[20] = (byte)tag.KeyType;
        _stream.Position = TagTableOffset + (long)entryLength * count;
        _stream.Write(entry);
        DbfTable.FlushToDisk(_stream);

        _stream.Position = 28;
        Span<byte> tagCount = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tagCount, (ushort)(count + 1));
        _stream.Write(tagCount);
        DbfTable.FlushToDisk(_stream);

        WriteNameTree([.. Tags.Select(t => t.Name), tag.Name], entryLength);
        ReloadTags();
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

        // The tree's anchor: the reserved slot just before the table points at the first tag as its
        // right child (byte 529), as in every .mdx dBASE software writes.
        _stream.Position = TagTableOffset - entryLength + 17;
        _stream.WriteByte(names.Count > 0 ? (byte)1 : (byte)0);
    }

    // Expressions are stored in the table's code page, as dBASE stores them, so "Québec" in a FOR
    // filter survives. A character the code page can't hold is refused rather than turned into '?'.
    private byte[] TextBytes(string text, int room)
    {
        byte[] bytes;
        try
        {
            bytes = DbfEncoding.Strict(Encoding).GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException($"The expression '{text}' has a character the table's code page can't hold.");
        }
        if (bytes.Length >= room)
            throw new ArgumentException($"The expression is {bytes.Length} bytes; an .mdx tag holds at most {room - 1}.");
        return bytes;
    }
}
