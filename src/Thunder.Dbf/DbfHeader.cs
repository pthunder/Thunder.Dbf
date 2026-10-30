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

/// <summary>
/// The fixed header and field descriptors at the start of a dBASE III/IV (and compatible) .dbf file.
/// </summary>
public sealed class DbfHeader
{
    private const int FixedHeaderSize = 32;
    private const int FieldDescriptorSize = 32;
    private const byte HeaderTerminator = 0x0D;

    public byte Version { get; private init; }
    public DateOnly LastUpdate { get; private init; }
    public uint RecordCount { get; private init; }
    public int HeaderLength { get; private init; }
    public int RecordLength { get; private init; }

    /// <summary>dBASE IV: set when a production .mdx with the same base name accompanies the table.</summary>
    public bool HasProductionMdx { get; private init; }

    /// <summary>Language driver / code page mark (byte 29).</summary>
    public byte LanguageDriver { get; private init; }

    public IReadOnlyList<DbfField> Fields { get; private init; } = [];

    /// <summary>True when the version byte indicates an accompanying memo (.dbt) file.</summary>
    public bool HasMemo => (Version & 0x80) != 0;

    public static DbfHeader Read(Stream stream)
    {
        Span<byte> fixedHeader = stackalloc byte[FixedHeaderSize];
        stream.ReadExactly(fixedHeader);

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader[8..10]);
        var fields = new List<DbfField>();
        var offset = 1; // byte 0 of each record is the deletion flag
        Span<byte> descriptor = stackalloc byte[FieldDescriptorSize];

        while (stream.Position < headerLength)
        {
            var first = stream.ReadByte();
            if (first == HeaderTerminator || first == -1)
                break;

            descriptor[0] = (byte)first;
            stream.ReadExactly(descriptor[1..]);

            var nameBytes = descriptor[..11];
            var nul = nameBytes.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(nul >= 0 ? nameBytes[..nul] : nameBytes);
            var length = descriptor[16];

            fields.Add(new DbfField(name, (char)descriptor[11], length, descriptor[17], offset));
            offset += length;
        }

        return new DbfHeader
        {
            Version = fixedHeader[0],
            LastUpdate = new DateOnly(1900 + fixedHeader[1], Math.Max(1, (int)fixedHeader[2]), Math.Max(1, (int)fixedHeader[3])),
            RecordCount = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[4..8]),
            HeaderLength = headerLength,
            RecordLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader[10..12]),
            HasProductionMdx = fixedHeader[28] == 0x01,
            LanguageDriver = fixedHeader[29],
            Fields = fields,
        };
    }
}
