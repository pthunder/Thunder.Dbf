# Thunder.Dbf.Sql

```
    ______        ______
   | .dbf |  ->  | SQL  |      ProjectThunder :: Thunder.Dbf.Sql
   |______|  <-  |______|      Legacy tables in SQLite, SQL Server or PostgreSQL.
```

Copy dBASE and FoxPro tables into a SQL database, then keep a live two-way bridge between each DBF table and its SQL twin until cut-over day. Works through any ADO.NET provider. Built on [Thunder.Dbf](https://www.nuget.org/packages/Thunder.Dbf).

```bash
dotnet add package Thunder.Dbf.Sql
```

```csharp
using Microsoft.Data.Sqlite;
using Thunder.Dbf;
using Thunder.Dbf.Sql;

using var db = DbfDatabase.Open(@"C:\legacy\data");            // only read, never changed
using var sqlite = new SqliteConnection("Data Source=legacy.db");
sqlite.Open();

var result = DbfMigrator.Migrate(db, sqlite, SqlDialect.Sqlite);
Console.WriteLine($"{result.Rows:N0} rows copied in {result.Elapsed.TotalSeconds:0.0} s");
```

For SQL Server or PostgreSQL, use their ADO.NET providers with `SqlDialect.SqlServer` or `SqlDialect.PostgreSql`.

## Live sync

`DbfBridge` moves changes both ways, in one serializable SQL transaction per sync, with a conflict policy for rows changed on both sides:

```csharp
using var table = DbfTable.Open(@"C:\legacy\data\CONTACT1.DBF", writable: true);
var bridge = new DbfBridge(table, sqlite, SqlDialect.Sqlite, "CONTACT1");
var changes = bridge.Sync();   // call it on a timer
```

## Learn more

- [SQL migration and sync guide](https://github.com/pthunder/Thunder.Dbf/blob/main/docs/sql.md)
- [Samples](https://github.com/pthunder/Thunder.Dbf/tree/main/samples) and [all the docs](https://github.com/pthunder/Thunder.Dbf/blob/main/docs/README.md)
- [Source and issues](https://github.com/pthunder/Thunder.Dbf)

MIT licensed. Made by [ProjectThunder](https://projectthunder.com).
