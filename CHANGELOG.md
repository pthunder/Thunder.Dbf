# Changelog

```
   _____
  | NEW |     ProjectThunder :: Thunder.Dbf
  |_____|     What changed, release by release.
```

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

- **`UPPER()` in `.cdx` keys over letters beyond ASCII,** in code pages 852, 1250 and 1251 (Central European and Cyrillic), where case-insensitive name tags are common: upper-cased as Visual FoxPro 9 does, which was checked for every byte against VFP running in each code page (.NET's capital when the code page has it, the byte kept when it doesn't, as for µ). Other code pages keep refusing such keys until they're checked the same way.
- **A deferred batch's pages and records are forced to disk before its journal note is emptied,** whatever `FlushWritesToDisk` says (one flush per batch). With flushes off, a power cut could keep the emptied note and lose the batch (3 of 50 Hyper-V power cuts; Thunder.Dbf still refused all three tables, by their key counts).
- **`RebuildIndex()` takes over a structural `.cdx` that was refused when the table opened** (keys out of step with the table, after a batch was cut off, say) and rebuilds it with the rest, as `MaintainIndex(path, rebuild: true)` does; it used to say "no index open" (found by the Visual FoxPro kill tests).
- **Fuzzed:** `tools/Thunder.Dbf.Fuzz` throws corrupted copies of the test files at every reader and writer. 100,000 cases found no crash, hang or runaway allocation, and six places where a damaged file gave a raw .NET exception instead of an explained one, all fixed with tests (`FuzzRegressionTests`): a table cut short (`EndOfStreamException` while reading, now `NotADbfFile`); a header counting more than 2^31 records (`OverflowException`); an index entry pointing past the table's end in `Seek` and `ReadInOrder` (`ArgumentOutOfRangeException`, now `IndexDamaged`); an index key expression that doesn't parse or fit the table, met while writing (now `IndexDamaged`); a numeric `.mdx` key whose digits aren't BCD; and the autoincrement flag on a field that isn't Integer, which Visual FoxPro never sets (now ignored).
- **Faster deferred batches:** inside a `DeferIndexWrites` batch the `.cdx` node cache shares its nodes instead of copying them in and out (the copies were most of a 7-tag batch's time on Windows), and each record's planned key changes are sized up front.
- **`DeferIndexWrites`, for migrations and bulk loads:** with it on, a batch under `LockTable()` keeps its `.cdx` changes in memory and writes each changed page once, when the lock is disposed. It holds the index lock meanwhile (Visual FoxPro users wait), and the batch is noted in the table's journal until its pages are written, so a batch cut off (killed, or a failed write) stops Thunder.Dbf writes until `RebuildIndex()`, updates included. Off by default; ignored while `UseJournal` is on. `docs/durability.md` has a table of what each setting trades.
- **Faster `.cdx` writes on Windows:** a writable table watches its folder for new files instead of checking the folder before every write (other systems, and a watcher that fails, keep the folder-time check). Notice of a new file comes a few milliseconds after it appears, so a write within those milliseconds doesn't see it; the next one does.
- **A FoxPro tag whose keys would be longer than 240 bytes is refused** with `InvalidValue` before anything is written. It used to fail with an `InvalidDataException` from inside the tree code (found by the Visual FoxPro stress tests; nothing was damaged).
- **Faster `.cdx` writes, again:** while a `.cdx` is exactly as this process last left it (same length and update counter), its tree pages come from memory instead of the file, and the `.cdx` is read and written unbuffered, so a page read no longer pushes out buffered writes first. Another program's change (Visual FoxPro's own, included), a failed change or a reload empties the cache. On a Mac with flushes off, appends to a 7-tag table went from about 10,000 a second to 14,500.
- **Faster writes.** `FlushWritesToDisk` (on by default; `DbfDatabaseOptions.FlushWritesToDisk`, `Flush Writes To Disk=false`) turns off the forced disk flushes behind every write, keeping their order, so a killed process is still covered and only a power cut or operating-system crash can lose the latest writes (as with Visual FoxPro). PACK always forces its writes, and so does every write while the journal is on. Whatever the setting, a `.cdx` that's exactly as this process left it (same length and update counter) is no longer reloaded and re-parsed before every write, one record's changes to a `.cdx` share one index lock, and the folder isn't listed for new index files, an unfinished PACK or a journal on every write, only when the folder changes (or once a second; a table keeping a journal looks every time). On a Mac with flushes off, 2,000 appends went from about 5,500 a second to 14,500 (a 2-tag table) and from 5,000 to 10,000 (7 tags).
- **Records an interrupted append left past the count are kept, not just removed:** before a writable exclusive open takes them off, their bytes go to `TABLE.DBF.cutoff-<UTC time>` beside the table (a new name each time; delete it once nothing in it is wanted), and `CutOffRecordsRemoved` and `CutOffBackupPath` report it (review of #13).
- **The journal holds keys up to 256 bytes** (Clipper's `.ntx` limit; it stopped at 240, so a cut-off write on a table with longer `.ntx` keys left a note that stopped writes), and refuses a longer key when it writes the note (review of #17).
- **The journal reader checks every count and length** against the note it reads; a whole note that doesn't make sense (damaged, or not written by Thunder.Dbf) stops writes with an explanation until a rebuild, instead of failing with an out-of-range error. Index and tag names a note holds go in the error's `Diagnostic`, not its message (review of #14).
- Lock calls that need a shorter wait (the open-time `.cdx` check, finishing a journal note) pass it per call instead of changing the table's `LockTimeout` for a moment.
- **Behavior change:** `DbfTable.Create` holds the new `.dbf` unshared while the table is open (other programs can't open it until it's closed), so `INDEX ON` and `PACK` can follow straight away.
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
