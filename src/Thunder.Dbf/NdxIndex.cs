//       ____________                 ProjectThunder :: Thunder.Dbf
//      | 3 | 7 | -> |  (page 9)      dBASE III .NDX: one B+ tree, one key expression,
//      |___|___|____|                one file, 512-byte pages, and a spare slot at the
//       /     |     \                end of every inner page for the rightmost child.
//    [1 2 3] [5 7] [8 9 11]
//
// Layout notes, with offsets, live in docs/formats/ndx.md.

using System.Buffers.Binary;
using System.Text;

namespace Thunder.Dbf;

/// <summary>
/// A dBASE III single-index (.ndx) file. Opened on its own it only reads; a writable
/// <see cref="DbfTable"/> opens the .ndx files next to it itself and keeps them up to date.
/// </summary>
public sealed partial class NdxIndex : IDisposable
{
    internal const int PageSize = 512;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly DbfTable? _table; // kept so a reload still knows which numeric keys are dates

    private NdxIndex(Stream stream, bool leaveOpen, Encoding encoding, DbfTable? table)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _table = table;
        Encoding = encoding;
    }

    /// <summary>The code page character keys and expressions decode with.</summary>
    public Encoding Encoding { get; }

    /// <summary>The index's one tag. An .ndx holds a single B-tree, named after its file.</summary>
    public NdxTag Tag { get; private set; } = null!;

    /// <summary>The one tag, as a list, so code written for .mdx and .cdx tags works here too.</summary>
    public IReadOnlyList<NdxTag> Tags => [Tag];

    public NdxTag this[string tagName] =>
        string.Equals(Tag.Name, tagName, StringComparison.OrdinalIgnoreCase)
            ? Tag
            : throw new KeyNotFoundException($"The index has no tag named '{tagName}'; its one tag is '{Tag.Name}'.");

    /// <summary>
    /// Opens an .ndx file. Character keys decode with <paramref name="encoding"/> (the table's encoding
    /// when <paramref name="table"/> is given, else Windows-1252).
    /// </summary>
    /// <param name="table">The table the index belongs to. dBASE stores date keys as plain numbers
    /// (Julian day numbers); with the table, a key expression that returns a date reads back as
    /// <see cref="DateOnly"/>. Without it, date keys read as numbers.</param>
    public static NdxIndex Open(string path, Encoding? encoding = null, DbfTable? table = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return Open(stream, encoding, leaveOpen: false, table);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="Open(string, Encoding?, DbfTable?)"/>
    /// <remarks>The tag is named after the file when <paramref name="stream"/> is a <see cref="FileStream"/>, else it has no name.</remarks>
    public static NdxIndex Open(Stream stream, Encoding? encoding = null, bool leaveOpen = false, DbfTable? table = null)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("NDX files need a seekable stream.", nameof(stream));

        var name = stream is FileStream file ? Path.GetFileNameWithoutExtension(file.Name).ToUpperInvariant() : "";
        var index = new NdxIndex(stream, leaveOpen, encoding ?? table?.Encoding ?? DbfEncoding.GetEncoding(DbfEncoding.DefaultCodePage), table);
        index.Tag = NdxTag.Load(index, name, table);
        return index;
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            _stream.Dispose();
    }

    /// <summary>Reads page <paramref name="page"/> (page 0 is the header).</summary>
    internal byte[] ReadPage(long page)
    {
        var offset = page * PageSize;
        if (page < 0 || offset + PageSize > _stream.Length)
            throw new InvalidDataException($"Index page {page} is outside the .ndx file.");

        var block = new byte[PageSize];
        DbfTable.DiscardReadBuffer(_stream); // the legacy app may have just changed the page
        _stream.Position = offset;
        _stream.ReadExactly(block);
        return block;
    }
}

/// <summary>The B-tree in an .ndx file: a key expression and its keys in order.</summary>
public sealed partial class NdxTag
{
    private const int MaxDepth = 32;
    private const int MaxKeyLength = 100;
    private const int ExpressionOffset = 24;
    private const int NumericKeyLength = 8;

    private readonly NdxIndex _index;
    private readonly uint _rootPage;

    private NdxTag(NdxIndex index, uint rootPage, string name)
    {
        _index = index;
        _rootPage = rootPage;
        Name = name;
    }

    /// <summary>The file name without its extension, upper case: dBASE refers to an .ndx by its file name.</summary>
    public string Name { get; }

    /// <summary>
    /// 'C' (character), 'N' (numeric) or 'D' (date). The file only says "character" or "number", so
    /// 'D' needs the table (see <see cref="NdxIndex.Open(string, Encoding?, DbfTable?)"/>).
    /// </summary>
    public char KeyType { get; private init; }
    public int KeyLength { get; private init; }

    /// <summary>Always false: dBASE III indexes only ascend.</summary>
    public bool IsDescending => false;
    public bool IsUnique { get; private init; }
    public string Expression { get; private init; } = "";

    /// <summary>Always null: dBASE III indexes have no FOR filter.</summary>
    public string? ForExpression => null;

    internal int ItemLength { get; private init; }
    internal int MaxKeys { get; private init; }

    internal static NdxTag Load(NdxIndex index, string name, DbfTable? table)
    {
        var header = index.ReadPage(0);
        var root = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
        var maxKeys = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        var numeric = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16));
        var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18));

        // An inner page holds MaxKeys full items plus the 4-byte child pointer of one more.
        if (numeric > 1 || keyLength == 0 || keyLength > MaxKeyLength || (numeric == 1 && keyLength != NumericKeyLength)
            || itemLength < keyLength + 8 || maxKeys == 0 || 4L + (long)maxKeys * itemLength + 4 > NdxIndex.PageSize)
            throw new InvalidDataException($"The .ndx header has an impossible key layout ({keyLength}-byte keys in {itemLength}-byte items, {maxKeys} per page).");
        if (root == 0)
            throw new InvalidDataException("The .ndx header has no root page.");

        // dBASE leaves stray memory after the expression's NUL; only the bytes before it count.
        var expression = IndexKeys.ReadText(header.AsSpan(ExpressionOffset), index.Encoding);
        var keyType = numeric == 0 ? 'C' : table is null ? 'N' : IndexKeys.InferKeyType(expression, table, fallback: 'N') == 'D' ? 'D' : 'N';

        return new NdxTag(index, root, name)
        {
            KeyType = keyType,
            KeyLength = keyLength,
            ItemLength = itemLength,
            MaxKeys = maxKeys,
            IsUnique = header[23] != 0,
            Expression = expression,
        };
    }

    /// <summary>Every key in index order.</summary>
    public IEnumerable<IndexEntry> Entries() => Walk(probe: null).Select(h => h.Entry);

    /// <summary>
    /// Keys in index order, starting at the first one at or after <paramref name="key"/>. Character
    /// keys match by prefix, like dBASE's SEEK.
    /// </summary>
    public IEnumerable<IndexEntry> Seek(object? key) => Walk(Probe(key)).Select(h => h.Entry);

    /// <summary>Only the keys equal to <paramref name="key"/> (prefix-equal for character keys).</summary>
    public IEnumerable<IndexEntry> SeekExact(object? key)
    {
        foreach (var hit in Walk(Probe(key)))
        {
            if (!hit.Matches)
                yield break;
            yield return hit.Entry;
        }
    }

    // ---- B-tree walk ----------------------------------------------------------------------

    private readonly record struct Hit(IndexEntry Entry, bool Matches);

    // The root is re-read for every walk: a split in the legacy app moves it.
    private IEnumerable<Hit> Walk(KeyProbe? probe) =>
        Walk(BinaryPrimitives.ReadUInt32LittleEndian(_index.ReadPage(0)), probe, position: probe is not null, depth: 0, visited: []);

    // Same shape as the .mdx walk: only the leftmost path positions; everything after it is read in
    // order, and each page may be visited once, so loops in a damaged file end in an error.
    private IEnumerable<Hit> Walk(uint page, KeyProbe? probe, bool position, int depth, HashSet<uint> visited)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"Tag {Name} is deeper than {MaxDepth} levels; the index is probably corrupt.");
        if (!visited.Add(page))
            throw new InvalidDataException($"Tag {Name} reaches index page {page} twice; the index is corrupt.");
        if (page == 0)
            throw new InvalidDataException($"Tag {Name} has an inner page pointing at page 0, the header; the index is corrupt.");

        var node = _index.ReadPage(page);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(node); // bytes 2-3 are leftover memory
        if (count > MaxKeys)
            throw new InvalidDataException($"Index page {page} claims {count} keys; tag {Name} holds at most {MaxKeys} per page.");

        // A page is an inner page when its first item points at a lower page. dBASE can leave an
        // inner page with no keys and its one child in slot 0, so the count can't decide it.
        var isLeaf = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(4)) == 0;
        var start = position && probe is not null ? FirstAtOrAfter(node, count, probe) : 0;

        if (isLeaf)
        {
            for (var i = start; i < count; i++)
            {
                var item = node.AsSpan(4 + i * ItemLength, ItemLength);
                var key = item.Slice(8, KeyLength);
                var record = IndexKeys.RecordNumber(BinaryPrimitives.ReadUInt32LittleEndian(item[4..]), Name);
                var matches = probe is not null && Compare(key, probe) == 0;
                yield return new Hit(new IndexEntry(DecodeKey(key), record), matches);
            }
            yield break;
        }

        // Inner page: count keys, count + 1 children. Key i is the highest key under child i; the
        // last child sits in the slot after the last key and has no key of its own.
        for (var i = start; i <= count; i++)
        {
            var child = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(4 + i * ItemLength));
            foreach (var hit in Walk(child, probe, position && i == start, depth + 1, visited))
                yield return hit;
        }
    }

    private int FirstAtOrAfter(byte[] node, int count, KeyProbe probe)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Compare(node.AsSpan(4 + mid * ItemLength + 8, KeyLength), probe) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // ---- keys -----------------------------------------------------------------------------

    private abstract record KeyProbe;
    private sealed record CharacterProbe(byte[] Bytes) : KeyProbe;
    private sealed record NumberProbe(double Value) : KeyProbe;

    /// <summary>Turns a search value into the stored key form.</summary>
    private KeyProbe Probe(object? key) => KeyType switch
    {
        'C' => new CharacterProbe(_index.Encoding.GetBytes(IndexKeys.ToText(key, Name))),
        'D' => new NumberProbe(IndexKeys.ToJulianDay(key, Name)),
        _ => new NumberProbe(key is DateOnly or DateTime ? IndexKeys.ToJulianDay(key, Name) : IndexKeys.ToDouble(key, Name)),
    };

    private static int Compare(ReadOnlySpan<byte> key, KeyProbe probe) => probe switch
    {
        CharacterProbe c => IndexKeys.CompareCharacter(key, c.Bytes),
        NumberProbe n => BinaryPrimitives.ReadDoubleLittleEndian(key).CompareTo(n.Value),
        _ => throw new InvalidOperationException(),
    };

    private object? DecodeKey(ReadOnlySpan<byte> key) => KeyType switch
    {
        'C' => _index.Encoding.GetString(key).TrimEnd(' ', '\0'),
        'D' => IndexKeys.FromJulianDay(BinaryPrimitives.ReadDoubleLittleEndian(key)),
        _ => IndexKeys.FromDouble(BinaryPrimitives.ReadDoubleLittleEndian(key)),
    };
}
