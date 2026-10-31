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
dBASE III and IV, FoxPro 2 and Visual FoxPro tables; `.dbt` and `.fpt` memos; dBASE IV `.mdx` indexes (read and kept up to date on writes); dBASE III `.ndx`, Clipper `.ntx` and FoxPro `.cdx`/`.idx` indexes (read and seek). dBASE II and dBASE 7 are refused with a clear error. See [Reading and writing](reading-and-writing.md#formats) and [Indexes](indexes.md#other-index-formats).

**How big can tables be?**
dBASE's own limits apply (2 GB files, 1 billion records). Thunder.Dbf streams everything: a million-row table scans in under a second, and a 1 GB memo file reads with under 10 MB of memory.

**Does it need ODBC, OLE DB, Jet or a 32-bit process?**
No. It's pure .NET with no native dependencies, 64-bit, on Windows, Linux and macOS.

**My accented letters look wrong.**
The table probably doesn't record its code page (byte 29 is 0), so it's read as Windows-1252. `Check()` warns about this. Set `DbfDatabaseOptions.Encoding`, add `Code Page=850` (or whichever applies) to the connection string, or put a `.cpg` file next to the table.

**What does "IndexWouldGoStale" mean?**
The table declares an index that Thunder.Dbf can't keep up to date: a FoxPro `.cdx`, or a production `.mdx` that's missing. Writing would leave it out of date and confuse the legacy app, so it's refused. Put the `.mdx` back, or opt in with `allowUnmaintainedIndexes` and rebuild the index in the legacy app afterwards.

**Can I test against our real data without sharing it?**
Yes. Set `THUNDER_TEST_DATA` to a private copy; see [Testing](testing.md). Please never attach real data to an issue; `thunder-dbf info` shows a table's structure without its contents.

**Who's behind it?**
ProjectThunder. It's MIT-licensed, and contributions are welcome; see [CONTRIBUTING.md](../CONTRIBUTING.md).
