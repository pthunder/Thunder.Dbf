//      [ header: next free 643 ][ ... block 640 ][ end at 641 ]      ProjectThunder :: Thunder.Dbf tests
//                                                 ^ 2 blocks short   A power cut kept the memo header and
//                                                                    lost the tail: report it, pad it back.

using System.Buffers.Binary;

namespace Thunder.Dbf.Tests;

public sealed class MemoTailTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-memo-tail-");

    public void Dispose() => _dir.Delete(recursive: true);

    // The memo file beside the table, whatever case its extension was written in (Linux tells them apart).
    private static string MemoFile(string table, string extension) =>
        Directory.GetFiles(Path.GetDirectoryName(table)!).Single(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData(DbfTableFormat.VisualFoxPro, ".FPT", 64, true)]
    [InlineData(DbfTableFormat.DbaseIV, ".DBT", 512, false)]
    public void A_memo_file_shorter_than_its_header_says_is_reported_and_padded_back(DbfTableFormat format, string extension, int blockSize, bool bigEndian)
    {
        var path = Path.Combine(_dir.FullName, "CALLS.DBF");
        using (var table = DbfTable.Create(path, format, [DbfFieldDefinition.Numeric("ID", 6), DbfFieldDefinition.Memo("NOTES")]))
        {
            for (var i = 1; i <= 20; i++)
                table.Append(i, $"call {i}: " + new string('x', 40 * i));
        }

        // The header's next free block moves two blocks on, and the blocks never reach the disk.
        var memoPath = MemoFile(path, extension);
        var bytes = File.ReadAllBytes(memoPath);
        var next = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        Assert.Equal(bytes.Length, (long)next * blockSize);
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(bytes, next + 2);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, next + 2);
        File.WriteAllBytes(memoPath, bytes);

        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Problems, p => p.Code == DbfErrorCode.MemoFileDamaged && p.Message.Contains($"{2 * blockSize:N0} bytes shorter", StringComparison.Ordinal));

        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            Assert.Equal((next + 2L) * blockSize, new FileInfo(memoPath).Length); // padded back on open
            Assert.Equal("call 20: " + new string('x', 800), table.Read(19)["NOTES"]); // every memo where it was
            table.Append(21, "after the repair");
        }

        using (var reopened = DbfTable.Open(path))
        {
            Assert.Equal("after the repair", reopened.Read(20)["NOTES"]);
            Assert.Equal("call 1: " + new string('x', 40), reopened.Read(0)["NOTES"]);
        }
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Empty(db.Check().Problems);
    }

    [Fact]
    public void A_shared_open_leaves_a_short_memo_file_to_the_exclusive_one()
    {
        var path = Path.Combine(_dir.FullName, "CALLS.DBF");
        using (var table = DbfTable.Create(path, DbfTableFormat.VisualFoxPro, [DbfFieldDefinition.Memo("NOTES")]))
            table.Append("one");
        var memoPath = MemoFile(path, ".FPT");
        var bytes = File.ReadAllBytes(memoPath);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, BinaryPrimitives.ReadUInt32BigEndian(bytes) + 1);
        File.WriteAllBytes(memoPath, bytes);

        using (DbfTable.Open(path, writable: true)) { }
        Assert.Equal(bytes.Length, new FileInfo(memoPath).Length); // others may have it open: not touched
    }
}
