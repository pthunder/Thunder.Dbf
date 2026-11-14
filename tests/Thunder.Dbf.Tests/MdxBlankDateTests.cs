//      #3 1900-01-01   #1 2026-11-13   #2 1E100   #4 1E100     ProjectThunder :: Thunder.Dbf tests
//      Jet keys a blank date as 1E100, so its blanks sort after every real date; dBASE and Thunder.Dbf
//      key it as 0. A tag Jet wrote keeps Jet's form, and either form is found and removed.

using System.Buffers.Binary;

namespace Thunder.Dbf.Tests;

public sealed class MdxBlankDateTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-mdx-blank-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static readonly DateOnly Late = new(2026, 11, 13), Early = new(1900, 1, 1);

    // ID and D, keyed on D; null is a blank date (spaces in the .dbf, as Jet stores it).
    private string JetTable(params DateOnly?[] dates)
    {
        var path = Path.Combine(_dir.FullName, "BLANKD.DBF");
        using (var table = DbfTable.Create(path, [DbfFieldDefinition.Numeric("ID", 5), DbfFieldDefinition.Date("D")]))
        {
            for (var i = 0; i < dates.Length; i++)
                table.Append(i + 1m, dates[i]);
        }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("BYD", "D");
        Jetify(path);
        return path;
    }

    // Rewrites the tag's one leaf as Jet keys it: blanks 1E100, after every real date, in record order.
    private static void Jetify(string path)
    {
        var bytes = File.ReadAllBytes(Path.ChangeExtension(path, ".MDX"));
        var (node, itemLength) = Leaf(bytes);
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(node));
        var items = Enumerable.Range(0, count).Select(i => bytes.AsSpan(node + 8 + i * itemLength, itemLength).ToArray()).ToList();
        var ordered = items.Where(item => Julian(item) != 0).Concat(items.Where(item => Julian(item) == 0)).ToList();
        for (var i = 0; i < count; i++)
        {
            if (Julian(ordered[i]) == 0)
                BinaryPrimitives.WriteDoubleLittleEndian(ordered[i].AsSpan(4), 1e100);
            ordered[i].CopyTo(bytes, node + 8 + i * itemLength);
        }
        File.WriteAllBytes(Path.ChangeExtension(path, ".MDX"), bytes);
    }

    private static double Julian(byte[] item) => BinaryPrimitives.ReadDoubleLittleEndian(item.AsSpan(4));

    // The tag's root, a leaf while the table is this small, and its item length.
    private static (int Node, int ItemLength) Leaf(byte[] file)
    {
        var header = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(544)) * 512;
        var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(header + 18));
        var node = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(header)) * 512;
        var count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(node));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(node + 8 + count * itemLength)));
        return (node, itemLength);
    }

    // (record, key) in the leaf's order.
    private static List<(int Record, double Julian)> Keys(string path)
    {
        var bytes = File.ReadAllBytes(Path.ChangeExtension(path, ".MDX"));
        var (node, itemLength) = Leaf(bytes);
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(node));
        return [.. Enumerable.Range(0, count).Select(i => (
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(node + 8 + i * itemLength)),
            BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(node + 8 + i * itemLength + 4))))];
    }

    private static void AssertInStep(string path)
    {
        using var db = DbfDatabase.Open(Path.GetDirectoryName(path)!);
        Assert.Empty(db.Check().Problems);
    }

    [Fact]
    public void A_Jet_tag_with_a_blank_last_record_can_be_written()
    {
        var path = JetTable(Late, null, Early, null);
        AssertInStep(path);

        using (var table = DbfTable.Open(path, writable: true))
        {
            table.Append(5m, null);
            table.Append(6m, new DateOnly(2000, 2, 29));
        }

        Assert.Equal([3, 6, 1, 2, 4, 5], Keys(path).Select(k => k.Record));
        Assert.All(Keys(path).Where(k => k.Record is 2 or 4 or 5), k => Assert.Equal(1e100, k.Julian));
        AssertInStep(path);

        using var mdx = MdxIndex.Open(new FileStream(Path.ChangeExtension(path, ".MDX"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Assert.Equal([2, 4, 5], mdx["BYD"].SeekExact(null).Select(e => e.RecordNumber));
        Assert.All(mdx["BYD"].SeekExact(null), e => Assert.Null(e.Key));
    }

    [Fact]
    public void Updating_a_blank_date_on_a_Jet_tag_moves_its_key()
    {
        var path = JetTable(null, Late, Early, Late);

        using (var table = DbfTable.Open(path, writable: true))
        {
            table.UpdateFields(0, new Dictionary<string, object?> { ["D"] = new DateOnly(2026, 11, 15) });
            table.UpdateFields(2, new Dictionary<string, object?> { ["D"] = null });
        }

        var keys = Keys(path);
        Assert.Equal(4, keys.Count);
        Assert.Equal([2, 4, 1, 3], keys.Select(k => k.Record));
        Assert.Equal(1e100, keys[^1].Julian);
        AssertInStep(path);
    }

    [Fact]
    public void A_tag_mixing_both_blank_forms_finds_and_removes_either()
    {
        // Jet's blanks, then one Thunder.Dbf 0.4.3 wrote as 0 before it knew Jet's form.
        var path = JetTable(Late, null);
        var bytes = File.ReadAllBytes(Path.ChangeExtension(path, ".MDX"));
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.Append(3m, Early);
        var (node, itemLength) = Leaf(File.ReadAllBytes(Path.ChangeExtension(path, ".MDX")));
        // Turn record 2's 1E100 into 0 at the front: the leaf is [#3 1900, #1 2026, #2 1E100] now.
        bytes = File.ReadAllBytes(Path.ChangeExtension(path, ".MDX"));
        var items = Enumerable.Range(0, 3).Select(i => bytes.AsSpan(node + 8 + i * itemLength, itemLength).ToArray()).ToList();
        BinaryPrimitives.WriteDoubleLittleEndian(items[2].AsSpan(4), 0d);
        foreach (var (item, i) in new[] { items[2], items[0], items[1] }.Select((item, i) => (item, i)))
            item.CopyTo(bytes, node + 8 + i * itemLength);
        File.WriteAllBytes(Path.ChangeExtension(path, ".MDX"), bytes);
        Assert.Equal([(2, 0d), (3, 2415021d), (1, 2461358d)], Keys(path));

        using (var table = DbfTable.Open(path, writable: true))
            table.UpdateFields(1, new Dictionary<string, object?> { ["D"] = Late });

        Assert.Equal([3, 1, 2], Keys(path).Select(k => k.Record));
        AssertInStep(path);
    }
}
