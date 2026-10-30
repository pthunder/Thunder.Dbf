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
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), 42u);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 2), (ushort)headerLength);
        BitConverter.TryWriteBytes(bytes.AsSpan(10, 2), (ushort)recordLength);
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
}
