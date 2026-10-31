# FoxPro .CDX / compact .IDX index format

```
     [ tag directory ]             ProjectThunder :: Thunder.Dbf
      /      |      \              A B-tree of tag names pointing at more B-trees,
   [NAME]  [CITY]  [ZIP]           512-byte pages, leaves packed down to the bytes
                                   each key doesn't share with its neighbour.
```

Worked out from Harbour's `include/hbrddcdx.h` and `src/rdd/dbfcdx/dbfcdx1.c` (read, not copied: Harbour is GPL with a linking exception), then checked against the real `.cdx` files in `tests/fixtures/infused-dbf/foxprodb` (Visual FoxPro) and `tests/fixtures/xsharp/rdd-tests/TEST10K.CDX` (X#, seven tags over 10,000 records). FoxPro 2.x, Visual FoxPro, Clipper's SIx driver, Harbour and X# all write this layout.

## Units

- A **page** is 512 bytes. Pointers are **byte offsets**; −1 (0xFFFFFFFF) means none.
- A **tag header** is 1,024 bytes: 512 bytes of fields, then a 512-byte expression pool.
- Harbour can write bigger pages: when the header's signature (bytes 20-23) is `RCHB`, bytes 18-19 give the page length. A header byte 15 of 0x21 there means large-file page numbers (not supported).

## Files

- **.cdx (compound):** offset 0 holds a tag header whose tree is the **tag directory**: its keys are the 10-byte tag names, space padded, and each key's "record number" is the offset of that tag's header. The directory is sorted by name; FoxPro lists tags in creation order, which is header-offset order.
- **.idx (compact):** offset 0 holds the one tag's header. Its name is the file name.
- Header byte 14 says which: bit 0x40 compound. Without bit 0x20 (compact) the file is an old FoxBASE uncompressed .idx (not supported).

## Tag header

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | root page offset |
| 4 | 4 | free page list offset (−1 = none) |
| 8 | 4 | update counter |
| 12 | 2 | key length (at most 240) |
| 14 | 1 | options: 0x01 unique, 0x08 has FOR, 0x20 compact, 0x40 compound, 0x80 structural (the directory) |
| 15 | 1 | signature (0x01) |
| 16 | 2 | Harbour: header length (1,024) |
| 18 | 2 | Harbour: page length |
| 20 | 4 | Harbour: `RCHB` |
| 499 | 1 | CLIP/Harbour: 1 = keys upper-cased (only when the signature is 0x01) |
| 502 | 2 | 0 = ascending, 1 = descending |
| 504 | 2 | FOR expression: position in the pool |
| 506 | 2 | FOR expression: length, counting its NUL (1 when there's none) |
| 508 | 2 | key expression: position in the pool |
| 510 | 2 | key expression: length, counting its NUL |
| 512 | 512 | expression pool; FoxPro puts the key expression at 0 and the FOR expression right after it |

## Pages

Every page starts with:

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | attributes: bit 0 root, bit 1 leaf (Visual FoxPro also sets bit 2 on some leaves; 0xFF = free page) |
| 2 | 2 | key count *n* |
| 4 | 4 | left sibling offset (−1 = none) |
| 8 | 4 | right sibling offset (−1 = none) |

### Interior pages

From offset 12, *n* items of key length + 8 bytes: the key, then the record number and the child page offset, both **big-endian**. Key *i* is the **highest key under child *i*** (with its record number), so there are *n* children and no extra rightmost pointer.

### Leaf pages

| Offset | Size | Field |
|---|---|---|
| 12 | 2 | free bytes in the page |
| 14 | 4 | record number mask |
| 18 | 1 | duplicate count mask |
| 19 | 1 | trailing count mask |
| 20 | 1 | bits for the record number |
| 21 | 1 | bits for the duplicate count |
| 22 | 1 | bits for the trailing count |
| 23 | 1 | bytes per entry |
| 24 | ... | *n* entries |

Each entry is a little-endian integer of "bytes per entry" bytes: the record number in the low bits, then the duplicate count, then the trailing count in the top bits.

- **Duplicate count:** how many leading bytes the key shares with the previous key on the page (always 0 for the first).
- **Trailing count:** how many trailing pad bytes were dropped: spaces for character keys, zero bytes for everything else.
- The remaining key length − duplicates − trailing bytes are stored packed against the **end of the page**, working backwards: the first key's bytes are last in the page, the next key's just before them.

So to rebuild key *i*: take the duplicate bytes from key *i* − 1, then its stored bytes, then the trailing pad.

## Keys

Nothing in the file says a key's type; FoxPro (and Harbour, and Thunder.Dbf given the table) evaluates the key expression on a blank record to find out. Every type is stored so that comparing bytes gives key order:

- **Character:** the expression result, space padded.
- **Numeric:** the IEEE double, big-endian; for zero and positive numbers flip the sign bit, for negative numbers flip every bit. −0 is stored as 0.
- **Integer (Visual FoxPro `I` fields, 4-byte keys):** big-endian, plus 0x80000000.
- **Date:** the Julian day number (0001-01-01 = 1,721,426) as a double, encoded like a number; a blank date is 0.
- **Logical:** `T` or `F`.

## Collation

FoxPro's MACHINE collation sorts by byte, and seeks assume that. Visual FoxPro's other collations (GENERAL and friends) store collation weights instead of the text, and some writers (X#'s `TEST10K.CDX`) store the text but sort it with a Windows language collation, so a few neighbours like "Macedon" / "MacLeod" are out of byte order. Such tags still read back in their own order; a seek can miss a key that sits at one of those spots.

## Visual FoxPro databases

A table in a VFP database container (.dbc) can use field names longer than the 10 characters a .dbf holds; the .dbc maps them. Key expressions use the long names, so without the .dbc they can't be evaluated, and such keys stay untyped.
