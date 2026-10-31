//          [ 42 ]                    ProjectThunder :: Thunder.Dbf
//         /      \                   Clipper .NTX: a classic B-tree in 1 KB pages.
//     [7 19]    [57 88]              Every key lives in exactly one page, inner pages
//     /  |  \   /  |  \              included, and every key is stored as text:
//    ..  ..  .. ..  ..  ..           numbers as digits, dates as YYYYMMDD.
//
// Layout notes, with offsets, live in docs/formats/ntx.md.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

/// <summary>
/// A Clipper (and Harbour, xHarbour, X#) single-index (.ntx) file. Opened on its own it's read-only;
/// a table opened writable keeps the .ntx files next to it up to date (see NtxIndexWriter.cs).
/// </summary>
public sealed partial class NtxIndex : IDisposable
{
    internal const int PageSize = 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly string _fileName;
    private readonly DbfTable? _table;

    private NtxIndex(Stream stream, bool leaveOpen, Encoding encoding, string fileName, DbfTable? table)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Encoding = encoding;
        _fileName = fileName;
        _table = table;
    }

    /// <summary>The code page character keys and expressions decode with.</summary>
    public Encoding Encoding { get; }

    /// <summary>The index's one tag.</summary>
    public NtxTag Tag { get; private set; } = null!;

    /// <summary>The one tag, as a list, so code written for .mdx and .cdx tags works here too.</summary>
    public IReadOnlyList<NtxTag> Tags => [Tag];

    /// <summary>The index's one tag, if it's called <paramref name="tagName"/> (in any case).</summary>
    /// <exception cref="KeyNotFoundException">The tag has another name.</exception>
    public NtxTag this[string tagName] =>
        string.Equals(Tag.Name, tagName, StringComparison.OrdinalIgnoreCase)
            ? Tag
            : throw new KeyNotFoundException($"The index has no tag named '{tagName}'; its one tag is '{Tag.Name}'.");

    /// <summary>
    /// Opens an .ntx file. Character keys decode with <paramref name="encoding"/> (the table's encoding
    /// when <paramref name="table"/> is given, else Windows-1252).
    /// </summary>
    /// <param name="table">The table the index belongs to. Clipper stores every key as text and the
    /// file doesn't say what the text means, so without the table all keys read as strings. With it,
    /// the key expression is evaluated on a blank record to learn its type, as Clipper itself does,
    /// and numeric, date and logical keys read back as numbers, <see cref="DateOnly"/> and bool.</param>
    /// <param name="path">The .ntx file.</param>
    /// <param name="encoding">How character keys decode, overriding the table's encoding.</param>
    public static NtxIndex Open(string path, Encoding? encoding = null, DbfTable? table = null)
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
    public static NtxIndex Open(Stream stream, Encoding? encoding = null, bool leaveOpen = false, DbfTable? table = null)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("NTX files need a seekable stream.", nameof(stream));

        var fileName = stream is FileStream file ? Path.GetFileNameWithoutExtension(file.Name).ToUpperInvariant() : "";
        var index = new NtxIndex(stream, leaveOpen, encoding ?? table?.Encoding ?? DbfEncoding.GetEncoding(DbfEncoding.DefaultCodePage), fileName, table);
        index.Tag = NtxTag.Load(index, fileName, table);
        return index;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_leaveOpen)
            _stream.Dispose();
    }

    /// <summary>Reads the 1,024-byte page at byte <paramref name="offset"/>.</summary>
    internal byte[] ReadPage(long offset)
    {
        if (offset < 0 || offset % PageSize != 0 || offset + PageSize > _stream.Length)
            throw new InvalidDataException($"Index page offset {offset} is outside the .ntx file or not on a page boundary.");

        var page = new byte[PageSize];
        DbfTable.DiscardReadBuffer(_stream); // the legacy app may have just changed the page
        _stream.Position = offset;
        _stream.ReadExactly(page);
        return page;
    }
}

/// <summary>The B-tree in an .ntx file: a key expression, an optional FOR filter, and keys in order.</summary>
public sealed partial class NtxTag
{
    private const int MaxDepth = 32;
    private const int MaxKeyLength = 256;
    private const int ExpressionLength = 256;

    // Header offsets (see docs/formats/ntx.md).
    private const int KeyExpressionOffset = 22;
    private const int UniqueOffset = 278;
    private const int DescendOffset = 280;
    private const int ForExpressionOffset = 282;
    private const int TagNameOffset = 538;
    private const int TagNameLength = 12;

    // Signature flags (header bytes 0-1).
    private const ushort FlagForExpression = 0x0001;
    private const ushort FlagDefault = 0x0006;
    private const ushort FlagOldDefault = 0x0003;
    private const ushort FlagLargeFile = 0x0200;
    private const ushort FlagCompound = 0x8000;
    private const ushort FlagMask = 0x87FF;

    // Clipper writes a negative number's digits as '0' - digit - 4, so they sort below '0'. A
    // negative key therefore starts with ',' (the transformed '0' that replaced the minus sign).
    private const byte NegativeMark = (byte)',';

    private readonly NtxIndex _index;
    private readonly long _root;
    private readonly bool _largeFile;

    private NtxTag(NtxIndex index, long root, bool largeFile, string name)
    {
        _index = index;
        _root = root;
        _largeFile = largeFile;
        Name = name;
    }

    /// <summary>The tag name stored in the header (Clipper 5.3, Harbour), else the file name.</summary>
    public string Name { get; }

    /// <summary>'C', 'N', 'D' or 'L'. Always 'C' unless the index was opened with its table.</summary>
    public char KeyType { get; private init; }
    /// <inheritdoc />
    public int KeyLength { get; private init; }

    /// <summary>Decimal places in a numeric key, as given to STR() when the key was built.</summary>
    public int KeyDecimals { get; private init; }
    /// <inheritdoc />
    public bool IsDescending { get; private init; }
    /// <inheritdoc />
    public bool IsUnique { get; private init; }
    /// <inheritdoc />
    public string Expression { get; private init; } = "";
    /// <inheritdoc />
    public string? ForExpression { get; private init; }

    internal int MaxKeys { get; private init; }

    /// <summary>Bytes per item: child pointer (4), record number (4), key, and any padding the writer added.</summary>
    internal int ItemLength { get; private init; }

    internal static NtxTag Load(NtxIndex index, string fileName, DbfTable? table)
    {
        var header = index.ReadPage(0);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if ((flags & FlagCompound) != 0)
            throw new NotSupportedException("This is a Harbour compound .ntx (CTX) file; only single-tag .ntx files are supported.");
        if ((flags & ~FlagMask) != 0 || ((flags & FlagDefault) != FlagDefault && flags != FlagOldDefault))
            throw new InvalidDataException($"The file doesn't start with an .ntx signature (0x{flags:X4}).");

        var root = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        var keyDecimals = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16));
        var maxKeys = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18));

        // A page holds a count, MaxKeys + 1 offsets, and MaxKeys + 1 items (the last only for its child pointer).
        if (keyLength == 0 || keyLength > MaxKeyLength || keyDecimals >= keyLength || itemLength < keyLength + 8 || maxKeys == 0
            || 2L + (maxKeys + 1L) * (2 + itemLength) > NtxIndex.PageSize)
            throw new InvalidDataException($"The .ntx header has an impossible key layout ({keyLength}-byte keys in {itemLength}-byte items, {maxKeys} per page).");

        var expression = IndexKeys.ReadText(header.AsSpan(KeyExpressionOffset, ExpressionLength), index.Encoding);
        if (expression.Length == 0)
            throw new InvalidDataException("The .ntx header has no key expression.");
        var forExpression = (flags & FlagForExpression) != 0
            ? IndexKeys.ReadText(header.AsSpan(ForExpressionOffset, ExpressionLength), index.Encoding)
            : "";
        var tagName = header[TagNameOffset] >= 0x20
            ? IndexKeys.ReadText(header.AsSpan(TagNameOffset, TagNameLength), Encoding.ASCII)
            : fileName;

        var largeFile = (flags & FlagLargeFile) != 0;
        return new NtxTag(index, largeFile ? root * (long)NtxIndex.PageSize : root, largeFile, tagName)
        {
            KeyType = table is null ? 'C' : IndexKeys.InferKeyType(expression, table, fallback: 'C'),
            KeyLength = keyLength,
            KeyDecimals = keyDecimals,
            MaxKeys = maxKeys,
            ItemLength = itemLength,
            IsUnique = header[UniqueOffset] != 0,
            IsDescending = header[DescendOffset] != 0,
            Expression = expression,
            ForExpression = forExpression.Length > 0 ? forExpression : null,
        };
    }

    /// <summary>Every key in index order.</summary>
    public IEnumerable<IndexEntry> Entries() => Walk(probe: null).Select(h => h.Entry);

    /// <summary>
    /// Keys in index order, starting at the first one at or after <paramref name="key"/> (at or
    /// before, for descending tags). Character keys match by prefix, like Clipper's SEEK.
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
    private IEnumerable<Hit> Walk(byte[]? probe) => Walk(CurrentRoot(), probe, position: probe is not null, depth: 0, visited: []);

    private long CurrentRoot()
    {
        var root = BinaryPrimitives.ReadUInt32LittleEndian(_index.ReadPage(0).AsSpan(4));
        return _largeFile ? root * (long)NtxIndex.PageSize : root;
    }

    // A true B-tree: slot i holds a key and the page of keys that come before it, and slot count
    // holds only the page of keys after the last one. In-order is child 0, key 0, child 1, key 1, ...
    // child count. Leaves are the pages whose child pointers are all zero.
    private IEnumerable<Hit> Walk(long offset, byte[]? probe, bool position, int depth, HashSet<long> visited)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"Tag {Name} is deeper than {MaxDepth} levels; the index is probably corrupt.");
        if (!visited.Add(offset))
            throw new InvalidDataException($"Tag {Name} reaches the index page at offset {offset} twice; the index is corrupt.");
        if (offset == 0)
            throw new InvalidDataException($"Tag {Name} points at offset 0, the header; the index is corrupt.");

        var page = _index.ReadPage(offset);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(page);
        if (count > MaxKeys)
            throw new InvalidDataException($"The index page at offset {offset} claims {count} keys; tag {Name} holds at most {MaxKeys} per page.");

        var start = position && probe is not null ? FirstAtOrAfter(page, count, probe, offset) : 0;
        for (var i = start; i <= count; i++)
        {
            var at = ItemOffset(page, i, offset, withKey: i < count);
            var child = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(at));
            if (child != 0)
            {
                var childOffset = _largeFile ? child * (long)NtxIndex.PageSize : child;
                foreach (var hit in Walk(childOffset, probe, position && i == start, depth + 1, visited))
                    yield return hit;
            }

            if (i < count)
            {
                var key = page.AsSpan(at + 8, KeyLength);
                var record = IndexKeys.RecordNumber(BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(at + 4)), Name);
                yield return new Hit(new IndexEntry(DecodeKey(key), record), probe is not null && IndexKeys.CompareCharacter(key, probe) == 0);
            }
        }
    }

    // Items are reached through a table of 2-byte offsets after the count, so a page can reorder
    // keys by swapping offsets instead of moving items. The last slot only needs its child pointer.
    private int ItemOffset(byte[] page, int slot, long pageOffset, bool withKey)
    {
        int at = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(2 + slot * 2));
        var length = withKey ? 8 + KeyLength : 4;
        if (at < 2 || at + length > NtxIndex.PageSize)
            throw new InvalidDataException($"Slot {slot} of the index page at offset {pageOffset} points outside the page.");
        return at;
    }

    private int FirstAtOrAfter(byte[] page, int count, byte[] probe, long pageOffset)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var key = page.AsSpan(ItemOffset(page, mid, pageOffset, withKey: true) + 8, KeyLength);
            if (Order(IndexKeys.CompareCharacter(key, probe)) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private int Order(int ascending) => IsDescending ? -ascending : ascending;

    // ---- keys -----------------------------------------------------------------------------

    /// <summary>
    /// Turns a search value into the bytes Clipper would have stored for it. Every key type is stored
    /// as text, so every comparison is a byte comparison; character probes compare as prefixes.
    /// </summary>
    private byte[] Probe(object? key) => KeyType switch
    {
        'N' => EncodeNumber(key switch
        {
            null => throw new ArgumentNullException(nameof(key), $"Tag {Name} has numeric keys; seek with a number."),
            double or float => (decimal)Convert.ToDouble(key, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(key, CultureInfo.InvariantCulture),
        }),
        'D' => Encoding.ASCII.GetBytes(key switch
        {
            null => "        ",
            DateOnly d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            _ => throw new ArgumentException($"Tag {Name} has date keys; seek with a DateOnly (or null for a blank date), not {key.GetType().Name}."),
        }),
        'L' => [(byte)(IndexKeys.ToBool(key, Name) ? 'T' : 'F')],
        _ => _index.Encoding.GetBytes(IndexKeys.ToText(key, Name)),
    };

    // STR(value, KeyLength, KeyDecimals) with the leading spaces turned into zeros, then, for a
    // negative number, the minus sign into a zero and every digit d into '0' - d - 4.
    private byte[] EncodeNumber(decimal value)
    {
        var rounded = Math.Round(value, Math.Min(KeyDecimals, 28), MidpointRounding.AwayFromZero); // decimal holds 28 places at most
        var text = rounded.ToString("F" + KeyDecimals, CultureInfo.InvariantCulture);
        if (text.Length > KeyLength)
            return Encoding.ASCII.GetBytes(new string('*', KeyLength)); // what STR() writes when it overflows

        var key = Encoding.ASCII.GetBytes(text.PadLeft(KeyLength, '0'));
        var minus = Array.IndexOf(key, (byte)'-');
        if (minus >= 0)
        {
            key[minus] = (byte)'0';
            for (var i = 0; i < key.Length; i++)
            {
                if (key[i] is >= (byte)'0' and <= (byte)'9')
                    key[i] = (byte)('0' - (key[i] - '0') - 4);
            }
        }
        return key;
    }

    private object? DecodeKey(ReadOnlySpan<byte> key) => KeyType switch
    {
        'N' => DecodeNumber(key),
        'D' => DecodeDate(key),
        'L' => key[0] == 'T',
        _ => _index.Encoding.GetString(key).TrimEnd(' ', '\0'),
    };

    /// <summary>Reverses <see cref="EncodeNumber"/>. A key that isn't a number (STR() overflow asterisks) comes back as its text.</summary>
    private static object? DecodeNumber(ReadOnlySpan<byte> key)
    {
        var chars = new char[key.Length];
        for (var i = 0; i < key.Length; i++)
            chars[i] = (char)key[i];

        if (key.Length > 0 && key[0] == NegativeMark)
        {
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] != '.')
                    chars[i] = (char)('0' - (chars[i] - '0' + 4));
            }
            chars[0] = '-';
        }

        var text = new string(chars).Trim(' ', '\0');
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : text;
    }

    private static DateOnly? DecodeDate(ReadOnlySpan<byte> key)
    {
        if (key.Length < 8)
            return null;
        var text = Encoding.ASCII.GetString(key[..8]);
        return DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }
}
