//    .--------------------------.        ProjectThunder :: Thunder.Dbf
//    | tag directory (a B-tree   |       FoxPro .CDX: a B-tree of tag names whose
//    | of tag names -> headers)  |       record numbers are the offsets of more
//    '--------------------------'        B-trees. Leaves squeeze every key down to
//       |         |          |           the bytes it doesn't share with its left
//    [NAME]    [CITY]     [ZIP]          neighbour, minus its trailing blanks.
//
// Layout notes, with offsets, live in docs/formats/cdx.md.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

/// <summary>
/// A FoxPro / Visual FoxPro compound index (.cdx) or compact single index (.idx). Also reads the
/// .cdx files Clipper's SIx driver, Harbour and X# write. A table opened for writing keeps its
/// .cdx and .idx files up to date (see docs/formats/cdx.md, "Writing").
/// </summary>
public sealed partial class CdxIndex : IDisposable
{
    internal const int HeaderLength = 1024;
    internal const int DefaultPageLength = 512;
    private const int MaxPageLength = 8192;

    // Index option flags (tag header byte 14).
    internal const byte OptionUnique = 0x01;
    internal const byte OptionCandidate = 0x04; // Visual FoxPro: CANDIDATE and PRIMARY KEY tags
    internal const byte OptionForFilter = 0x08;
    internal const byte OptionCompact = 0x20;
    internal const byte OptionCompound = 0x40;

    private const uint HarbourSignature = 0x52434842; // "RCHB": Harbour wrote this file and may use other page sizes
    private const byte HarbourLargeFileSignature = 0x21;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    private CdxIndex(Stream stream, bool leaveOpen, Encoding encoding, int pageLength)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Encoding = encoding;
        PageLength = pageLength;
    }

    /// <summary>The code page character keys and expressions decode with.</summary>
    public Encoding Encoding { get; }

    /// <summary>Bytes per B-tree page: 512 in every file FoxPro writes.</summary>
    public int PageLength { get; }

    /// <summary>True for a .cdx (a directory of tags), false for a single-tag compact .idx.</summary>
    public bool IsCompound { get; private set; }

    /// <summary>The tags, in the order they were created.</summary>
    public IReadOnlyList<CdxTag> Tags { get; private set; } = [];

    public CdxTag this[string tagName] =>
        Tags.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"The index has no tag named '{tagName}'.");

    /// <summary>
    /// Opens a .cdx or compact .idx file. Character keys decode with <paramref name="encoding"/> (the
    /// table's encoding when <paramref name="table"/> is given, else Windows-1252).
    /// </summary>
    /// <param name="table">The table the index belongs to. A .cdx doesn't record its key types:
    /// FoxPro evaluates each key expression to find out. With the table, each expression is evaluated
    /// on a blank record the same way, and numeric, date and logical keys decode to numbers,
    /// <see cref="DateOnly"/> and bool. Without it, every key reads as a string. With a table opened
    /// from a file and a writable <see cref="FileStream"/> (how a writable table opens its indexes),
    /// the index is opened to be kept up to date: every tag must be one Thunder.Dbf can maintain
    /// exactly, or opening throws <see cref="NotSupportedException"/> saying why.</param>
    public static CdxIndex Open(string path, Encoding? encoding = null, DbfTable? table = null)
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
    public static CdxIndex Open(Stream stream, Encoding? encoding = null, bool leaveOpen = false, DbfTable? table = null)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("CDX files need a seekable stream.", nameof(stream));

        var header = new byte[HeaderLength];
        stream.Position = 0;
        if (stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false) < HeaderLength)
            throw new InvalidDataException("The file is too short to be a .cdx or .idx index.");

        var options = header[14];
        if ((options & OptionCompact) == 0)
            throw new NotSupportedException("This is an uncompressed FoxBASE-style .idx; only compact .idx and .cdx files are supported.");

        var pageLength = DefaultPageLength;
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)) == HarbourSignature)
        {
            if (header[15] == HarbourLargeFileSignature)
                throw new NotSupportedException("This Harbour .cdx uses large-file page numbers, which aren't supported.");
            pageLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18));
            if (pageLength < DefaultPageLength || pageLength > MaxPageLength || (pageLength & (pageLength - 1)) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16)) != HeaderLength)
                throw new InvalidDataException($"The .cdx header claims {pageLength}-byte pages, which no index uses.");
        }

        var index = new CdxIndex(stream, leaveOpen, encoding ?? table?.Encoding ?? DbfEncoding.GetEncoding(DbfEncoding.DefaultCodePage), pageLength)
        {
            IsCompound = (options & OptionCompound) != 0,
            _table = table,
        };
        index.Tags = index.LoadTags();
        if (index.OpenedForWriting)
            index.PrepareForWriting();
        return index;
    }

    private IReadOnlyList<CdxTag> LoadTags()
    {
        if (!IsCompound)
        {
            var name = _stream is FileStream file ? Path.GetFileNameWithoutExtension(file.Name).ToUpperInvariant() : "";
            return [CdxTag.Load(this, name, headerOffset: 0, _table)];
        }

        // The header at offset 0 is itself a tag: its keys are tag names and its "record numbers"
        // are the offsets of their headers. FoxPro lists tags in creation order, which is header order.
        var directory = CdxTag.LoadDirectory(this);
        var tags = new List<CdxTag>();
        var seen = new HashSet<long>();
        foreach (var entry in directory.Entries())
        {
            if (!seen.Add(entry.RecordNumber))
                throw new InvalidDataException($"Two .cdx tags share the header at offset {entry.RecordNumber}; the index is corrupt.");
            tags.Add(CdxTag.Load(this, ((string)entry.Key!).Trim(' ', '\0'), entry.RecordNumber, _table));
        }
        return tags.OrderBy(t => t.HeaderOffset).ToList().AsReadOnly();
    }

    /// <summary>Finds the structural .cdx next to a table (same name), which FoxPro opens with the table.</summary>
    public static string? FindFor(string tablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(tablePath))!;
        var wanted = Path.GetFileNameWithoutExtension(tablePath) + ".cdx";
        return Directory.EnumerateFiles(directory)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), wanted, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            _stream.Dispose();
    }

    /// <summary>Reads <paramref name="length"/> bytes at <paramref name="offset"/>, which must be page-aligned and inside the file.</summary>
    internal byte[] Read(long offset, int length)
    {
        if (offset < 0 || offset % DefaultPageLength != 0 || offset + length > _stream.Length)
            throw new InvalidDataException($"Offset {offset} is outside the .cdx file or not on a page boundary.");

        var block = new byte[length];
        DbfTable.DiscardReadBuffer(_stream); // the legacy app may have just changed the page
        _stream.Position = offset;
        _stream.ReadExactly(block);
        return block;
    }
}

/// <summary>One tag (B-tree) in a .cdx or .idx file: a key expression, an optional FOR filter, and keys in order.</summary>
public sealed partial class CdxTag
{
    private const int MaxDepth = 32;
    private const int InteriorHeaderLength = 12;
    private const int LeafHeaderLength = 24;
    private const int TagNameLength = 10;

    // Tag header offsets (see docs/formats/cdx.md). The expressions live in a 512-byte pool after the first 512 bytes.
    private const int IgnoreCaseOffset = 499;
    private const int DescendingOffset = 502;
    private const int ForPositionOffset = 504;
    private const int KeyPositionOffset = 508;
    private const int PoolOffset = 512;
    private const int PoolLength = 512;

    private const ushort LeafAttribute = 0x02;
    private const uint NoPage = 0xFFFFFFFF;

    private readonly CdxIndex _index;
    private readonly long _root;

    private CdxTag(CdxIndex index, long headerOffset, long root, string name)
    {
        _index = index;
        HeaderOffset = headerOffset;
        _root = root;
        Name = name;
    }

    public string Name { get; }

    /// <summary>'C', 'N', 'D' or 'L'. Always 'C' unless the index was opened with its table.</summary>
    public char KeyType { get; private init; }
    public int KeyLength { get; private init; }
    public bool IsDescending { get; private init; }
    public bool IsUnique { get; private init; }

    /// <summary>True for a Visual FoxPro CANDIDATE (or PRIMARY KEY) tag: every record is indexed and no two may share a key.</summary>
    public bool IsCandidate { get; private init; }
    public string Expression { get; private init; } = "";
    public string? ForExpression { get; private init; }

    /// <summary>True when keys were upper-cased as they were stored (a CLIP / Harbour extension).</summary>
    internal bool IgnoreCase { get; private init; }
    internal long HeaderOffset { get; }

    /// <summary>A collation name or code page some writer stored in the tag header, or null when there's none.</summary>
    internal string? CollationMarker { get; private init; }

    private int PageLength => _index.PageLength;
    private byte TrailByte => KeyType == 'C' ? (byte)' ' : (byte)0;

    internal static CdxTag LoadDirectory(CdxIndex index) => Load(index, "", 0, table: null, directory: true);

    internal static CdxTag Load(CdxIndex index, string name, long headerOffset, DbfTable? table) =>
        Load(index, name, headerOffset, table, directory: false);

    private static CdxTag Load(CdxIndex index, string name, long headerOffset, DbfTable? table, bool directory)
    {
        var header = index.Read(headerOffset, CdxIndex.HeaderLength);
        var root = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
        var options = header[14];
        var label = directory ? "The .cdx tag directory" : $"Tag {name}";

        // An inner page must fit at least two keys, each with its record number and child pointer.
        if (keyLength == 0 || InteriorHeaderLength + 2L * (keyLength + 8) > index.PageLength || (directory && keyLength != TagNameLength))
            throw new InvalidDataException($"{label} has {keyLength}-byte keys, which don't fit a {index.PageLength}-byte page.");
        if (root is 0 or NoPage)
            throw new InvalidDataException($"{label} has no root page.");
        if ((options & CdxIndex.OptionCompact) == 0)
            throw new InvalidDataException($"{label} isn't a compact index tag (options 0x{options:X2}).");

        var expression = directory ? "" : ReadExpression(header, KeyPositionOffset, index.Encoding, label);
        var forExpression = directory ? "" : ReadExpression(header, ForPositionOffset, index.Encoding, label);
        if (!directory && expression.Length == 0)
            throw new InvalidDataException($"{label} has no key expression.");

        return new CdxTag(index, headerOffset, root, name)
        {
            KeyType = directory || table is null ? 'C' : IndexKeys.InferKeyType(expression, table, fallback: 'C'),
            KeyLength = keyLength,
            IsDescending = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(DescendingOffset)) != 0,
            IsUnique = (options & CdxIndex.OptionUnique) != 0,
            IsCandidate = (options & CdxIndex.OptionCandidate) != 0,
            CollationMarker = directory ? null : ReadCollationMarker(header),
            Expression = expression,
            ForExpression = forExpression.Length > 0 ? forExpression : null,
            IgnoreCase = header[15] is 0x01 or 0x21 && header[IgnoreCaseOffset] == 1,
        };
    }

    // Each expression is a (position, length) pair into the pool. FoxPro counts the trailing NUL in
    // the length; some writers leave the length 0, so a 0 length reads up to the NUL.
    private static string ReadExpression(byte[] header, int pairOffset, Encoding encoding, string label)
    {
        var position = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pairOffset));
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pairOffset + 2));
        if (position + length > PoolLength)
            throw new InvalidDataException($"{label} has an expression at {position}+{length}, past its {PoolLength}-byte expression area.");
        if (length == 0 && position == 0 && pairOffset == ForPositionOffset)
            return ""; // no FOR expression
        return IndexKeys.ReadText(header.AsSpan(PoolOffset + position, length > 0 ? length : PoolLength - position), encoding);
    }

    // Harbour's reserved areas where Advantage-style writers name a collation (the "lang" field at
    // 92, 26 bytes) and Visual FoxPro a code page (494, 5 bytes). FoxPro's MACHINE order leaves both empty.
    private static string? ReadCollationMarker(byte[] header)
    {
        var lang = header.AsSpan(92, 26);
        var codePage = header.AsSpan(494, 5);
        if (lang.IndexOfAnyExcept((byte)0) < 0 && codePage.IndexOfAnyExcept((byte)0) < 0)
            return null;
        var text = Encoding.ASCII.GetString(lang).Trim('\0', ' ');
        return text.Length > 0 ? text : $"code page bytes {Convert.ToHexString(codePage)}";
    }

    /// <summary>Every key in index order (highest first for descending tags).</summary>
    public IEnumerable<IndexEntry> Entries() => Walk(probe: null).Select(h => h.Entry);

    /// <summary>
    /// Keys in index order, starting at the first one at or after <paramref name="key"/> (at or
    /// before, for descending tags). Character keys match by prefix, like FoxPro's SEEK with SET EXACT OFF.
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

    // FoxPro (like Harbour) stores every tree in ascending order; DESCENDING only flips which way
    // it's read, so FoxPro's own SET ORDER TO ... DESCENDING can flip it back without a rebuild.
    private IEnumerable<Hit> Walk(byte[]? probe) => Walk(probe, backwards: IsDescending);

    // The root is re-read for every walk: a split in the legacy app moves it.
    private IEnumerable<Hit> Walk(byte[]? probe, bool backwards) =>
        Walk(RootOffset(), probe, position: probe is not null, backwards, depth: 0, visited: []);

    private long RootOffset() => BinaryPrimitives.ReadUInt32LittleEndian(_index.Read(HeaderOffset, 4));

    // Like the .mdx walk: interior key i is the highest key under child i, only the outermost path
    // positions, and every page may be visited once. Leaves also link to their neighbours, but
    // following the tree keeps one code path and the cycle check simple. Forwards, the walk starts
    // at the first key at or after the probe; backwards, at the last key at or before it.
    private IEnumerable<Hit> Walk(long offset, byte[]? probe, bool position, bool backwards, int depth, HashSet<long> visited)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"Tag {Name} is deeper than {MaxDepth} levels; the index is probably corrupt.");
        if (!visited.Add(offset))
            throw new InvalidDataException($"Tag {Name} reaches the index page at offset {offset} twice; the index is corrupt.");

        var page = _index.Read(offset, PageLength);
        var attributes = BinaryPrimitives.ReadUInt16LittleEndian(page);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(2));
        // Bit 0 marks the root and bit 1 a leaf; Visual FoxPro also sets bit 2 on some leaves. A free
        // page is 0xFF, and anything past bit 2 means the pointer led somewhere that isn't a node.
        if (attributes > 7)
            throw new InvalidDataException($"Tag {Name} reaches the page at offset {offset}, which isn't an index node (type {attributes}).");

        if ((attributes & LeafAttribute) != 0)
        {
            var (keys, records) = DecodeLeaf(page, count, offset);
            if (backwards)
            {
                var last = position && probe is not null ? FirstAfter(keys, probe) - 1 : count - 1;
                for (var i = last; i >= 0; i--)
                    yield return LeafHit(keys[i], records[i], probe);
                yield break;
            }
            var start = position && probe is not null ? FirstAtOrAfter(keys, probe) : 0;
            for (var i = start; i < count; i++)
                yield return LeafHit(keys[i], records[i], probe);
            yield break;
        }

        var itemLength = KeyLength + 8;
        if (InteriorHeaderLength + (long)count * itemLength > PageLength)
            throw new InvalidDataException($"The index page at offset {offset} claims {count} keys, more than tag {Name} fits in a page.");

        var interiorKeys = new byte[count][];
        for (var i = 0; i < count; i++)
            interiorKeys[i] = page.AsSpan(InteriorHeaderLength + i * itemLength, KeyLength).ToArray();

        // Interior items: key, then record number and child offset, both big-endian. Backwards, no
        // child after the first one whose highest key is past the probe can hold a match.
        var first = !(position && probe is not null) ? (backwards ? count - 1 : 0)
            : backwards ? Math.Min(FirstAfter(interiorKeys, probe), count - 1)
            : FirstAtOrAfter(interiorKeys, probe);
        for (var i = first; backwards ? i >= 0 : i < count; i += backwards ? -1 : 1)
        {
            var child = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(InteriorHeaderLength + i * itemLength + KeyLength + 4));
            if (child is 0 or NoPage)
                throw new InvalidDataException($"The index page at offset {offset} has no child for key {i}; tag {Name} is corrupt.");
            foreach (var hit in Walk(child, probe, position && i == first, backwards, depth + 1, visited))
                yield return hit;
        }
    }

    private Hit LeafHit(byte[] key, int record, byte[]? probe) =>
        new(new IndexEntry(DecodeKey(key), record), probe is not null && IndexKeys.CompareCharacter(key, probe) == 0);

    /// <summary>
    /// Unpacks a leaf. After the 24-byte header come fixed-size entries packing a record number, a
    /// count of bytes shared with the previous key and a count of trailing blanks dropped; the bytes
    /// in between are stored back to front from the end of the page.
    /// </summary>
    private (byte[][] Keys, int[] Records) DecodeLeaf(byte[] page, int count, long offset)
    {
        int recordBits = page[20], duplicateBits = page[21], trailBits = page[22], entryLength = page[23];
        if (entryLength is 0 or > 8 || recordBits > 32 || duplicateBits > 16 || trailBits > 16
            || recordBits + duplicateBits + trailBits > entryLength * 8)
            throw new InvalidDataException($"The leaf at offset {offset} has an impossible entry layout ({recordBits}/{duplicateBits}/{trailBits} bits in {entryLength} bytes).");

        var entriesEnd = LeafHeaderLength + (long)count * entryLength;
        if (entriesEnd > PageLength)
            throw new InvalidDataException($"The leaf at offset {offset} claims {count} keys, more than fit in a page.");

        var recordMask = (1UL << recordBits) - 1;
        var duplicateMask = (1UL << duplicateBits) - 1;
        var trailMask = (1UL << trailBits) - 1;

        var keys = new byte[count][];
        var records = new int[count];
        var previous = new byte[KeyLength];
        var dataStart = PageLength;
        for (var i = 0; i < count; i++)
        {
            ulong packed = 0;
            for (var b = entryLength - 1; b >= 0; b--)
                packed = packed << 8 | page[LeafHeaderLength + i * entryLength + b];

            var duplicates = i == 0 ? 0 : (int)(packed >> recordBits & duplicateMask);
            var trailing = (int)(packed >> (recordBits + duplicateBits) & trailMask);
            var stored = KeyLength - duplicates - trailing;
            dataStart -= stored;
            if (stored < 0 || dataStart < entriesEnd)
                throw new InvalidDataException($"Key {i} of the leaf at offset {offset} doesn't fit its page; tag {Name} is corrupt.");

            var key = new byte[KeyLength];
            previous.AsSpan(0, duplicates).CopyTo(key);
            page.AsSpan(dataStart, stored).CopyTo(key.AsSpan(duplicates));
            key.AsSpan(KeyLength - trailing).Fill(TrailByte);

            keys[i] = key;
            records[i] = IndexKeys.RecordNumber((uint)(packed & recordMask), Name);
            previous = key;
        }
        return (keys, records);
    }

    private static int FirstAtOrAfter(byte[][] keys, byte[] probe) => Partition(keys, probe, inclusive: false);

    private static int FirstAfter(byte[][] keys, byte[] probe) => Partition(keys, probe, inclusive: true);

    // The first key that sorts after the probe (or matches it, unless inclusive), prefix-wise.
    private static int Partition(byte[][] keys, byte[] probe, bool inclusive)
    {
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var order = IndexKeys.CompareCharacter(keys[mid], probe);
            if (order < 0 || (inclusive && order == 0))
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // ---- keys -----------------------------------------------------------------------------

    /// <summary>
    /// Turns a search value into the bytes FoxPro would have stored, so every comparison is a byte
    /// comparison. Numbers and dates are doubles arranged to sort as bytes; character probes compare as prefixes.
    /// </summary>
    private byte[] Probe(object? key) => KeyType switch
    {
        'N' when KeyLength == 4 => EncodeInteger(key),
        'N' => IndexKeys.WriteOrderedDouble(IndexKeys.ToDouble(key, Name)),
        'D' => IndexKeys.WriteOrderedDouble(IndexKeys.ToJulianDay(key, Name)),
        'L' => [(byte)(IndexKeys.ToBool(key, Name) ? 'T' : 'F')],
        _ => _index.Encoding.GetBytes(IgnoreCase ? IndexKeys.ToText(key, Name).ToUpperInvariant() : IndexKeys.ToText(key, Name)),
    };

    // 4-byte numeric keys (Harbour's integer keys) are big-endian with the sign bit flipped.
    private byte[] EncodeInteger(object? key)
    {
        var bytes = new byte[4];
        var value = Convert.ToInt32(key ?? throw new ArgumentNullException(nameof(key), $"Tag {Name} has numeric keys; seek with a number."),
            CultureInfo.InvariantCulture);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, unchecked((uint)value + 0x80000000));
        return bytes;
    }

    private object? DecodeKey(byte[] key) => KeyType switch
    {
        'N' when KeyLength == 4 => (decimal)unchecked((int)(BinaryPrimitives.ReadUInt32BigEndian(key) - 0x80000000)),
        'N' => IndexKeys.FromDouble(IndexKeys.ReadOrderedDouble(key)),
        'D' => IndexKeys.FromJulianDay(IndexKeys.ReadOrderedDouble(key)),
        'L' => key[0] == 'T',
        _ => _index.Encoding.GetString(key).TrimEnd(' ', '\0'),
    };
}
