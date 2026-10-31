# SQL: migrate and bridge

```
     ______                    ______        ProjectThunder :: Thunder.Dbf.Sql
    | .dbf |  <== changes ==> | SQL  |       Copy legacy tables into SQLite, SQL Server or
    |______|                  |______|       PostgreSQL once, or keep them in step both ways.
```

`Thunder.Dbf.Sql` works through any ADO.NET provider (Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, Npgsql) and adds no dependencies of its own. The core library stays dependency-free.

## The mirror table

Each DBF table becomes a SQL table with the same fields, plus three columns:

| Column | Meaning |
|---|---|
| `_id` | the SQL primary key (identity) |
| `_recno` | the DBF record number it mirrors; NULL for a new SQL row not yet in the DBF |
| `_deleted` | the dBASE delete mark |

The types used:

| DBF | SQLite | SQL Server | PostgreSQL |
|---|---|---|---|
| `C(n)` | varchar(n) | nvarchar(n) | varchar(n) |
| `N(n,0)`, up to 18 digits | bigint | bigint | bigint |
| `N(n,d)` | text (exact) | decimal(n,d) | decimal(n,d) |
| `D` | text (`yyyy-MM-dd`) | date | date |
| `L` | integer | bit | boolean |
| `M` | text | nvarchar(max) | text |
| binary memo | blob | varbinary(max) | bytea |

SQLite has no exact decimal or date type, so those travel as text and nothing is rounded through a double.

## Migrate: one shot

```csharp
using var db = DbfDatabase.Open(@"\\server\legacy\data");
using var sql = new SqliteConnection("Data Source=legacy.db");
sql.Open();

var result = DbfMigrator.Migrate(db, sql, SqlDialect.Sqlite, new DbfMigrationOptions
{
    Tables = ["CONTACT1", "CONTACT2"],   // or null for every table
    TablePrefix = "legacy_",
    IncludeDeleted = false,
});
```

```bash
thunder-dbf migrate \\server\legacy\data legacy.db
```

The legacy files are only read. Rows go in batched transactions (5,000 by default). An existing SQL table is an error unless `ReplaceExisting` is set.

## Bridge: live, both ways

```csharp
using var table = db.OpenTable("CONTACT1");               // a writable DbfDatabase
var bridge = new DbfBridge(table, sql, SqlDialect.SqlServer, "CONTACT1")
{
    ConflictPolicy = DbfConflictPolicy.DbfWins,
};

while (!stopping)
{
    var result = bridge.Sync();          // moves only what changed, either way
    await Task.Delay(TimeSpan.FromSeconds(30));
}
```

A `_thunder_sync` table remembers each row's fingerprint on both sides at the last sync. Each sync then:

| Since the last sync | Sync does |
|---|---|
| Only the DBF record changed | copies it to SQL |
| Only the SQL row changed | writes it to the DBF (locked, index maintained) and copies the DBF's stored form back, so both agree |
| Both changed | `ConflictPolicy` picks the winner, and the result lists the conflict |
| New SQL row (`_recno` NULL) | appended to the DBF; the row learns its record number |
| SQL row deleted | the DBF record gets the dBASE delete mark |
| DBF record deleted or recalled | `_deleted` changes in SQL |
| Nothing | nothing is written |

Writes into the DBF go through `DbfTable`, so they take the legacy app's locks, keep the production `.mdx` exact, and leave untouched fields alone. The `LiveBridge` sample shows a full round trip.

**Choosing the policy.** `DbfWins` (the default) treats the legacy system as the system of record, which suits a migration where the old app is still in charge. Switch to `SqlWins` once the new app takes over.
