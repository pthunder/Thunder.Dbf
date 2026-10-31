# Test fixtures

```
   ___________________
  |  ___   ___   ___  |    Real files from real apps,
  | |dbf| |fpt| |cdx| |    collected so we never have to
  | |___| |___| |___| |    trust a file we made ourselves.
  |___________________|
```

Every file here was downloaded unchanged. Each folder keeps its source project's license.

| Folder | Source | Pinned at | License |
|---|---|---|---|
| `infused-dbf/` | [infused/dbf](https://github.com/infused/dbf) `spec/fixtures/` (Ruby DBF gem) | `cc0b139d` | MIT (see `infused-dbf/LICENSE`) |
| `dbfread/` | [olemb/dbfread](https://github.com/olemb/dbfread) `tests/cases/`, `examples/files/` (Python) | `dce54464` | MIT (see `dbfread/LICENSE`) |
| `dbfdatareader/` | [yellowfeather/DbfDataReader](https://github.com/yellowfeather/DbfDataReader) `test/fixtures/` (.NET) | `4f6ac3e2` | MIT (see `dbfdatareader/LICENSE`) |
| `siemens-step7/` | The `.DBF`, `.DBT` and `.MDX` files from `ToolBoxLibUnitTests/S7Blocks/UnitTest Simatic Project.zip` in [dotnetprojects/DotNetSiemensPLCToolBoxLibrary](https://github.com/dotnetprojects/DotNetSiemensPLCToolBoxLibrary), a Siemens STEP 7 project saved by SIMATIC Manager (folder layout kept) | `734b1413` | LGPL-2.1 (see `siemens-step7/LICENSE`) |
| `census-tiger/` | US Census Bureau [TIGER/Line 2024 Places, District of Columbia](https://www2.census.gov/geo/tiger/TIGER2024/PLACE/tl_2024_11_place.zip) (`.dbf` and `.cpg` only) | 2024 release | US government work, public domain |
| `retroinv/` | The dBASE III Plus reference files in `TEST/GOLDEN/DB3/` of [nrnelson/retroinv](https://github.com/nrnelson/retroinv): tables, a memo file and eight `.NDX` indexes written by dBASE III Plus 1.1 itself, running in DOSBox-X (their `README.md` says how each was made) | `0e55bde0` | MIT (see `retroinv/LICENSE`) |
| `ndbfreaderex/` | `NDbfReaderEx_Test/test_ix.DBF` and `test_ix.NDX` from [emelhu/NDbfReaderEx](https://github.com/emelhu/NDbfReaderEx) (.NET) | `3f30544c` | MIT (see `ndbfreaderex/LICENSE.md`) |
| `vfp9/` | `kinds.DBF`, `.CDX` and `.FPT`: 200 made-up records with one `.cdx` tag of every kind (character, `UPPER()`, DESCENDING, numeric, currency, double, date, datetime, logical, joined keys, UNIQUE, FOR, nullable, GENERAL collation, CANDIDATE), written by Visual FoxPro 9 SP2 (09.00.0000.5815) on Windows 11 by `kinds.prg`, which is kept beside them. Made for this project by ProjectThunder; SHA-256 of the `.DBF` `ace7d64f…bf127f0`, `.CDX` `596af258…6c03a6f4` | 2026-10-31 | MIT, as the project |
| `xsharp/` | Clipper-format `.ntx` indexes, a 10,000-record table and its `.cdx` from [X-Sharp/XSharpPublic](https://github.com/X-Sharp/XSharpPublic): `rdd-tests/` is `src/Runtime/XSharp.Rdd.Tests/` (and its `dbfs/`), `explorer/` is `src/Tests/TypedSDK/Explorer/`. `10kName.xxx` is an `.ntx` under another extension, kept as named | `7af480f0` | Apache-2.0 (see `xsharp/License.txt`, the license of `src/Runtime` and `src/`) |

## What's covered

| Version byte | Dialect | Files |
|---|---|---|
| `0x02` | dBASE II (refused on purpose) | `infused-dbf/dbase_02.dbf` |
| `0x03` | dBASE III / IV, no memo | `dbase_03*.dbf`, `polygon.dbf` (zero fields), `people.dbf`, `MS__KHDM.DBF`, Census TIGER |
| `0x83` | dBASE III with `.dbt` memo | `dbase_83.dbf` + `.dbt`, `INNAKLKVT20180904.dbf` + `.dbt` |
| `0x8B` | dBASE IV with `.dbt` memo | `dbase_8b.dbf` + `.dbt`, `client.dbf` + `.dbt` |
| `0x8C` | dBASE 7 (refused on purpose) | `infused-dbf/dbase_8c.dbf` |
| `0xF5` | FoxPro 2 with `.fpt` memo | `dbase_f5.dbf` + `.fpt` |
| `0x30` | Visual FoxPro | `dbase_30.dbf` + `.fpt`, `foxprodb/*` (with `.cdx` indexes), `cp1251.dbf`, `mazovia.dbf`, `memotest.dbf` + `.FPT`, `foxpro_currency_01.dbf` |
| `0x31` | Visual FoxPro with autoincrement | `dbase_31.dbf`, `FolderRoot.dbf` |
| `0x32` | Visual FoxPro with varchar/varbinary | `dbase_32.dbf` |

The Siemens project adds 28 more `0x03` and `0x8B` tables and **20 real dBASE IV `.MDX` production indexes**. SIMATIC Manager stores its projects as dBASE IV files.

The index readers are checked against:

| Format | Files |
|---|---|
| dBASE IV `.mdx` | the 20 Siemens production indexes |
| dBASE III `.ndx` | `retroinv/*.NDX` (one leaf; three levels with leaves at two depths; 100-byte keys; pages left behind by deletes; date keys), `ndbfreaderex/test_ix.NDX` |
| Clipper `.ntx` | `xsharp/rdd-tests/*.ntx` and `10kName.xxx` (10,000 keys: character, numeric with negative values, date), `xsharp/explorer/*.ntx` |
| FoxPro `.cdx` | `infused-dbf/foxprodb/*.CDX` (Visual FoxPro, with 4-byte integer keys), `xsharp/rdd-tests/TEST10K.CDX` (seven tags over 10,000 records) |

Two of them disagree with their own tables, and the tests say so: `xsharp/explorer/Orders.dbf` gained two blank records after its indexes were built, and `xsharp/rdd-tests/10kState.ntx` was built when every state in its table was `IL`.

**Still missing:** a real compact `.idx`, and real descending `.ntx` and `.cdx` tags; those are only tested with hand-built files.
