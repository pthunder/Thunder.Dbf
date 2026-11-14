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
        var ordered = items.Where(item => !Blank(item)).Concat(items.Where(Blank)).ToList();
        for (var i = 0; i < count; i++)
        {
            if (Blank(ordered[i]))
                BinaryPrimitives.WriteDoubleLittleEndian(ordered[i].AsSpan(4), 1e100);
            ordered[i].CopyTo(bytes, node + 8 + i * itemLength);
        }
        File.WriteAllBytes(Path.ChangeExtension(path, ".MDX"), bytes);
    }

    private static bool Blank(byte[] item) => MdxKeys.IsZeroBlank(item.AsSpan(4));

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

    // The .mdx's bytes, read alongside a writer that still has it open (Windows refuses a plain read).
    private static byte[] ReadShared(string path)
    {
        using var mdx = new FileStream(Path.ChangeExtension(path, ".MDX"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[mdx.Length];
        mdx.ReadExactly(bytes);
        return bytes;
    }

    // (record, key) in the leaf's order.
    private static List<(int Record, double Julian)> Keys(string path)
    {
        var bytes = ReadShared(path);
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
        // Two Jet blanks; then record 3's becomes a 0 at the front, as Thunder.Dbf 0.4.3 would have
        // keyed it: the leaf is [#3 0, #1 2026, #2 1E100], both forms in one tag.
        var path = JetTable(Late, null, null);
        var bytes = File.ReadAllBytes(Path.ChangeExtension(path, ".MDX"));
        var (node, itemLength) = Leaf(bytes);
        var items = Enumerable.Range(0, 3).Select(i => bytes.AsSpan(node + 8 + i * itemLength, itemLength).ToArray()).ToList();
        BinaryPrimitives.WriteDoubleLittleEndian(items[2].AsSpan(4), 0d);
        foreach (var (item, i) in new[] { items[2], items[0], items[1] }.Select((item, i) => (item, i)))
            item.CopyTo(bytes, node + 8 + i * itemLength);
        File.WriteAllBytes(Path.ChangeExtension(path, ".MDX"), bytes);
        Assert.Equal([(3, 0d), (1, 2461358d), (2, 1e100)], Keys(path));
        AssertInStep(path);
        using (var mdx = MdxIndex.Open(new FileStream(Path.ChangeExtension(path, ".MDX"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            Assert.Equal([3, 2], mdx["BYD"].SeekExact(null).Select(e => e.RecordNumber));

        using (var table = DbfTable.Open(path, writable: true))
        {
            table.UpdateFields(2, new Dictionary<string, object?> { ["D"] = Early }); // removes the 0
            table.UpdateFields(1, new Dictionary<string, object?> { ["D"] = Late });  // removes the 1E100
        }

        Assert.Equal([(3, 2415021d), (1, 2461358d), (2, 2461358d)], Keys(path));
        AssertInStep(path);
    }

    private sealed class Killed : Exception;

    [Fact]
    public void A_journal_note_in_one_blank_form_keeps_the_records_key_in_the_other()
    {
        // [#1 0, #3 2000-01-01, #2 1E100]: record 1's blank as 0.4.3 keyed it, record 2's as Jet does.
        var path = JetTable(null, null, new DateOnly(2000, 1, 1));
        var bytes = File.ReadAllBytes(Path.ChangeExtension(path, ".MDX"));
        var (node, itemLength) = Leaf(bytes);
        var items = Enumerable.Range(0, 3).Select(i => bytes.AsSpan(node + 8 + i * itemLength, itemLength).ToArray()).ToList();
        BinaryPrimitives.WriteDoubleLittleEndian(items[1].AsSpan(4), 0d);
        foreach (var (item, i) in new[] { items[1], items[0], items[2] }.Select((item, i) => (item, i)))
            item.CopyTo(bytes, node + 8 + i * itemLength);
        File.WriteAllBytes(Path.ChangeExtension(path, ".MDX"), bytes);
        Assert.Equal([(1, 0d), (3, 2451545d), (2, 1e100)], Keys(path));

        // The tag has learned Jet's form, so the note keys record 1's blank as 1E100; the write is cut
        // off after its new keys go in, and the replay then finds no Jet blank left in the tag.
        using (var table = DbfTable.Open(path, writable: true))
        {
            table.UseJournal = true;
            table.UpdateFields(1, new Dictionary<string, object?> { ["D"] = new DateOnly(2001, 1, 1) });
            table.CrashPoint = point => { if (point == "new keys") throw new Killed(); };
            Assert.Throws<Killed>(() => table.UpdateFields(0, new Dictionary<string, object?> { ["D"] = new DateOnly(2002, 1, 1) }));
        }

        using (var table = DbfTable.Open(path, writable: true))
            table.UpdateFields(2, new Dictionary<string, object?> { ["ID"] = 30m });

        Assert.Equal([1], Keys(path).Where(k => MdxKeys.IsBlankDate(BitConverter.GetBytes(k.Julian))).Select(k => k.Record));
        Assert.Equal(3, Keys(path).Count);
        AssertInStep(path);
    }

    [Fact]
    public void The_first_blank_in_a_tag_and_every_blank_in_a_new_tag_take_Jets_form()
    {
        // A new tag, as Thunder.Dbf makes it (no Jet rewrite), keys its blanks as Jet does.
        var path = Path.Combine(_dir.FullName, "NEWTAG.DBF");
        using (var table = DbfTable.Create(path, [DbfFieldDefinition.Numeric("ID", 5), DbfFieldDefinition.Date("D")]))
        {
            table.Append(1m, Late);
            table.Append(2m, null);
            table.Append(3m, Early);
        }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("BYD", "D");
        Assert.Equal([(3, 2415021d), (1, 2461358d), (2, 1e100)], Keys(path));

        // Its one blank goes; a fresh open then sees a tag with no blank, and the next blank is Jet's.
        using (var table = DbfTable.Open(path, writable: true))
            table.UpdateFields(1, new Dictionary<string, object?> { ["D"] = Late });
        using (var table = DbfTable.Open(path, writable: true))
            table.Append(4m, null);

        Assert.Equal([(3, 2415021d), (1, 2461358d), (2, 2461358d), (4, 1e100)], Keys(path));
        AssertInStep(path);
    }

    [Fact]
    public void A_UNIQUE_date_tag_keeps_one_blank_whatever_its_form()
    {
        var path = Path.Combine(_dir.FullName, "BLANKU.DBF");
        using (var table = DbfTable.Create(path, [DbfFieldDefinition.Numeric("ID", 5), DbfFieldDefinition.Date("D")]))
            table.Append(1m, null);
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("BYD", "D", unique: true);
        Assert.Equal([(1, 1e100)], Keys(path));

        // Writer A sees Jet's blank, then moves it to a date: A keeps Jet's form.
        using var a = DbfTable.Open(path, writable: true);
        a.UpdateFields(0, new Dictionary<string, object?> { ["D"] = Late });

        // Another writer's blank lands as 0.4.3 would have keyed it: [#2 0, #1 2026].
        using (var b = DbfTable.Open(path, writable: true))
            b.Append(2m, null);
        using (var mdx = new FileStream(Path.ChangeExtension(path, ".MDX"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var bytes = new byte[mdx.Length];
            mdx.ReadExactly(bytes);
            var (node, itemLength) = Leaf(bytes);
            var items = Enumerable.Range(0, 2).Select(i => bytes.AsSpan(node + 8 + i * itemLength, itemLength).ToArray()).ToList();
            BinaryPrimitives.WriteDoubleLittleEndian(items[1].AsSpan(4), 0d);
            mdx.Position = node + 8;
            mdx.Write(items[1]);
            mdx.Write(items[0]);
        }
        Assert.Equal([(2, 0d), (1, 2461358d)], Keys(path));

        // A's new blank is 1E100: record 2 already holds the blank, so no second one goes in.
        a.Append(3m, null);
        a.Dispose();

        Assert.Equal([2], Keys(path).Where(k => MdxKeys.IsBlankDate(BitConverter.GetBytes(k.Julian))).Select(k => k.Record));
        AssertInStep(path);
    }
}
