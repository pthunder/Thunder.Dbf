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

Each entry is a little-endian integer of "bytes per entry" bytes: the record number in the low bits, then the duplicate count, then the trailing count in the top bits. The two counts always sit at the top of the entry, and the record number fills everything below them, even past 32 bits: record 67,108,864 with 4-byte keys (3-bit counts) needs a 5-byte entry, whose record field is 34 bits wide (byte 20 says 34) while the 4-byte record mask stops at 0xFFFFFFFF. Harbour's `dbfcdx1.c` packs and reads entries that way: the record number is the entry's low 4 bytes under the mask, and the counts are read down from the entry's top bits.

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

- **Leaves are re-packed whole.** A changed leaf is decoded, changed and packed again with freshly chosen widths, the way FoxPro chooses them: the duplicate and trailing counts each get the bits that hold the key length (3 for 4-byte keys, 4 for 10, 5 for 30, 6 for 50), and an entry gets the fewest bytes that leave room for the leaf's highest record number. Trailing pad is spaces for character keys and zero bytes for the rest, and a shared prefix never overlaps the dropped trailing bytes, this key's or the previous one's: it's counted only against the bytes the previous key keeps, as Visual FoxPro 9 packs it (a key that adds a date after blanks repeats the blanks). Re-packing every untouched leaf of the Visual FoxPro files in `tests/fixtures` reproduces them byte for byte, apart from the free gap between entries and key bytes: Visual FoxPro leaves stale bytes there, and Thunder.Dbf writes zeros. X# and Harbour let the prefix run into the previous key's trailing blanks, so their leaves pack a little tighter; Visual FoxPro reads both.
- **Order.** Entries are kept in byte order, equal keys by record number. Interior entry *i* is the last key (and its record) under child *i*, and is updated whenever that changes.
- **Splits.** A leaf or interior page that no longer fits splits in two, and the new page is linked into the left/right sibling chain of its level. When keys arrive in order at the right edge (autoincrement IDs), the left page keeps everything it held, so such tables get full pages. A full root never moves (Visual FoxPro holds on to its address): its keys go down into two new pages and the root is rewritten in place above them.
- **Removal.** An emptied page is unlinked from its siblings, its parent loses the entry for it, and it goes on the free list (offset 4 of the file's first header; each free page holds the next one's offset in its first four bytes, like Harbour). A root left with one child takes that child's entries, in place. New pages always come from the end of the file: following a damaged free-list link could overwrite a live page, so the list is kept for FoxPro and Harbour but not reused here.
- **Attributes.** Visual FoxPro sets attribute bit 0x04 on the (root) leaves of its tags in every real file here, for reasons it doesn't document; new leaves copy the attributes of the leaf they split from.
- **Keys.** Character keys are built from the record's stored bytes, not its decoded text: decoding drops NULs and trailing bytes that FoxPro keeps, so a field holding `A\0` keys as `41 00 20 20 ...`, not `41 20 20 20 ...`. A character key may join (with `+`) bare character fields, `UPPER()` of one (ASCII letters, and in code pages 437, 850, 852, 866, 1250, 1251, 1252, 1253, 1254 and 1257 every letter, upper-cased as Visual FoxPro 9 SP2 does there, measured byte for byte with VFP running in each: .NET's capital when the code page has it, the byte kept when it doesn't, and VFP's own result for the few bytes where it differs, such as ž in 1252, which VFP leaves lower case; in any other code page a value with non-ASCII bytes is refused at the write). In 852, 1250 and 1251, tags built and kept this way were also checked end to end, with VFP's SEEK and REINDEX; in the other seven only the byte table has been measured so far, `DTOS()` of a date field (eight spaces for a blank date) and `STR()` of a whole-number field wide enough for any value it holds. Numbers are the ordered 8-byte double of a bare `N`, `F` or `B` field, read through the field's text so it's the correctly rounded value; a bare Visual FoxPro integer field is the 4-byte big-endian value plus 0x80000000; dates are the ordered Julian-day double, 0 for a blank date; logicals are `T`/`F`.
- **Unique.** Between rebuilds a UNIQUE tag changes the way Visual FoxPro changes it: the record that brings a key in holds it until that record's key changes or it leaves the tag; nothing takes its place then (the key is simply gone until a REINDEX), and a lower-numbered record that takes the same key later doesn't take it over. A rebuild (`RebuildIndex()`, `Pack()`, a takeover) holds the lowest-numbered record of each key, as REINDEX does. dBASE `.mdx`/`.ndx` and Clipper `.ntx` unique tags keep the lowest record at every write instead.
- **Candidate, FOR, descending.** A CANDIDATE (or PRIMARY KEY) tag holds every record, and a write that would give a second record the same key is refused before the table changes, as FoxPro refuses it. The check counts keys an `AppendRange` batch has planned but not yet written to the index, so two equal keys in one batch are refused too; a rebuild checks its keys among themselves, not against the tree it replaces. A FOR filter (including `!DELETED()`) decides whether a record is in the tag, so deleting and recalling move keys in and out. A descending tag's tree stays ascending.
- **Rebuild.** `RebuildIndex()` builds each tag's new tree in new pages, leaves packed full, writes its root over the old root, and only then puts the old tree's pages on the free list.
- **Currency, datetime, nullable.** A bare currency field keys as its stored ten-thousandths, big-endian, with the sign bit flipped. A bare datetime field keys as the ordered double of whole seconds since day 0 times 1/86400 (adding the seconds over 86,400 to the day comes out one step off for about a quarter of real values; an empty one is 0). A bare nullable character field's key starts with Visual FoxPro's null marker byte, 0x80 then the value, or all zeros for .NULL.; its tag's options carry 0x02.
- **New tags** (`CreateIndexTag` on a FoxPro table) get a 1,024-byte header at the end of the file, with options 0x60 plus 0x01 UNIQUE, 0x02 nullable key, 0x04 CANDIDATE and 0x08 FOR; the key expression at the start of the pool and the FOR expression right after it; descending at 502; and bytes 16-23 holding the key count and the distinct key count, the first two of Visual FoxPro's optimiser statistics. The root goes on the next page, the tree is filled, and the tag directory names it last. A new structural `.cdx` starts with the directory header Visual FoxPro 9 writes and an empty directory leaf on the next page.
- **Header.** The big-endian update counter at offset 8 of the file's first header is bumped after each change, as Harbour does in shared mode.

### What's refused, and why

A `.cdx` with any tag that can't be kept exactly as FoxPro would keep it isn't attached: writes are refused with `IndexWouldGoStale` (unless the table is opened with `allowUnmaintainedIndexes`, and FoxPro rebuilds the index before it's used). `DbfDatabase.Check()` gives the reason. A tag is refused when:

- its header names a collation other than MACHINE (byte 92 or 494 onwards), or it was built with CLIP/Harbour's IGNORECASE: GENERAL and the language collations store sort weights that aren't reproduced here;
- its expression uses a nullable field (Visual FoxPro adds a null marker byte to such keys), a datetime (`T`) or currency (`Y`) field: no real file here shows their key layout, and guessing would mislead FoxPro;
- its expression can't be evaluated against the table (a database container's long field names), or its key length doesn't match what the expression returns;
- the keys it stores for the first and last records of its first and last leaves aren't the keys built here for those records (a collation or layout that isn't reproduced, or an index that belongs to another table or is out of step);
- it's named after a different `.dbf` in the same folder;
- its key is numeric and more than a bare field (`AGE+0`, `VAL(ZIP)`, `ROUND(PRICE,2)`): FoxPro works such expressions out in binary floating point, and this library's evaluator in decimal, so the double can differ in the last bit;
- its key is character and uses anything other than character fields, `UPPER()`, `DTOS()` and `STR()` as described above (`SUBSTR`, `LEFT`, `ALLTRIM`, `PADR`, `TRANSFORM`, literals), whose byte-level results aren't reproduced exactly; in a double-byte code page (932, 936, 949, 950) `UPPER()` is refused too, and so is a FOR filter over text, because FoxPro's string functions count bytes there.

The whole file is also refused, as damaged rather than as a sort order (`InvalidDataException`, so it isn't attached), when its pages don't each have one owner: a page two tags share, a tag page also used by the tag directory or a tag header, a page on the free list that a tree still uses, or a level whose pages don't link to their neighbours both ways. That's checked when the index is attached, and again whenever the file's size or update counter has changed since the last check (another program wrote to it).

Changes are placed by byte order, so keys out of byte order anywhere in a tag (the root, an interior page or any leaf) refuse it as a sort order: X# sorts some character tags case-blind without saying so (`TEST10K.CDX`'s CITY, FIRST and LAST tags are like that). `MaintainIndex(path, rebuild: true)` takes such an index over and rebuilds it in byte order; if that rebuild fails, the index is let go again and keeps blocking writes. On an index that's already kept, `MaintainIndex(path, rebuild: true)` still rebuilds it.

Damage is bounded: every walk keeps a visited set and a depth limit, leaf bit widths and counts are checked against the page, and child and sibling pointers must be page-aligned and inside the file, so a damaged index raises `InvalidDataException` instead of hanging or overwriting the wrong page.

### Locking

Writes run under the table's own locks (Visual FoxPro's header and record lock positions), and every tree operation also runs under Visual FoxPro's index lock: one byte at offset 0x7FFFFFFE (2,147,483,646) of the `.cdx` or `.idx` file itself, taken exclusive. The position comes from Harbour (read, not copied): `include/hbrdddbf.h` defines `IDX_LOCKPOS_VFP` as 0x7ffffffe with `IDX_LOCKPOOL_VFP` 0, so `hb_dbfLockIdxFile` in `src/rdd/dbf1.c` locks that single byte, and `doc/locks.txt` lists both the CDX read and write lock for [V]FP at `@0x7ffffffe : 1`. Under the lock the tag's header is re-checked (a REINDEX by another program stops the write) and every page is re-read; nothing is cached from one operation to the next. Before the lock is released the update counter is bumped and the change flushed. Reloading the tag list before a write takes the same lock.

The lock follows the table's `LockTimeout` and `RequireLocks`. On macOS, where .NET can't lock part of a file, it isn't seen by other programs, but it still keeps tables opened in this process from changing the same index at once.

### What a crash can leave

There's no journal; the writes are ordered so a crash loses no key that was there before, with the new pages forced to disk before any page starts pointing at them. A tag's root never moves, because Visual FoxPro reads each root's address once, when it opens the table, and keeps using it.

- **A split** writes the new page first, then the page that makes it reachable (the parent), and only then shrinks the page whose keys moved. A full root moves its keys down into two new pages and is then rewritten in place, in one 512-byte page write. A crash in between leaves those keys listed twice (in both pages) or the new page unused at the end of the file, never missing.
- **An insert** writes the pages it changes from the top down, so a parent's last key can be ahead of its child's, never behind it.
- **A removal** writes from the bottom up; an emptied page is dropped from its parent first, then from its neighbours' links, and goes on the free list last, when nothing points at it. A crash can leave a parent's last key behind the child's, a neighbour linking to a page no longer in the tree, or a page that's in neither the tree nor the free list (wasted space).
- **A root with one child** takes the child's entries in one write over the root page; the child's page is freed after that reaches the disk.
- **A rebuild** builds the new tree in new pages and writes its root over the old root last: a crash leaves the old tree whole (plus unused pages) or the new one, with the old pages possibly not yet on the free list. The 512-byte page writes count on the disk writing a sector whole, as the old header switch did.
- **Not yet covered:** a crash between a split's writes can leave sibling links out of step with the parent. No key is lost, but Thunder.Dbf's page checks then refuse the tag, `RebuildIndex()` included; REINDEX in FoxPro, or deleting the `.cdx` and recreating its tags in FoxPro, repairs it. A journal that makes every intermediate state valid is on the roadmap.
- **Between the table and the index:** the record is written before its keys, as for every format (see [Durability](../durability.md)), so the index can miss the last write.

Any of these is fixed by `RebuildIndex()` or REINDEX in FoxPro.
