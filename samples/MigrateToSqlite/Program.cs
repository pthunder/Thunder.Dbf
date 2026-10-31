//      .dbf .dbf .dbf                 ProjectThunder :: Thunder.Dbf sample
//        \   |   /                    Copy a whole legacy folder into SQLite in one shot.
//         \  |  /                     The legacy files are only read, never changed.
//        [ SQLite ]
//
//   dotnet run --project samples/MigrateToSqlite -- [folder] [output.db]
//
// The same code works for SQL Server or PostgreSQL: swap the connection (Microsoft.Data.SqlClient,
// Npgsql) and pass SqlDialect.SqlServer or SqlDialect.PostgreSql.

using Microsoft.Data.Sqlite;
using Thunder.Dbf;
using Thunder.Dbf.Sql;

var folder = args.Length > 0 ? args[0] : Path.Combine(RepoRoot(), "tests", "fixtures", "dbfdatareader");
var output = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "thunder-migrated.db");
File.Delete(output);

using var db = DbfDatabase.Open(folder);
using var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = output }.ToString());
sqlite.Open();

var result = DbfMigrator.Migrate(db, sqlite, SqlDialect.Sqlite, new DbfMigrationOptions { TablePrefix = "legacy_" });
foreach (var table in result.Tables)
    Console.WriteLine($"{table.Table,-24} -> {table.SqlTable,-30} {table.Rows,8:N0} rows");
Console.WriteLine($"{result.Rows:N0} rows in {result.Elapsed.TotalSeconds:0.00} s, into {output}");

// Every SQL table has the DBF fields plus _id, _recno (the DBF record number) and _deleted,
// so it can be kept in step later with DbfBridge.
using var query = sqlite.CreateCommand();
query.CommandText = "select CLIENT, CITY, STATE from legacy_client where _deleted = 0 order by CLIENT limit 3";
using var reader = query.ExecuteReader();
while (reader.Read())
    Console.WriteLine($"  {reader.GetString(0),-28} {reader.GetString(1)}, {reader.GetString(2)}");

static string RepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Thunder.Dbf.slnx")))
            return dir.FullName;
    throw new DirectoryNotFoundException("Run this sample from inside the Thunder.Dbf repository, or pass a folder.");
}
