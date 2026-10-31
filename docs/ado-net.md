# ADO.NET provider

```
     _______________
    | SELECT * FROM |      ProjectThunder :: Thunder.Dbf
    |   CONTACT1    |      DbConnection, DbCommand and DbDataReader over dBASE and
    |_______________|      FoxPro files: a drop-in for the old Jet and VFP OLE DB drivers.
```

`Thunder.Dbf.Data` speaks plain ADO.NET, so reports, Dapper, `DataTable.Load` and anything else built on `DbConnection` can read, and carefully write, legacy data. It needs no drivers and no 32-bit process.

```csharp
using Thunder.Dbf.Data;

using var connection = new DbfConnection(@"Data Source=\\server\legacy\data");   // read-only by default
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT COMPANY, CITY FROM CONTACT1 WHERE STATE = @state ORDER BY COMPANY LIMIT 50";
command.Parameters.Add(new DbfParameter("@state", "CA"));

using var reader = command.ExecuteReader();
while (reader.Read())
    Console.WriteLine($"{reader.GetString(0)}, {reader.GetString(1)}");
```

The connection string is the same one `DbfDatabase.Open` takes (see [Connecting](connecting.md)). `DbfProviderFactory.Instance` is available for tools that work by provider name.

## The SQL

A deliberately small dialect:

```
SELECT * | col [AS alias], ... FROM table [WHERE cond] [ORDER BY col [ASC|DESC], ...] [LIMIT n] [OFFSET n]
INSERT INTO table (cols) VALUES (values)
UPDATE table SET col = value, ... WHERE cond
DELETE FROM table WHERE cond                     -- sets the dBASE delete mark

cond    := comparisons (= <> != < <= > >=), [NOT] LIKE, IS [NOT] NULL, AND, OR, NOT, ( )
operand := column | @param | 'text' | number | TRUE | FALSE | NULL
```

- **Deleted records:** left out unless the query mentions `_DELETED`. The pseudo-columns `_RECNO` (the 1-based record number) and `_DELETED` can be selected and filtered on.
- **Comparing text:** `=` ignores trailing spaces, as dBASE does; `LIKE` is case-insensitive.
- **Writes need a `WHERE`:** `UPDATE` and `DELETE` refuse to run without one. Write `WHERE _DELETED = FALSE` if you really mean every live row.
- **Refused by name,** with a hint for each: joins, functions, grouping, subqueries, `IN`, `BETWEEN`, `UNION` and several statements at once. For anything bigger, [migrate to a SQL database](sql.md).

Errors are `DbfException`s: unsupported or malformed SQL is `UnsupportedSql` (HTTP 400), and values that don't fit are `InvalidValue` (422). `DbfException` derives from `IOException`, not `DbException`, because the same exception comes from the whole library; code written against several ADO.NET providers should catch `DbfException` as well as `DbException`.

### Limits

SQL often arrives from a web form, so a statement that's expensive to parse or run is stopped early:

| Limit | Value | When it's hit |
|---|---|---|
| Statement length | 64 KB | `UnsupportedSql` before parsing |
| Words and symbols | 10,000 | `UnsupportedSql` before parsing |
| Nesting of `NOT` and parentheses | 64 levels | `UnsupportedSql` |
| `ORDER BY` without a small `LIMIT` | 256 MB of sort keys | `QueryTooLarge` (HTTP 413) |
| `CommandTimeout` | 30 seconds (0 = none) | `Timeout` (HTTP 504); a reader's clock runs until it's closed |

`ORDER BY` with `LIMIT` (plus `OFFSET`) up to 100,000 rows keeps only that many sort keys, however big the table. `command.Cancel()`, from any thread, stops a running command or its reader at the next record with an `OperationCanceledException`. `ExecuteReader(CommandBehavior.SchemaOnly)` never changes anything, even for an `INSERT`, `UPDATE` or `DELETE`.

## Indexes do the work

An equality on an indexed field is answered from the production `.mdx` instead of a scan. `command.Explain()` shows the plan:

```
seek CONTACT1 index tag CONTZIP for ZIP = '94105'
read record 4 of CONTACT1                         (WHERE _RECNO = 4)
scan CONTACT1 (no index tag covers ...)
```

Candidates from an index are re-checked against the whole `WHERE`, so using an index can only skip records, never change the answer. Tests compare every index-assisted query with a forced scan.

## Writes

A writable connection (`Read Only=false`) runs `INSERT`, `UPDATE` and `DELETE` through `DbfTable`. So writes take dBASE locks, keep the production `.mdx` exact, and validate every value before anything is written. `UPDATE` writes only the columns in its `SET`, under the record lock, onto the record as it is on disk at that moment: a change the legacy app made to another field after the `WHERE` found the record survives. There are no transactions: a multi-row `UPDATE` isn't atomic, but every value is checked before the first record changes.
