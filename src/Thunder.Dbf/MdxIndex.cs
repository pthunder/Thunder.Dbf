//                 [ 42 | 87 ]                 ProjectThunder :: Thunder.Dbf
//                /     |     \                dBASE IV .MDX: up to 47 B-trees ("tags")
//       [ 7 | 42 ] [ 51 | 87 ] [ 90 | 99 ]    in one file, all in 512-byte pages.
//        |    |     |    |      |    |
//       rec  rec   rec  rec    rec  rec       Leaves point at records; interior keys
//                                             are the last key of each child.
//
// Layout notes, with offsets, live in docs/formats/mdx.md.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

/// <summary>One key in an index, and the 1-based record number it points at.</summary>
public readonly record struct MdxEntry(object? Key, int RecordNumber);

/// <summary>A dBASE IV multiple-index (.mdx) file. Read-only for now.</summary>
public sealed class MdxIndex : IDisposable
{
    internal const int PageSize = 512;
    private const int TagTableOffset = 544;
    private const int MaxTags = 47;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    private MdxIndex(Stream stream, bool leaveOpen, int blockSize, Encoding encoding)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        BlockSize = blockSize;
        Encoding = encoding;
    }

    public int BlockSize { get; }
    public Encoding Encoding { get; }
    public IReadOnlyList<MdxTag> Tags { get; private set; } = [];

    /// <summary>True when dBASE opens this index automatically with its table.</summary>
    public bool IsProduction { get; private set; }

    public MdxTag this[string tagName] =>
        Tags.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"The index has no tag named '{tagName}'.");

    /// <summary>Opens an .mdx file. Character keys decode with <paramref name="encoding"/> (Windows-1252 by default).</summary>
    public static MdxIndex Open(string path, Encoding? encoding = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return Open(stream, encoding);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static MdxIndex Open(Stream stream, Encoding? encoding = null, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("MDX files need a seekable stream.", nameof(stream));

        var header = new byte[TagTableOffset + 32 * MaxTags];
        stream.Position = 0;
        if (stream.ReadAtLeast(header, TagTableOffset, throwOnEndOfStream: false) < TagTableOffset)
            throw new InvalidDataException("The file is too short to be an .mdx index.");

        var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22, 2));
        if (blockSize < PageSize || blockSize % PageSize != 0)
            throw new InvalidDataException($"An .mdx block size of {blockSize} bytes isn't a multiple of {PageSize}.");

        var tagCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
        var entryLength = header[26] == 0 ? 32 : header[26];
        if (tagCount > MaxTags || entryLength < 21)
            throw new InvalidDataException($"The .mdx header claims {tagCount} tags of {entryLength} bytes.");

        var index = new MdxIndex(stream, leaveOpen, blockSize, encoding ?? DbfEncoding.GetEncoding(DbfEncoding.DefaultCodePage))
        {
            IsProduction = header[24] == 0x01,
        };

        var tags = new List<MdxTag>(tagCount);
        for (var i = 0; i < tagCount; i++)
        {
            var entry = header.AsSpan(TagTableOffset + entryLength * i, entryLength);
            var headerPage = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var nameBytes = entry[4..15];
            var nul = nameBytes.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(nul >= 0 ? nameBytes[..nul] : nameBytes).TrimEnd();
            tags.Add(MdxTag.Load(index, name, headerPage));
        }
        index.Tags = tags.AsReadOnly();
        return index;
    }

    /// <summary>Finds the production .mdx next to a table, if the table says it has one.</summary>
    public static string? FindFor(string tablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(tablePath))!;
        var wanted = Path.GetFileNameWithoutExtension(tablePath) + ".mdx";
        return Directory.EnumerateFiles(directory)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), wanted, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            _stream.Dispose();
    }

    internal byte[] ReadBlock(int page)
    {
        var offset = (long)page * PageSize;
        if (page <= 0 || offset + BlockSize > _stream.Length)
            throw new InvalidDataException($"Index page {page} is outside the .mdx file.");

        var block = new byte[BlockSize];
        _stream.Position = offset;
        _stream.ReadExactly(block);
        return block;
    }
}

/// <summary>One tag (B-tree) in an .mdx file: a key expression, an optional FOR filter, and keys in order.</summary>
public sealed class MdxTag
{
    private const int MaxDepth = 32;

    private readonly MdxIndex _index;
    private readonly int _headerPage;

    private MdxTag(MdxIndex index, int headerPage, string name)
    {
        _index = index;
        _headerPage = headerPage;
        Name = name;
    }

    public string Name { get; }

    /// <summary>'C' (character), 'N' (numeric) or 'D' (date).</summary>
    public char KeyType { get; private init; }
    public int KeyLength { get; private init; }
    public bool IsDescending { get; private init; }
    public bool IsUnique { get; private init; }
    public string Expression { get; private init; } = "";
    public string? ForExpression { get; private init; }

    internal int ItemLength { get; private init; }
    internal int MaxKeys { get; private init; }

    internal static MdxTag Load(MdxIndex index, string name, int headerPage)
    {
        var header = index.ReadBlock(headerPage);
        var keyType = (char)header[9];
        if (keyType is not ('C' or 'N' or 'D'))
            throw new InvalidDataException($"Tag {name} has key type '{keyType}'.");

        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12, 2));
        var maxKeys = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14, 2));
        var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2));
        if (keyLength == 0 || itemLength < keyLength + 4 || 12 + maxKeys * itemLength > index.BlockSize)
            throw new InvalidDataException($"Tag {name} has an impossible key layout ({keyLength}-byte keys in {itemLength}-byte items, {maxKeys} per node).");

        return new MdxTag(index, headerPage, name)
        {
            KeyType = keyType,
            KeyLength = keyLength,
            ItemLength = itemLength,
            MaxKeys = maxKeys,
            IsDescending = (header[8] & 0x08) != 0,
            IsUnique = (header[8] & 0x40) != 0 || header[23] != 0,
            Expression = ReadAscii(header.AsSpan(24, 221)),
            ForExpression = header[245] != 0 ? ReadAscii(header.AsSpan(762, Math.Min(220, header.Length - 762))) : null,
        };
    }

    /// <summary>Every key in index order.</summary>
    public IEnumerable<MdxEntry> Entries() => Walk(RootPage(), probe: null, depth: 0).Select(h => h.Entry);

    /// <summary>
    /// Keys in index order, starting at the first one at or after <paramref name="key"/> (at or
    /// before, for descending tags). Character keys match by prefix, like dBASE's SEEK.
    /// </summary>
    public IEnumerable<MdxEntry> Seek(object key) => Walk(RootPage(), Probe(key), depth: 0).Select(h => h.Entry);

    /// <summary>Only the keys equal to <paramref name="key"/> (prefix-equal for character keys).</summary>
    public IEnumerable<MdxEntry> SeekExact(object key)
    {
        var probe = Probe(key);
        foreach (var entry in Walk(RootPage(), probe, depth: 0))
        {
            if (!entry.Matches)
                yield break;
            yield return entry.Entry;
        }
    }

    private int RootPage() => BinaryPrimitives.ReadInt32LittleEndian(_index.ReadBlock(_headerPage));

    // ---- B-tree walk ----------------------------------------------------------------------

    private readonly record struct Hit(MdxEntry Entry, bool Matches);

    private IEnumerable<Hit> Walk(int page, object? probe, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"Tag {Name} is deeper than {MaxDepth} levels; the index is probably corrupt.");

        var node = _index.ReadBlock(page);
        var count = BinaryPrimitives.ReadInt32LittleEndian(node);
        if (count < 0 || count > MaxKeys)
            throw new InvalidDataException($"Index page {page} claims {count} keys; tag {Name} holds at most {MaxKeys} per node.");

        var isLeaf = BinaryPrimitives.ReadInt32LittleEndian(node.AsSpan(8 + count * ItemLength, 4)) == 0;
        var start = probe is null ? 0 : FirstAtOrAfter(node, count, probe);

        if (isLeaf)
        {
            for (var i = start; i < count; i++)
            {
                var item = node.AsSpan(8 + i * ItemLength, ItemLength);
                var key = item.Slice(4, KeyLength);
                var matches = probe is not null && Compare(key, probe) == 0;
                yield return new Hit(new MdxEntry(DecodeKey(key), BinaryPrimitives.ReadInt32LittleEndian(item)), matches);
            }
            yield break;
        }

        // Interior: n keys, n + 1 child pointers. Only the first child we enter needs the probe.
        for (var i = start; i <= count; i++)
        {
            var child = BinaryPrimitives.ReadInt32LittleEndian(node.AsSpan(8 + i * ItemLength, 4));
            foreach (var hit in Walk(child, i == start ? probe : null, depth + 1))
            {
                // Past the first child we no longer compare, so recompute matches for exact seeks.
                yield return i == start || probe is null
                    ? hit
                    : hit with { Matches = CompareDecoded(hit.Entry.Key, probe) == 0 };
            }
        }
    }

    private int FirstAtOrAfter(byte[] node, int count, object probe)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var key = node.AsSpan(8 + mid * ItemLength + 4, KeyLength);
            if (Order(Compare(key, probe)) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private int Order(int ascending) => IsDescending ? -ascending : ascending;

    // ---- keys -----------------------------------------------------------------------------

    /// <summary>Turns a search value into something keys can be compared with.</summary>
    private object Probe(object key) => KeyType switch
    {
        'C' => _index.Encoding.GetBytes(key as string ?? Convert.ToString(key, CultureInfo.InvariantCulture) ?? ""),
        'N' => Convert.ToDecimal(key, CultureInfo.InvariantCulture),
        _ => key switch
        {
            DateOnly d => MdxKeys.JulianDay(d),
            DateTime dt => MdxKeys.JulianDay(DateOnly.FromDateTime(dt)),
            _ => throw new ArgumentException($"Tag {Name} has date keys; seek with a DateOnly, not {key.GetType().Name}."),
        },
    };

    /// <summary>Ascending comparison of a stored key with a probe. Character probes compare as prefixes.</summary>
    private int Compare(ReadOnlySpan<byte> key, object probe) => probe switch
    {
        byte[] prefix => key[..Math.Min(prefix.Length, key.Length)].SequenceCompareTo(prefix.AsSpan(0, Math.Min(prefix.Length, key.Length))),
        decimal number => MdxKeys.DecodeNumeric(key).CompareTo(number),
        double julian => BinaryPrimitives.ReadDoubleLittleEndian(key).CompareTo(julian),
        _ => throw new InvalidOperationException(),
    };

    private int CompareDecoded(object? key, object probe) => probe switch
    {
        byte[] => Compare(_index.Encoding.GetBytes(((string?)key ?? "").PadRight(KeyLength)), probe),
        decimal number => ((decimal)key!).CompareTo(number),
        double julian => (key is DateOnly d ? MdxKeys.JulianDay(d) : 0d).CompareTo(julian),
        _ => throw new InvalidOperationException(),
    };

    private object? DecodeKey(ReadOnlySpan<byte> key) => KeyType switch
    {
        'C' => _index.Encoding.GetString(key).TrimEnd(' ', '\0'),
        'N' => MdxKeys.DecodeNumeric(key),
        _ => MdxKeys.DecodeDate(key),
    };

    private static string ReadAscii(ReadOnlySpan<byte> bytes)
    {
        var nul = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul >= 0 ? bytes[..nul] : bytes).Trim();
    }
}

/// <summary>dBASE IV key encodings: 12-byte BCD numbers and Julian-day dates.</summary>
internal static class MdxKeys
{
    public const int NumericKeyLength = 12;
    private const int ExponentBias = 52;
    private const int MaxDigits = 20;
    private const int JulianDayOfDayNumberZero = 1721426;

    public static double JulianDay(DateOnly date) => date.DayNumber + JulianDayOfDayNumberZero;

    public static DateOnly? DecodeDate(ReadOnlySpan<byte> key)
    {
        var julian = BinaryPrimitives.ReadDoubleLittleEndian(key);
        var dayNumber = (long)julian - JulianDayOfDayNumberZero;
        return julian == 0 || dayNumber < 0 || dayNumber > DateOnly.MaxValue.DayNumber ? null : DateOnly.FromDayNumber((int)dayNumber);
    }

    // value = ±0.d1d2d3... × 10^(byte0 - 52); byte1 bit 7 is the sign; digits are packed BCD, high nibble first.
    public static decimal DecodeNumeric(ReadOnlySpan<byte> key)
    {
        var digits = new StringBuilder(MaxDigits);
        for (var i = 2; i < NumericKeyLength && i < key.Length; i++)
        {
            digits.Append((char)('0' + (key[i] >> 4)));
            digits.Append((char)('0' + (key[i] & 0x0F)));
        }
        var significant = digits.ToString().TrimEnd('0');
        if (significant.Length == 0)
            return 0m;

        var exponent = key[0] - ExponentBias - significant.Length;
        var value = decimal.Parse(significant + "E" + exponent.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        return (key[1] & 0x80) != 0 ? -value : value;
    }

    public static byte[] EncodeNumeric(decimal value)
    {
        var key = new byte[NumericKeyLength];
        if (value == 0)
        {
            key[0] = ExponentBias;
            key[1] = 0x01;
            return key;
        }

        var text = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        var point = text.IndexOf('.');
        var whole = (point < 0 ? text : text[..point]).TrimStart('0');
        var fraction = point < 0 ? "" : text[(point + 1)..].TrimEnd('0');

        int exponent;
        string significant;
        if (whole.Length > 0)
        {
            exponent = whole.Length;
            significant = whole + fraction;
        }
        else
        {
            var leadingZeros = fraction.Length - fraction.TrimStart('0').Length;
            exponent = -leadingZeros;
            significant = fraction[leadingZeros..];
        }

        // Xbase64 counts every digit it printed after stripping zeros: 100 counts 3, .05 counts 2.
        var count = Math.Min(whole.Length + fraction.Length, 31);
        significant = significant.Length > MaxDigits ? significant[..MaxDigits] : significant;

        key[0] = (byte)(ExponentBias + exponent);
        key[1] = (byte)((count << 2) | 0x01 | (value < 0 ? 0x80 : 0));
        for (var i = 0; i < significant.Length; i++)
        {
            var digit = (byte)(significant[i] - '0');
            key[2 + i / 2] |= (byte)(i % 2 == 0 ? digit << 4 : digit);
        }
        return key;
    }
}
