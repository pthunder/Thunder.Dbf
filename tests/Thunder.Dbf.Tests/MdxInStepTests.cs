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

    [Fact]
    public void An_update_cut_off_with_its_new_key_in_and_its_old_one_not_out_is_caught()
    {
        // BYID 9966 -> #662's place, as the PC's kills left it: the key is in, the record never changed.
        using (var mdx = MdxIndex.Open(new FileStream(Path.ChangeExtension(_path, ".MDX"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)))
            mdx["BYID"].Insert(MdxKeys.EncodeNumeric(9966m, NumericKeyStyle.Jet), 162);

        AssertCaughtUntilRebuilt("BYID holds 501 keys for 500 records");
    }

    [Fact]
    public void An_append_cut_off_after_its_record_and_before_its_keys_is_caught()
    {
        var mdxPath = Path.ChangeExtension(_path, ".MDX");
        var before = File.ReadAllBytes(mdxPath);
        using (var table = DbfTable.Open(_path, writable: true))
            table.Append(501, "late");
        File.WriteAllBytes(mdxPath, before); // the record and the count reached the disk, the keys didn't

        AssertCaughtUntilRebuilt("holds 500 keys for 501 records");
    }

    [Fact]
    public void A_table_in_step_opens_and_checks_clean()
    {
        using (var table = DbfTable.Open(_path, writable: true))
            table.UpdateFields(10, new Dictionary<string, object?> { ["ID"] = 9_999m });
        using var db = DbfDatabase.Open(_dir.FullName);
        Assert.Empty(db.Check().Problems);
    }

    private void AssertCaughtUntilRebuilt(string expected)
    {
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Contains(db.Check().Problems, p => p.Code == DbfErrorCode.IndexDamaged && p.Message.Contains(expected, StringComparison.Ordinal));

        using (var table = DbfTable.Open(_path, writable: true))
        {
            var refused = Assert.Throws<DbfException>(() => table.UpdateFields(0, new Dictionary<string, object?> { ["NAME"] = "changed" }));
            Assert.Equal(DbfErrorCode.IndexDamaged, refused.Code);
            Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        }

        using (var table = DbfTable.Open(_path, writable: true, exclusive: true))
            table.RebuildIndex();
        using (var table = DbfTable.Open(_path, writable: true))
            table.UpdateFields(0, new Dictionary<string, object?> { ["NAME"] = "changed" });
        using (var db = DbfDatabase.Open(_dir.FullName))
            Assert.Empty(db.Check().Problems);
    }
}
