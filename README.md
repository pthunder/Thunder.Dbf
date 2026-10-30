# Thunder.Dbf

```
       .-----.
      /     /      T H U N D E R . D B F
     /     /__     a ProjectThunder joint
    /__      /
      /    /       dBASE  .  FoxPro  .  Clipper
     /   /         ...now on Windows AND Mac
    /  /
   / /
  //
  '
```

A pure C# engine for xBase files (dBASE, FoxPro, Clipper) that runs on Windows, macOS and Linux with no native dependencies.

**The goal:** let people build a safe web app on top of a legacy dBASE or FoxPro system, while the legacy app keeps running on the same files.

## Quick start

```csharp
// Point it at the legacy app's data folder. Read-only unless you say otherwise.
using var db = DbfDatabase.Open(@"\\server\goldmine\data");

Console.Write(db.Check());              // the "Test connection" handshake: headers only, milliseconds

foreach (var row in db["CONTACT1"].ReadAll())
    Console.WriteLine(row["COMPANY"]);
```

Or a connection string: `Data Source=C:\Data;Read Only=false;Code Page=1252`.

Every failure is a `DbfException` with a code, the file involved, a plain-English fix and an HTTP status. That covers access denied, file in use, read-only shares, names or links escaping the folder, encrypted tables, unfinished transactions, missing or damaged memos and indexes, and stale-index writes.

**From the command line** (`dotnet run --project tools/Thunder.Dbf.Cli --`, or install it as the `thunder-dbf` tool):

```bash
thunder-dbf check  \\server\goldmine\data      # what's readable, writable, locked or damaged
thunder-dbf import contacts.csv                 # CSV -> DBF, types and widths worked out for you
thunder-dbf import contacts.csv --plan          # just show the shape it would create
thunder-dbf import contacts.csv --shape-only    # the empty table, no rows
thunder-dbf info   CONTACT1.DBF                 # fields, memo file, index tags
thunder-dbf export CONTACT1.DBF contacts.csv    # back out to CSV
```

**Big files are fine.** Everything streams, so memory stays flat:

| Test | Time | Managed memory |
|---|---|---|
| Write 1M rows (88 MB table) | 2.2 s | 3 MB |
| Scan 1M rows | 0.7 s | 7 MB |
| 1 GB memo file: write, read the last memo, scan all | 2.6 s, instant, 0.9 s | under 10 MB |
| Import a 77 MB, 1.4M-row CSV | 4.7 s | 9 MB |

## Status

Early. Today it can create DBF tables and read, append, update, delete and recall records. It handles character, numeric, float, date, logical and memo-pointer fields, plus Visual FoxPro's integer, currency, double and datetime fields. Text is decoded through the table's code page, or a shapefile-style `.cpg` file next to the table.

Memo fields read and write their text through dBASE III `.dbt`, dBASE IV `.dbt` and FoxPro `.fpt` files. Updates rewrite only the fields that changed, so formatting left by other apps survives.

dBASE IV `.mdx` indexes can be read: every tag, records in key order, and `SEEK`-style lookups by numeric, character or date key. A built-in dBASE expression evaluator handles key and `FOR` expressions such as `UPPER(LTRIM(TRIM(_SKZ)))` and `STR(PUNITTYPE,11,0)+STR(PUNITID,11,0)`. Every tag in 20 real SIMATIC Manager indexes matches keys rebuilt from its table. Tables with a production `.mdx` or `.cdx` refuse writes until index maintenance lands, unless you opt out with `allowUnmaintainedIndexes`.

```csharp
using var table = DbfTable.Open("CHANGELOG.DBF");
using var index = table.OpenProductionIndex()!;

foreach (var record in table.ReadInOrder(index["ID_TAG"]))
    Console.WriteLine(record["ID"]);

var match = table.Seek(index["USER_TAG"], "Calvin");
```

The format notes are in [docs/formats/mdx.md](docs/formats/mdx.md). MDX writes and locking come next.

It's tested against 56 real tables from dBASE III, dBASE IV, FoxPro 2, Visual FoxPro, the US Census Bureau and a Siemens STEP 7 project (see [tests/fixtures/SOURCES.md](tests/fixtures/SOURCES.md)).

```csharp
using var table = DbfTable.Create("people.dbf",
[
    DbfFieldDefinition.Character("NAME", 20),
    DbfFieldDefinition.Numeric("BALANCE", 10, 2),
    DbfFieldDefinition.Date("HIRED"),
]);

table.Append("Smith", 1234.50m, new DateOnly(1997, 4, 12));

foreach (var record in table.ReadAll())
    Console.WriteLine($"{record["NAME"]} owes {record["BALANCE"]}");
```

## Roadmap

1. ~~DBF records: read, write, append, delete flags, code pages~~ done
2. ~~DBT memo files (dBASE III and IV)~~ done, plus FoxPro `.fpt`
3. ~~MDX indexes (dBASE IV): read, seek, ordered scans~~ done
4. MDX writes that keep every tag in sync
5. dBASE-compatible record and file locking
6. FoxPro (`.cdx` / `.fpt`) and Clipper (`.ntx`) formats, including Visual FoxPro field flags (binary, nullable, autoincrement) and its two-digit-year headers
7. Safe JSON API service: the only process that writes the files. It provides:
   - a read-only mode first, then writes enabled per table
   - authentication, plus per-table and per-field permissions
   - schema validation and a journal of every write
   - backups before risky operations, and an OpenAPI description generated from the DBF schema
8. Live bridge inside that service: two-way sync with SQL Server, Postgres or SQLite while the legacy app keeps running
9. Migrator (a one-shot bridge) and an optional ADO.NET provider

Steps 4 and 5 come first because the web layer depends on them: a web app is only safe on top of a legacy system if every write keeps the indexes right and respects the legacy app's locks.

## Build

Needs the .NET 10 SDK.

```bash
dotnet test
```

## License

MIT
