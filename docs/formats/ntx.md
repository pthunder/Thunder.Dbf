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

Signature flag bits (Harbour's names): 0x0001 FOR expression present, 0x0006 default, 0x0008 partial, 0x0010 extended locking, 0x0020 custom, 0x0200 large file (page numbers instead of offsets), 0x8000 compound (a Harbour CTX file with several tags; not supported here). Anything outside 0x87FF isn't an NTX.

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
