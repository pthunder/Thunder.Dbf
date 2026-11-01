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
public sealed partial class MdxIndex : IDisposable
{
    internal const int PageSize = 512;
    private const int TagTableOffset = 544;
    private const int MaxTags = 47;
    private const int HeaderAreaSize = 4 * PageSize;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    private MdxIndex(Stream stream, bool leaveOpen, int blockSize, Encoding encoding)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        BlockSize = blockSize;
        Encoding = encoding;
    }

    /// <summary>Bytes per block, from the file header.</summary>
    public int BlockSize { get; }
    /// <summary>The encoding character keys are decoded with.</summary>
    public Encoding Encoding { get; }
    /// <summary>The tags, in the order they appear in the file.</summary>
    public IReadOnlyList<MdxTag> Tags { get; private set; } = [];

    /// <summary>True when dBASE opens this index automatically with its table.</summary>
    public bool IsProduction { get; private set; }

    /// <summary>The tag called <paramref name="tagName"/>, in any case.</summary>
    /// <exception cref="KeyNotFoundException">The index has no such tag.</exception>
    public MdxTag this[string tagName] =>
        Tags.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"The index has no tag named '{tagName}'.");

    /// <summary>Opens an .mdx file. Character keys decode with <paramref name="encoding"/> (Windows-1252 by default).</summary>
    public static MdxIndex Open(string path, Encoding? encoding = null)
    {
        var stream = DbfException.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return Open(stream, encoding);
        }
        catch (InvalidDataException ex)
        {
            stream.Dispose();
            throw new DbfException(DbfErrorCode.IndexDamaged, $"The .mdx index can't be read: {ex.Message}",
                "Rebuild it in the legacy app (REINDEX) or restore it from a backup.", path, ex);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads an .mdx index from a seekable stream.</summary>
    /// <param name="stream">The index file's contents.</param>
    /// <param name="encoding">How character keys decode; Windows-1252 when null.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the index is disposed.</param>
    public static MdxIndex Open(Stream stream, Encoding? encoding = null, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("MDX files need a seekable stream.", nameof(stream));

        var header = ReadHeaderArea(stream);
        var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22, 2));
        if (blockSize < PageSize || blockSize % PageSize != 0)
            throw new InvalidDataException($"An .mdx block size of {blockSize} bytes isn't a multiple of {PageSize}.");

        var index = new MdxIndex(stream, leaveOpen, blockSize, encoding ?? DbfEncoding.GetEncoding(DbfEncoding.DefaultCodePage))
        {
            IsProduction = header[24] == 0x01,
        };
        index.Tags = index.LoadTags(header);
        return index;
    }

    /// <summary>
    /// Re-reads the tag table, in case the legacy app added, dropped or rebuilt tags since the index
    /// was opened, so writes maintain every tag that exists now.
    /// </summary>
    internal void ReloadTags() => Tags = LoadTags(ReadHeaderArea(_stream));

    private static byte[] ReadHeaderArea(Stream stream)
    {
        var header = new byte[HeaderAreaSize];
        DbfTable.DiscardReadBuffer(stream);
        stream.Position = 0;
        if (stream.ReadAtLeast(header.AsMemory(0, TagTableOffset).Span, TagTableOffset, throwOnEndOfStream: false) < TagTableOffset)
            throw new InvalidDataException("The file is too short to be an .mdx index.");

        var tagCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
        var entryLength = header[26] == 0 ? 32 : header[26];
        if (tagCount > MaxTags || entryLength < 21 || TagTableOffset + (long)entryLength * tagCount > HeaderAreaSize)
            throw new InvalidDataException($"The .mdx header claims {tagCount} tags of {entryLength} bytes, more than its 2,048-byte header holds.");

        // Read every declared tag entry exactly; a short read must not leave entries zero-filled.
        var tableBytes = entryLength * tagCount;
        if (stream.ReadAtLeast(header.AsSpan(TagTableOffset, tableBytes), tableBytes, throwOnEndOfStream: false) < tableBytes)
            throw new InvalidDataException("The .mdx file ends inside its tag table.");
        return header;
    }

    private IReadOnlyList<MdxTag> LoadTags(byte[] header)
    {
        var tagCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
        var entryLength = header[26] == 0 ? 32 : header[26];
        if (tagCount > MaxTags || entryLength < 21 || TagTableOffset + (long)entryLength * tagCount > HeaderAreaSize)
            throw new InvalidDataException($"The .mdx header claims {tagCount} tags of {entryLength} bytes, more than its 2,048-byte header holds.");

        var tags = new List<MdxTag>(tagCount);
        for (var i = 0; i < tagCount; i++)
        {
            var entry = header.AsSpan(TagTableOffset + entryLength * i, entryLength);
            var headerPage = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var nameBytes = entry[4..15];
            var nul = nameBytes.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(nul >= 0 ? nameBytes[..nul] : nameBytes).TrimEnd();
            tags.Add(MdxTag.Load(this, name, headerPage, Encoding));
        }
        return tags.AsReadOnly();
    }

    /// <summary>Finds the production .mdx next to a table, if the table says it has one.</summary>
    public static string? FindFor(string tablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(tablePath))!;
        var wanted = Path.GetFileNameWithoutExtension(tablePath) + ".mdx";
        return Directory.EnumerateFiles(directory)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
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
        DbfTable.DiscardReadBuffer(_stream); // other programs change indexes too
        _stream.Position = offset;
        _stream.ReadExactly(block);
        return block;
    }
}

/// <summary>One tag (B-tree) in an .mdx file: a key expression, an optional FOR filter, and keys in order.</summary>
public sealed partial class MdxTag
{
    private const int MaxDepth = 32;
    private const int MaxKeyLength = 100;

    private readonly MdxIndex _index;
    private readonly int _headerPage;

    private MdxTag(MdxIndex index, int headerPage, string name)
    {
        _index = index;
        _headerPage = headerPage;
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>The page holding this tag's header block.</summary>
    internal int HeaderPage => _headerPage;

    /// <summary>'C' (character), 'N' (numeric) or 'D' (date).</summary>
    public char KeyType { get; private init; }
    /// <inheritdoc />
    public int KeyLength { get; private init; }
    /// <inheritdoc />
    public bool IsDescending { get; private init; }
    /// <inheritdoc />
    public bool IsUnique { get; private init; }
    /// <inheritdoc />
    public string Expression { get; private init; } = "";
    /// <inheritdoc />
    public string? ForExpression { get; private init; }

    internal int ItemLength { get; private init; }
    internal int MaxKeys { get; private init; }

    internal static MdxTag Load(MdxIndex index, string name, int headerPage, Encoding encoding)
    {
        var header = index.ReadBlock(headerPage);
        var keyType = (char)header[9];
        if (keyType is not ('C' or 'N' or 'D'))
            throw new InvalidDataException($"Tag {name} has key type '{keyType}'.");

        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12, 2));
        var maxKeys = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14, 2));
        var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2));
        var expectedLength = keyType switch { 'N' => MdxKeys.NumericKeyLength, 'D' => 8, _ => 0 };
        if (keyLength == 0 || keyLength > MaxKeyLength || (expectedLength != 0 && keyLength != expectedLength)
            || itemLength < keyLength + 4 || maxKeys == 0 || 12L + (long)maxKeys * itemLength > index.BlockSize)
            throw new InvalidDataException($"Tag {name} has an impossible key layout ({keyType} keys of {keyLength} bytes in {itemLength}-byte items, {maxKeys} per node).");

        return new MdxTag(index, headerPage, name)
        {
            KeyType = keyType,
            KeyLength = keyLength,
            ItemLength = itemLength,
            MaxKeys = maxKeys,
            IsDescending = (header[8] & 0x08) != 0,
            IsUnique = (header[8] & 0x40) != 0 || header[23] != 0,
            Expression = ReadText(header.AsSpan(24, 221), encoding),
            ForExpression = header[245] != 0 && header.Length > 762 ? ReadText(header.AsSpan(762, Math.Min(220, header.Length - 762)), encoding) : null,
        };
    }

    /// <summary>Every key in index order.</summary>
    public IEnumerable<MdxEntry> Entries() => Walk(probe: null).Select(h => h.Entry);

    /// <summary>
    /// Keys in index order, starting at the first one at or after <paramref name="key"/> (at or
    /// before, for descending tags). Character keys match by prefix, like dBASE's SEEK.
    /// </summary>
    public IEnumerable<MdxEntry> Seek(object? key) => Walk(Probe(key)).Select(h => h.Entry);

    /// <summary>Only the keys equal to <paramref name="key"/> (prefix-equal for character keys).</summary>
    public IEnumerable<MdxEntry> SeekExact(object? key)
    {
        foreach (var hit in Walk(Probe(key)))
        {
            if (!hit.Matches)
                yield break;
            yield return hit.Entry;
        }
    }

    // ---- B-tree walk ----------------------------------------------------------------------

    private readonly record struct Hit(MdxEntry Entry, bool Matches);

    private IEnumerable<Hit> Walk(KeyProbe? probe)
    {
        var root = BinaryPrimitives.ReadInt32LittleEndian(_index.ReadBlock(_headerPage));
        return Walk(root, probe, position: probe is not null, depth: 0, visited: []);
    }

    // The probe reaches every leaf so matches always come from the stored bytes; only the
    // leftmost path uses it to position. Every page may be visited once per walk, so cycles
    // and shared subtrees in a damaged file can't make the walk loop or explode.
    private IEnumerable<Hit> Walk(int page, KeyProbe? probe, bool position, int depth, HashSet<int> visited)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"Tag {Name} is deeper than {MaxDepth} levels; the index is probably corrupt.");
        if (!visited.Add(page))
            throw new InvalidDataException($"Tag {Name} reaches index page {page} twice; the index is corrupt.");

        var node = _index.ReadBlock(page);
        var count = BinaryPrimitives.ReadInt32LittleEndian(node);
        if (count < 0 || count > MaxKeys)
            throw new InvalidDataException($"Index page {page} claims {count} keys; tag {Name} holds at most {MaxKeys} per node.");

        var isLeaf = BinaryPrimitives.ReadInt32LittleEndian(node.AsSpan(8 + count * ItemLength, 4)) == 0;
        var start = position && probe is not null ? FirstAtOrAfter(node, count, probe) : 0;

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

        // Interior: n keys, n + 1 child pointers.
        for (var i = start; i <= count; i++)
        {
            var child = BinaryPrimitives.ReadInt32LittleEndian(node.AsSpan(8 + i * ItemLength, 4));
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

    private abstract record KeyProbe;
    private sealed record CharacterProbe(byte[] Bytes) : KeyProbe;
    private sealed record NumericProbe(byte[] Bcd) : KeyProbe;
    private sealed record DateProbe(double Julian) : KeyProbe;

    /// <summary>Turns a search value into the stored key form, so comparisons use the index's own bytes.</summary>
    private KeyProbe Probe(object? key) => KeyType switch
    {
        'C' => new CharacterProbe(_index.Encoding.GetBytes(key as string ?? Convert.ToString(key, CultureInfo.InvariantCulture)
            ?? throw new ArgumentNullException(nameof(key), $"Tag {Name} has character keys; seek with a string."))),
        'N' => new NumericProbe(key switch
        {
            double d => MdxKeys.EncodeNumeric(d),
            float f => MdxKeys.EncodeNumeric(f),
            null => throw new ArgumentNullException(nameof(key), $"Tag {Name} has numeric keys; seek with a number."),
            _ => MdxKeys.EncodeNumeric(Convert.ToDecimal(key, CultureInfo.InvariantCulture)),
        }),
        _ => new DateProbe(key switch
        {
            null => 0d, // a blank date
            DateOnly d => MdxKeys.JulianDay(d),
            DateTime dt => MdxKeys.JulianDay(DateOnly.FromDateTime(dt)),
            _ => throw new ArgumentException($"Tag {Name} has date keys; seek with a DateOnly (or null for a blank date), not {key.GetType().Name}."),
        }),
    };

    /// <summary>Ascending comparison of a stored key with a probe. Character probes compare as prefixes.</summary>
    private static int Compare(ReadOnlySpan<byte> key, KeyProbe probe) => probe switch
    {
        CharacterProbe c => CompareCharacter(key, c.Bytes),
        NumericProbe n => MdxKeys.CompareNumeric(key, n.Bcd),
        DateProbe d => BinaryPrimitives.ReadDoubleLittleEndian(key).CompareTo(d.Julian),
        RawProbe r => CompareRaw(key, r),
        _ => throw new InvalidOperationException(),
    };

    // A shorter probe matches any key it prefixes. A longer one only matches if its extra bytes
    // are the spaces the key would have been padded with.
    private static int CompareCharacter(ReadOnlySpan<byte> key, byte[] probe)
    {
        var window = Math.Min(probe.Length, key.Length);
        var order = key[..window].SequenceCompareTo(probe.AsSpan(0, window));
        if (order != 0 || probe.Length <= key.Length)
            return order;
        return probe.AsSpan(key.Length).IndexOfAnyExcept((byte)' ') < 0 ? 0 : -1;
    }

    private object? DecodeKey(ReadOnlySpan<byte> key) => KeyType switch
    {
        'C' => _index.Encoding.GetString(key).TrimEnd(' ', '\0'),
        'N' => MdxKeys.DecodeNumeric(key),
        _ => MdxKeys.DecodeDate(key),
    };

    private static string ReadText(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var nul = bytes.IndexOf((byte)0);
        return encoding.GetString(nul >= 0 ? bytes[..nul] : bytes).Trim(' ');
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
    // Returns decimal when the value fits, otherwise double (dBASE allows exponents decimal can't hold).
    public static object DecodeNumeric(ReadOnlySpan<byte> key)
    {
        var significant = Digits(key).TrimEnd('0');
        if (significant.Length == 0)
            return 0m;

        var exponent = key[0] - ExponentBias - significant.Length;
        var text = (IsNegative(key) ? "-" : "") + significant + "E" + exponent.ToString(CultureInfo.InvariantCulture);
        if (exponent is >= -28 and <= 28 && significant.Length <= 28
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value != 0)
            return value;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new InvalidDataException("A numeric key in the .mdx holds digits that aren't BCD; the index is damaged."); // found by fuzzing
    }

    /// <summary>Orders two numeric keys by value, straight from their bytes, for any exponent.</summary>
    public static int CompareNumeric(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var aZero = IsZero(a);
        var bZero = IsZero(b);
        if (aZero || bZero)
            return aZero && bZero ? 0 : aZero ? (IsNegative(b) ? 1 : -1) : (IsNegative(a) ? -1 : 1);

        var aNegative = IsNegative(a);
        if (aNegative != IsNegative(b))
            return aNegative ? -1 : 1;

        var magnitude = a[0] != b[0] ? a[0].CompareTo(b[0]) : a[2..NumericKeyLength].SequenceCompareTo(b[2..NumericKeyLength]);
        return aNegative ? -magnitude : magnitude;
    }

    private static bool IsNegative(ReadOnlySpan<byte> key) => (key[1] & 0x80) != 0;

    private static bool IsZero(ReadOnlySpan<byte> key) => key[2..NumericKeyLength].IndexOfAnyExcept((byte)0) < 0;

    private static string Digits(ReadOnlySpan<byte> key)
    {
        var digits = new StringBuilder(MaxDigits);
        for (var i = 2; i < NumericKeyLength && i < key.Length; i++)
        {
            digits.Append((char)('0' + (key[i] >> 4)));
            digits.Append((char)('0' + (key[i] & 0x0F)));
        }
        return digits.ToString();
    }

    public static byte[] EncodeNumeric(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException($"{value} can't be an index key.");
        if (value == 0)
            return EncodeNumeric(0m);

        // The shortest round-trip form ("1E+100", "2.5E-50", "123.45") has exactly the digits the
        // value was written with; "E19" would add binary noise like 1.0000000000000000159E+100.
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var mark = text.IndexOfAny(['E', 'e']);
        var mantissa = mark < 0 ? text : text[..mark];
        var power = mark < 0 ? 0 : int.Parse(text[(mark + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        var significant = digits[leadingZeros..].TrimEnd('0');
        var exponent = (point < 0 ? mantissa.Length : point) + power - leadingZeros;
        return Pack(value < 0, significant, exponent, significant.Length);
    }

    public static byte[] EncodeNumeric(decimal value)
    {
        if (value == 0)
            return Pack(negative: false, "", 0, 0);

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
        var count = whole.Length + fraction.Length;

        return Pack(value < 0, significant, exponent, count);
    }

    private static byte[] Pack(bool negative, string significant, int exponent, int count)
    {
        if (ExponentBias + exponent is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(exponent), "The number is outside the range a dBASE numeric key can hold.");

        var key = new byte[NumericKeyLength];
        significant = significant.Length > MaxDigits ? significant[..MaxDigits] : significant;
        key[0] = (byte)(ExponentBias + exponent);
        key[1] = (byte)((Math.Min(count, 31) << 2) | 0x01 | (negative ? 0x80 : 0));
        for (var i = 0; i < significant.Length; i++)
        {
            var digit = (byte)(significant[i] - '0');
            key[2 + i / 2] |= (byte)(i % 2 == 0 ? digit << 4 : digit);
        }
        return key;
    }
}
