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
| 4 | 4 | free page list offset, kept in the file's first header (0 or −1 = none; the real files here all hold 0) |
| 8 | 4 | update counter (Harbour: big-endian, bumped on every change so other processes drop cached pages) |
| 12 | 2 | key length (at most 240) |
| 14 | 1 | options: 0x01 unique, 0x04 candidate (Visual FoxPro CANDIDATE and PRIMARY KEY tags), 0x08 has FOR, 0x20 compact, 0x40 compound, 0x80 structural (the directory) |
| 15 | 1 | signature (0x01) |
| 16 | 2 | Harbour: header length (1,024). Visual FoxPro keeps undocumented counts in 16-35 of its tag headers |
| 18 | 2 | Harbour: page length |
| 20 | 4 | Harbour: `RCHB` |
| 92 | 26 | Advantage/Harbour: collation name (empty for FoxPro's MACHINE order) |
| 494 | 5 | Visual FoxPro: code page (empty in every real file here) |
| 499 | 1 | CLIP/Harbour: 1 = keys upper-cased (only when the signature is 0x01) |
| 502 | 2 | 0 = ascending, 1 = descending. The tree is in ascending order either way; descending only changes the reading direction (as in Harbour's `dbfcdx`) |
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

## Writing

A table opened writable keeps its structural `.cdx` (the one named after the table, which Visual FoxPro tables flag in their header) up to date with every append, update, delete and recall, and `RebuildIndex()` rebuilds it. Other `.cdx` and compact `.idx` files are kept too once `MaintainIndex(path)` names them. The code is in `src/Thunder.Dbf/CdxIndexWriter.cs`; splits, the free list and the leaf layout follow Harbour's `src/rdd/dbfcdx` (read, not copied).

### What's written, and how

- **Leaves are re-packed whole.** A changed leaf is decoded, changed and packed again with freshly chosen widths, the way FoxPro chooses them: the duplicate and trailing counts each get the bits that hold the key length (3 for 4-byte keys, 4 for 10, 5 for 30, 6 for 50), and an entry gets the fewest bytes that leave room for the leaf's highest record number. Trailing pad is spaces for character keys and zero bytes for the rest, and a shared prefix never overlaps the dropped trailing bytes. Re-packing every untouched leaf in the real files in `tests/fixtures` (four Visual FoxPro `.cdx` files and X#'s `TEST10K.CDX`, over 470 leaves) reproduces them byte for byte, apart from the free gap between entries and key bytes: Visual FoxPro leaves stale bytes there, and Thunder.Dbf writes zeros.
- **Order.** Entries are kept in byte order, equal keys by record number. Interior entry *i* is the last key (and its record) under child *i*, and is updated whenever that changes.
- **Splits.** A leaf or interior page that no longer fits splits in two, and the new page is linked into the left/right sibling chain of its level. When keys arrive in order at the right edge (autoincrement IDs), the left page keeps everything it held, so such tables get full pages. A full root splits and a new root grows above it; the old root loses the root bit (0x01) of its attributes.
- **Removal.** An emptied page is unlinked from its siblings, its parent loses the entry for it, and it goes on the free list (offset 4 of the file's first header; each free page holds the next one's offset in its first four bytes, like Harbour). A root left with one child hands the root role to it. New pages always come from the end of the file: following a damaged free-list link could overwrite a live page, so the list is kept for FoxPro and Harbour but not reused here.
- **Attributes.** Visual FoxPro sets attribute bit 0x04 on the (root) leaves of its tags in every real file here, for reasons it doesn't document; new leaves copy the attributes of the leaf they split from.
- **Keys.** Character keys are the expression's bytes, space padded; numbers (including `B` fields and numeric expressions) are the ordered 8-byte double, built through the number's text so it's the correctly rounded value; a bare Visual FoxPro integer field is the 4-byte big-endian value plus 0x80000000; dates are the ordered Julian-day double, 0 for a blank date; logicals are `T`/`F`.
- **Unique, candidate, FOR, descending.** A UNIQUE tag holds the lowest-numbered record of each key, as a rebuild would. A CANDIDATE (or PRIMARY KEY) tag holds every record, and a write that would give a second record the same key is refused before the table changes, as FoxPro refuses it. A FOR filter (including `!DELETED()`) decides whether a record is in the tag, so deleting and recalling move keys in and out. A descending tag's tree stays ascending.
- **Header.** The big-endian update counter at offset 8 of the file's first header is bumped after each change, as Harbour does in shared mode.

### What's refused, and why

A `.cdx` with any tag that can't be kept exactly as FoxPro would keep it isn't attached: writes are refused with `IndexWouldGoStale` (unless the table is opened with `allowUnmaintainedIndexes`, and FoxPro rebuilds the index before it's used). `DbfDatabase.Check()` gives the reason. A tag is refused when:

- its header names a collation other than MACHINE (byte 92 or 494 onwards), or it was built with CLIP/Harbour's IGNORECASE: GENERAL and the language collations store sort weights that aren't reproduced here;
- its expression uses a nullable field (Visual FoxPro adds a null marker byte to such keys), a datetime (`T`) or currency (`Y`) field: no real file here shows their key layout, and guessing would mislead FoxPro;
- its expression can't be evaluated against the table (a database container's long field names), or its key length doesn't match what the expression returns;
- the keys it stores for the first and last records of its first and last leaves aren't the keys built here for those records (a collation or layout that isn't reproduced, or an index that belongs to another table or is out of step);
- it's named after a different `.dbf` in the same folder.

Changes are placed by byte order, so a node found out of byte order while changing it stops the write with `IndexDamaged` (X# sorts some character tags case-blind without saying so; `TEST10K.CDX`'s CITY, FIRST and LAST tags are like that). `RebuildIndex()` puts such a tag in byte order.

Damage is bounded: every walk keeps a visited set and a depth limit, leaf bit widths and counts are checked against the page, and child and sibling pointers must be page-aligned and inside the file, so a damaged index raises `InvalidDataException` instead of hanging or overwriting the wrong page.

### Locking

Writes run under the table's own locks (Visual FoxPro's header and record lock positions); the `.cdx` file itself isn't locked. Harbour also locks the `.cdx` while it changes it; Visual FoxPro's own index-file locking isn't documented, so a FoxPro session changing the same table at the same moment is kept out by the table locks only (and on macOS, where .NET can't lock part of a file, not at all).
