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

**How big can tables be?**
dBASE's own limits apply (2 GB files, 1 billion records). Thunder.Dbf streams everything: a million-row table scans in under a second, and a 1 GB memo file reads with under 10 MB of memory.

**Is it fast?**

Reads are: a Seek through a `.cdx` tag answers in microseconds, and reading a whole table runs at hundreds of thousands of records a second, ahead of the Visual FoxPro OLE DB driver on the same machine. Writes are as safe as you ask them to be. By default every write is forced to disk in a crash-safe order, which is slower than Visual FoxPro, which never forces its writes. With `FlushWritesToDisk = false` they're many times faster, close to the OLE DB driver on tables with a few index tags, and bulk loads with `DeferIndexWrites` are faster again. See [Durability](durability.md#choosing) for what each setting trades.

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
