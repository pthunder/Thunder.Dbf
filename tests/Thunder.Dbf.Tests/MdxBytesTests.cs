//       [ 6E 3A 00 .. ]                  ProjectThunder :: Thunder.Dbf tests
//       [ 9C 01 7F .. ]  = DCA108..      Rewrite the split code all you like:
//       [ 00 00 00 .. ]                  the bytes it leaves had better not move.

using System.Security.Cryptography;

namespace Thunder.Dbf.Tests;

// The .mdx a fixed sequence of writes leaves, pinned by hash. The expected value was computed with the
// decoded insert path that #39 replaced, so any change to how keys are placed, nodes split, separators
// chosen or padding zeroed shows here, with and without a deferred batch. Only the two dates in the
// file header (bytes 1-3 and 44-46, the day it was made and last changed) are blanked before hashing.
// Re-pinned when numeric keys began counting significant digits, as dBASE's engine does: compared
// with the file before, exactly 301 bytes differ, every one the digit-count byte of a numeric key.
public sealed class MdxBytesTests : IDisposable
{
    private const string Expected = "16E6D7DC66FB4A7AD3B32B511CADDECD406DE75DA2F32F7A83B194B5167DF980";

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-mdx-bytes-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_fixed_run_of_writes_leaves_the_same_mdx_bytes(bool deferred)
    {
        var path = Path.Combine(_dir.FullName, "PINNED.DBF");
        using (DbfTable.Create(path, [DbfFieldDefinition.Numeric("ID", 9), DbfFieldDefinition.Character("NAME", 30), DbfFieldDefinition.Date("HIRED"), DbfFieldDefinition.Character("C", 3)])) { }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            table.CreateIndexTag("ID", "id", unique: true);
            table.CreateIndexTag("NAME", "name");
            table.CreateIndexTag("HIRED", "hired", descending: true);
            table.CreateIndexTag("C", "c", unique: true);
        }

        var random = new Random(5);
        const int rows = 6_000; // enough for splits to reach interior nodes and grow new roots
        using (var table = DbfTable.Open(path, writable: true))
        {
            table.FlushWritesToDisk = false;
            table.DeferIndexWrites = deferred;
            using (table.LockTable())
            {
                // Keys in random order, duplicates, and a unique tag with few distinct values.
                table.AppendRange(Enumerable.Range(0, rows).Select(_ => new object?[] { random.Next(rows / 2), $"N{random.Next(500):D4}", new DateOnly(2000, 1, 1).AddDays(random.Next(4000)), $"{random.Next(40):D3}" }), commitEvery: 997);
                for (var i = 0; i < rows / 10; i++)
                    table.Append(random.Next(rows), $"N{random.Next(500):D4}", new DateOnly(2001, 1, 1), $"{random.Next(60):D3}");
                for (var i = 0; i < rows / 20; i++)
                    table.UpdateFields(random.Next(table.RecordCount), new Dictionary<string, object?> { ["NAME"] = $"U{random.Next(300):D4}", ["C"] = $"{random.Next(70):D3}" });
            }
        }

        var bytes = SharedFile.ReadAllBytes(Path.ChangeExtension(path, ".MDX"));
        bytes.AsSpan(1, 3).Clear();
        bytes.AsSpan(44, 3).Clear();
        Assert.Equal(Expected, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
