# Connecting

```
   ____________________________
  | \\server\goldmine\data     |     ProjectThunder :: Thunder.Dbf
  |____________________________|     Point it at the folder. Tables, memos, indexes and
     |   |   |   |   |   |           code pages are found and paired up for you.
```

## Opening a folder

```csharp
using var db = DbfDatabase.Open(@"C:\Legacy\Data");                         // read-only
using var db = DbfDatabase.Open(folder, new DbfDatabaseOptions { ReadOnly = false });
```

Or a connection string:

```
Data Source=\\server\share\data;Read Only=false;Code Page=1252
```

| Setting | Default | Meaning |
|---|---|---|
| `Data Source` (or `Folder`, `Path`) | required | The data folder |
| `Read Only` / `Mode=ReadWrite` | `true` | Writes are refused unless this is off |
| `Code Page` (or `Encoding`) | from each table | Text encoding for every table: `1252`, `437`, `850`, `utf-8`, `mazovia`, ... |
| `Ignore Missing Memo` | `false` | Read memo fields as null when a `.dbt`/`.fpt` is missing |
| `Allow Unmaintained Indexes` | `false` | Write even when an index can't be kept up to date (a missing `.mdx`, an unnamed `.ndx`/`.ntx`/`.idx`, a sort order or mode Thunder.Dbf doesn't write) |
| `Maintain FoxPro Indexes` | `false` | Preview: keep FoxPro `.cdx`/`.idx` indexes up to date, so FoxPro tables can take writes |
| `Exclusive` | `false` | Open tables so no other program can open them, like `USE ... EXCLUSIVE`; needed for `PACK`, rebuilds and `INDEX ON`. Enforced on Windows; advisory on Linux and macOS |
| `Lock Scheme` | `Auto` | Whose lock offsets to use: `Dbase`, `Clipper`, `VisualFoxPro` or `Jet` (Access) (see [Locking](locking.md)) |

`db.TableNames` lists the tables. `db["NAME"]` gives a table that the database keeps open and disposes with itself. `db.OpenTable("NAME")` gives one you own.

Tables can also be opened directly, from a path or from streams:

```csharp
using var table = DbfTable.Open("CONTACT1.DBF", writable: true);
using var fromStreams = DbfTable.Open(dbfStream, memoStream: dbtStream, indexStream: mdxStream);
```

## The handshake: `Check()`

`Check()` reads only headers, never records, so it's fast enough for a "Test connection" button. For each table it reports:

- whether it can be read and written (by the file system *and* by the library's own rules)
- record count, format, encoding, memo format and index tags
- problems, each a `DbfException` with its fix, and warnings (an unrecorded code page, a platform that can't lock)

```
\\server\goldmine\data: 7 tables, read/write, checked in 14 ms. All good.
  [ok] Contact1: dBASE IV with memo, 38 records, DbaseIV memos, 15 index tags
  [ok] ContHist: dBASE IV with memo, 2 records, DbaseIV memos, 4 index tags
```

## Names that can't escape

`db["..."]` only accepts plain table names, matched against the files actually in the folder. These are refused with `PathEscapesFolder`:

- paths, `..` and drive letters
- tables or companion files that are links, symbolic or through a parent folder (links are refused, not followed)

The folder you open is resolved to its real path first, so connecting through a link to the folder itself is fine. See [Security](security.md).

## Error codes

Every failure is a `DbfException`. `Message` and `Hint` are safe to show anyone; `FilePath` and `Diagnostic` hold the paths and account names, for logs only.

| Code | HTTP | Means | Usual fix |
|---|---|---|---|
| `FolderNotFound` | 404 | The folder isn't there or isn't reachable | Check the path and that the share is mounted |
| `TableNotFound` | 404 | No table by that name | Check the spelling against `TableNames` |
| `InvalidConnectionString` | 400 | A setting is unknown or malformed | Fix the connection string |
| `InvalidCsv` | 400 | A CSV is malformed (no header, unclosed quote, wrong encoding) | Fix the file, or set `CsvEncoding` |
| `InvalidValue` | 422 | A value doesn't fit its field | Fix the value, or widen the field |
| `UnsupportedSql` | 400 | The ADO.NET provider's SQL can't run this statement | Rewrite it within [the supported SQL](ado-net.md#the-sql) |
| `QueryTooLarge` | 413 | A query would need too much memory (sorting a huge result) | Add a `LIMIT`, or narrow the `WHERE` |
| `Timeout` | 504 | A command ran past its `CommandTimeout` | Let an index answer it, add a `LIMIT`, or raise the timeout |
| `AccessDenied` | 403 | The operating system refused access | Grant the account read (or modify) rights on the file and share |
| `PathEscapesFolder` | 403 | A name or link reaches outside the folder | Use a plain name; replace links with real files |
| `EncryptedTable` | 403 | dBASE IV PROTECT encrypted the table | Save an unencrypted copy from dBASE |
| `ReadOnlyConnection` | 409 | Opened read-only | Open writable |
| `ReadOnlyLocation` | 409 | The file, share or volume is read-only | Clear the attribute or get write access |
| `FileInUse` | 409 | Another program holds the file exclusively, or has it open while this one asks for exclusive use | Close it, or open read-only |
| `ExclusiveUseRequired` | 409 | `PACK`, a rebuild or `INDEX ON` on a table not opened exclusively | Close the legacy app, open with `exclusive: true` |
| `TableAlreadyExists` | 409 | Creating would overwrite or adopt existing files | Choose another name |
| `IndexWouldGoStale` | 409 | Writing would leave an index out of date | Put the `.mdx` next to the table, name the index with `MaintainIndex`, or see the reason in `Diagnostic` |
| `IncompleteTransaction` | 409 | dBASE IV left a transaction unfinished | Let the legacy app roll it back |
| `RecordLocked`, `TableLocked` | 423 | Another program holds the lock | Retry shortly, or raise `LockTimeout` |
| `NotADbfFile`, `UnsupportedFormat` | 500 | Not a table, or dBASE II/7 | Check the file; save as dBASE III/IV or FoxPro |
| `MemoFileMissing`, `MemoFileDamaged`, `IndexDamaged` | 500 | A companion file is missing or corrupt | Restore it, or rebuild the index |
| `LockingUnsupported` | 500 | `RequireLocks` is on where locks can't be taken | Run on Windows or Linux |
