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
- **Keeps unchanged bytes:** `Update` rewrites only the fields whose values changed, so other apps' formatting survives, such as `Y`/`N` logicals and zero-padded memo pointers.
- **Maintains memos:** new text goes into new memo blocks; unchanged memos keep theirs. dBASE IV free-block chains are respected.
- **Updates the production index:** every tag, including `FOR` filters and unique tags. See [Indexes](indexes.md).
- **Handles Visual FoxPro extras:** null flags are set and cleared, and autoincrement fields take the next number when you pass `null`.
- **Commits in a safe order:** memo, records, the header count, then the index. See [Durability](durability.md).

`Update` writes the whole record you pass, so a change another program made between your `Read` and your `Update` is overwritten. `UpdateFields` avoids that: it locks the record, re-reads it, changes only the named fields and writes it back in one step, and returns the record before and after. Prefer it whenever the legacy app may be editing the same records; the SQL `UPDATE`, the web service's `PATCH` and the SQL bridge all use it.

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

New tables are dBASE IV (`0x03`, or `0x8B` with memos), within dBASE IV's limits of 255 fields and 4,000-byte records. Creating refuses to overwrite or adopt existing files with the same name (`.dbf`, `.dbt`, `.mdx`, `.cpg`, in any letter case). For the quickest way to a table's shape, see [CSV import](csv.md).

## Formats

| Version byte | Format | Read | Write |
|---|---|---|---|
| `0x03` | dBASE III / IV | ✅ | ✅ |
| `0x83` | dBASE III + `.dbt` | ✅ | ✅ |
| `0x8B` | dBASE IV + `.dbt` | ✅ | ✅ |
| `0xF5` | FoxPro 2 + `.fpt` | ✅ | ✅ |
| `0x30`-`0x32` | Visual FoxPro | ✅ | ✅ (refused while a `.cdx` can't be maintained) |
| `0x02`, `0x04`, `0x8C` | dBASE II, dBASE 7 | refused with `UnsupportedFormat` | |
