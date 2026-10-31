# FAQ

```
    ___
   | ? |     ProjectThunder :: Thunder.Dbf
   |___|     Quick answers.
```

**Is it safe to point at live data?**
Yes. `DbfDatabase.Open` is read-only unless you set `ReadOnly = false`, and reads take no locks, just as dBASE's reads don't.

**Can the legacy app keep running while Thunder.Dbf writes?**
Yes, on Windows and Linux. Writes take dBASE's record and header locks, keep the production `.mdx` exact and leave untouched bytes alone. On macOS, locks can't be enforced; see [Locking](locking.md).

**Which formats does it read and write?**
dBASE III and IV, FoxPro 2 and Visual FoxPro tables; `.dbt` and `.fpt` memos; dBASE IV `.mdx`, dBASE III `.ndx` and Clipper `.ntx` indexes (read and kept up to date on writes); FoxPro `.cdx`/`.idx` indexes (read and seek, and kept up to date as a preview). dBASE II and dBASE 7 are refused with a clear error. See [Reading and writing](reading-and-writing.md#formats) and [Indexes](indexes.md#other-index-formats).

**Is it thread-safe?**
A `DbfTable` (like a `FileStream` or a `DbConnection`) is for one thread at a time, so give each thread its own. Separate tables on the same file are safe together: inside one process they share one set of locks, so two threads or two web requests each with their own table see each other's locks, and across processes the operating system's file locks do the same on Windows and Linux. The web service opens a table per request this way. See [Locking](locking.md).

**Can it create Visual FoxPro tables?**
Yes. `DbfTable.Create(path, DbfTableFormat.VisualFoxPro, fields)` writes a Visual FoxPro table with an `.fpt` for memos, including integer, currency, double, datetime, nullable and autoincrement fields. dBASE III, dBASE IV (the default) and FoxPro 2 work the same way. See [Creating tables](reading-and-writing.md#creating-tables).

**How big can tables be?**
dBASE's own limits apply (2 GB files, 1 billion records). Thunder.Dbf streams everything: a million-row table scans in under a second, and a 1 GB memo file reads with under 10 MB of memory.

**Does it need ODBC, OLE DB, Jet or a 32-bit process?**
No. It's pure .NET with no native dependencies, 64-bit, on Windows, Linux and macOS.

**My accented letters look wrong.**
The table probably doesn't record its code page (byte 29 is 0), so it's read as Windows-1252. `Check()` warns about this. Set `DbfDatabaseOptions.Encoding`, add `Code Page=850` (or whichever applies) to the connection string, or put a `.cpg` file next to the table.

**What does "IndexWouldGoStale" mean?**
An index belongs to the table but can't be kept up to date. It might be a production `.mdx` that's missing, a FoxPro `.cdx` without the `MaintainFoxProIndexes` preview, an `.ndx`/`.ntx` not named with `MaintainIndex`, or an index in a sort order Thunder.Dbf doesn't write. Writing would leave it out of date and confuse the legacy app, so the write is refused. The exception's `Diagnostic` says which index and why. [Indexes](indexes.md#refused-rather-than-guessed) lists the fixes.

**Can I test against our real data without sharing it?**
Yes. Set `THUNDER_TEST_DATA` to a private copy; see [Testing](testing.md). Please never attach real data to an issue; `thunder-dbf info` shows a table's structure without its contents.

**Who's behind it?**
ProjectThunder. It's MIT-licensed, and contributions are welcome; see [CONTRIBUTING.md](../CONTRIBUTING.md).
