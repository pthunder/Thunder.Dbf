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
/// .cdx files Clipper's SIx driver, Harbour and X# write. With the <c>maintainFoxProIndexes</c>
/// preview, a table opened for writing keeps its .cdx and .idx files up to date; without it, a table
/// with a structural .cdx refuses writes (see docs/formats/cdx.md, "Writing").
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

    /// <summary>The tag called <paramref name="tagName"/>, in any case.</summary>
    /// <exception cref="KeyNotFoundException">The index has no such tag.</exception>
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
    /// <see cref="DateOnly"/> and bool. Without it, every key reads as a string.</param>
    /// <param name="path">The .cdx or .idx file.</param>
    /// <param name="encoding">How character keys decode, overriding the table's encoding.</param>
    public static CdxIndex Open(string path, Encoding? encoding = null, DbfTable? table = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return Open(stream, encoding, leaveOpen: false, table);
        }
        catch
        {
            DbfLocks.CloseWhenSafe(stream);
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
        // Settled now, while the table it was opened with is there to ask (see ResolveCurrencyLayout).
        foreach (var tag in index.Tags)
            tag.ResolveCurrencyLayout();
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

    /// <inheritdoc />
    public void Dispose()
    {
        // On Linux closing any handle drops this process's locks on the file: the FoxPro index lock
        // another table's CdxIndex holds, too. So the close waits while any are held.
        if (!_leaveOpen)
            DbfLocks.Close(_stream);
    }

    /// <summary>Reads <paramref name="length"/> bytes at <paramref name="offset"/>, which must be page-aligned and inside the file.</summary>
    internal byte[] Read(long offset, int length)
    {
        // Past the length last seen: check the real one before refusing (another program may have grown the file).
        if (offset < 0 || offset % DefaultPageLength != 0 || (offset + length > FileLength() && offset + length > FileLength(fresh: true)))
            throw new InvalidDataException($"Offset {offset} is outside the .cdx file or not on a page boundary.");

        var block = new byte[length];
        DbfTable.DiscardReadBuffer(_stream); // the legacy app may have just changed the page
        _stream.Position = offset;
        try
        {
            _stream.ReadExactly(block);
        }
        catch (EndOfStreamException)
        {
            _knownLength = -1; // another program shortened the file
            throw new InvalidDataException($"Offset {offset} is outside the .cdx file or not on a page boundary.");
        }
        if (_deferred is { Count: > 0 } deferred)
        {
            // A batch's pages, not yet written back.
            for (var at = 0; at < length; at += DefaultPageLength)
            {
                if (deferred.TryGetValue(offset + at, out var page))
                    page.AsSpan(0, Math.Min(DefaultPageLength, length - at)).CopyTo(block.AsSpan(at));
            }
        }
        return block;
    }

    // The file's length as last seen. Asking the operating system is a system call, and every page
    // read and write checks it, so a page past it is checked against the real length before it's
    // refused (another program may have grown the file), and the length is read fresh under the
    // index lock (see CurrentState).
    private long _knownLength = -1;

    internal long FileLength(bool fresh = false)
    {
        if (fresh || _knownLength < 0)
            _knownLength = _stream.Length;
        return _knownLength;
    }
}

/// <summary>One tag (B-tree) in a .cdx or .idx file: a key expression, an optional FOR filter, and keys in order.</summary>
public sealed partial class CdxTag
{
    private const int MaxDepth = 32;
    private const int InteriorHeaderLength = 12;
    internal const int LeafHeaderLength = 24;
    internal const int TagNameLength = 10;

    // Tag header offsets (see docs/formats/cdx.md). The expressions live in a 512-byte pool after the first 512 bytes.
    private const int IgnoreCaseOffset = 499;
    internal const int DescendingOffset = 502;
    internal const int ForPositionOffset = 504;
    internal const int KeyPositionOffset = 508;
    internal const int PoolOffset = 512;
    internal const int PoolLength = 512;

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

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>'C', 'N', 'D', 'L', or 'T' for a datetime field. Always 'C' unless the index was opened with its table.</summary>
    public char KeyType { get; private init; }
    /// <inheritdoc />
    public int KeyLength { get; private init; }
    /// <inheritdoc />
    public bool IsDescending { get; private init; }
    /// <inheritdoc />
    public bool IsUnique { get; private init; }

    /// <summary>True for a Visual FoxPro CANDIDATE (or PRIMARY KEY) tag: every record is indexed and no two may share a key.</summary>
    public bool IsCandidate { get; private init; }

    /// <inheritdoc />
    public string Expression { get; private init; } = "";
    /// <inheritdoc />
    public string? ForExpression { get; private init; }

    /// <summary>True when keys were upper-cased as they were stored (a CLIP / Harbour extension).</summary>
    internal bool IgnoreCase { get; private init; }

    // Two Visual FoxPro key layouts that aren't the plain ones, as VFP 9 writes them for a bare field.
    // A currency field's key is its stored ten-thousandths, big-endian with the sign bit flipped, not a
    // double. A nullable character field's key has a marker byte in front: 0x80 then the value, and
    // all zeros for .NULL.
    private DbfField? CurrencyField { get; init; }
    private DbfTable? Table { get; init; }

    /// <summary>
    /// A tag on a bare nullable field: Visual FoxPro puts a marker byte in front of every key, 0x80 then
    /// the field's usual key, and stores .NULL. as all zeros. It does that for every type, so the key is
    /// one byte longer than the field's usual one.
    /// </summary>
    private bool HasNullMarker { get; init; }

    /// <summary>A null-marked key over a currency field: always Visual FoxPro's ten-thousandths.</summary>
    private bool IsMarkedCurrency { get; init; }

    // The length of the value part of a key: the key without its null marker byte.
    private int ValueLength => HasNullMarker ? KeyLength - 1 : KeyLength;

    /// <summary>The key length Visual FoxPro gives a tag on a bare field of this type, without a null marker.</summary>
    internal static int? UsualKeyLength(DbfField field) => field.Type switch
    {
        'C' => field.Length,
        'I' => 4,
        'N' or 'F' or 'B' or 'Y' or 'D' or 'T' => 8,
        'L' => 1,
        _ => null,
    };

    // Harbour keys a currency field as a number, an ordered double like any other. Nothing in the
    // file says which a tag holds, and the bytes alone can't: ten-thousandths can spell a believable
    // double (460,718,241,880,001.7408 in currency is, bit for bit, the double 1.0). So a key is
    // checked against the record it points at: the tag's lowest and highest keys, each compared with
    // its record's stored value keyed both ways. Zero is the same eight bytes either way and says
    // nothing; so does a key that matches neither (an index out of step), and the one value whose
    // two keys are the same bytes. The key is read again after its record, so the pair is one moment's.
    internal enum CurrencyLayout
    {
        /// <summary>Not a currency tag, or nothing to go by: no keys, or only zeros.</summary>
        Unknown,
        VisualFoxPro,
        Harbour,
        /// <summary>A key that is both layouts' key for its record, and none that's only one's.</summary>
        Either,
    }

    private CurrencyLayout? _currencyLayout; // kept once it's Visual FoxPro's or Harbour's
    private CurrencyLayout? _undecided;      // "nothing to go by", kept for one walk: asked once, not per key

    /// <summary>True when keys are read as Visual FoxPro's ten-thousandths: whenever nothing shows them to be Harbour's.</summary>
    internal bool IsCurrencyKey => CurrencyField is not null && ResolveCurrencyLayout() != CurrencyLayout.Harbour;

    internal void ForgetCurrencyLayout() => _currencyLayout = _undecided = null;

    internal CurrencyLayout ResolveCurrencyLayout()
    {
        if (CurrencyField is not { } currency || Table is not { } table)
            return CurrencyLayout.Unknown;
        if ((_currencyLayout ?? _undecided) is { } known)
            return known;
        var answer = CurrencyLayout.Unknown;
        try
        {
            Span<byte> stored = stackalloc byte[8];
            Span<byte> asCurrency = stackalloc byte[8];
            foreach (var last in new[] { false, true })
            {
                if (EdgeEntry(last) is not { } edge || !table.TryReadStored(edge.Record, currency, stored))
                    continue;
                var scaled = BinaryPrimitives.ReadInt64LittleEndian(stored);
                if (scaled == 0)
                    continue;
                // The same entry before and after its record was read, or another program is mid-change.
                if (EdgeEntry(last) is not { } again || again.Record != edge.Record || !again.Key.AsSpan().SequenceEqual(edge.Key))
                    continue;
                BinaryPrimitives.WriteUInt64BigEndian(asCurrency, unchecked((ulong)scaled ^ 0x8000_0000_0000_0000));
                var isVisualFoxPros = edge.Key.AsSpan().SequenceEqual(asCurrency);
                // Harbour's double for the value, give or take how it rounds one.
                var value = scaled / 10_000d;
                var isHarbours = Math.Abs(IndexKeys.ReadOrderedDouble(edge.Key) - value) <= Math.Max(0.00005, Math.Abs(value) * 1e-12);
                if (isVisualFoxPros && isHarbours)
                    answer = CurrencyLayout.Either;
                else if (isVisualFoxPros || isHarbours)
                    return (_currencyLayout = isHarbours ? CurrencyLayout.Harbour : CurrencyLayout.VisualFoxPro).Value;
            }
        }
        catch (InvalidDataException)
        {
            // A damaged tree: whatever reads it next reports that.
        }
        return (_undecided = answer).Value;
    }

    // The tag's lowest or highest stored key and its record, or null when there's none (or the tree can't be followed).
    private (byte[] Key, int Record)? EdgeEntry(bool last)
    {
        var offset = RootOffset();
        for (var depth = 0; depth <= MaxDepth; depth++)
        {
            var page = _index.Read(offset, PageLength);
            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(page);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(2));
            if (attributes > 7 || count == 0)
                return null;
            if ((attributes & LeafAttribute) != 0)
            {
                var (keys, records) = DecodeLeaf(page, count, offset);
                return (keys[last ? count - 1 : 0], records[last ? count - 1 : 0]);
            }
            var itemLength = KeyLength + 8;
            if (InteriorHeaderLength + (long)count * itemLength > PageLength)
                return null;
            offset = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(InteriorHeaderLength + (last ? count - 1 : 0) * itemLength + KeyLength + 4));
            if (offset is 0 or NoPage)
                return null;
        }
        return null;
    }
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

        var bare = directory || table is null ? null
            : table.Fields.FirstOrDefault(f => string.Equals(f.Name, expression.Trim(), StringComparison.OrdinalIgnoreCase));

        return new CdxTag(index, headerOffset, root, name)
        {
            CurrencyField = bare is { Type: 'Y' } && keyLength == 8 ? bare : null,
            Table = table,
            HasNullMarker = bare is { IsNullable: true } && keyLength == UsualKeyLength(bare) + 1,
            IsMarkedCurrency = bare is { Type: 'Y', IsNullable: true } && keyLength == 9,
            KeyType = directory || table is null ? 'C' : IsDateTimeField(expression, table, keyLength) ? 'T' : IndexKeys.InferKeyType(expression, table, fallback: 'C'),
            KeyLength = keyLength,
            IsDescending = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(DescendingOffset)) != 0,
            IsUnique = (options & CdxIndex.OptionUnique) != 0,
            IsCandidate = (options & CdxIndex.OptionCandidate) != 0,
            CollationMarker = directory ? null : ReadCollationMarker(header),
            Expression = expression,
            ForExpression = forExpression.Length > 0 ? forExpression : null,
            IgnoreCase = header[15] is 0x01 or 0x21 && header[IgnoreCaseOffset] == 1,
            Layout = LayoutOf(header),
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
    public IEnumerable<IndexEntry> Entries()
    {
        // Each walk asks afresh, when it starts and not when it's set up: keys may have arrived since.
        _undecided = null;
        foreach (var hit in Walk(probe: null))
            yield return hit.Entry;
    }

    /// <summary>
    /// Keys in index order, starting at the first one at or after <paramref name="key"/> (at or
    /// before, for descending tags). Character keys match by prefix, like FoxPro's SEEK with SET EXACT OFF.
    /// </summary>
    public IEnumerable<IndexEntry> Seek(object? key)
    {
        _undecided = null;
        foreach (var hit in Walk(Probe(key)))
            yield return hit.Entry;
    }

    /// <summary>Only the keys equal to <paramref name="key"/> (prefix-equal for character keys).</summary>
    public IEnumerable<IndexEntry> SeekExact(object? key)
    {
        _undecided = null;
        // On a nullable date or datetime field a blank value reads back as null like .NULL. does, so a
        // null finds both: the .NULL. keys (all zeros) and the blank ones (the marker, then the blank key).
        if (key is null && HasNullMarker && KeyType is 'D' or 'T')
        {
            byte[] nulls = new byte[KeyLength], blanks = [0x80, .. ProbeValue(null)];
            foreach (var probe in IsDescending ? [blanks, nulls] : new[] { nulls, blanks })
                foreach (var hit in Walk(probe))
                {
                    if (!hit.Matches)
                        break;
                    yield return hit.Entry;
                }
            yield break;
        }
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

    // While the file is as this process left it, the root is where the tag was loaded with it: no
    // change made here moves a root (Visual FoxPro keeps them in place too).
    private long RootOffset() => _index.Unchanged ? _root : BinaryPrimitives.ReadUInt32LittleEndian(_index.Read(HeaderOffset, 4));

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
        if (entryLength is 0 or > 8 || recordBits == 0 || duplicateBits > 16 || trailBits > 16
            || recordBits + duplicateBits + trailBits > entryLength * 8)
            throw new InvalidDataException($"The leaf at offset {offset} has an impossible entry layout ({recordBits}/{duplicateBits}/{trailBits} bits in {entryLength} bytes).");

        var entriesEnd = LeafHeaderLength + (long)count * entryLength;
        if (entriesEnd > PageLength)
            throw new InvalidDataException($"The leaf at offset {offset} claims {count} keys, more than fit in a page.");

        // As Harbour's dbfcdx1.c reads them: the trailing count sits in the entry's top bits and the
        // duplicate count just below it, wherever the record number ends, and the record number is
        // masked to at most 32 bits. Past 4-byte record numbers (record 67,108,864 with 4-byte keys
        // takes 5-byte entries) the record field is wider than its mask.
        var recordMask = recordBits >= 32 ? uint.MaxValue : (1UL << recordBits) - 1;
        var duplicateMask = (1UL << duplicateBits) - 1;
        var trailMask = (1UL << trailBits) - 1;
        var trailShift = entryLength * 8 - trailBits;
        var duplicateShift = trailShift - duplicateBits;

        var keys = new byte[count][];
        var records = new int[count];
        var previous = new byte[KeyLength];
        var dataStart = PageLength;
        for (var i = 0; i < count; i++)
        {
            ulong packed = 0;
            for (var b = entryLength - 1; b >= 0; b--)
                packed = packed << 8 | page[LeafHeaderLength + i * entryLength + b];

            var duplicates = i == 0 ? 0 : (int)(packed >> duplicateShift & duplicateMask);
            var trailing = (int)(packed >> trailShift & trailMask);
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

    // A tag on a bare Visual FoxPro datetime field keys whole seconds as Julian days, not dates: its
    // blank value is null, which would otherwise make it look like a date tag.
    private static bool IsDateTimeField(string expression, DbfTable table, int keyLength) =>
        table.Fields.FirstOrDefault(f => string.Equals(f.Name, expression.Trim(), StringComparison.OrdinalIgnoreCase)) is { Type: 'T' } field
        && (keyLength == 8 || (field.IsNullable && keyLength == 9));

    /// <summary>
    /// Turns a search value into the bytes FoxPro would have stored, so every comparison is a byte
    /// comparison. Numbers and dates are doubles arranged to sort as bytes; character probes compare as prefixes.
    /// </summary>
    private byte[] Probe(object? key)
    {
        if (!HasNullMarker)
            return ProbeValue(key);
        // A null seeks the .NULL. keys (all zeros); a value seeks behind the marker byte.
        if (key is null)
            return new byte[KeyLength];
        var value = ProbeValue(key);
        var bytes = new byte[value.Length + 1];
        bytes[0] = 0x80;
        value.CopyTo(bytes, 1);
        return bytes;
    }

    private byte[] ProbeValue(object? key) => KeyType switch
    {
        _ when IsCurrencyKey || IsMarkedCurrency => EncodeCurrency(key),
        'N' when ValueLength == 4 => EncodeInteger(key),
        'N' => IndexKeys.WriteOrderedDouble(IndexKeys.ToDouble(key, Name)),
        'D' => IndexKeys.WriteOrderedDouble(IndexKeys.ToJulianDay(key, Name)),
        'T' => IndexKeys.WriteOrderedDouble(IndexKeys.ToJulianDateTime(key, Name)),
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

    private byte[] EncodeCurrency(object? key)
    {
        var value = Convert.ToDecimal(key ?? throw new ArgumentNullException(nameof(key), $"Tag {Name} has currency keys; seek with a number."),
            CultureInfo.InvariantCulture);
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, unchecked((ulong)decimal.ToInt64(decimal.Round(value * 10_000m)) ^ 0x8000_0000_0000_0000));
        return bytes;
    }

    private object? DecodeKey(byte[] key) =>
        !HasNullMarker ? DecodeValue(key)
        : key[0] == 0 ? null // .NULL.
        : DecodeValue(key[1..]);

    private object? DecodeValue(byte[] key) => KeyType switch
    {
        _ when IsCurrencyKey || IsMarkedCurrency => unchecked((long)(BinaryPrimitives.ReadUInt64BigEndian(key) ^ 0x8000_0000_0000_0000)) / 10_000m,
        'N' when ValueLength == 4 => (decimal)unchecked((int)(BinaryPrimitives.ReadUInt32BigEndian(key) - 0x80000000)),
        'N' => IndexKeys.FromDouble(IndexKeys.ReadOrderedDouble(key)),
        'D' => IndexKeys.FromJulianDay(IndexKeys.ReadOrderedDouble(key)),
        'T' => IndexKeys.FromJulianDateTime(IndexKeys.ReadOrderedDouble(key)),
        'L' => key[0] == 'T',
        _ => _index.Encoding.GetString(key).TrimEnd(' ', '\0'),
    };
}
