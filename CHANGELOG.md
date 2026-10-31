# Changelog

```
   _____
  | NEW |     ProjectThunder :: Thunder.Dbf
  |_____|     What changed, release by release.
```

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Engine (Thunder.Dbf)

FoxPro `.cdx`/`.idx` maintenance (still the opt-in `MaintainFoxProIndexes` preview) is safer:

- **Visual FoxPro's index lock:** every change, and every reload of the tag list, runs under the lock Visual FoxPro takes on the index file itself (one byte at 0x7FFFFFFE), following `LockTimeout` and `RequireLocks`; pages are re-read under it and the update counter is bumped before it's released.
- **Crash-safe page order:** splits write the new page before the page that links it in, emptied pages go on the free list only once nothing points at them, and a rebuild builds a whole new tree and switches to it in one header write. A crash can leave keys listed twice or unused pages, never a key missing.
- **Visual FoxPro's UNIQUE tags** change the way Visual FoxPro changes them between rebuilds: the first record with a key keeps it, and nothing is promoted when it leaves. A rebuild still picks the lowest record.
- **Candidate keys** are checked against every key an `AppendRange` batch has planned, so two equal keys in one batch are refused; a rebuild checks its keys among themselves.
- **Keys built byte for byte:** character keys come from the stored field bytes (a field holding `A\0` keys as FoxPro keys it), and leaves pack record numbers past 67,108,863 the way Harbour and FoxPro do, for both reading and writing.
- **Refused rather than guessed:** `.cdx` files whose pages have two owners or one-way sibling links (as damaged); numeric keys over more than a bare field; character keys built with functions other than `UPPER`, `DTOS` and `STR` of fields; `UPPER` and text FOR filters in double-byte code pages.
- **Takeovers:** `MaintainIndex(path, rebuild: true)` takes over a tag out of byte order anywhere (the root included), rebuilds an index that's already kept, and lets the index go again if the rebuild fails.
- **FoxPro 2 detection:** a version-0x03 table with the index flag, no `.mdx` and a same-named `.cdx` is only taken for FoxPro 2 when the `.cdx` opens and its tags name only the table's fields; otherwise it stays dBASE IV and the missing `.mdx` keeps blocking writes.

## [0.1.0] - 2026-10-31

The first public release.

### Engine (Thunder.Dbf)

- **Tables:** read and write dBASE III, dBASE IV, FoxPro 2 and Visual FoxPro tables, including Visual FoxPro integer, currency, double and datetime fields, null flags and autoincrement counters.
- **Memos:** dBASE III and IV `.dbt` and FoxPro `.fpt` files, preserving dBASE IV free-block chains and FoxPro block types.
- **dBASE IV `.mdx` indexes:** read and seek, and kept exact on every append, update, delete and recall (unique tags, `FOR` filters, node splits, free pages), plus `RebuildIndex()`.
- **Other indexes:**
  - Read and seek dBASE III `.ndx`, Clipper `.ntx` and FoxPro `.cdx`/`.idx`, all through `IDbfIndexTag`.
  - Keep `.ndx` and `.ntx` up to date on every write, `.ntx` under Clipper's own index lock.
  - FoxPro `.cdx`/`.idx` maintenance is an opt-in preview (`MaintainFoxProIndexes`).
- **Which indexes are kept:**
  - An index is kept only when it is surely the table's: the header names it, or it's named after the table, or `MaintainIndex(path)` names it.
  - National sort orders, Clipper custom/changes-only indexes and over-wide numeric keys are refused rather than guessed at.
  - After a failed index update, writes stop until `RebuildIndex()`.
- **FoxPro 2 tables** that share dBASE's version byte are recognized by their `.cdx`.
- **Expressions:** a dBASE evaluator for key and `FOR` expressions (`UPPER`, `SUBSTR`, `DTOS`, `STR`, `VAL`, `IIF`, `DELETED()` and more), with three-valued `.NULL.` logic.
- **Locking:** record, header, append and table locks at dBASE, Clipper or Visual FoxPro offsets (`LockScheme`), shared across every table opened in one process; `LockRecord`/`LockTable`; `Refresh()` to see other programs' changes.
- **`UpdateFields`:** changes only the named fields, re-reading the record under its lock, so concurrent edits to other fields survive.
- **Typed records and LINQ:**
  - `ReadAll<T>`, `Read<T>`, `AppendRecord` and `UpdateRecord` map records to plain C# classes.
  - `AsQueryable<T>()` turns an equality on an indexed field into a seek, but only when the seek must give the same answer as a scan.
  - `Explain()` shows which plan a query uses.
- **`INDEX ON` and `PACK`:**
  - `CreateIndexTag` adds `.mdx` tags, creating the file when needed, laid out as dBASE software writes them.
  - `Pack` removes deleted records and rebuilds every index, keeping a safety copy until it's done.
- **Connecting:** one-line `DbfDatabase.Open` from a folder or a connection string, read-only by default; a `Check()` handshake; links and path tricks refused.
- **Errors:** `DbfException` with codes, safe messages, hints, HTTP statuses, and diagnostics kept separate for logs.
- **CSV:** import with the column shape worked out from the data, streamed in two passes; spreadsheet-safe export.
- **ADO.NET provider:** `DbfConnection`, `DbfCommand`, `DbfDataReader`, with index-assisted lookups, `CommandTimeout` and `Cancel()`, top-N sorts, and limits on statement size and nesting.
- **Code pages:** over 60, including Mazovia.
- **Durability:** crash-safe commit order, and an `AppendRange` bulk load.

### SQL (Thunder.Dbf.Sql)

- `DbfMigrator`: copies tables to SQLite, SQL Server or PostgreSQL.
- `DbfBridge`: live two-way sync with conflict policies, in one serializable SQL transaction. Deletes follow the conflict policy, a retry after a failed commit doesn't append rows twice, and a packed table is refused rather than paired up wrongly.

### Web service (Thunder.Dbf.Server)

- A JSON API with hashed API keys, per-table and per-field permissions, schema validation, a two-line (intent, then done) write journal, consistent backups taken under the table lock, per-address and per-key rate limits, response-size budgets and OpenAPI.
- OpenID Connect sign-in beside API keys: bearer tokens from Entra ID, Auth0, Keycloak or any provider, checked by JwtBearer against the provider's keys and the configured audience. Roles, groups or any claim map, first match wins, to access profiles shaped like an API key's permissions; a token that maps to none gets 403. Users show up as `oidc:<sub>` in the journal, logs and rate limits. Sending a key and a token together is a 400.
- An OpenAPI schema per table: `GET /tables/{name}/openapi` and `GET /openapi/tables.json` add a `Table_<NAME>` component schema, typed from the DBF header, for each table the caller may read, without hidden fields. `/openapi/v1.json` stays generic and names no tables.

### Tools

- `thunder-dbf` command line: `check`, `info`, `import`, `export`, `migrate`.
- Samples, documentation, and a publish gate (editorial check, history check, CI on three operating systems, CodeQL, Dependabot, weekly maintenance).

### Packages and releases

- NuGet packages `Thunder.Dbf` and `Thunder.Dbf.Sql`, with XML docs for every public member, Source Link, deterministic builds and `.snupkg` symbols; `thunder-dbf` as a .NET global tool (`dotnet tool install -g thunder-dbf`).
- The web service as self-contained zips for Windows (x64), Linux (x64) and macOS (Apple silicon) on each GitHub release.
- A release workflow triggered by `vX.Y.Z` tags: checks the tag against the version and changelog, tests, packs, publishes to nuget.org with trusted publishing (no stored API key) and creates the GitHub release with checksums. CI packs and checks the packages on every pull request.
