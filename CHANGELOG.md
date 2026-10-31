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
- **Other indexes:** read and seek dBASE III `.ndx`, Clipper `.ntx` and FoxPro `.cdx`/`.idx`, all through `IDbfIndexTag`.
- **Expressions:** a dBASE evaluator for key and `FOR` expressions (`UPPER`, `SUBSTR`, `DTOS`, `STR`, `VAL`, `IIF`, `DELETED()` and more), with three-valued `.NULL.` logic.
- **Locking:** record, header, append and table locks at dBASE, Clipper or Visual FoxPro offsets (`LockScheme`), shared across every table opened in one process; `LockRecord`/`LockTable`; `Refresh()` to see other programs' changes.
- **`UpdateFields`:** changes only the named fields, re-reading the record under its lock, so concurrent edits to other fields survive.
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

### Tools

- `thunder-dbf` command line: `check`, `info`, `import`, `export`, `migrate`.
- Samples, documentation, and a publish gate (editorial check, history check, CI on three operating systems, CodeQL, Dependabot, weekly maintenance).

### Packages and releases

- NuGet packages `Thunder.Dbf` and `Thunder.Dbf.Sql`, with XML docs for every public member, Source Link, deterministic builds and `.snupkg` symbols; `thunder-dbf` as a .NET global tool (`dotnet tool install -g thunder-dbf`).
- The web service as self-contained zips for Windows (x64), Linux (x64) and macOS (Apple silicon) on each GitHub release.
- A release workflow triggered by `vX.Y.Z` tags: checks the tag against the version and changelog, tests, packs, publishes to nuget.org with trusted publishing (no stored API key) and creates the GitHub release with checksums. CI packs and checks the packages on every pull request.
