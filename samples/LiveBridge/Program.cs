//      ______                    ______        ProjectThunder :: Thunder.Dbf sample
//     | .dbf |  <== changes ==> | SQL  |       Keep a live legacy table and a SQL table in step,
//     |______|                  |______|       both ways, while the legacy app keeps running.
//
//   dotnet run --project samples/LiveBridge
//
// In production you'd call bridge.Sync() on a timer (every few seconds or minutes), typically
// inside the Thunder.Dbf server or a Windows service next to the data.

using Microsoft.Data.Sqlite;
using Thunder.Dbf;
using Thunder.Dbf.Sql;

// Work on a copy of a real dBASE IV table (with its memo file and production index).
var work = Directory.CreateTempSubdirectory("thunder-bridge-sample-");
foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT", "CHANGELOG.MDX" })
    File.Copy(Path.Combine(RepoRoot(), "tests", "fixtures", "siemens-step7", "siprom", file), Path.Combine(work.FullName, file));

using var sqlite = new SqliteConnection($"Data Source={Path.Combine(work.FullName, "mirror.db")};Pooling=False");
sqlite.Open();

using (var table = DbfTable.Open(Path.Combine(work.FullName, "CHANGELOG.DBF"), writable: true))
{
    var bridge = new DbfBridge(table, sqlite, SqlDialect.Sqlite, "CHANGELOG");
    Report("First sync mirrors the table", bridge.Sync());

    // The legacy side changes a record...
    var values = table.Read(0).Values.ToArray();
    values[Field(table, "USER")] = "edited-in-dbase";
    table.Update(0, values);

    // ...and a web app changes SQL: an edit, a new row, and a delete.
    Execute(sqlite, "update CHANGELOG set USER = 'edited-on-the-web' where _recno = 2");
    Execute(sqlite, "insert into CHANGELOG (ID, USER, ACTION, OBJECTID, LEVEL) values (99, 'added-on-the-web', 1, 1, 1)");
    Execute(sqlite, "delete from CHANGELOG where _recno = 3");

    Report("Second sync moves every change across", bridge.Sync());
    Report("Third sync has nothing to do", bridge.Sync());

    // The new row reached the DBF and its production index, so dBASE's SEEK finds it.
    using var index = table.OpenProductionIndex()!;
    var found = table.Seek(index["USER_TAG"], "added-on-the-web");
    Console.WriteLine($"SEEK finds the web's new row as DBF record {found?.RecordNumber}");
}

sqlite.Close();
SqliteConnection.ClearAllPools();
work.Delete(recursive: true);

static void Report(string what, DbfSyncResult result) =>
    Console.WriteLine($"{what}: {result.ToSql} to SQL, {result.ToDbf} to DBF, {result.AddedToDbf} added, {result.DeletedInDbf} deleted, {result.Conflicts.Count} conflicts");

static void Execute(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static int Field(DbfTable table, string name) => table.Fields.Select((f, i) => (f, i)).Single(x => x.f.Name == name).i;

static string RepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Thunder.Dbf.slnx")))
            return dir.FullName;
    throw new DirectoryNotFoundException("Run this sample from inside the Thunder.Dbf repository.");
}
