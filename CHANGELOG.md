# Changelog

```
   _____
  | NEW |     ProjectThunder :: Thunder.Dbf
  |_____|     What changed, release by release.
```

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

- `Check()`'s summary says "No problems; N note(s) below." when tables only have notes (such as a missing code page), instead of "All good." above a warning.

### Engine (Thunder.Dbf)

FoxPro `.cdx`/`.idx` maintenance (still the opt-in `MaintainFoxProIndexes` preview) is safer:

- **PACK, rebuilds and INDEX ON need exclusive use,** as they do in dBASE and FoxPro: `Pack()`, `RebuildIndex()`, `CreateIndexTag()` and `MaintainIndex(path, rebuild: true)` throw `ExclusiveUseRequired` unless the table was opened with `DbfTable.Open(path, writable: true, exclusive: true)` (`DbfDatabaseOptions.Exclusive`, `Exclusive=true` in a connection string). An exclusive open fails with `FileInUse` while another program has the table open. Run against a table Visual FoxPro had open shared, a rebuild left VFP reading freed pages (error 2066, "tag is corrupted") and a PACK renumbered records under it (found by the Visual FoxPro tests). **Breaking:** code that packs or rebuilds must open the table exclusively.
- **Creating FoxPro `.cdx` tags:** on a FoxPro or Visual FoxPro table, `CreateIndexTag` adds a tag to the structural `.cdx` (creating it, and flagging it in the table's header, when there's none), like VFP's `INDEX ON ... TAG`, with UNIQUE, CANDIDATE (a new overload), FOR and DESCENDING. Tags are laid out as Visual FoxPro 9 lays them out and hold the keys VFP's own `INDEX ON` gives: every kind of tag in a VFP-built table, made again here, matches VFP's key for key. Needs exclusive use and `maintainFoxProIndexes`.
- **`.cdx` keys over currency, datetime and nullable fields** (a bare field) are kept, built byte for byte as Visual FoxPro 9 builds them; they used to be refused. Leaves are packed as VFP 9 packs them (a shared prefix stops at the previous key's trailing blanks).
- **Crash-safe key changes:** an update, delete or recall now writes the new keys, then the record, then removes the old keys, each on disk before the next, so a writer killed between steps leaves an extra key, never a missing one (UNIQUE tags, where a key belongs to one record, still change after the record). The Visual FoxPro kill tests found 7 in 60 killed updates leaving a record its new key couldn't find. Opt-in **journal**: `UseJournal = true` (`DbfDatabaseOptions.Journal`, `Journal=true`) notes each such key change, and each batch of appends, in `TABLE.DBF.journal` first, and the next write, shared or not, in any Thunder.Dbf process keeping the same indexes, finishes one that was cut off: the index ends up exact, UNIQUE tags included, and an append cut off before its count is taken back off (the Visual FoxPro kill tests found it came back as error 2091 when the next writer wasn't exclusive).
- **Attaching a Clipper `.ntx` while Harbour or Clipper programs write** walks its key order under Clipper's index lock, so a page caught mid-change isn't taken for keys out of order.
- **`FIELD->NAME` in index keys and expressions** (and Harbour's `_FIELD->NAME`) means the table's own field, as in Clipper and Harbour; it was refused as another work area (found with a Harbour-built `.ntx`).
- **A `Jet` lock scheme for tables Access shares:** Microsoft's Jet and ACE read a held dBASE header byte (4,026,531,838) as an exclusive open and fail at once, so with the dBASE scheme every Thunder.Dbf append and update gave Jet users "already opened exclusively" errors (1,580 in 15 seconds with 8 Jet users in the Windows tests). `LockScheme = DbfLockScheme.Jet` (`Lock Scheme=Jet`) makes header changes under the append byte, which Jet itself takes to append, for Access or Jet **reading** while Thunder.Dbf writes. Jet mustn't write while anything else writes, not even another Jet program: it takes no lock on the `.mdx` and tears it (the Windows tests saw it with two Jet processes and nothing else), which the locking docs now say. `Auto` still picks dBASE, which the Borland Database Engine (GoldMine) is assumed to use.
- `LockScheme` can be changed whenever the table holds no lock, not only before its first lock.
- **Interrupted appends are cleaned up:** records left past the header's count by an append that was cut off (Visual FoxPro calls the table corrupted, error 2091) are reported by `Check()` and taken off, with the indexes rebuilt, when the table is opened `writable: true, exclusive: true`. Shared writers leave them, since they can't be told from another program's append in progress. Only bytes that look like whole records are touched, and only when every index next to the table is kept or can be taken over with a rebuild, so no key is left pointing at the next record appended there.
- **The open-time `.cdx` check is safe next to Visual FoxPro:** it walks the tree without VFP's index lock, then confirms, under the table's header lock (which VFP holds through an append) and the index lock, that the update counter and the record count didn't move, and repeats if they did, so another program's append in progress isn't taken for damage. On a table other users never stop writing to, it walks under those locks after two tries, waits at most a quarter of a second for each, and if they're still busy leaves the full check to the first write, which holds them anyway (found in the VFP office runs, where a 7-tag table never held still); an index that still looks out of step gets about a second to settle. It also checks that the last records have their keys in every non-UNIQUE tag that includes them, FOR tags among them.
- **A tag short of the table is caught on open:** a tag with no FOR filter and no UNIQUE must hold one key per record, so a different count (a writer killed between writing a record and its keys) now refuses the index, with the reason in the error's `Diagnostic`, instead of taking it as it is. On a table opened exclusively, `MaintainIndex(path, rebuild: true)` takes over and rebuilds such an index, or one whose keys are otherwise out of step with the table (found by the Visual FoxPro tests: 2 of 20 killed writers left a tag one key short).
- **Tag roots stay put:** a full root's keys move down into new pages and the root is rewritten in place; shrinking and rebuilds also keep the root's address. Visual FoxPro reads each root's address once, when it opens the table, so a moved root left it walking a stale page while Thunder.Dbf wrote (found by the Visual FoxPro round-trip test).

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
