//    ___________
//   |  KEEP OUT |     ProjectThunder :: Thunder.Dbf tests
//   |___________|     PACK and REINDEX want the table to themselves,
//        | |          as they always did in dBASE and FoxPro.

namespace Thunder.Dbf.Tests;

public sealed class ExclusiveUseTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-exclusive-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Table()
    {
        var path = Path.Combine(_dir.FullName, "PEOPLE.DBF");
        using var table = DbfTable.Create(path, [DbfFieldDefinition.Character("NAME", 10), DbfFieldDefinition.Numeric("AGE", 3)]);
        table.Append("Ada", 36m);
        table.Append("Grace", 45m);
        table.CreateIndexTag("NAME", "NAME");
        table.Delete(1);
        return path;
    }

    // Found against Visual FoxPro 9: a PACK or REINDEX while VFP had the table open shared left VFP
    // reading freed index pages ("tag is corrupted", error 2066) and its record numbers renumbered.
    [Fact]
    public void Pack_rebuilds_and_INDEX_ON_refuse_a_table_opened_shared()
    {
        var path = Table();
        var before = SharedFile.ReadAllBytes(path);
        using var table = DbfTable.Open(path, writable: true);
        Assert.False(table.IsExclusive);

        Assert.Equal(DbfErrorCode.ExclusiveUseRequired, Assert.Throws<DbfException>(() => table.Pack()).Code);
        Assert.Equal(DbfErrorCode.ExclusiveUseRequired, Assert.Throws<DbfException>(table.RebuildIndex).Code);
        Assert.Equal(DbfErrorCode.ExclusiveUseRequired, Assert.Throws<DbfException>(() => table.CreateIndexTag("AGE", "AGE")).Code);
        Assert.Equal(DbfErrorCode.ExclusiveUseRequired,
            Assert.Throws<DbfException>(() => table.MaintainIndex(Path.ChangeExtension(path, ".MDX"), rebuild: true)).Code);

        var error = Assert.Throws<DbfException>(() => table.Pack());
        Assert.Contains("exclusive: true", error.Hint);
        Assert.Equal(409, error.HttpStatus);
        Assert.Equal(before, SharedFile.ReadAllBytes(path));
    }

    [Fact]
    public void An_exclusive_table_packs_and_rebuilds()
    {
        var path = Table();
        using var table = DbfTable.Open(path, writable: true, exclusive: true);
        Assert.True(table.IsExclusive);
        Assert.Equal(1, table.Pack());
        table.RebuildIndex();
        table.CreateIndexTag("AGE", "AGE");
        Assert.Equal(1, table.RecordCount);
    }

    [Fact]
    public void An_exclusive_open_fails_while_another_program_has_the_table_and_keeps_others_out()
    {
        var path = Table();
        using (DbfTable.Open(path))
            Assert.Equal(DbfErrorCode.FileInUse, Assert.Throws<DbfException>(() => DbfTable.Open(path, writable: true, exclusive: true)).Code);

        using var exclusive = DbfTable.Open(path, writable: true, exclusive: true);
        Assert.Equal(DbfErrorCode.FileInUse, Assert.Throws<DbfException>(() => DbfTable.Open(path)).Code);
    }

    [Fact]
    public void A_connection_string_can_ask_for_exclusive_use()
    {
        Table();
        using var db = DbfDatabase.Open($"Data Source={_dir.FullName};Read Only=false;Exclusive=true");
        Assert.True(db.Options.Exclusive);
        var table = db["PEOPLE"];
        Assert.True(table.IsExclusive);
        Assert.Equal(1, table.Pack());
    }

    [Fact]
    public void Check_looks_at_an_exclusive_table_already_open_and_reports_one_another_program_holds()
    {
        var path = Table();
        using (var db = DbfDatabase.Open(_dir.FullName, new DbfDatabaseOptions { ReadOnly = false, Exclusive = true }))
        {
            Assert.True(db.Check().Ok);              // the probe holds it exclusively, then lets go
            _ = db["PEOPLE"];                         // now this database holds it
            var report = db.Check();
            Assert.True(report.Ok, report.ToString());
            Assert.True(report.Tables.Single().CanWrite);
        }

        using var other = DbfTable.Open(path);
        using var blocked = DbfDatabase.Open(_dir.FullName, new DbfDatabaseOptions { ReadOnly = false, Exclusive = true });
        Assert.Contains(blocked.Check().Tables.Single().Problems, p => p.Code == DbfErrorCode.FileInUse);
    }
}
