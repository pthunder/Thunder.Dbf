//      36 29 95 00 ..   Jet           ProjectThunder :: Thunder.Dbf tests
//      36 09 95 00 ..   dBASE         The same 95, three ways. A number key carries a digit count,
//      36 09 95 00 ..   Thunder.Dbf   and Jet steps from match to match by bytes: write its form.

using System.Buffers.Binary;

namespace Thunder.Dbf.Tests;

public sealed class MdxDigitCountTests : IDisposable
{
    private static readonly string FixtureRoot = FindFixtureRoot();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-mdx-digits-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void Keys_count_significant_digits_as_dBASE_writes_them()
    {
        // Every numeric key SIMATIC Manager's dBASE engine wrote is the key Thunder.Dbf would write.
        var checkedKeys = 0;
        foreach (var mdx in Directory.EnumerateFiles(Path.Combine(FixtureRoot, "siemens-step7"), "*.MDX", SearchOption.AllDirectories))
        {
            foreach (var (_, key, _) in NumericKeys(mdx).Where(k => k.Leaf))
            {
                var value = MdxKeys.DecodeNumeric(key.Bytes);
                Assert.Equal(Convert.ToHexString(key.Bytes), Convert.ToHexString(MdxKeys.EncodeNumeric((decimal)value)));
                checkedKeys++;
            }
        }
        Assert.True(checkedKeys > 40, $"only {checkedKeys} numeric keys found");

        Assert.Equal(4, MdxKeys.DigitCount(MdxKeys.EncodeNumeric(15940m))); // SIMATIC's SYMLISTS.MDX holds this one
        Assert.Equal(1, MdxKeys.DigitCount(MdxKeys.EncodeNumeric(100m)));
        Assert.Equal(1, MdxKeys.DigitCount(MdxKeys.EncodeNumeric(0.05m)));
        Assert.Equal(MdxKeys.EncodeNumeric(15940m), MdxKeys.EncodeNumeric(15940d));
    }

    [Fact]
    public void A_tag_Jet_wrote_keeps_Jets_digit_count()
    {
        var path = JetStyleTable(rows: 400);

        // The repro from the Windows PC: delete #95, and give #351 the number 95.
        using (var table = DbfTable.Open(path, writable: true))
        {
            table.Delete(94);
            table.UpdateFields(350, new Dictionary<string, object?> { ["ID"] = 95m });
            table.Append(401m, "new");
        }

        var keys = NumericKeys(Path.ChangeExtension(path, ".MDX")).Select(k => k.Key).ToList();
        Assert.All(keys, k => Assert.Equal(JetDigits, MdxKeys.DigitCount(k.Bytes)));
        var nineFives = keys.Where(k => MdxKeys.DecodeNumeric(k.Bytes) is 95m).Select(k => Convert.ToHexString(k.Bytes)).Distinct();
        Assert.Single(nineFives); // the deleted 95 and the live one are the same bytes, as Jet compares them

        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.RebuildIndex();
        Assert.All(NumericKeys(Path.ChangeExtension(path, ".MDX")), k => Assert.Equal(JetDigits, MdxKeys.DigitCount(k.Key.Bytes)));
    }

    [Fact]
    public void Keys_earlier_versions_left_in_a_Jet_tag_dont_hide_its_count()
    {
        var path = JetStyleTable(rows: 400);
        // As if 0.4.0 had written some of them: its printed-digit count for 10, 20 ... 400.
        Patch(path, key => MdxKeys.DecodeNumeric(key) is decimal d && d % 10 == 0 ? MdxKeys.PrintedDigits(key) : null);

        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.RebuildIndex();

        Assert.All(NumericKeys(Path.ChangeExtension(path, ".MDX")), k => Assert.Equal(JetDigits, MdxKeys.DigitCount(k.Key.Bytes)));
    }

    [Fact]
    public void A_tag_earlier_versions_wrote_moves_to_significant_digits()
    {
        var path = JetStyleTable(rows: 400);
        Patch(path, key => MdxKeys.PrintedDigits(key)); // every key as 0.4.0 wrote it: nothing here is a fixed width

        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.RebuildIndex();

        Assert.All(NumericKeys(Path.ChangeExtension(path, ".MDX")), k => Assert.Equal(MdxKeys.SignificantDigits(k.Key.Bytes), MdxKeys.DigitCount(k.Key.Bytes)));
    }

    [Fact]
    public void Lookups_find_every_key_whatever_its_count()
    {
        var path = JetStyleTable(rows: 400);
        using var table = DbfTable.Open(path);
        using var mdx = table.OpenProductionIndex()!;
        foreach (var id in new[] { 1m, 95m, 100m, 400m })
            Assert.Equal(id, Assert.Single(mdx["ID"].SeekExact(id)).Key);
    }

    // ---- a table whose ID tag Jet might have written ----------------------------------------

    private const int JetDigits = 10; // what Jet wrote for every key of an N(20,5) on the Windows PC

    private string JetStyleTable(int rows)
    {
        var path = Path.Combine(_dir.FullName, $"P{Guid.NewGuid():N}"[..8] + ".DBF");
        using (var table = DbfTable.Create(path, [DbfFieldDefinition.Numeric("ID", 20, 5), DbfFieldDefinition.Character("NAME", 20)]))
        {
            for (var i = 1; i <= rows; i++)
                table.Append((decimal)i, $"name{i}");
        }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("ID", "id");
        Patch(path, _ => JetDigits);
        return path;
    }

    // Rewrites the digit count of every numeric key, leaf and interior, as another writer would have.
    private static void Patch(string path, Func<byte[], int?> count)
    {
        var mdx = Path.ChangeExtension(path, ".MDX");
        var bytes = File.ReadAllBytes(mdx);
        foreach (var (_, key, _) in NumericKeys(mdx))
        {
            if (count(key.Bytes) is { } digits)
                bytes[key.Offset + 1] = (byte)((bytes[key.Offset + 1] & 0x83) | (digits << 2));
        }
        File.WriteAllBytes(mdx, bytes);
    }

    private readonly record struct RawKey(int Offset, byte[] Bytes);

    // Every numeric key in the file, read straight from the pages: (tag, key, whether it's in a leaf).
    private static IEnumerable<(string Tag, RawKey Key, bool Leaf)> NumericKeys(string mdx)
    {
        var file = File.ReadAllBytes(mdx);
        var tags = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(28));
        var entryLength = file[26] == 0 ? 32 : file[26];
        for (var t = 0; t < tags; t++)
        {
            var entry = 544 + t * entryLength;
            var header = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(entry)) * 512;
            var name = System.Text.Encoding.ASCII.GetString(file, entry + 4, 11).TrimEnd('\0', ' ');
            if (file[header + 9] != 'N')
                continue;
            var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(header + 18));
            var pages = new Stack<int>([BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(header))]);
            while (pages.TryPop(out var page))
            {
                var node = page * 512;
                var count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(node));
                var leaf = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(node + 8 + count * itemLength)) == 0;
                for (var i = 0; i < count; i++)
                {
                    var at = node + 8 + i * itemLength + 4;
                    yield return (name, new RawKey(at, file[at..(at + MdxKeys.NumericKeyLength)]), leaf);
                }
                if (!leaf)
                {
                    for (var i = 0; i <= count; i++)
                        pages.Push(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(node + 8 + i * itemLength)));
                }
            }
        }
    }

    private static string FindFixtureRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Couldn't find tests/fixtures above the test output folder.");
    }
}
