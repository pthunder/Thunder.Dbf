# dBASE III .NDX index format

```
          [ root | -> ]            ProjectThunder :: Thunder.Dbf
          /         \              One file, one B+ tree, one key expression,
     [leaf]        [leaf]          all little-endian, all in 512-byte pages.
```

Worked out from the Xbase64 4.1.11 source (`src/core/xbixndx.cpp`) and the notes in retroinv's MIT-licensed `SRC/DBF/NDX.C`, then checked against the eight `.NDX` files dBASE III Plus 1.1 wrote in `tests/fixtures/retroinv` and the one in `tests/fixtures/ndbfreaderex`.

## Units

- A **page** is 512 bytes. Every pointer in the file is a page number: byte offset = page × 512.
- Page 0 is the header. The tree starts at page 1.

## Header (page 0)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | root page |
| 4 | 4 | pages in the file, header included (the next free page) |
| 8 | 4 | 0 |
| 12 | 2 | key length (character: the expression's width, at most 100; numeric: 8) |
| 14 | 2 | keys per page = (512 − 4) / item length |
| 16 | 2 | key type: 0 = character, 1 = numeric (numbers *and* dates) |
| 18 | 2 | item length = key length + 8, rounded up to a multiple of 4 |
| 20 | 3 | 0 |
| 23 | 1 | unique flag (1 = built with SET UNIQUE ON) |
| 24 | ≤488 | key expression, NUL terminated |

dBASE III Plus copies stray memory into the header after the expression's NUL; only the bytes before the NUL mean anything.

There is no FOR expression, no descending flag and no tag name: the file name is the index's name.

## Pages

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | key count *n* |
| 2 | 2 | leftover memory; ignore |
| 4 | ... | items of item length: lower page (4), record number (4), key |

- **Leaf vs inner:** read the lower page of item 0. Zero means leaf. Don't use the count: after deletes dBASE can leave an inner page with *n* = 0 and its one child in slot 0.
- **Leaves:** items 0 … *n* − 1 hold a record number (1-based) and a key; the lower page is 0.
- **Inner pages:** *n* keys and *n* + 1 children. Item *i* holds child *i* and the **highest key under child *i***; its record number is 0. The rightmost child sits in slot *n* and only its 4-byte lower page field is used, so a page holds keys-per-page full items plus 4 bytes.
- **Uneven depths:** leaves need not all be at the same depth. dBASE's `INDEX ON` hangs a last, partly filled page directly under an upper level (`NUM2000.NDX`, `NUM993.NDX`).
- Bytes after the last item, and key bytes past the key length, are leftover memory.
- Pages emptied by deletes stay in the file, outside the tree, and are never reused.
- No sibling links: walking in order needs a parent stack.

## Keys

- **Character:** the expression result, space padded. Compared byte by byte; a seek compares only the search key's length.
- **Numeric:** an IEEE double, little-endian. Compared by value.
- **Date:** also type 1: an IEEE double holding the Julian day number (0001-01-01 = 1,721,426; a blank date is 0). Nothing in the file says a numeric index holds dates, so a reader needs the table: Thunder.Dbf evaluates the key expression on a blank record and treats a date result as a date.

## Seeking

Descend into the first child whose key is ≥ the search key, then step forward through the leaves. A run of equal keys can span pages (`WIDE1.NDX` has eight), so a seek has to keep walking past a page boundary.
