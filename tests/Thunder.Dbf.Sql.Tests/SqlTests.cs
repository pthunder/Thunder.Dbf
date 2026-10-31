//      ______        ______           ProjectThunder :: Thunder.Dbf.Sql tests
//     | .dbf |  <=>  | SQL  |         Real legacy tables into a real SQLite database,
//     |______|       |______|         and kept in step both ways.

using Microsoft.Data.Sqlite;

namespace Thunder.Dbf.Sql.Tests;

public sealed class SqlTests : IDisposable
{
    private static readonly string FixtureRoot = FindFixtureRoot();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-sql-");
    private readonly string _data;
    private readonly SqliteConnection _sqlite;

    public SqlTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_dir.FullName, "data")).FullName;
        foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT", "CHANGELOG.MDX" })
            File.Copy(Path.Combine(FixtureRoot, "siemens-step7/siprom", file), Path.Combine(_data, file));
        foreach (var file in new[] { "client.dbf", "client.dbt" })
            File.Copy(Path.Combine(FixtureRoot, "dbfdatareader", file), Path.Combine(_data, file));
        File.Copy(Path.Combine(FixtureRoot, "infused-dbf/dbase_31.dbf"), Path.Combine(_data, "products.dbf"));

        _sqlite = new SqliteConnection($"Data Source={Path.Combine(_dir.FullName, "mirror.db")};Pooling=False");
        _sqlite.Open();
    }

    public void Dispose()
    {
        _sqlite.Dispose();
        _dir.Delete(recursive: true);
    }

    private long Scalar(string sql)
    {
        using var command = _sqlite.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private object? Value(string sql)
    {
        using var command = _sqlite.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var command = _sqlite.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ---- migration ----------------------------------------------------------------------------

    [Fact]
    public void Every_table_migrates_with_its_values()
    {
        using var db = DbfDatabase.Open(_data);
        var result = DbfMigrator.Migrate(db, _sqlite, SqlDialect.Sqlite);

        Assert.Equal(["CHANGELOG", "client", "products"], result.Tables.Select(t => t.Table));
        Assert.Equal(77, Scalar("select count(*) from products"));
        Assert.Equal("Chai", Value("select PRODUCTNAM from products where _recno = 1"));
        Assert.Equal("18", Value("select UNITPRICE from products where _recno = 1"));    // currency kept exact, as text
        Assert.StartsWith("85-200", (string)Value("select CLIEN_HIST from client where _recno = 1")!); // memos in full
        Assert.Equal(result.Rows, Scalar("select (select count(*) from CHANGELOG) + (select count(*) from client) + (select count(*) from products)"));
    }

    [Fact]
    public void Migration_refuses_to_overwrite_unless_asked()
    {
        using var db = DbfDatabase.Open(_data);
        DbfMigrator.Migrate(db, _sqlite, SqlDialect.Sqlite, new DbfMigrationOptions { Tables = ["client"] });

        var error = Assert.Throws<DbfException>(() => DbfMigrator.Migrate(db, _sqlite, SqlDialect.Sqlite, new DbfMigrationOptions { Tables = ["client"] }));
        Assert.Equal(DbfErrorCode.TableAlreadyExists, error.Code);

        DbfMigrator.Migrate(db, _sqlite, SqlDialect.Sqlite, new DbfMigrationOptions { Tables = ["client"], ReplaceExisting = true });
        Assert.Equal(8, Scalar("select count(*) from client"));
    }

    [Fact]
    public void Unknown_tables_are_named()
    {
        using var db = DbfDatabase.Open(_data);
        var error = Assert.Throws<DbfException>(() => DbfMigrator.Migrate(db, _sqlite, SqlDialect.Sqlite, new DbfMigrationOptions { Tables = ["CONTACT1"] }));
        Assert.Contains("CONTACT1", error.Message);
    }

    [Fact]
    public void Dialects_describe_types_their_own_way()
    {
        var name = new DbfField("NAME", 'C', 30, 0, 1);
        var money = new DbfField("AMT", 'N', 12, 2, 31);
        var flag = new DbfField("OK", 'L', 1, 0, 43);

        Assert.Equal("nvarchar(30)", SqlDialect.SqlServer.TypeFor(name));
        Assert.Equal("decimal(12,2)", SqlDialect.PostgreSql.TypeFor(money));
        Assert.Equal("text", SqlDialect.Sqlite.TypeFor(money));
        Assert.Equal("bit", SqlDialect.SqlServer.TypeFor(flag));
        Assert.Equal("\"we\"\"ird\"", SqlDialect.PostgreSql.Quote("we\"ird"));
        Assert.Equal("[we]]ird]", SqlDialect.SqlServer.Quote("we]ird"));
    }

    // ---- bridge -------------------------------------------------------------------------------

    private DbfBridge Bridge(DbfTable table, DbfConflictPolicy policy = DbfConflictPolicy.DbfWins) =>
        new(table, _sqlite, SqlDialect.Sqlite, "CHANGELOG") { ConflictPolicy = policy };

    private DbfTable OpenChangelog() => DbfTable.Open(Path.Combine(_data, "CHANGELOG.DBF"), writable: true);

    [Fact]
    public void First_sync_mirrors_and_second_sync_does_nothing()
    {
        using var table = OpenChangelog();
        var bridge = Bridge(table);

        Assert.Equal(3, bridge.Sync().ToSql);
        Assert.Equal(3, Scalar("select count(*) from CHANGELOG"));
        Assert.Equal(0, bridge.Sync().Changes);
    }

    [Fact]
    public void DBF_changes_reach_SQL()
    {
        using var table = OpenChangelog();
        var bridge = Bridge(table);
        bridge.Sync();

        var values = table.Read(0).Values.ToArray();
        values[Field(table, "USER")] = "from-dbase";
        table.Update(0, values);
        table.Delete(1);

        var result = bridge.Sync();
        Assert.Equal(2, result.ToSql);
        Assert.Equal("from-dbase", Value("select USER from CHANGELOG where _recno = 1"));
        Assert.Equal(1, Scalar("select _deleted from CHANGELOG where _recno = 2"));
    }

    [Fact]
    public void SQL_changes_reach_the_DBF_and_its_index()
    {
        using (var table = OpenChangelog())
        {
            var bridge = Bridge(table);
            bridge.Sync();
            Execute("update CHANGELOG set USER = 'from-web', LEVEL = 7 where _recno = 2");
            Execute("insert into CHANGELOG (ID, USER, ACTION, OBJECTID, LEVEL) values (99, 'new-row', 1, 1, 1)");
            Execute("delete from CHANGELOG where _recno = 3");

            var result = bridge.Sync();
            Assert.Equal(1, result.ToDbf);
            Assert.Equal(1, result.AddedToDbf);
            Assert.Equal(1, result.DeletedInDbf);
            Assert.Equal(0, bridge.Sync().Changes); // settled
        }

        using var reopened = DbfTable.Open(Path.Combine(_data, "CHANGELOG.DBF"));
        using var index = reopened.OpenProductionIndex()!;
        Assert.Equal("from-web", reopened.Read(1)["USER"]);
        Assert.Equal(7m, reopened.Read(1)["LEVEL"]);
        Assert.True(reopened.Read(2).IsDeleted);
        Assert.Equal(4, reopened.Seek(index["ID_TAG"], 99m)!.RecordNumber);   // the new row is indexed
        Assert.Equal(2, reopened.Seek(index["USER_TAG"], "from-web")!.RecordNumber);
        Assert.Equal(4, Scalar("select _recno from CHANGELOG where ID = 99")); // and learned its record number
    }

    [Theory]
    [InlineData(DbfConflictPolicy.DbfWins, "dbf-edit")]
    [InlineData(DbfConflictPolicy.SqlWins, "sql-edit")]
    public void Conflicts_follow_the_policy(DbfConflictPolicy policy, string expected)
    {
        using var table = OpenChangelog();
        var bridge = Bridge(table, policy);
        bridge.Sync();

        var values = table.Read(0).Values.ToArray();
        values[Field(table, "USER")] = "dbf-edit";
        table.Update(0, values);
        Execute("update CHANGELOG set USER = 'sql-edit' where _recno = 1");

        var result = bridge.Sync();
        Assert.Equal([new DbfSyncConflict(1, policy == DbfConflictPolicy.DbfWins ? "DBF" : "SQL")], result.Conflicts);
        Assert.Equal(expected, table.Read(0)["USER"]);
        Assert.Equal(expected, Value("select USER from CHANGELOG where _recno = 1"));
        Assert.Equal(0, bridge.Sync().Changes);
    }

    [Fact]
    public void Changes_made_by_another_program_between_syncs_are_picked_up()
    {
        using (var table = OpenChangelog())
            Bridge(table).Sync();

        using (var legacyApp = OpenChangelog())
            legacyApp.Append(legacyApp.Read(0).Values.Select((v, i) => legacyApp.Fields[i].Name == "ID" ? (object?)500m : v).ToArray());

        using var bridgeTable = OpenChangelog();
        Assert.Equal(1, Bridge(bridgeTable).Sync().ToSql);
        Assert.Equal(1, Scalar("select count(*) from CHANGELOG where ID = 500"));
    }

    [Fact]
    public void A_SQL_delete_doesnt_win_over_a_newer_DBF_edit_when_the_DBF_wins()
    {
        using var table = OpenChangelog();
        var bridge = Bridge(table);
        bridge.Sync();

        Execute("delete from CHANGELOG where _recno = 1");
        var values = table.Read(0).Values.ToArray();
        values[Field(table, "USER")] = "edited";
        table.Update(0, values);

        var result = bridge.Sync();
        Assert.Equal([new DbfSyncConflict(1, "DBF")], result.Conflicts);
        Assert.False(table.Read(0).IsDeleted);
        Assert.Equal("edited", Value("select USER from CHANGELOG where _recno = 1"));
        Assert.Equal(0, bridge.Sync().Changes);
    }

    [Fact]
    public void A_record_recalled_after_its_SQL_row_was_deleted_comes_back_to_SQL()
    {
        using var table = OpenChangelog();
        var bridge = Bridge(table);
        bridge.Sync();
        Execute("delete from CHANGELOG where _recno = 2");
        Assert.Equal(1, bridge.Sync().DeletedInDbf);
        Assert.True(table.Read(1).IsDeleted);

        table.Recall(1); // someone undoes the delete in the legacy app

        bridge.Sync();
        Assert.Equal(1, Scalar("select count(*) from CHANGELOG where _recno = 2 and _deleted = 0"));
        Assert.Equal(0, bridge.Sync().Changes);
    }

    [Fact]
    public void A_retry_after_a_failed_commit_adopts_the_rows_it_already_appended()
    {
        using var table = OpenChangelog();
        var bridge = Bridge(table);
        bridge.Sync();
        Execute("insert into CHANGELOG (ID, USER, ACTION, OBJECTID, LEVEL) values (99, 'new-row', 1, 1, 1)");
        bridge.Sync();
        Assert.Equal(4, table.RecordCount);

        // What a crash between the DBF append and the SQL commit leaves behind.
        Execute("update CHANGELOG set _recno = null where ID = 99");
        Execute("delete from _thunder_sync where recno = 4");

        var retry = bridge.Sync();
        Assert.Equal(1, retry.AddedToDbf);
        Assert.Equal(0, retry.ToSql);
        Assert.Equal(4, table.RecordCount);                        // not appended twice
        Assert.Equal(1, Scalar("select count(*) from CHANGELOG where ID = 99"));
        Assert.Equal(4, Scalar("select _recno from CHANGELOG where ID = 99"));
        Assert.Equal(0, bridge.Sync().Changes);
    }

    [Fact]
    public void A_packed_table_is_refused_instead_of_paired_up_wrongly()
    {
        using var table = OpenChangelog();
        var bridge = Bridge(table);
        bridge.Sync();
        Execute("insert into _thunder_sync (table_name, recno, dbf_print, sql_print) values ('CHANGELOG', 50, 'x', 'x')");

        var error = Assert.Throws<DbfException>(() => bridge.Sync());
        Assert.Contains("packed", error.Message);
    }

    [Fact]
    public void A_field_named_like_a_mirror_column_is_refused()
    {
        // Create won't name a field _RECNO, but files from other programs can, so patch the name in.
        var path = Path.Combine(_data, "clash.dbf");
        DbfTable.Create(path, [DbfFieldDefinition.Character("XRECNO", 5)]).Dispose();
        using (var file = File.OpenWrite(path))
        {
            file.Position = 32; // the first field descriptor starts with its name
            file.WriteByte((byte)'_');
        }

        using var table = DbfTable.Open(path, writable: true);
        var error = Assert.Throws<DbfException>(() => new DbfBridge(table, _sqlite, SqlDialect.Sqlite, "clash"));
        Assert.Contains("_RECNO", error.Message);
    }

    [Fact]
    public void Wide_whole_numbers_stay_exact()
    {
        var wide = new DbfField("BIG", 'N', 20, 0, 1);
        Assert.Equal("text", SqlDialect.Sqlite.TypeFor(wide));
        Assert.Equal("decimal(20,0)", SqlDialect.SqlServer.TypeFor(wide));
        Assert.False(SqlDialect.SqlServer.UniqueAllowsManyNulls); // _recno gets a filtered unique index there
    }

    private static int Field(DbfTable table, string name) =>
        table.Fields.Select((f, i) => (f, i)).Single(x => x.f.Name == name).i;

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
