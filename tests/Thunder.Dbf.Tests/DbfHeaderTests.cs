//       .--.
//    .-(    ).        ProjectThunder :: Thunder.Dbf tests
//   (___.__)__)       If it passes here, it would have
//    / / / / /        passed on a Windows 95 box in 1997.
//   / / / / /

using System.Buffers.Binary;
using System.Text;

namespace Thunder.Dbf.Tests;

public class DbfHeaderTests
{
    private static byte[] BuildHeader(byte version, bool productionMdx, params (string Name, char Type, byte Length, byte Decimals)[] fields)
    {
        var headerLength = 32 + 32 * fields.Length + 1;
        var recordLength = 1 + fields.Sum(f => f.Length);
        var bytes = new byte[headerLength];

        bytes[0] = version;
        bytes[1] = 126; bytes[2] = 10; bytes[3] = 30; // 2026-10-30
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 42u);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)headerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), (ushort)recordLength);
        bytes[28] = productionMdx ? (byte)1 : (byte)0;
        bytes[29] = 0x57;

        for (var i = 0; i < fields.Length; i++)
        {
            var at = 32 + 32 * i;
            Encoding.ASCII.GetBytes(fields[i].Name).CopyTo(bytes, at);
            bytes[at + 11] = (byte)fields[i].Type;
            bytes[at + 16] = fields[i].Length;
            bytes[at + 17] = fields[i].Decimals;
        }

        bytes[^1] = 0x0D;
        return bytes;
    }

    [Fact]
    public void Reads_dBASE_IV_header_with_memo_and_mdx()
    {
        var bytes = BuildHeader(0x8B, productionMdx: true,
            ("LASTNAME", 'C', 20, 0),
            ("BALANCE", 'N', 10, 2),
            ("HIREDATE", 'D', 8, 0),
            ("NOTES", 'M', 10, 0));

        var header = DbfHeader.Read(new MemoryStream(bytes));

        Assert.Equal(0x8B, header.Version);
        Assert.True(header.HasMemo);
        Assert.True(header.HasProductionMdx);
        Assert.Equal(new DateOnly(2026, 10, 30), header.LastUpdate);
        Assert.Equal(42u, header.RecordCount);
        Assert.Equal(49, header.RecordLength);
        Assert.Equal(0x57, header.LanguageDriver);
        Assert.Equal(["LASTNAME", "BALANCE", "HIREDATE", "NOTES"], header.Fields.Select(f => f.Name));
        Assert.Equal(new DbfField("BALANCE", 'N', 10, 2, 21), header.Fields[1]);
    }

    [Fact]
    public void Reads_dBASE_III_header_without_memo()
    {
        var bytes = BuildHeader(0x03, productionMdx: false, ("ID", 'N', 6, 0));

        var header = DbfHeader.Read(new MemoryStream(bytes));

        Assert.False(header.HasMemo);
        Assert.False(header.HasProductionMdx);
        Assert.Single(header.Fields);
        Assert.Equal(1, header.Fields[0].Offset);
    }

    // Regressions from an independent review of the header reader.

    private static byte[] OneCharField() => BuildHeader(0x03, productionMdx: false, ("A", 'C', 1, 0));

    [Fact]
    public void Missing_terminator_is_rejected()
    {
        var bytes = OneCharField()[..^1];
        Assert.Throws<InvalidDataException>(() => DbfHeader.Read(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(34)]
    [InlineData(64)]
    public void Header_length_too_short_for_the_descriptors_is_rejected(int headerLength)
    {
        var bytes = OneCharField();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)headerLength);
        Assert.Throws<InvalidDataException>(() => DbfHeader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void Fields_wider_than_the_record_are_rejected()
    {
        var bytes = OneCharField();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), 1);
        Assert.Throws<InvalidDataException>(() => DbfHeader.Read(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(31)]
    public void Truncated_fixed_header_is_rejected(int length)
    {
        Assert.Throws<InvalidDataException>(() => DbfHeader.Read(new MemoryStream(OneCharField()[..length])));
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x04)]
    [InlineData(0x8C)]
    public void dBASE_II_and_7_layouts_are_refused(byte version)
    {
        var bytes = OneCharField();
        bytes[0] = version;
        Assert.Throws<NotSupportedException>(() => DbfHeader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void Header_can_start_mid_stream_and_leaves_stream_after_terminator()
    {
        var header = OneCharField();
        var bytes = new byte[128 + header.Length];
        header.CopyTo(bytes, 128);
        var stream = new MemoryStream(bytes) { Position = 128 };

        var read = DbfHeader.Read(stream);

        Assert.Single(read.Fields);
        Assert.Equal(bytes.Length, stream.Position);
    }

    [Theory]
    [InlineData(126, 0, 0)]
    [InlineData(126, 13, 1)]
    [InlineData(126, 2, 30)]
    public void Impossible_update_dates_read_as_null(byte year, byte month, byte day)
    {
        var bytes = OneCharField();
        (bytes[1], bytes[2], bytes[3]) = (year, month, day);
        Assert.Null(DbfHeader.Read(new MemoryStream(bytes)).LastUpdate);
    }

    [Fact]
    public void Visual_FoxPro_flags_mean_cdx_and_memo_not_mdx()
    {
        var bytes = OneCharField();
        bytes[0] = 0x30;
        bytes[28] = 0x03; // structural CDX + memo

        var header = DbfHeader.Read(new MemoryStream(bytes));

        Assert.True(header.IsVisualFoxPro);
        Assert.True(header.HasStructuralCdx);
        Assert.True(header.HasMemo);
        Assert.False(header.HasProductionMdx);
    }

    [Fact]
    public void Fields_cannot_be_mutated_through_a_cast()
    {
        var header = DbfHeader.Read(new MemoryStream(OneCharField()));
        Assert.IsNotType<List<DbfField>>(header.Fields);
    }

    [Theory]
    [InlineData(0x30, 24, 2024)]   // Visual FoxPro after 2000: year modulo 100
    [InlineData(0x30, 99, 1999)]
    [InlineData(0xF5, 5, 2005)]    // FoxPro 2 too
    [InlineData(0x03, 124, 2024)]  // dBASE: years since 1900
    [InlineData(0x03, 85, 1985)]
    public void Last_update_years_follow_each_dialect(byte version, byte storedYear, int expectedYear)
    {
        var bytes = OneCharField();
        bytes[0] = version;
        (bytes[1], bytes[2], bytes[3]) = (storedYear, 6, 15);

        Assert.Equal(new DateOnly(expectedYear, 6, 15), DbfHeader.Read(new MemoryStream(bytes)).LastUpdate);
    }
}
