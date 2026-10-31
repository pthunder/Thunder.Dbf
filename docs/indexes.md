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

## Rebuilding

```csharp
table.RebuildIndex();   // like dBASE's REINDEX: every tag, from the table
```

Use it after writing with `allowUnmaintainedIndexes`, or when an index has gone out of step.

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
| `.ndx` | dBASE III | ✅ | not yet |
| `.ntx` | Clipper | ✅ | not yet |
| `.cdx`, `.idx` | FoxPro, Visual FoxPro | ✅ | not yet |

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
