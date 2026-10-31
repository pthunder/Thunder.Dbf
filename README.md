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

[![CI](https://github.com/pthunder/Thunder.Dbf/actions/workflows/ci.yml/badge.svg)](https://github.com/pthunder/Thunder.Dbf/actions/workflows/ci.yml)
[![CodeQL](https://github.com/pthunder/Thunder.Dbf/actions/workflows/codeql.yml/badge.svg)](https://github.com/pthunder/Thunder.Dbf/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![ProjectThunder](https://img.shields.io/badge/by-ProjectThunder-ffd23f.svg)](https://projectthunder.com)

**Put a safe, modern app on top of a legacy dBASE or FoxPro system, while the legacy app keeps running on the same files.**

Thunder.Dbf is a pure .NET library, plus tools, for xBase data: dBASE III and IV, FoxPro, Visual FoxPro and Clipper. It reads and writes tables, memos and indexes exactly as the old apps expect, takes the same locks they do, and never needs ODBC, OLE DB, Jet or a 32-bit process. It runs on Windows, Linux and macOS.

```csharp
using var db = DbfDatabase.Open(@"\\server\goldmine\data");   // read-only unless you say otherwise
Console.Write(db.Check());                                      // what's readable, writable, locked or damaged

var contacts = db["CONTACT1"];
using var index = contacts.OpenProductionIndex()!;
var acme = contacts.Seek(index["CONTCOMP"], "ACME");            // like dBASE's SEEK, through GoldMine's own index
```

## What's in the box

| Package | What it does |
|---|---|
| **Thunder.Dbf** | The engine. Reads and writes tables, `.dbt`/`.fpt` memos and indexes: dBASE IV `.mdx`, dBASE III `.ndx` and Clipper `.ntx` are kept exact on every write, and FoxPro `.cdx` as a preview. Typed records with LINQ, `INDEX ON` and `PACK`. Takes dBASE-compatible locks, evaluates dBASE expressions, imports and exports CSV, and includes an ADO.NET provider. No dependencies. |
| **Thunder.Dbf.Sql** | Migrates tables to SQLite, SQL Server or PostgreSQL, and keeps a live two-way bridge between a DBF table and its SQL twin. |
| **Thunder.Dbf.Server** | A safe JSON web API over the legacy folder: hashed API keys, per-table and per-field permissions, validation, a write journal, backups, rate limits, OpenAPI. |
| **thunder-dbf** | The command line: `check`, `info`, `import`, `export`, `migrate`. |

## Why it's safe to point at live data

- **Read-only by default.** Writes are switched on deliberately: per database, per table, and in the web service per API key.
- **Writes behave like dBASE.** They take the same record and header locks dBASE does and keep every index tag in step. Fields you didn't change keep their exact bytes, and everything is flushed in a crash-safe order.
- **Nothing is half-written.** Every value is checked before anything is written. A character the code page can't hold is an error, never a silent `?`.
- **Errors explain themselves.** Every failure has a code, a hint and an HTTP status, and messages never leak paths or account names.
- **Untrusted input can't escape the folder.** Table names and links are checked, and hostile files can't crash or hang the process.
- **Tested on real files,** not only on files it wrote itself (see below).

## Install

From [nuget.org](https://www.nuget.org/packages/Thunder.Dbf):

```bash
dotnet add package Thunder.Dbf
```

```bash
dotnet add package Thunder.Dbf.Sql
```

```bash
dotnet tool install -g thunder-dbf
```

The web service is a self-contained download for Windows, Linux and macOS on each [GitHub release](https://github.com/pthunder/Thunder.Dbf/releases).

## Quick start

```bash
git clone https://github.com/pthunder/Thunder.Dbf.git
```

```bash
cd Thunder.Dbf && dotnet test Thunder.Dbf.slnx
```

```bash
dotnet run --project tools/Thunder.Dbf.Cli -- check tests/fixtures/siemens-step7/siprom
```

Then work through [Getting started](docs/getting-started.md), or run the [samples](samples):

| Sample | Shows |
|---|---|
| [ReadLegacyData](samples/ReadLegacyData) | Connect, handshake, read, and seek through an `.mdx` index |
| [CsvToDbf](samples/CsvToDbf) | A table's shape from a CSV, no Excel round trip, and back out again |
| [MigrateToSqlite](samples/MigrateToSqlite) | Copy a whole legacy folder into SQLite |
| [LiveBridge](samples/LiveBridge) | Changes flowing both ways between a live DBF and SQL |
| [AdoNet](samples/AdoNet) | Plain ADO.NET over DBF files: parameterized SQL, readers, writes |

## What people build with it

- **Web portals on running CRMs and ERPs** (GoldMine, accounting and inventory systems) without touching the legacy app.
- **Gradual migrations to SQL Server or PostgreSQL**, with a live bridge until cut-over day.
- **Replacing Microsoft's 32-bit Jet and VFP OLE DB drivers** in reports and add-ins.
- **BI dashboards** on nightly copies, **CSV-to-DBF shaping** for legacy imports, and data in **Siemens STEP 7** projects and **GIS shapefiles**.

More in [Use cases](docs/use-cases.md).

## Formats

| | Read | Write |
|---|---|---|
| dBASE III and IV tables, `.dbt` memos | ✅ | ✅ |
| FoxPro 2 and Visual FoxPro tables, `.fpt` memos | ✅ | ✅ |
| Visual FoxPro integer, currency, double, datetime, nulls, autoincrement | ✅ | ✅ |
| dBASE IV `.mdx` indexes | ✅ | ✅, every tag kept exact |
| dBASE III `.ndx`, Clipper `.ntx` indexes | ✅ | ✅, with Clipper's index lock |
| FoxPro / Visual FoxPro `.cdx`/`.idx` indexes | ✅ | preview: opt in with `MaintainFoxProIndexes` (otherwise writes are refused) |
| Code pages: over 60, including DOS 437/850, Windows 125x and Polish Mazovia | ✅ | ✅ |

## Tested on real files

The test suite runs on Windows, Linux and macOS against files written by real software:

- **dBASE III Plus** (running in DOSBox), **dBASE IV**, **FoxPro 2**, **Visual FoxPro**, and **X#** (Clipper-format `.ntx` and FoxPro `.cdx` indexes)
- a **Siemens STEP 7** project, with 20 real `.mdx` indexes
- **US Census Bureau** TIGER data

Every index tag in those files is rebuilt from its own table and compared key for key. The same check passed on real GoldMine CRM data, which isn't included. Each source and its license is listed in [tests/fixtures/SOURCES.md](tests/fixtures/SOURCES.md).

Measured at scale:

| Test | Time | Memory |
|---|---|---|
| Write 1 million rows (88 MB) | 2.2 s | 3 MB |
| Scan 1 million rows | 0.7 s | 7 MB |
| Read across a 1 GB memo file | under 1 s | under 10 MB |
| Import a 77 MB CSV | 4.7 s | 9 MB |

## Documentation

[docs/](docs/README.md) has guides for connecting, reading and writing, indexes, locking, durability, CSV, SQL, the web service, the command line, security and testing, plus byte-level notes on every format.

## Roadmap

Done:

1. ~~DBF records: read, write, append, delete flags, code pages~~
2. ~~Memo files: dBASE III and IV `.dbt`, FoxPro `.fpt`~~
3. ~~dBASE IV `.mdx` indexes: read, seek, ordered scans~~
4. ~~`.mdx` writes that keep every tag in sync~~
5. ~~dBASE-compatible record and file locking~~
6. ~~FoxPro and Clipper formats: `.cdx`, `.ntx` and `.ndx` reading, Visual FoxPro field flags (binary, nullable, autoincrement), two-digit-year headers~~
7. ~~Safe JSON API service~~
8. ~~Live bridge with SQL Server, PostgreSQL or SQLite~~
9. ~~Migrator and ADO.NET provider~~
10. ~~OpenID Connect sign-in for the web service, and an OpenAPI schema per table~~
11. ~~Typed records and a LINQ provider that seeks through `.mdx` tags~~
12. ~~`INDEX ON` (`CreateIndexTag`) and `PACK`~~
13. ~~Keeping `.ndx` and `.ntx` indexes up to date on writes~~
14. ~~0.1.0 on nuget.org: `Thunder.Dbf`, `Thunder.Dbf.Sql` and the `thunder-dbf` tool, plus web service downloads on each GitHub release~~

Next:

- FoxPro `.cdx` writing out of preview: a pass of the [Visual FoxPro round-trip test](docs/testing.md#checking-against-visual-foxpro-itself) (the index lock, large record numbers, batch candidate keys and crash-safe page order are in)
- Creating `.ndx` and `.ntx` indexes (`INDEX ON` writes `.mdx` and `.cdx` tags)
- A journal for `.cdx` page changes, so a crash mid-split always leaves an index Thunder.Dbf can rebuild itself
- Stable keys for the SQL bridge that survive a `PACK` (today a packed table is refused and the mirror is rebuilt)

Ideas and requests are welcome in [issues](https://github.com/pthunder/Thunder.Dbf/issues).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Every change meets the [editorial guidelines](docs/maintainers/editorial-guidelines.md): security first, no real data, tests that can fail, and an ASCII banner on every file. Report security problems privately ([SECURITY.md](SECURITY.md)).

## Credits

The format notes stand on the shoulders of [Xbase64](https://sourceforge.net/projects/xdb/) and [Harbour](https://github.com/harbour/core), from which formats were learned and no code was copied. Test files come from [infused/dbf](https://github.com/infused/dbf), [dbfread](https://github.com/olemb/dbfread), [DbfDataReader](https://github.com/yellowfeather/DbfDataReader), [DotNetSiemensPLCToolBoxLibrary](https://github.com/dotnetprojects/DotNetSiemensPLCToolBoxLibrary), [retroinv](https://github.com/nrnelson/retroinv), [NDbfReaderEx](https://github.com/emelhu/NDbfReaderEx), [X#](https://github.com/X-Sharp/XSharpPublic) and the US Census Bureau.

## About ProjectThunder

Thunder.Dbf is made by [ProjectThunder](https://projectthunder.com). For help putting a modern app on a legacy system, or migrating off one, visit [projectthunder.com](https://projectthunder.com).

## License

[MIT](LICENSE). Test fixtures keep their own licenses (MIT, Apache-2.0, LGPL-2.1 and public domain), as listed in [tests/fixtures/SOURCES.md](tests/fixtures/SOURCES.md). They're test data, not part of the published packages.
