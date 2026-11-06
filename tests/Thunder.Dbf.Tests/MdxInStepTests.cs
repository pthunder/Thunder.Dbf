//      records  1 2 3 4 5           ProjectThunder :: Thunder.Dbf tests
//      keys     1 2 3 4 5 9966      A writer killed between the table and its .mdx leaves a key too
//                         ^ extra   many or too few: Check() reports it, writes stop until a rebuild.

namespace Thunder.Dbf.Tests;

public sealed class MdxInStepTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-mdx-instep-");
    private readonly string _path;

    public MdxInStepTests()
    {
        _path = Path.Combine(_dir.FullName, "PEOPLE.DBF");
        using (var table = DbfTable.Create(_path, [DbfFieldDefinition.Numeric("ID", 10), DbfFieldDefinition.Character("NAME", 20)]))
        {
            table.FlushWritesToDisk = false;
            for (var i = 1; i <= 500; i++)
                table.Append(i, $"name{i}");
        }
        using (var table = DbfTable.Open(_path, writable: true, exclusive: true))
        {
            table.CreateIndexTag("BYID", "id");
            table.CreateIndexTag("BYNAME", "name");
        }
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private void AssertWritesRefused(bool exclusive, string says)
    {
        using var table = DbfTable.Open(_path, writable: true, exclusive: exclusive);
        var refused = Assert.Throws<DbfException>(() => table.UpdateFields(0, new Dictionary<string, object?> { ["NAME"] = "changed" }));
        Assert.Equal(DbfErrorCode.IndexDamaged, refused.Code);
        Assert.Contains(says, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_cut_off_with_its_new_key_in_and_its_old_one_not_out_is_caught()
    {
        // BYID 9966 -> #662's place, as the PC's kills left it: the key is in, the record never changed.
        using (var mdx = MdxIndex.Open(new FileStream(Path.ChangeExtension(_path, ".MDX"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)))
            mdx["BYID"].Insert(MdxKeys.EncodeNumeric(9966m, NumericKeyStyle.Jet), 162);

        AssertCaughtUntilRebuilt(exclusiveSays: "BYID holds 501 keys for 500 records", sharedSays: "BYID holds 501 keys for 500 records");
    }

    [Fact]
    public void An_append_cut_off_after_its_record_and_before_its_keys_is_caught()
    {
        var mdxPath = Path.ChangeExtension(_path, ".MDX");
        var before = File.ReadAllBytes(mdxPath);
        using (var table = DbfTable.Open(_path, writable: true))
            table.Append(501, "late");
        File.WriteAllBytes(mdxPath, before); // the record and the count reached the disk, the keys didn't

        AssertCaughtUntilRebuilt(exclusiveSays: "holds 500 keys for 501 records", sharedSays: "holds 500 keys for 501 records");
    }

    [Fact]
    public void A_UNIQUE_tag_whose_last_records_share_a_key_is_in_step()
    {
        // A UNIQUE tag keeps one record per key: the last record, a repeat, rightly has no key of its own.
        var path = Path.Combine(_dir.FullName, "CATS.DBF");
        using (var table = DbfTable.Create(path, [DbfFieldDefinition.Character("STATUS", 10)]))
        {
            foreach (var status in new[] { "ACTIVE", "LAPSED", "ACTIVE", "ACTIVE" })
                table.Append(status);
        }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("STATUS", "status", unique: true);

        foreach (var exclusive in new[] { false, true })
        {
            using var table = DbfTable.Open(path, writable: true, exclusive: exclusive);
            table.UpdateFields(1, new Dictionary<string, object?> { ["STATUS"] = "LAPSED" });
        }
        using var db = DbfDatabase.Open(_dir.FullName);
        Assert.Empty(db.Check().Problems);
    }

    [Fact]
    public void Check_finds_a_UNIQUE_tag_a_key_short()
    {
        var path = Path.Combine(_dir.FullName, "CATS.DBF");
        using (var table = DbfTable.Create(path, [DbfFieldDefinition.Character("STATUS", 10)]))
        {
            foreach (var status in new[] { "ACTIVE", "LAPSED", "ACTIVE", "GONE" })
                table.Append(status);
            table.Delete(3); // a deleted record keeps its key, as dBASE keeps it
        }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("STATUS", "status", unique: true);
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Empty(db.Check().Problems);

        // A kill that handed LAPSED on to nobody: the tag loses that key.
        using (var mdx = MdxIndex.Open(new FileStream(Path.ChangeExtension(path, ".MDX"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)))
            Assert.True(mdx["STATUS"].Remove(System.Text.Encoding.ASCII.GetBytes("LAPSED    "), 2));
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Problems, p => p.Message.Contains("UNIQUE tag STATUS holds 2 keys for 3 distinct values", StringComparison.Ordinal));
    }

    [Fact]
    public void A_table_in_step_opens_and_checks_clean()
    {
        using (var table = DbfTable.Open(_path, writable: true))
            table.UpdateFields(10, new Dictionary<string, object?> { ["ID"] = 9_999m });
        using var db = DbfDatabase.Open(_dir.FullName);
        Assert.Empty(db.Check().Problems);
    }

    // Every writable open counts every key, shared or exclusive, as a .cdx open does.
    private void AssertCaughtUntilRebuilt(string exclusiveSays, string? sharedSays)
    {
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Problems, p => p.Code == DbfErrorCode.IndexDamaged && p.Message.Contains(exclusiveSays, StringComparison.Ordinal));

        AssertWritesRefused(exclusive: true, exclusiveSays);
        if (sharedSays is not null)
            AssertWritesRefused(exclusive: false, sharedSays);

        using (var table = DbfTable.Open(_path, writable: true, exclusive: true))
            table.RebuildIndex();
        using (var table = DbfTable.Open(_path, writable: true))
            table.UpdateFields(0, new Dictionary<string, object?> { ["NAME"] = "changed" });
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Empty(db.Check().Problems);
    }
}
