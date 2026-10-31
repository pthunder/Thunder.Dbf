# Clipper .NTX index format

```
            [ 42 ]                 ProjectThunder :: Thunder.Dbf
           /      \                One file, one classic B-tree (keys in inner
      [7 19]      [57 88]          pages too), little-endian, 1,024-byte pages.
```

Worked out from Harbour's `include/hbrddntx.h` and `src/rdd/dbfntx/dbfntx1.c` (read, not copied: Harbour is GPL with a linking exception), then checked against the real `.ntx` files in `tests/fixtures/xsharp`, written by X#'s DBFNTX driver. Clipper 5.x, Harbour, xHarbour and X# all write this layout.

## Units

- A **page** is 1,024 bytes. Pointers are **byte offsets** (multiples of 1,024), except in Harbour's large-file variant, where they are page numbers.
- The header is page 0 (offset 0).

## Header (offset 0)

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | signature flags: 0x0006 (Clipper), 0x0003 (old Clipper); see below |
| 2 | 2 | version: an update counter |
| 4 | 4 | root page offset |
| 8 | 4 | first free page offset (0 = none) |
| 12 | 2 | item length = key length + 8 |
| 14 | 2 | key length |
| 16 | 2 | key decimals (numeric keys: the decimals STR() was given) |
| 18 | 2 | max keys per page |
| 20 | 2 | half a page of keys (max keys / 2) |
| 22 | 256 | key expression, NUL terminated |
| 278 | 1 | unique flag |
| 279 | 1 | unused |
| 280 | 1 | 1 = descending |
| 281 | 1 | unused |
| 282 | 256 | FOR expression, NUL terminated (Clipper 5.3, Harbour) |
| 538 | 12 | tag name, NUL padded (Clipper 5.3, Harbour; empty in older files: use the file name) |
| 550 | 1 | custom-index flag (Harbour) |

Signature flag bits (Harbour's names): 0x0001 FOR expression present, 0x0006 default, 0x0008 partial, 0x0010 extended locking, 0x0020 custom, 0x0040 changes only, 0x0080 template, 0x0100 equal keys sorted by record number, 0x0200 large file (page numbers instead of offsets), 0x8000 compound (a Harbour CTX file with several tags; not supported here). Anything outside 0x87FF isn't an NTX.

## Pages

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | key count *n* |
| 2 | 2 × (max keys + 1) | item offsets: slot *i*'s item is at this byte offset in the page |
| ... | item length each | items: child page offset (4), record number (4), key |

- The offset table lets Clipper insert a key by shuffling 2-byte offsets instead of moving items. Always go through it: slot order is key order, item order on the page isn't.
- Slot *i* < *n* holds a key, its record number, and the child page with the keys **before** it. Slot *n* holds only the child with the keys after the last one.
- Leaves are pages whose child pointers are all 0.
- A true B-tree: every key appears exactly once, in a leaf or in an inner page. In order: child 0, key 0, child 1, key 1, …, key *n* − 1, child *n*.
- With the descending flag set, the whole tree is in descending order.

## Keys

Every key is text, so every comparison is a byte comparison (`memcmp`). The file doesn't say what the text means; Clipper (and Thunder.Dbf, given the table) evaluates the key expression to find out.

- **Character:** the expression result, space padded.
- **Numeric:** `STR(value, key length, key decimals)` with the leading spaces turned into `0`. For a negative number the minus sign also becomes `0`, then every digit *d* becomes the character `'0' − d − 4` (so `0` → `,` and `9` → `#`), which sorts negatives below positives and larger magnitudes first. A negative key therefore starts with `,`. Example, N(6,2): 12.5 → `012.50`; −12.5 → `,+*.',`. STR() overflow writes asterisks.
- **Date:** `DTOS()` form, `YYYYMMDD`; a blank date is eight spaces.
- **Logical:** `T` or `F`.

## Collation

Clipper sorts character keys with the active nation module and Harbour with its code page's collation. For plain ASCII and the default (binary) setting that's byte order, which is what seeks assume. A file built with a language collation still reads back in its own order.

The file doesn't record which collation built it. Before keeping an `.ntx` up to date, Thunder.Dbf walks its keys once, in order, and stops at the first adjacent pair out of byte order (reversed for a descending index); if there is one, the index is refused. A collation that happens to order every key it holds the same way bytes do can't be told apart this way.

## Writing

A table opened writable by path opens every `.ntx` next to it whose key expression names only its fields, and keeps each one up to date on append, update, delete and recall (`NtxIndexWriter.cs`). `RebuildIndex()` rebuilds them from the table, like Clipper's `REINDEX`. The page layout, split rule and free list follow Harbour's `src/rdd/dbfntx/dbfntx1.c` (read, not copied), and the results are checked byte for byte against the X# files in `tests/fixtures/xsharp`.

- **Keys** are built the way Clipper builds them (see [Keys](#keys)): `KeyFor` reproduces every key in the X# fixtures exactly. A FOR expression in the header decides which records are indexed.
- **Order:** keys compare as bytes, the whole tree reversed when the descending flag is set. Equal keys are kept in record-number order, the order `INDEX ON` leaves them in, so single inserts and a rebuild produce the same sequence. Removal still finds an entry wherever equal keys sit, since a file Clipper maintained may hold them in another order.
- **Unique** indexes keep the lowest record number for each key; when that record leaves, the next lowest takes its place.
- **Pages** are written the way Harbour lays out a fresh one: count, then the offset table with an entry for every slot up to max keys (the unused ones too, each pointing at its own item area, because Clipper writes the next key wherever a free slot points), then the items in slot order. Unused bytes are zero.
- **Inserting** goes into a leaf. A page that overflows (max keys + 1) splits around its middle item, which moves up into the parent; each half keeps about the header's half-page count. A root that splits gets a new root above it.
- **Removing** a key from an inner page replaces it with its predecessor (the last key of the rightmost leaf below its left child), so only leaves lose keys. Pages may fall below half full, which Clipper reads fine, but never empty: an emptied page borrows an item through its parent from a sibling that has two or more, or merges with that sibling, which can empty the parent in turn. An inner root left without keys hands the root role to its one child.
- **Free pages** go on the header's free list the way Harbour keeps it: a page with no keys whose slot 0 child pointer links to the next free page (0 ends the list). New pages are always added at the end of the file instead of taken from the list, because following a damaged link would overwrite a live page; Clipper and Harbour still reuse the list.
- **Header:** the root (offset 4) and free list (offset 8) change as the tree does. The update counter at offset 2 goes up by one each time a change is flushed under the index lock, which is how Clipper and Harbour notice that pages they cached are stale. The signature, key layout, expressions, flags and tag name are never touched.
- **Locking:** Clipper and Harbour lock the `.ntx` itself while they read or change it, because the table's record and header locks don't stop two programs changing different records' keys in the same page at once. Thunder.Dbf takes the same lock, exclusively, around every tree operation (one insert, one removal, one unique-key lookup, the emptying that starts a rebuild): byte 1,000,000,000 of the `.ntx`, one byte long, which is Harbour's `IDX_LOCKPOS_CLIPPER` (and `IDX_LOCKPOS_CLIPPER2`, used with the 0x0010 flag) with no lock pool. A large file (0x0200) makes Harbour use its 64-bit scheme instead, so there the lock covers 65,537 bytes from 0x7FFFFFFF00000001 (`IDX_LOCKPOS_HB64` and its `IDX_LOCKPOOL_HB64` pool; Harbour's `include/hbrdddbf.h` and `hb_dbfLockIdxFile` in `src/rdd/dbf1.c`, read, not copied). Under the lock the header's layout is checked again and every page is read fresh; nothing read before the lock is used after it, and the update counter is bumped and the change flushed before the lock is released. A table's record changes become a few lock rounds (remove the old key, insert the new one), each complete on its own. The lock follows the table's settings: `LockTimeout`, `RequireLocks`, and on macOS, where .NET can't lock part of a file, it coordinates only tables in this process. A lock that can't be had in time fails the write; since the record is already written by then, the error is `IndexDamaged` (with the `TableLocked` cause inside) and `RebuildIndex()` puts the index right. That record's changes to the other indexes are undone, so no index holds half of them.
- **Refused files:** some `.ntx` files can't be kept the way Clipper or Harbour would keep them, and a table refuses writes (`IndexWouldGoStale`) rather than change them: custom (0x0020), changes-only (0x0040), template (0x0080) and multi-key (0x0400) indexes, whose keys the program decides; numeric keys with room for more than 15 significant digits (key length, less one for the decimal point), where `STR()` of a double starts producing digits of its own (Harbour's `hb_itemStrBuf`); and keys out of byte order (see [Collation](#collation)). The check runs when the index is attached and again whenever another program changes its header; a file that turns unmaintainable after the table opened stops the next write, and `RebuildIndex()` leaves it alone.
- **Rebuilds** cut the file back to its header and start again from one empty leaf, so a rebuilt index has no free pages.
- **Large files** (Harbour's 0x0200 flag) store page numbers instead of offsets, and are written that way. Plain files stop growing at 4 GB, the limit of a 32-bit offset.
- **Damage:** every page read is bounded (count against max keys, offsets inside the page, pointers on page boundaries inside the file, leaf and inner pages not mixed); descents are limited to 32 levels and searches never visit a page twice. Before a page on the way down is changed, its children are checked to be distinct pages and none of them the page or a page above it, and a sibling borrowed from or merged with must not be on the path either: a page referenced twice is damage, never freed or merged with itself. A damaged index fails the write with `IndexDamaged`, the record's index changes are undone where possible, and `RebuildIndex()` repairs it.
- A header that allows fewer than two keys per page leaves nothing to split, so writes through it fail with an explanation; no Clipper-family writer produces one.
