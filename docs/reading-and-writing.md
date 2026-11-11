# Reading and writing

```
  +---+----------------------+------------+----------+---+------------+
  | * | NAME (C 20)          | BALANCE    | HIRED    | A | NOTES      |     ProjectThunder :: Thunder.Dbf
  +---+----------------------+------------+----------+---+------------+     Records in, records out,
  |   | Smith                |    1234.50 | 19970412 | T |         42 |     exactly as dBASE wrote them.
  +---+----------------------+------------+----------+---+------------+
```

## Field types

| Type | dBASE / FoxPro meaning | .NET value |
|---|---|---|
| `C` | Character, space padded (Clipper/FoxPro allow over 255) | `string` (trailing spaces trimmed) |
| `N`, `F` | Numeric and float, ASCII | `decimal`, or `double` beyond decimal's range |
| `D` | Date, `YYYYMMDD` | `DateOnly?` (null when blank) |
| `L` | Logical: `T F Y N ?` | `bool?` |
| `M` | Memo text in `.dbt`/`.fpt` | `string` (`byte[]` for binary memos) |
| `G`, `P` | FoxPro OLE object, picture | `byte[]` |
| `I` | Visual FoxPro integer | `int` |
| `Y` | Visual FoxPro currency | `decimal` (4 places) |
| `B` | Visual FoxPro double | `double` |
| `T` | Visual FoxPro datetime | `DateTime` (unspecified kind: DBF has no time zones) |
| others | | raw `byte[]`, untouched |

Text is decoded with the table's code page (header byte 29), a `.cpg` file next to the table, or the encoding you pass. Over 60 DOS and Windows code pages are recognized, including Mazovia (Polish DOS).

## Reading

```csharp
var record = table.Read(0);              // zero-based index; record.RecordNumber is 1-based, like RECNO()
var name = record["NAME"];               // by name, any case
var deleted = record.IsDeleted;          // marked with '*', not yet packed away
var isNull = record.IsNull(3);           // a real Visual FoxPro .NULL., not just blank

foreach (var r in table.ReadAll()) { }   // streams; memory stays flat for any table size
table.Refresh();                         // see records other programs added since opening
```

## Writing

```csharp
table.Append("Smith", 1234.50m, new DateOnly(1997, 4, 12), true, "Notes go to the memo file");
table.Update(0, values);                 // values in field order
table.UpdateFields(0, new Dictionary<string, object?> { ["CITY"] = "Boston" });
                                         // only CITY, onto the record as it is on disk right now
table.Delete(0);                         // dBASE DELETE: sets the mark
table.Recall(0);                         // dBASE RECALL: clears it
table.AppendRange(rows);                 // bulk: a million rows in about two seconds
```

What every write does for you:

- **Locks** the record (or the header and append bytes) the way dBASE does. See [Locking](locking.md).
- **Validates every value before writing anything:** widths, decimals, types, and characters the code page can't hold. Nothing is silently turned into `?`.
- **Keeps unchanged bytes:** `Update` rewrites only the fields whose values changed (compared with the record on disk; to keep other programs' edits, use `UpdateFields`), so other apps' formatting survives, such as `Y`/`N` logicals and zero-padded memo pointers.
- **Maintains memos:** new text goes into new memo blocks; unchanged memos keep theirs. dBASE IV free-block chains are respected.
- **Updates the production index:** every tag, including `FOR` filters and unique tags. See [Indexes](indexes.md).
- **Handles Visual FoxPro extras:** null flags are set and cleared, and autoincrement fields take the next number when you pass `null`.
- **Commits in a safe order:** an append writes the memo, the record, the header count, then the index keys; an update that changes keys writes the new keys, the record, then removes the old keys. See [Durability](durability.md).

### Faster writes

Every write is forced to the disk by default, which is the safe setting and the slow part. Two opt-in settings trade some of that safety for speed; [Durability](durability.md#choosing) has the full table of what each costs.

```csharp
table.FlushWritesToDisk = false;      // still crash-safe against a killed process; a power cut can lose the latest writes
table.DeferIndexWrites = true;        // for migrations and bulk loads, inside LockTable():
using (table.LockTable())
{
    foreach (var row in rows)
        table.Append(row);
}                                     // each changed .cdx or .mdx page is written once, here
```

`AppendRange` commits once per `commitEvery` rows, so it's fast with the default settings too.

## Packing

```csharp
using var table = DbfTable.Open(path, writable: true, exclusive: true);   // like USE ... EXCLUSIVE
int removed = table.Pack();                                               // like dBASE's PACK
```

`Pack` removes deleted records for good, renumbers the rest and rebuilds every index the table keeps. Memo blocks of removed records stay in the memo file as unused space, as dBASE IV leaves them.

Record numbers change and index pages are replaced, so, as dBASE and FoxPro require `EXCLUSIVE` for `PACK`, the table must be opened with `exclusive: true` (`Exclusive=true` in a connection string); otherwise `Pack` throws `ExclusiveUseRequired`. Opening exclusively fails with `FileInUse` while the legacy app has the table open, and keeps it out until you're done. That's enforced by Windows, where the legacy apps run; on Linux and macOS .NET's exclusive open is only advisory, and programs that don't ask (Harbour, for one, uses its own locks) aren't kept out, so make sure they're closed. Rebuild anything that remembered record numbers. The [SQL bridge](sql.md) refuses a packed table until its mirror is rebuilt.

It's built not to lose data:
- Every surviving record's index keys are worked out before anything changes.
- A copy of the table (`NAME.dbf.packing`) is kept beside it until the pack has finished. It gets that name only once it's complete and on disk, and no record moves before then: a pack that fails while copying (a full disk) leaves the table as it was and writable.
- The smaller record count is on disk before the file is shortened.

If the process dies halfway, every write to that table is refused until it's restored. Replace the `.dbf` with the `.packing` copy, delete the copy, then call `RebuildIndex()`.

## Changing a table's structure

> **Preview.** `ModifyStructure`, `AddField`, `DropField`, `AlterField` and `DropIndexTag` are marked `[Experimental("THDBF001")]`: calling them is a build error until you opt in with `<NoWarn>$(NoWarn);THDBF001</NoWarn>` in the project, or `#pragma warning disable THDBF001` around the call. Back up tables you can't afford to lose before changing them.

```csharp
using var old = DbfTable.Open(path, writable: true, exclusive: true);    // like USE ... EXCLUSIVE
var fields = old.FieldDefinitions.ToList();                              // the fields as they are
fields[0] = DbfFieldDefinition.Character("NAME", 40);                    // widen NAME
fields.Add(DbfFieldDefinition.Date("JOINED"));                           // add JOINED
using var table = old.ModifyStructure(fields);                           // like MODIFY STRUCTURE
```

`ModifyStructure` adds, drops, renames, widens, narrows and retypes fields, as dBASE's and FoxPro's `MODIFY STRUCTURE` do. It returns the table with its new structure, opened the same way, and closes the one you called it on. `AddField(field)`, `DropField(name)` and `AlterField(name, field)` are shortcuts for one field; `AlterField` with a new name renames, and so does `renames: new Dictionary<string, string> { ["OLD"] = "NEW" }`.

- **Values follow their field by name** (or through the rename) and convert where the types allow: text holding a number to a numeric field, numbers, dates and logicals to text (dates as `YYYYMMDD`, like `DTOS()`), a date to a datetime and back when no time of day is lost, text to a memo and back. A field stored the same way as before keeps its bytes exactly. New fields start blank, a Visual FoxPro `.NULL.` where they're `Nullable`; a new `AutoIncrement` field numbers the existing records, and one that was already counting carries on from its next number.
- **A change that would lose a value is refused**, naming the first record it would change: text too long for its new width, a number too wide or with too many decimals, text that isn't a number or date, a `.NULL.` in a field that can't hold one. Numbers are checked against the value itself, not a rounded copy: a double with more significant digits than the new field writes, or too small for it (`1E-100` in an integer), is a loss. A blank value stays blank in a `Nullable` field rather than becoming `.NULL.`. A blank number going into a Visual FoxPro integer, double or currency field is a loss: those have no blank, only 0, and a `Nullable` one's `.NULL.` isn't a blank either. Nothing is written. `allowLoss: true` shortens, rounds or blanks such values instead, and `table.StructureValuesChanged` says how many it changed.
- **Every record is carried across**, deleted ones too (still deleted), in the same order, so record numbers don't change.
- **Memos stay where they are.** While the table keeps a memo field, the new memo file is a copy of the old one with blocks added for new memos, so memos of a dropped field stay as unused space, as dBASE leaves them; `Pack` doesn't reclaim it either. That includes their text: dropping a memo field doesn't erase what it held, from the new memo file or from the backup. To be rid of sensitive text for good, copy the records you keep into a new table and delete the old files. Dropping the last memo field leaves no memo file beside the table: the old one goes into the backup.
- **The production `.mdx` or structural `.cdx` is rebuilt** with the same tags over the new fields (on a FoxPro table, open with `maintainFoxProIndexes: true`). A tag whose expression names a field the change drops or renames is refused: take it out first with `DropIndexTag(name)` and create it again afterwards. Other indexes (`.ndx`, `.ntx`, `.idx`) are refused too, since their key widths are fixed: open the table without them and recreate them in the legacy app.
- **The format stays the same**, and so do the code page mark and the memo block size. The version byte follows the fields, as dBASE and FoxPro set it: adding the first memo field makes a `0x03` table `0x83` (dBASE III's memo file, which dBASE IV, FoxPro and Clipper all open), `0x8B` when it has a dBASE IV `.mdx`, or `0xF5` for FoxPro 2; dropping the last makes it `0x03` again. A Visual FoxPro table goes from `0x30` to `0x31` when it gains an autoincrement field, and back. A FoxPro 2 table without memos or a `.cdx` has dBASE's `0x03`, so nothing in it says FoxPro: a memo added to one gets a dBASE `.dbt`.
- **Refused:** a Visual FoxPro table in a database container (`.dbc`, the header's flag or a path in its backlink), since the `.dbc` keeps its own record of the fields: change it in Visual FoxPro, or `REMOVE TABLE` it first. Visual FoxPro 9 tables with varchar, varbinary or blob fields (version `0x32`). General and Picture fields can be dropped but not kept, since Thunder.Dbf doesn't create them.

Like `PACK`, it needs the table to itself (`exclusive: true`, or it throws `ExclusiveUseRequired`).

### It rewrites the whole table

There's no way to add a field in place: every record gets wider, so dBASE and FoxPro write a new file too, and so does `ModifyStructure`. It reads every record and every memo (reading a record decodes its memos, and so does building each tag), writes the records into a new table, copies the memo file whole and builds every tag again. The time and the disk space grow with the table:

- **Time.** It grows in step with the record count. Widening one field of a dBASE IV table with 68-byte records, a memo field and two tags took 1.4 s for 100,000 records (6.8 MB) and 16 s for 1,000,000 (68 MB), on a Mac with Apple silicon and an SSD; a Visual FoxPro table took about the same. Allow about 15 seconds per million records on a fast local disk, and more on a network share or a slower disk, or with more tags or long memos. Nothing reports progress meanwhile, and nobody can use the table, so run it when the legacy app is closed, as you would `PACK`.
- **Disk space.** While it runs, the new table, memo file and index sit beside the old ones, so allow the table's size again, plus the change in record width. Afterwards the old files are kept (below), so the space isn't given back until you delete them.

### What a crash can leave

The steps, and what an open does at each, are in [the protocol](formats/restructure.md). In short: the change first makes its backup folder, `NAME.DBF.restructured-<UTC time>` (named in `table.StructureBackupPath`), which holds its lock and a manifest naming every file that moves. The new table is written into `NAME.DBF.restructure-partial` and renamed `NAME.DBF.restructuring` once every byte of it is on disk. Then each file moves in two renames, the old one into the backup and the new one into its place: the old memo file first, then the decision (`restructure.commit` in the backup, published by a rename), then the `.dbf`, the index, and a memo file the table didn't have before. Finished or undone, the staging folder is renamed `NAME.DBF.restructure-retired` before anything in it is deleted, and the backup keeps only the old files. Delete it once you're happy with the change.

- **Killed before the decision**, the next `writable: true, exclusive: true` open puts back every file the backup holds and throws the new files away: the table is byte for byte as it was. Meanwhile the old `.dbf` reads its own memo file or the copy, which holds every memo it had, and a memo file it never had isn't left beside it.
- **Killed after it**, the next exclusive open moves the rest of the new files in, even when the `.dbf` itself is in the backup between its two renames.
- **Killed while an open is finishing or undoing a change**, the next exclusive open carries on from the same decision: the manifest and the decision are kept until every file is where it belongs. Two opens at once take turns on the change's lock, and one that finds the change over, or another change under way, leaves it alone.
- **A move that fails** (another program opened a file, say) undoes the change before the decision, with `FileInUse` or another code saying why and the table as it was. After it, the error is `IncompleteTransaction` and says the change is decided, and the next exclusive open finishes it; failing only to clear the staging folder away says the new structure is in place.
- **While a change is under way, or one was cut off**, Thunder.Dbf writers are turned away with `IncompleteTransaction`, and readers too while the `.dbf` or memo file is between its two renames. On Linux and macOS an open also checks, by device and inode, that the `.dbf` it opened is the one at the path once it has looked, and opens again when it isn't: a handle opened just before the swap would otherwise go on reading and writing the old file, by then the backup. That check needs a 64-bit process and, on Linux, glibc 2.33 or later or musl; without it, keep other Thunder.Dbf writers away from a table while its structure changes. Other programs don't look for the folder at all, so keep them closed, as for `PACK`.
- A `.restructure-partial` or `.restructure-retired` folder left by a kill is ignored, and cleared by the next change or writable exclusive open. A staging folder's id or a backup's manifest that's missing, damaged or names anything but this table's own files stops the open with `IncompleteTransaction` and instructions, before or after the decision alike, rather than moving files on its say-so. Any of the change's folders that turns out to be a link stops it with `PathEscapesFolder`, unentered.

`Update` writes the whole record you pass, so a change another program made between your `Read` and your `Update` is overwritten. `UpdateFields` avoids that: it locks the record, re-reads it, changes only the named fields and writes it back in one step, and returns the record before and after. Prefer it whenever the legacy app may be editing the same records; the SQL `UPDATE`, the web service's `PATCH` and the SQL bridge all use it.

## Typed records and LINQ

Map a table to a plain C# class. Properties match fields by name, in any case, or by `[DbfField("NAME")]`, and values are converted to the property's type:

```csharp
public sealed class Contact
{
    public string? Company { get; set; }
    public string? Zip { get; set; }
    [DbfField("LASTDATE")] public DateTime? LastCall { get; set; }
    public decimal? Credit { get; set; }
    [DbfRecordNumber] public int Recno { get; set; }   // RECNO(), filled when reading
    [DbfIgnore] public string? Note { get; set; }       // not a field
}

foreach (var c in contacts.ReadAll<Contact>()) { }     // live records, streamed
var one = contacts.Read<Contact>(41);
var index = contacts.AppendRecord(new Contact { Company = "ACME", Zip = "94105" });
one.Credit = 2500m;
contacts.UpdateRecord(41, one);                        // through UpdateFields: other fields stay as they are on disk
```

`AsQueryable<T>()` gives a LINQ query. An equality on a field with its own complete `.mdx` tag (not `UNIQUE`, no `FOR` filter other than `.NOT. DELETED()`) becomes a seek. Everything else scans, streaming. Either way the whole query then runs as LINQ to Objects over the candidates, so an index can only make it faster, never change the answer:

```csharp
var nearby = contacts.AsQueryable<Contact>()
    .Where(c => c.Zip == "94105" && c.Credit > 1000)
    .OrderBy(c => c.Company)
    .Take(20);
Console.WriteLine(nearby.Explain());   // seek CONTACT1 index tag ZIP for ZIP = '94105'
var list = nearby.ToList();
```

A seek is only used when it must agree with a scan:

- **The property must hold the field exactly:** `string` for character fields, `decimal` for numbers (or `int`/`long` when the field has no decimals and is narrow enough), `DateOnly`/`DateTime` for dates.
- **The value must be a constant or a captured variable.** It's read once, when the query starts.
- **Some queries always scan:** a query that uses the same table twice, and a `Where` that comes after `Skip`, `Select` and the like.

Deleted records are left out. Typed records must be classes.

A blank field reads as `null` into a nullable property and as the type's default into one that isn't. Written back, a default date (`DateTime.MinValue`, `DateOnly.MinValue`) or a `'\0'` character is a blank again (in a `Nullable` field too: a blank, not a `.NULL.`), so reading a record and updating it leaves its blank fields as they were. A non-nullable `bool` or number can't say "blank": it writes `F` or `0`. Nor can a non-nullable property say `.NULL.`: one read from a `.NULL.` is written back as a blank. Declare the property nullable (`bool?`, `decimal?`, `DateTime?`) where the difference matters.

Typed records are safe in trimmed and Native AOT apps (iOS, for one): the class's public properties are kept for you. `AsQueryable<T>()` isn't, and is marked so your build warns; there, use `ReadAll<T>()` with ordinary LINQ, or `Seek` through the tag. See [Mobile and AOT](getting-started.md#mobile-and-aot).

## Creating tables

```csharp
using var table = DbfTable.Create("PEOPLE.DBF",
[
    DbfFieldDefinition.Character("NAME", 30),
    DbfFieldDefinition.Numeric("BALANCE", 12, 2),
    DbfFieldDefinition.Date("JOINED"),
    DbfFieldDefinition.Logical("ACTIVE"),
    DbfFieldDefinition.Memo("NOTES"),                     // creates PEOPLE.DBT too
]);
```

New tables are dBASE IV unless you pick another format. Choose the one the program that will open the table expects:

```csharp
using var table = DbfTable.Create("PEOPLE.DBF", DbfTableFormat.VisualFoxPro,
[
    DbfFieldDefinition.Integer("ID") with { AutoIncrement = true },
    DbfFieldDefinition.Character("NAME", 30),
    DbfFieldDefinition.Currency("RATE"),
    DbfFieldDefinition.DateTime("UPDATED") with { Nullable = true },
    DbfFieldDefinition.Memo("NOTES"),                     // creates PEOPLE.FPT too
]);

table.Append(null, "ACME", 125.50m, DateTime.Now, "First customer", null);   // null ID: the next number
```

| `DbfTableFormat` | Version byte | Memo file | Field types | Limits |
|---|---|---|---|---|
| `DbaseIII` | `0x03`, `0x83` with memos | `.dbt` | C, N, D, L, M | 128 fields, 4,000-byte records |
| `DbaseIV` (default) | `0x03`, `0x8B` with memos | `.dbt` | C, N, F, D, L, M | 255 fields, 4,000-byte records |
| `FoxPro2` | `0x03`, `0xF5` with memos | `.fpt` | C, N, F, D, L, M | 255 fields, 4,000-byte records |
| `VisualFoxPro` | `0x30`, `0x31` with autoincrement | `.fpt` | C, N, F, D, L, M, plus I (`Integer`), Y (`Currency`), B (`Double`), T (`DateTime`) | 255 fields (254 with nullable fields), 65,500-byte records |

Visual FoxPro tables can also have `Nullable` fields (kept in the hidden `_NullFlags` field, which comes last in `table.Fields`; `Append`, `AppendRange` and `Update` work it out, so they take one value per field you created, though a last `null` for it is still accepted), and `AutoIncrement` integer fields with a starting number and step. The headers match what Visual FoxPro 9 writes, field flags included. The code page mark defaults to Windows-1252 (`0x57` for dBASE, `0x03` for the FoxPros); pass `languageDriver` for another.

A FoxPro 2 table without memo fields has the same version byte as dBASE (`0x03`). While the new table is open, Thunder.Dbf knows it's FoxPro 2: it takes FoxPro's locks and refuses `INDEX ON`, which would add a dBASE `.mdx`. Opened again later, nothing in the file says FoxPro 2, so it's read as dBASE unless a `.cdx` sits next to it. Create its indexes in FoxPro.

Creating refuses to overwrite or adopt existing files with the same name (`.dbf`, `.dbt`, `.fpt`, `.mdx`, `.cdx`, `.cpg`, in any letter case). For the quickest way to a table's shape, see [CSV import](csv.md).

## Formats

| Version byte | Format | Read | Write |
|---|---|---|---|
| `0x03` | dBASE III / IV | ✅ | ✅ |
| `0x83` | dBASE III + `.dbt` | ✅ | ✅ |
| `0x8B` | dBASE IV + `.dbt` | ✅ | ✅ |
| `0xF5` | FoxPro 2 + `.fpt` | ✅ | ✅ |
| `0x30`-`0x32` | Visual FoxPro | ✅ | ✅ (a table with a `.cdx` needs the `MaintainFoxProIndexes` preview) |
| `0x02`, `0x04`, `0x8C` | dBASE II, dBASE 7 | refused with `UnsupportedFormat` | |
