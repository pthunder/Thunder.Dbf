# Use cases

```
   1988 ---------------------------------------------- 2026      ProjectThunder :: Thunder.Dbf
    dBASE IV  FoxPro  Clipper  GoldMine  ...still running        Legacy apps don't retire on
                                                                  schedule. Here's what people
                                                                  build around them.
```

## 1. A safe web portal on a running CRM

*A sales team still runs GoldMine on dBASE files. Management wants a web page where field staff can look up contacts and log calls from their phones.*

- Run the [web service](web-service.md) on the file server, read-only at first.
- An API key for the portal can read `CONTACT1` (but not the notes field) and write only `CONTHIST`.
- Lookups use GoldMine's own indexes (`CONTCOMP`, `CONTCITY`), so they're instant.
- Calls logged from the web take GoldMine's record locks and update its `.mdx`, so GoldMine shows them straight away.

## 2. Migrating a Clipper or dBASE inventory system to SQL Server

*A 1990s inventory app works, but nobody can change it and only one PC still runs it.*

1. `thunder-dbf check` finds the encrypted, damaged and code-page-less tables before anyone else trips over them.
2. `DbfMigrator` copies everything into SQL Server in one pass.
3. While the replacement is built, a [bridge](sql.md) keeps SQL Server in step with the old app every minute, in both directions.
4. On cut-over day, switch the conflict policy to `SqlWins` and retire the old app.

## 3. Replacing the 32-bit Jet and VFP OLE DB drivers

*Reports and Excel add-ins read FoxPro tables through Microsoft's VFP OLE DB provider, which is 32-bit only and stuck on old Windows.*

- Read the same tables natively in 64-bit .NET, on Windows, Linux or macOS, without installing drivers.
- Full-table scans run about 24% faster than DbfDataReader with flat memory, and nothing goes through COM.

## 4. Reporting and BI on legacy data

*Finance wants a Power BI dashboard over a FoxPro accounting system.*

- A nightly `thunder-dbf migrate` into SQLite or PostgreSQL gives the BI tool a database it understands.
- The legacy files are only ever read.

## 5. Shaping tables for legacy imports

*An old app imports `.dbf` files, and someone used to build them by exporting from Excel, which stopped supporting dBASE output years ago.*

- `thunder-dbf import data.csv` gives the right types and widths, keeps ZIP codes as text, and handles accented names (or reports exactly which row a character can't be stored in).
- `--shape-only` gives the empty table structure the old app expects.

## 6. Industrial and engineering data

*Siemens SIMATIC STEP 7 projects, and other engineering tools of their era, store project data as dBASE IV tables with `.mdx` indexes.*

- Inspect, audit or extract that data in modern tooling without the original software.
- Thunder.Dbf's index tests run against real STEP 7 projects.

## 7. GIS attribute tables

*Shapefiles keep their attributes in a `.dbf`, often with a `.cpg` naming the encoding.*

- Read and write attribute tables with the right encoding (the `.cpg` is honored).
- Census Bureau TIGER files are part of the test suite.

## 8. Data recovery and auditing

*A table "won't open" after a crash, or nobody is sure what is in the old system.*

- `Check()` pinpoints damaged memos, missing indexes, unfinished dBASE IV transactions and encrypted tables, each with a fix.
- Reads tolerate the quirks of real files: overflowed numerics (`****`), zero dates, lowercase logicals, truncated memo files.
- `RebuildIndex()` repairs an index that's gone out of step.
