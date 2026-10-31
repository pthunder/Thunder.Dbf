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

## Writing

A table opened writable with `DbfTable.Open(path, writable: true)` finds every `.ndx` next to it whose key expression names only its fields, opens it read-write and keeps it in step with each append, update, delete and recall. `RebuildIndex()` rebuilds it like dBASE's `REINDEX`. (Thunder.Dbf can't create a new `.ndx`; make it in dBASE with `INDEX ON`.)

What gets written:

- **Keys** exactly as dBASE III stores them: character keys in the table's code page, cut or space padded to the key length; numbers as IEEE doubles; dates as Julian-day doubles, a blank date as 0. Every key in the eight dBASE III Plus fixtures is rebuilt byte for byte from its table.
- **Every record** gets a key: there's no FOR filter, and deleted records stay in the index, as in dBASE III. A delete or recall leaves the keys alone.
- **Unique indexes** (byte 23 set) keep one entry per key, for the lowest-numbered record that has it, which is what `INDEX ON ... UNIQUE` would choose. When that record changes key, the next lowest takes its place.
- **Equal keys** go after the ones already there. dBASE doesn't define an order among equal keys.

Pages:

- **Splits:** a page that overflows its keys-per-page splits in half and the new right half goes at the end of the file. The parent gains the left half's last key as the separator for the left half, and the old separator moves to the new right half. A full root splits too, and a new root grows above it. Leaves and inner pages keep the layout above: inner items have record number 0, and the rightmost child sits alone in the slot after the last key.
- **Removing:** a key leaves its leaf; a page that empties leaves its parent, and the parent goes too if that leaves it with no children. Pages are never merged. Emptied pages stay in the file, out of the tree, as dBASE III leaves them. A separator larger than the real last key under it still routes seeks correctly, so it isn't touched. An inner root left with one child hands the root to that child.
- **New pages** always come from the end of the file, never from emptied ones, so a damaged pointer can't make a write overwrite a live page.

Sort order: the file doesn't record one. dBASE III compares character keys as bytes and numeric keys as numbers, but localized versions could order text by a collation, and an index built that way would have new keys misfiled. Before keeping an `.ndx` up to date, Thunder.Dbf walks its keys once, in order, stopping at the first adjacent pair out of order; if there is one, writes are refused with `IndexWouldGoStale`. The walk runs when the index is attached and again when another program changes its header layout. A collation that orders every key the index holds the same way bytes do can't be told apart.

Locking: dBASE III PLUS takes no lock on the `.ndx` itself. Its multi-user commands (`RLOCK()`, `FLOCK()`, and the automatic locks of `APPEND` and the rest) lock the table, and index pages are updated under those. We found no documented dBASE III lock position for index files, and none appears in other xBase implementations: Harbour's index lock schemes are Clipper's, ComIX's, FoxPro's and its own. Thunder.Dbf therefore relies on the table's locks too: every write holds the record lock and the header lock while it updates the `.ndx`, so Thunder.Dbf writers are serialized among themselves; another program updating a different record's keys at the same moment is not stopped by anything in the `.ndx`, just as two copies of dBASE III PLUS aren't.

Header fields updated: the **root page** (offset 0) when the root splits or collapses, and the **page count** (offset 4) on every new page and every commit, set from the file's real length, since dBASE III allocates its next page from it. Everything else in the header (key length, keys per page, key type, item length, unique flag, the expression and the stray bytes after it) is left exactly as dBASE wrote it, also by a rebuild. A rebuild truncates the file to its header and starts again from one empty leaf, so the file shrinks.

Damage: every page read during a write is checked (page inside the file, not the header, key count no more than keys per page, record numbers non-zero), the descent is limited to 32 levels, and a removal visits each page at most once. Before a page on the way down is changed, its children are checked to be distinct pages and none of them the page or a page above it: a page referenced twice is damage. A damaged index stops the write with `IndexDamaged`; it never loops. An `.ndx` whose header can't be read can't be kept up to date, so it blocks writes with `IndexWouldGoStale` instead.
