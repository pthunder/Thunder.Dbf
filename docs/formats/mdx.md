# dBASE IV .MDX index format

```
          [ root ]                 ProjectThunder :: Thunder.Dbf
          /   |   \                One file, up to 47 B-trees ("tags"),
     [leaf] [leaf] [leaf]          all little-endian, all in 512-byte pages.
```

Worked out from the Xbase64 4.1.11 source (`src/core/xbixmdx.cpp`, `xbbcd.cpp`, `xbdate.cpp`) and checked byte by byte against the 20 real MDX files SIMATIC Manager wrote in `tests/fixtures/siemens-step7`.

## Units

- A **page** is 512 bytes. Every pointer in the file is a page number: byte offset = page × 512.
- A **block** (one B-tree node) is `block size` bytes, header bytes 22-23. That equals block factor (bytes 20-21) × 512. Every Siemens file uses 1024.

## File header (page 0)

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | version, 0x02 |
| 1-3 | 3 | created: YY (year − 1900), MM, DD |
| 4 | 16 | table base name, upper case, NUL padded |
| 20 | 2 | block factor, in 512-byte pages |
| 22 | 2 | block size in bytes |
| 24 | 1 | production index flag (1 = opened automatically with the table) |
| 25 | 1 | tag table slots (0x30 = 48) |
| 26 | 1 | tag entry length (32) |
| 28 | 2 | tags in use |
| 32 | 4 | file size in pages |
| 36 | 4 | first free page (0 = none); free blocks chain through their bytes 4-7 |
| 44-46 | 3 | last update: YY MM DD |

Pages 0-3 (2,048 bytes) hold the header and tag table.

## Tag table (starts at byte 544)

Entry *i* is at 544 + 32*i*:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | tag header page |
| 4 | 11 | tag name, NUL padded |
| 15 | 1 | key format (0x10) |
| 16-18 | 3 | left, right and parent thread: a binary search tree over tag names (1-based, 0 = none); readers can ignore it |
| 19 | 1 | 0x02 |
| 20 | 1 | key type: `C`, `N` or `D` |

## Tag header (one block at the tag header page)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | root node page |
| 8 | 1 | key format: 0x08 = descending, 0x40 = unique |
| 9 | 1 | key type |
| 12 | 2 | key length (C: expression width, N: 12, D: 8) |
| 14 | 2 | max keys per node = (block size − 12) / item length |
| 18 | 2 | item length = key length + 4, rounded up to a multiple of 4 |
| 23 | 1 | unique flag (nonzero; Siemens writes 0x40) |
| 24 | ≤221 | key expression, ASCII, NUL terminated |
| 245 | 1 | 1 when there's a FOR expression |
| 246 | 1 | 1 when the tag has keys |
| 762 | ≤220 | FOR expression, NUL terminated |

## Nodes

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | key count *n* |
| 4 | 4 | ignore (free-list link when the node is free) |
| 8 | ... | items: 4-byte pointer + key bytes, padded to item length |

- **Leaf vs interior:** read the uint32 at 8 + *n* × item length. Zero means leaf.
- **Leaves:** each pointer is a 1-based record number.
- **Interior nodes:** *n* keys and *n* + 1 pointers, each a child page. Key *i* is the last key in child *i*'s subtree.
- No sibling links: walking in order needs a parent stack.
- Duplicate keys are **not** guaranteed to be in record-number order (Siemens files aren't).

## Keys

- **Character:** the expression result, space padded. Compared byte by byte; a seek compares only the search key's length.
- **Date:** IEEE double, little endian, holding a Julian day number (0001-01-01 = 1,721,426).
- **Numeric:** 12 bytes, value = ±0.*d1d2d3…* × 10^*E*:
  - byte 0 = 52 + *E*
  - byte 1 = sign (bit 7) | digit count << 2 | 0x01
  - bytes 2-11 = up to 20 BCD digits, first digit in the high nibble
  - Zero is `34 01 00 …`.
  - Examples: 1 → `35 05 10`, 13 → `36 09 13`.
  - Compare by decoded value: Xbase64's byte compare sorts zero wrongly.
