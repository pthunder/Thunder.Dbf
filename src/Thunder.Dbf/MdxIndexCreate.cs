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
        ThrowIfDeferring();
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
        ThrowIfDeferring();
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

    /// <summary>
    /// Takes a tag out of the tag table, like dBASE's <c>DELETE TAG</c>, in three writes that each leave
    /// dBASE's name tree whole, so a kill between any two loses nothing a lookup by name needs:
    /// <list type="number">
    /// <item>The remaining entries, moved down over the dropped one, each with its links in the name
    /// tree rebuilt over the remaining tags, and the tree's anchor, all in one write. The tree now
    /// reaches only them; the count still includes the last slot, which keeps its old entry (the last
    /// tag again, so a reader walking the table lists it twice until the next write; Thunder.Dbf reads
    /// one tag per header block).</item>
    /// <item>The smaller count.</item>
    /// <item>The slot past it cleared, as dBASE leaves unused slots.</item>
    /// </list>
    /// The tag's blocks are left unused, never freed: nothing points at them any more.
    /// </summary>
    internal void DropTag(MdxTag tag)
    {
        ThrowIfDeferring();
        var headerArea = ReadHeaderArea(_stream);
        var entryLength = headerArea[26] == 0 ? 32 : headerArea[26];
        var count = Tags.Count;
        if (!Tags.Any(t => t.HeaderPage == tag.HeaderPage))
            throw new InvalidOperationException($"The index has no tag named {tag.Name}.");

        // The table as listed, one entry per tag, the dropped one taken out.
        var kept = Enumerable.Range(0, count)
            .Select(i => headerArea.AsSpan(TagTableOffset + entryLength * i, entryLength).ToArray())
            .Where(entry => BinaryPrimitives.ReadInt32LittleEndian(entry) != tag.HeaderPage)
            .ToList();
        var tree = NameTree(Tags.Where(t => t.HeaderPage != tag.HeaderPage).Select(t => t.Name).ToList());

        // 0. A before-image of the header and tag table, forced to disk beside the index: a power cut
        // can tear the writes below sector by sector, losing a tag, and the next exclusive open puts
        // this back (see RestoreDropTag).
        var imagePath = _stream is FileStream file ? file.Name + DropTagImageSuffix : null;
        if (imagePath is not null)
        {
            var image = new byte[HeaderAreaSize];
            DbfTable.DiscardReadBuffer(_stream);
            _stream.Position = 0;
            _stream.ReadExactly(image);
            using (var before = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                before.Write(image);
                before.Flush(flushToDisk: true);
            }
            DropTagPoint?.Invoke("before-image");
        }

        // 1. The anchor's slot and the kept entries with their new links, as one write.
        var anchorAt = TagTableOffset - entryLength;
        var block = headerArea.AsSpan(anchorAt, entryLength * (1 + kept.Count)).ToArray();
        block[17] = kept.Count > 0 ? (byte)1 : (byte)0;
        for (var i = 0; i < kept.Count; i++)
        {
            kept[i].CopyTo(block, entryLength * (1 + i));
            block[entryLength * (1 + i) + 16] = tree.Left[i];
            block[entryLength * (1 + i) + 17] = tree.Right[i];
            block[entryLength * (1 + i) + 18] = tree.Parent[i];
        }
        _stream.Position = anchorAt;
        _stream.Write(block);
        DbfTable.FlushToDisk(_stream);
        DropTagPoint?.Invoke("entries");

        // 2. The count.
        _stream.Position = 28;
        Span<byte> tagCount = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tagCount, (ushort)(count - 1));
        _stream.Write(tagCount);
        DbfTable.FlushToDisk(_stream);
        DropTagPoint?.Invoke("count");

        // 3. The slot past the new count.
        _stream.Position = TagTableOffset + (long)entryLength * (count - 1);
        _stream.Write(new byte[entryLength]);
        DbfTable.FlushToDisk(_stream);
        DropTagPoint?.Invoke("cleared");
        if (imagePath is not null)
            File.Delete(imagePath);
        ReloadTags();
    }

    /// <summary>The before-image a DELETE TAG keeps beside the .mdx while it rewrites the tag table.</summary>
    internal const string DropTagImageSuffix = ".droptag";

    /// <summary>
    /// A DELETE TAG cut off leaves its before-image beside the .mdx, and the tag table may be torn.
    /// An exclusive open writes the image back (the tag is then not dropped) and deletes it; anything
    /// else refuses to write until one has. An image of the wrong size, or a link, is refused.
    /// </summary>
    internal static void RestoreDropTag(string mdxPath, bool exclusive)
    {
        var imagePath = mdxPath + DropTagImageSuffix;
        if (!File.Exists(imagePath))
            return;
        if (!exclusive)
            throw new DbfException(DbfErrorCode.IncompleteTransaction, "A DELETE TAG on this table's .mdx was cut off, so its tag table may be torn.",
                "Open the table with writable: true, exclusive: true, which puts the tag table back as it was.", mdxPath);
        if (new FileInfo(imagePath).LinkTarget is not null || new FileInfo(imagePath).Length != HeaderAreaSize)
            throw new DbfException(DbfErrorCode.IndexDamaged, "A DELETE TAG on this table's .mdx was cut off, and its copy of the tag table isn't one Thunder.Dbf wrote.",
                "Rebuild the index in dBASE (or delete the .mdx and recreate its tags), then delete the .droptag file beside it.", mdxPath);
        var image = File.ReadAllBytes(imagePath);
        using (var index = DbfException.OpenFile(mdxPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            index.Write(image);
            index.Flush(flushToDisk: true);
        }
        DropTagPoint?.Invoke("restored");
        File.Delete(imagePath);
    }

    /// <summary>For the tests: called after each of DropTag's writes.</summary>
    [ThreadStatic]
    internal static Action<string>? DropTagPoint;

    // The left / right / parent slots (1-based, 0 = none) of a binary search tree over the tag names,
    // inserted in table order: the same tree dBASE IV builds.
    private static (byte[] Left, byte[] Right, byte[] Parent) NameTree(IReadOnlyList<string> names)
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
        return (left, right, parent);
    }

    private void WriteNameTree(IReadOnlyList<string> names, int entryLength)
    {
        var (left, right, parent) = NameTree(names);
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
