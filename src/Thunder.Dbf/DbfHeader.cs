//        .-----.
//       /     /      ProjectThunder :: Thunder.Dbf
//      /     /__     -------------------------------
//     /__      /     dBASE never died. It was just
//       /    /       waiting for a faster disk.
//      /   /
//     /  /
//    / /
//   //
//   '

using System.Buffers.Binary;
using System.Text;

namespace Thunder.Dbf;

// The first 32 bytes of every .dbf, as dBASE IV left them for us:
//
// +-----+----------+-----------+---------+---------+-------+-----+------+-------+
// | ver | YY MM DD | # records | hdr len | rec len |  ...  | MDX | lang |  ...  |
// +-----+----------+-----------+---------+---------+-------+-----+------+-------+
//   0     1..3       4..7        8..9      10..11    12..27  28    29     30..31
//
// Then one 32-byte descriptor per field, then 0x0D, then the records.

/// <summary>A field (column) descriptor from a DBF header.</summary>
public sealed record DbfField(string Name, char Type, int Length, int DecimalCount, int Offset)
{
    /// <summary>Visual FoxPro field flags (descriptor byte 18). Always 0 for other dialects.</summary>
    public byte Flags { get; init; }

    public bool IsSystem => (Flags & 0x01) != 0;
    public bool IsNullable => (Flags & 0x02) != 0;
    public bool IsBinary => (Flags & 0x04) != 0;
    public bool IsAutoIncrement => (Flags & 0x08) != 0;

    /// <summary>Visual FoxPro autoincrement step (descriptor byte 23).</summary>
    public int AutoIncrementStep { get; init; }

    /// <summary>This field's bit in the Visual FoxPro _NullFlags field, when it's nullable.</summary>
    internal int? NullBit { get; init; }
}

/// <summary>A field to create in a new table.</summary>
public sealed record DbfFieldDefinition(string Name, char Type, int Length, int DecimalCount = 0)
{
    public static DbfFieldDefinition Character(string name, int length) => new(name, 'C', length);
    public static DbfFieldDefinition Numeric(string name, int length, int decimals = 0) => new(name, 'N', length, decimals);
    public static DbfFieldDefinition Date(string name) => new(name, 'D', 8);
    public static DbfFieldDefinition Logical(string name) => new(name, 'L', 1);
    public static DbfFieldDefinition Memo(string name) => new(name, 'M', 10);
}

/// <summary>
/// The fixed header and field descriptors at the start of a dBASE III/IV (and compatible) .dbf file.
/// </summary>
public sealed class DbfHeader
{
    private const int FixedHeaderSize = 32;
    private const int FieldDescriptorSize = 32;
    private const byte HeaderTerminator = 0x0D;
    private const int MaxNameLength = 10;
    private const int MaxFieldsForCreate = 255;     // dBASE IV limits
    private const int MaxRecordLengthForCreate = 4000;
    private const int AutoIncrementNextOffset = 19;

    // Kept verbatim so rewriting the header never disturbs bytes we don't understand yet.
    private readonly byte[] _fixed;
    private readonly int[] _autoIncrementNext;

    private DbfHeader(byte[] fixedHeader, IReadOnlyList<DbfField> fields, int[]? autoIncrementNext = null)
    {
        _fixed = fixedHeader;
        Fields = fields;
        _autoIncrementNext = autoIncrementNext ?? new int[fields.Count];
        NullFlagsFieldIndex = fields.Select((f, i) => (f, i)).Where(x => x.f.Type == '0').Select(x => (int?)x.i).FirstOrDefault();
    }

    /// <summary>Index of the Visual FoxPro _NullFlags system field, if the table has one.</summary>
    internal int? NullFlagsFieldIndex { get; }

    /// <summary>The value Visual FoxPro will assign next to an autoincrement field.</summary>
    public int NextAutoIncrement(int fieldIndex) => _autoIncrementNext[fieldIndex];

    internal void SetNextAutoIncrement(int fieldIndex, int value) => _autoIncrementNext[fieldIndex] = value;

    public byte Version => _fixed[0];

    /// <summary>Visual FoxPro tables (0x30-0x32) use byte 28 as a flag mask and add a 263-byte backlink after the descriptors.</summary>
    public bool IsVisualFoxPro => Version is 0x30 or 0x31 or 0x32;

    /// <summary>Date of last update, or null when the stored bytes aren't a real date. FoxPro's two-digit years can read as 1900s.</summary>
    public DateOnly? LastUpdate
    {
        get
        {
            var year = 1900 + _fixed[1];
            return _fixed[2] is >= 1 and <= 12 && _fixed[3] >= 1 && _fixed[3] <= DateTime.DaysInMonth(year, _fixed[2])
                ? new DateOnly(year, _fixed[2], _fixed[3])
                : null;
        }
        internal set
        {
            var date = value ?? throw new ArgumentNullException(nameof(value));
            _fixed[1] = (byte)(date.Year - 1900);
            _fixed[2] = (byte)date.Month;
            _fixed[3] = (byte)date.Day;
        }
    }

    public uint RecordCount
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_fixed.AsSpan(4, 4));
        internal set => BinaryPrimitives.WriteUInt32LittleEndian(_fixed.AsSpan(4, 4), value);
    }

    public int HeaderLength => BinaryPrimitives.ReadUInt16LittleEndian(_fixed.AsSpan(8, 2));
    public int RecordLength => BinaryPrimitives.ReadUInt16LittleEndian(_fixed.AsSpan(10, 2));

    /// <summary>dBASE IV: set when a production .mdx with the same base name accompanies the table.</summary>
    public bool HasProductionMdx => !IsVisualFoxPro && _fixed[28] == 0x01;

    /// <summary>Visual FoxPro: set when a structural .cdx with the same base name accompanies the table.</summary>
    public bool HasStructuralCdx => IsVisualFoxPro && (_fixed[28] & 0x01) != 0;

    /// <summary>Language driver / code page mark (byte 29).</summary>
    public byte LanguageDriver => _fixed[29];

    public IReadOnlyList<DbfField> Fields { get; }

    /// <summary>True when the table has a companion memo file (.dbt for dBASE, .fpt for FoxPro).</summary>
    public bool HasMemo => IsVisualFoxPro ? (_fixed[28] & 0x02) != 0 : (Version & 0x80) != 0;

    /// <summary>
    /// Reads the header starting at the stream's current position and stops right after the 0x0D terminator.
    /// Throws <see cref="InvalidDataException"/> for truncated or inconsistent headers.
    /// Field names are decoded with <paramref name="encoding"/>, else the language driver's code page.
    /// </summary>
    public static DbfHeader Read(Stream stream, Encoding? encoding = null)
    {
        var fixedHeader = new byte[FixedHeaderSize];
        ReadOrThrow(stream, fixedHeader, "fixed header");

        var version = fixedHeader[0];
        if (version == 0x02 || (version & 0x07) == 0x04)
            throw new NotSupportedException($"Version byte 0x{version:X2} is dBASE II or dBASE 7, which use a different header layout.");

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(8, 2));
        var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader.AsSpan(10, 2));
        var nameEncoding = encoding ?? DbfEncoding.For(fixedHeader[29]);
        var isVisualFoxPro = version is 0x30 or 0x31 or 0x32;
        var fields = new List<DbfField>();
        var autoIncrementNext = new List<int>();
        var nullBits = 0;
        var offset = 1; // byte 0 of each record is the deletion flag
        var consumed = FixedHeaderSize;
        Span<byte> descriptor = stackalloc byte[FieldDescriptorSize];

        while (true)
        {
            if (consumed >= headerLength)
                throw new InvalidDataException($"No 0x0D terminator within the declared header length of {headerLength} bytes.");

            var first = stream.ReadByte();
            consumed++;
            if (first == -1)
                throw new InvalidDataException("The file ends inside the field descriptors.");
            if (first == HeaderTerminator)
                break;
            if (consumed - 1 + FieldDescriptorSize > headerLength)
                throw new InvalidDataException($"Field descriptor {fields.Count + 1} runs past the declared header length of {headerLength} bytes.");

            descriptor[0] = (byte)first;
            ReadOrThrow(stream, descriptor[1..], $"field descriptor {fields.Count + 1}");
            consumed += FieldDescriptorSize - 1;

            var nameBytes = descriptor[..11];
            var nul = nameBytes.IndexOf((byte)0);
            var name = nameEncoding.GetString(nul >= 0 ? nameBytes[..nul] : nameBytes).TrimEnd(' ');
            var type = (char)descriptor[11];
            int length = descriptor[16];
            int decimals = descriptor[17];
            if (type == 'C' && decimals > 0)
            {
                // Clipper and FoxPro store character widths over 255 with the decimals byte as the high byte.
                length |= decimals << 8;
                decimals = 0;
            }
            if (length == 0)
                throw new InvalidDataException($"Field '{name}' has zero length.");

            // Descriptor bytes 18-23 only carry flags and autoincrement data in Visual FoxPro;
            // dBASE IV uses them for work-area bookkeeping.
            var flags = isVisualFoxPro ? descriptor[18] : (byte)0;
            int? nullBit = null;
            if (isVisualFoxPro)
            {
                if (type is 'V' or 'Q')
                    nullBits++; // varchar "full length" bit comes first (Harbour dbf1.c)
                if ((flags & 0x02) != 0)
                    nullBit = nullBits++;
            }

            fields.Add(new DbfField(name, type, length, decimals, offset)
            {
                Flags = flags,
                AutoIncrementStep = (flags & 0x08) != 0 ? descriptor[23] : 0,
                NullBit = nullBit,
            });
            autoIncrementNext.Add((flags & 0x08) != 0 ? BinaryPrimitives.ReadInt32LittleEndian(descriptor[AutoIncrementNextOffset..]) : 0);
            offset += length;
        }

        // Zero fields is legal: shapefiles with no attributes ship a .dbf like that.
        if (offset > recordLength)
            throw new InvalidDataException($"The fields need {offset} bytes per record but the header says records are {recordLength}.");

        return new DbfHeader(fixedHeader, fields.AsReadOnly(), [.. autoIncrementNext]);
    }

    /// <summary>Writes a field's autoincrement counter back into its descriptor.</summary>
    internal void WriteAutoIncrement(Stream stream, int fieldIndex)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, _autoIncrementNext[fieldIndex]);
        stream.Position = FixedHeaderSize + FieldDescriptorSize * fieldIndex + AutoIncrementNextOffset;
        stream.Write(bytes);
    }

    private static void ReadOrThrow(Stream stream, Span<byte> buffer, string what)
    {
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
            throw new InvalidDataException($"The file ends inside the {what}.");
    }

    /// <summary>Builds the header for a new table. Tables with memo fields get the dBASE IV memo version (0x8B).</summary>
    public static DbfHeader Create(IReadOnlyList<DbfFieldDefinition> definitions, byte languageDriver = 0x57)
    {
        if (definitions.Count == 0)
            throw new ArgumentException("A table needs at least one field.", nameof(definitions));
        if (definitions.Count > MaxFieldsForCreate)
            throw new ArgumentException($"dBASE IV tables hold at most {MaxFieldsForCreate} fields, not {definitions.Count}.", nameof(definitions));

        var fields = new List<DbfField>(definitions.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 1;

        foreach (var def in definitions)
        {
            Validate(def);
            if (!names.Add(def.Name))
                throw new ArgumentException($"Duplicate field name '{def.Name}'.", nameof(definitions));

            fields.Add(new DbfField(def.Name, char.ToUpperInvariant(def.Type), def.Length, def.DecimalCount, offset));
            offset += def.Length;
        }

        if (offset > MaxRecordLengthForCreate)
            throw new ArgumentException($"dBASE IV records hold at most {MaxRecordLengthForCreate} bytes; these fields need {offset}.", nameof(definitions));

        var hasMemo = fields.Any(f => f.Type == 'M');
        var fixedHeader = new byte[FixedHeaderSize];
        fixedHeader[0] = hasMemo ? (byte)0x8B : (byte)0x03;
        BinaryPrimitives.WriteUInt16LittleEndian(fixedHeader.AsSpan(8, 2), (ushort)(FixedHeaderSize + FieldDescriptorSize * fields.Count + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(fixedHeader.AsSpan(10, 2), (ushort)offset);
        fixedHeader[29] = languageDriver;

        return new DbfHeader(fixedHeader, fields.AsReadOnly()) { LastUpdate = DateOnly.FromDateTime(DateTime.Today) };
    }

    /// <summary>Writes the complete header (fixed part, descriptors, terminator) at the stream's current position.</summary>
    internal void WriteFull(Stream stream)
    {
        stream.Write(_fixed);

        Span<byte> descriptor = stackalloc byte[FieldDescriptorSize];
        foreach (var field in Fields)
        {
            descriptor.Clear();
            Encoding.ASCII.GetBytes(field.Name, descriptor[..11]);
            descriptor[11] = (byte)field.Type;
            descriptor[16] = (byte)field.Length;
            descriptor[17] = (byte)field.DecimalCount;
            stream.Write(descriptor);
        }

        stream.WriteByte(HeaderTerminator);
    }

    /// <summary>Rewrites only the fixed 32 bytes (record count, last update) at the start of the stream.</summary>
    internal void WriteFixed(Stream stream)
    {
        stream.Position = 0;
        stream.Write(_fixed);
    }

    private static void Validate(DbfFieldDefinition def)
    {
        if (string.IsNullOrEmpty(def.Name) || def.Name.Length > MaxNameLength || !def.Name.All(char.IsAscii))
            throw new ArgumentException($"Field name '{def.Name}' must be 1-{MaxNameLength} ASCII characters.");

        var (min, max) = char.ToUpperInvariant(def.Type) switch
        {
            'C' => (1, 254),
            'N' or 'F' => (1, 20),
            'D' => (8, 8),
            'L' => (1, 1),
            'M' => (10, 10),
            _ => throw new ArgumentException($"Field type '{def.Type}' isn't supported for new tables yet."),
        };

        if (def.Length < min || def.Length > max)
            throw new ArgumentException($"Field '{def.Name}' of type {def.Type} must be {min}-{max} bytes long, not {def.Length}.");

        if (def.DecimalCount < 0 || (def.DecimalCount > 0 && def.DecimalCount >= def.Length - 1))
            throw new ArgumentException($"Field '{def.Name}' can't have {def.DecimalCount} decimals in {def.Length} bytes.");
    }
}
