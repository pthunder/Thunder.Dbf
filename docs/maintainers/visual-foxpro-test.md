# Running the Visual FoxPro round-trip test

```
   Thunder.Dbf --writes--> .dbf + .cdx --VFP 9 walks every tag--> same order?
   Thunder.Dbf <--checks-- .dbf + .cdx <--VFP 9 edits records---- same as a rebuild?
```

FoxPro `.cdx` writing is an opt-in preview (`MaintainFoxProIndexes`) until this round-trip test, in which Visual FoxPro 9 checks the files Thunder.Dbf writes, has passed. This page is the checklist for running it on a Windows PC. A person, or an automated agent, on that machine can follow it top to bottom.

## What you need

- Windows 10 or 11, x64 or ARM64. VFP 9 is a 32-bit program and runs on both.
- **Visual FoxPro 9.0 SP2**, from a Visual Studio (MSDN) subscription: my.visualstudio.com → Downloads → search "Visual FoxPro". VFP isn't redistributable, so it never goes in the repository.
- **.NET 10 SDK**: https://dotnet.microsoft.com/download
- **Git**, and this repository.

## 1. Install

1. Install Visual FoxPro 9, then SP2. The default folder is `C:\Program Files (x86)\Microsoft Visual FoxPro 9\`.
2. Start VFP once by hand and close it. That gets past the first-run prompts, which would otherwise block a hidden run.
3. Install the .NET 10 SDK, then check it in PowerShell:

   ```powershell
   dotnet --version
   ```

   It should print `10.x`.

## 2. Get the code

```powershell
git clone https://github.com/pthunder/Thunder.Dbf.git
cd Thunder.Dbf
dotnet test Thunder.Dbf.slnx
```

Every test should pass before going further. If one fails here, the problem isn't VFP: report that first.

## 3. Run the round trip

```powershell
$env:THUNDER_VFP = "C:\Program Files (x86)\Microsoft Visual FoxPro 9\vfp9.exe"
dotnet test tests/Thunder.Dbf.Tests --filter VisualFoxPro --logger "console;verbosity=normal"
```

Eight tests run. Each works in its own temporary folder, and VFP's window stays hidden throughout.

**Indexes on tables VFP made** (`VisualFoxProRoundTripTests`, two tests):

| Table | Comes from | What it exercises |
|---|---|---|
| `calls.dbf` | written by Visual FoxPro | VFP's own `.cdx`, a candidate key (`CALL_ID`), memos |
| `TEST10K.DBF` | written by X# (FoxPro 2 format) | 10,000 records, seven tags; the case-blind tags are taken over and rebuilt in byte order first |

1. Thunder.Dbf appends 400 records, updates 60, deletes 40 and recalls one: enough to split index pages.
2. VFP walks every tag in order and writes the record order to a file. It must match the order Thunder.Dbf's index holds.
3. VFP appends 150 records and edits and deletes others, memos included, through its own indexes and locks.
4. Thunder.Dbf checks that every tag holds exactly what a fresh rebuild would. UNIQUE tags are skipped here, since they only match a rebuild right after one.

**What Thunder.Dbf writes since 0.3.0** (`VisualFoxProWritingTests`, six tests). VFP last checked Thunder.Dbf at 0.2.0; these cover what's been added to FoxPro writing since:

| Test | Table | What it exercises |
|---|---|---|
| `..._reads_and_edits_a_table_Thunder_creates` (Visual FoxPro, FoxPro 2) | made by `DbfTable.Create`, 40 records | the header, field descriptors and `.fpt` Thunder.Dbf writes; integer, numeric, float, currency, date, datetime, logical and memo values; nullable fields (Visual FoxPro) |
| `..._walks_and_keeps_a_cdx_Thunder_creates` (Visual FoxPro, FoxPro 2) | the same, 450 records | a structural `.cdx` made from nothing by `CreateIndexTag`: an `UPPER(name)` tag, a compound key, a descending tag, a `FOR` filter, currency, datetime and nullable keys, and a candidate tag (Visual FoxPro) |
| `..._sees_the_nulls_of_records_written_without_NullFlags` | `kinds.DBF`, written by VFP 9 | `Append`, `AppendRange` and `Update` given values for the declared fields only, leaving `_NullFlags` to Thunder.Dbf (#37); a tag on the nullable field |
| `..._opens_and_keeps_a_table_after_ModifyStructure` | `kinds.DBF` with eight tags | the `ModifyStructure` preview: `DropIndexTag`, then a field dropped, one in a tag widened, a nullable field and a memo field added |

Each of these, depending on the test:

- VFP lists every value of every record, deleted ones too, and it must be exactly what Thunder.Dbf reads. A difference names the record and shows both readings.
- VFP walks every tag, and edits through them, as steps 2 to 4 above.
- VFP appends and edits, setting `.NULL.` in nullable fields, and Thunder.Dbf must read what VFP wrote. For a new table, Thunder.Dbf then writes again on top and VFP reads it once more.
- After `ModifyStructure`, every kept value and memo must read as before the change, and the dropped tag must be gone.

The whole run takes about five minutes. `TEST10K.DBF` is the slowest, up to a couple of minutes; the others take seconds each, mostly VFP starting up.

## 4. What the results mean

| Result | Meaning | Next |
|---|---|---|
| All pass | VFP reads what Thunder.Dbf writes, and the other way round | Report it. `.cdx` writing can come out of preview in the next release |
| `<step>: VFP reads record N as ... but Thunder.Dbf reads ...` | A value written by one side reads differently on the other | A real bug in writing or reading that kind of value. Report it with the full output |
| `Record N: VFP reads OPT=..., but null was given` | `_NullFlags` worked out wrongly for values given without it | A real bug. Report it |
| `Tag X: VFP walks ... but the index holds ...` | VFP reads a tag in a different order than Thunder.Dbf wrote it | A real bug in the writer. Report it with the full output |
| `Tag X after VFP's edits: the file holds ..., a rebuild gives ...` | VFP's own writes left something Thunder.Dbf reads differently | A reader bug, or VFP behaving differently from Harbour's notes. Report it |
| `VFP reported: <number> <message>` | VFP hit an error running the script | The message says what. Error 1705 ("File access is denied") usually means a file is still open: close any VFP windows |
| `VFP didn't finish within 5 minutes` | VFP is waiting on a dialog | Start VFP by hand once (step 1.2), then retry |
| `THUNDER_VFP points at ..., which doesn't exist` | Wrong path | Fix `$env:THUNDER_VFP` |
| The tests pass instantly without starting VFP | `THUNDER_VFP` isn't set in this window | Set it in the same PowerShell window as `dotnet test` |

## 5. Reporting

Open an issue at https://github.com/pthunder/Thunder.Dbf/issues titled "Visual FoxPro round trip: pass" or "...: fail", with:

- the full `dotnet test` output. A failure names the tag and the first record numbers that differ, which is usually enough to find the bug.
- Windows version, and VFP's version (`? VERSION()` in VFP's command window)

The tests work in temporary folders that they delete afterwards, and they only ever use the repository's test fixtures and tables they make themselves, so nothing private is involved.

## Also worth checking by hand

The automated test can't cover these. They're the remaining questions about `.cdx` writing:

- **Locks both ways.** Open `calls.dbf` in VFP (`USE calls SHARED`, then `FLOCK()`) and leave it. A Thunder.Dbf write to the same table should wait, then fail with `TableLocked`. And while Thunder.Dbf holds a write, VFP's `REPLACE` should wait.
- **The update counter.** After VFP edits a table, check whether the four bytes at offset 8 of the `.cdx` changed. Thunder.Dbf uses them to notice another program's changes.
- **Big tables.** VFP reading a `.cdx` whose leaves need 5-byte entries (from record 67,108,864 with 4-byte keys). That needs a very large generated table and isn't automated yet.
