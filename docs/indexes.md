# Indexes

```
                [ 42 | 87 ]                 ProjectThunder :: Thunder.Dbf
               /     |     \                dBASE IV .mdx: up to 47 B-trees in one file,
      [ 7 | 42 ] [ 51 | 87 ] [ 90 | 99 ]    read, sought, and kept exact on every write.
```

## Reading and seeking

```csharp
using var index = table.OpenProductionIndex()!;          // the .mdx next to the table
foreach (var tag in index.Tags)
    Console.WriteLine($"{tag.Name}: {tag.Expression} {(tag.ForExpression is { } f ? "FOR " + f : "")}");

var record = table.Seek(index["CONTCOMP"], "ACME");      // first match, like SEEK; prefix match for text
foreach (var r in table.ReadInOrder(index["CONTCITY"])) { }        // records in key order

foreach (var entry in index["CONTZIP"].Seek("94"))       // keys from "94" onward
    Console.WriteLine($"{entry.Key} -> record {entry.RecordNumber}");
```

Tags with character, numeric (12-byte BCD) and date (Julian day) keys are supported, ascending and descending, unique or not, with or without `FOR` filters.

## Writes keep every tag exact

When a table with a production `.mdx` is opened writable by path, or with `indexStream`, every `Append`, `Update`, `Delete` and `Recall` updates every tag:

- **Keys** are computed from each tag's own dBASE expression, such as `Upper(company)+Upper(contact)+Substr(RecID,1,5)`, `accountNo+Dtos(onDate)+recId` or `STR(PUNITTYPE,11,0)+STR(PUNITID,11,0)`.
- **`FOR` filters** decide membership. With the common `FOR .NOT. DELETED()`, deleting a record removes its keys and recalling it puts them back.
- **Unique tags** keep the first record with each key. When that record leaves, the next record with the same key takes its place.
- **Node splits and new roots** happen as needed, and emptied nodes go back on the file's free list.
- **Keys are worked out before anything is written,** so an expression that fails leaves the table and its index untouched.

`table.MaintainsIndex` says whether this is happening. A table that declares a production index which isn't available is refused writes (`IndexWouldGoStale`) rather than silently letting it go stale.

## Creating tags (INDEX ON)

```csharp
table.CreateIndexTag("LASTNAME", "UPPER(LAST)+UPPER(FIRST)");                  // INDEX ON ... TAG LASTNAME
table.CreateIndexTag("ACTIVE", "CUSTNO", forExpression: ".NOT. DELETED()", unique: true);
table.CreateIndexTag("NEWEST", "DTOS(HIRED)", descending: true);
```

This adds a tag to the production `.mdx`, creating the file (and setting the table's flag for it) when the table has none. The new tag is filled from every record and kept up to date by every write after that. It's laid out byte for byte as dBASE software writes it: the tests rebuild a Siemens table's six tags from scratch and compare them with the `.mdx` SIMATIC Manager wrote.

- **The key's type** (character, number or date) and **width** come from the expression's results on the real records, so no existing key is cut short.
- **Every key is worked out first.** An expression that fails on some record, or gives text for one record and a number for another, stops the command with nothing written.
- **A crash can't leave a half-filled tag.** The tag is filled while it's still unlisted, and listed last.
- **Expressions and filters are stored in the table's code page.** A character the code page can't hold is refused.

Like dBASE's `INDEX ON`, it needs the table to itself: open it with `exclusive: true`, or it throws `ExclusiveUseRequired`. A program that already had the `.mdx` open wouldn't see the new tag until it reopened it. For dBASE IV tables only: FoxPro keeps its tags in `.cdx` files.

## Rebuilding

```csharp
table.RebuildIndex();   // like dBASE's REINDEX: every tag, from the table
```

Use it after writing with `allowUnmaintainedIndexes`, or when an index has gone out of step. An index another program left out of step isn't attached when the table opens; take it over with `MaintainIndex(path, rebuild: true)`, which rebuilds it from the table. It rebuilds every index the table keeps, not only the `.mdx`. Like REINDEX in dBASE and FoxPro it needs the table opened with `exclusive: true` (so does `MaintainIndex(path, rebuild: true)`): a program with the table open would go on reading index pages a rebuild has replaced, which Visual FoxPro reports as a corrupted tag. `table.Pack()` rebuilds them too (see [Packing](reading-and-writing.md#packing)).

## How it's verified

- **Siemens:** every tag in 20 real SIMATIC Manager `.mdx` files is rebuilt from its table and compared key for key.
- **GoldMine:** the same check passed on 31 tags of GoldMine CRM data, before and after random writes.
- **Hand-built files** cover multi-level trees, duplicates spanning leaves, cycles, and keys beyond `decimal`'s range.
- **Thousands of random changes** are checked against the same oracle.
- **The tests catch real mistakes:** deliberately breaking the split logic makes them fail.

## Other index formats

| Format | From | Read and seek | Kept up to date on writes |
|---|---|---|---|
| `.mdx` | dBASE IV | ✅ | ✅ |
| `.ndx` | dBASE III | ✅ | ✅ |
| `.ntx` | Clipper, Harbour | ✅ | ✅, under Clipper's index lock |
| `.cdx`, `.idx` | FoxPro, Visual FoxPro | ✅ | preview, with `maintainFoxProIndexes` |

### Which indexes are kept

- **`.mdx` and the structural `.cdx`:** the table's header says it has them, so they're always the table's own.
- **`.ndx`, `.ntx` and `.idx`:** these record nothing about their table. One named after the table (`CUSTOMER.NTX` for `CUSTOMER.DBF`) is kept automatically. Any other that looks like it belongs (its key names only this table's fields) blocks writes until you name it, as the legacy app does with `USE CUSTOMER INDEX CUSTNAME, CUSTZIP`:

  ```csharp
  using var table = DbfTable.Open(@"data\CUSTOMER.DBF", writable: true);
  table.MaintainIndex(@"data\CUSTNAME.NTX");
  table.MaintainIndex(@"data\CUSTZIP.NTX");
  ```

  Guessing could be wrong: two tables with an `ID` field would both claim an index on `ID`.

### Refused rather than guessed

Thunder.Dbf keeps an index only when it can write exactly what the legacy program would. Otherwise writes stop with `IndexWouldGoStale`, and the reason is in the exception's `Diagnostic`. It refuses:

- **Keys not in byte order:** an index built with a national or case-blind sort order. `MaintainIndex(path, rebuild: true)` takes it over and rebuilds it in byte order, like REINDEX with `SET COLLATE TO "MACHINE"`, wherever in the tree the disorder is. If that rebuild fails, the index is let go and keeps blocking writes. On an index that's already kept, `rebuild: true` still rebuilds it.
- **Clipper's custom, changes-only, template and multi-key indexes,** whose keys the program sets itself.
- **Numeric `.ntx` keys wider than 15 significant digits,** which Clipper formats differently.
- **`.cdx` tags over nullable, datetime or currency fields,** or with a collation other than MACHINE.
- **`.cdx` keys this library can't build byte for byte:** a numeric key that's more than a bare field (FoxPro computes `AGE+0` or `VAL(ZIP)` in binary floating point, this library in decimal), and a character key built with anything but fields, `UPPER()`, `DTOS()` and `STR()` of a whole-number field joined with `+` (no `SUBSTR`, `ALLTRIM`, `PADR` or literals). In a double-byte code page (Japanese, Chinese, Korean) `UPPER()` and FOR filters over text are refused too, because FoxPro's string functions count bytes there.
- **A damaged `.cdx`:** pages shared by two tags, a free page still in a tree, or sibling links that don't point back. Reported as damage, not as a sort order, so `rebuild: true` doesn't take it over: REINDEX it in FoxPro.
- **A stray `.cdx` next to a dBASE IV table** whose `.mdx` is missing: the table is only taken for FoxPro 2 when the `.cdx` opens and its tags name only the table's fields. Otherwise the missing `.mdx` keeps blocking writes.
- **An index created next to the table after it was opened:** reopen the table.

If an index update fails partway, every later write to that table stops until `RebuildIndex()` succeeds. A half-applied change is never built on.

Every format opens the same way, and every tag works through `IDbfIndexTag`:

```csharp
using var ntx = table.OpenIndex("CUSTNAME.NTX");          // .mdx, .ndx, .ntx, .cdx or .idx
using var cdx = table.OpenStructuralIndex();               // the FoxPro .cdx next to the table
var record = table.Seek(ntx.Tags[0], "SMITH");
foreach (var r in table.ReadInOrder(cdx!.Tag("LASTNAME"))) { }
```

Pass the table (as `OpenIndex` does) so key types come out right. CDX and NTX files don't record their key type; like FoxPro and Clipper, the reader works it out by evaluating the key expression on a blank record.

A table with a structural `.cdx` refuses writes until `.cdx` maintenance lands, unless you opt in with `allowUnmaintainedIndexes` and rebuild the index in FoxPro afterwards. Seeks compare bytes, as dBASE, Clipper and FoxPro's MACHINE collation do. Indexes built with a language collation (FoxPro GENERAL, or some X# settings) may order a few keys differently.

Byte-level notes: [mdx](formats/mdx.md), [ndx](formats/ndx.md), [ntx](formats/ntx.md), [cdx](formats/cdx.md).
