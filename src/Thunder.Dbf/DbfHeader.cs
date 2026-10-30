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
public sealed record DbfField(string Name, char Type, int Length, int DecimalCount, int Offset);

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

    // Kept verbatim so rewriting the header never disturbs bytes we don't understand yet.
    private readonly byte[] _fixed;

    private DbfHeader(byte[] fixedHeader, IReadOnlyList<DbfField> fields)
    {
        _fixed = fixedHeader;
        Fields = fields;
    }

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
        var fields = new List<DbfField>();
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
            var length = descriptor[16];
            if (length == 0)
                throw new InvalidDataException($"Field '{name}' has zero length.");

            fields.Add(new DbfField(name, (char)descriptor[11], length, descriptor[17], offset));
            offset += length;
        }

        // Zero fields is legal: shapefiles with no attributes ship a .dbf like that.
        if (offset > recordLength)
            throw new InvalidDataException($"The fields need {offset} bytes per record but the header says records are {recordLength}.");

        return new DbfHeader(fixedHeader, fields.AsReadOnly());
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
