# Changelog

```
   _____
  | NEW |     ProjectThunder :: Thunder.Dbf
  |_____|     What changed, release by release.
```

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0] - Unreleased

The first public release.

### Engine (Thunder.Dbf)

- **Tables:** read and write dBASE III, dBASE IV, FoxPro 2 and Visual FoxPro tables, including Visual FoxPro integer, currency, double and datetime fields, null flags and autoincrement counters.
- **Memos:** dBASE III and IV `.dbt` and FoxPro `.fpt` files, preserving dBASE IV free-block chains and FoxPro block types.
- **dBASE IV `.mdx` indexes:** read and seek, and kept exact on every append, update, delete and recall (unique tags, `FOR` filters, node splits, free pages), plus `RebuildIndex()`.
- **Other indexes:**
  - Read and seek dBASE III `.ndx`, Clipper `.ntx` and FoxPro `.cdx`/`.idx`, all through `IDbfIndexTag`.
  - Keep `.ndx` and `.ntx` up to date on every write, `.ntx` under Clipper's own index lock.
  - FoxPro `.cdx`/`.idx` maintenance is an opt-in preview (`MaintainFoxProIndexes`). It runs under Visual FoxPro's own index lock (one byte at 0x7FFFFFFE of the index file), writes splits, removals and rebuilds in an order a crash can't turn into lost keys, keeps UNIQUE tags the way Visual FoxPro does between rebuilds, checks candidate keys against a whole `AppendRange` batch, builds character keys from the stored bytes, and handles record numbers past 67,108,863.
  - It refuses `.cdx` files whose pages have two owners (two tags, or a tag and the free list) or one-way sibling links, numeric keys over more than a bare field (FoxPro computes those in binary floating point), and character keys built with functions other than `UPPER`, `DTOS` and `STR` of fields (or `UPPER` in a double-byte code page).
  - `MaintainIndex(path, rebuild: true)` rebuilds an index that's already kept, takes over one whose root or interior pages are out of byte order, and lets the index go again if the rebuild fails.
- **Which indexes are kept:**
  - An index is kept only when it is surely the table's: the header names it, or it's named after the table, or `MaintainIndex(path)` names it.
  - National sort orders, Clipper custom/changes-only indexes and over-wide numeric keys are refused rather than guessed at.
  - After a failed index update, writes stop until `RebuildIndex()`.
- **FoxPro 2 tables** that share dBASE's version byte are recognized by their `.cdx`, when it opens and its tags name only the table's fields; otherwise the table stays dBASE IV, with its missing `.mdx` still blocking writes.
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
