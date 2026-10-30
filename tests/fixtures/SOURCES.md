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
| `census-tiger/` | US Census Bureau [TIGER/Line 2024 Places, District of Columbia](https://www2.census.gov/geo/tiger/TIGER2024/PLACE/tl_2024_11_place.zip) (`.dbf` and `.cpg` only) | 2024 release | US government work, public domain |

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

**Still missing:** dBASE IV `.mdx` and `.ndx` index files and Clipper `.ntx` files. No public ones turned up; the plan is to generate them with Xbase64 and, ideally, real dBASE IV.
