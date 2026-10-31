# dBASE III Plus reference files

Every `.DBF`, `.DBT` and `.NDX` file here was written by dBASE III Plus 1.1
itself, running headless in DOSBox-X 2025.02.01 (SDL2, Debian package), so
readers and writers can be tested against what the original program writes.
The leftover memory that dBASE copies into some header bytes includes the
DOSBox-X DOS environment (its PATH and `Z:\COMMAND.COM`), so another
DOSBox-X version or configuration can change those bytes and nothing else.  Regenerate them with:

```
TEST/DBASE/install.sh "${RETRO_DL:-$HOME/retro-dl}/dbase11/dBase III Plus 1.1 (1987) (3.5-720k)" "${RETRO_DL:-$HOME/retro-dl}/dbase11/DBASE"
TEST/DBASE/mkgold.sh
```

`install.sh` installs dBASE outside the repository; `mkgold.sh` runs
`TEST/DBASE/MKGOLD.PRG` through `TEST/DBASE/rundb.sh` with the DOS date set to
2026-10-08 and copies the results here.  Two runs give byte-identical files.

| File | Contents |
|---|---|
| `ITEMS.DBF`, `ITEMS.DBT` | the ITEMS layout (version 0x83, 21 fields); 5 records: full values, a negative and a blank N(9,2), a blank date, a deleted record, a record that is blank except for its ID, a short memo, a memo longer than one 512-byte block followed by another memo, empty memos |
| `ITEMS1.NDX` | `INDEX ON STR(ITEM_ID,8)`: one leaf page |
| `CATS.DBF`, `LOANS.DBF` | layouts without memo (version 0x03); LOANS has a blank RET_DATE |
| `USERS.DBF` | the USERS layout; the logical MUSTCHG holds `T`, `F` and blank |
| `BIG.DBF` | 300 records (ID N 8, NAME C 10, D date) |
| `BIG1.NDX` | `STR(ID,8)`, built in one pass by `INDEX ON`: full leaves under one root |
| `BIG2.NDX` | `NAME`, created on the empty table and updated by every append in scrambled key order, so it shows page splits |
| `BIG3.NDX` | the date field `D`: numeric keys (IEEE double holding the Julian day number) |
| `NUM2000.DBF`, `NUM2000.NDX`, `NUM993.DBF`, `NUM993.NDX` | `STR(ID,8)` over 2000 and 993 records: three levels, built bottom-up by `INDEX ON`, with leaves at different depths (a trailing single-child level is folded into its parent) and inner pages holding 31 keys plus the rightmost child |
| `WIDE.DBF`, `WIDE1.NDX` | 60 records with a 100-character key (4 keys per page); records 30-37 share one key |
| `WIDED.DBF`, `WIDED1.NDX` | a copy of WIDE whose index then had the keys of records 1-16 and of the equal-key run replaced (delete plus insert): emptied pages stay in the file outside the tree, and inner pages are left with no key and one child in slot 0 |
| `MKGOLD.TXT` | dBASE's own `LIST STRUCTURE` and `LIST` output for every table (CR/LF) |

What dBASE leaves in these files that a reader must tolerate and need not
reproduce:

- Field descriptor bytes 12-15 hold a memory address and byte 20 the work
  area number (tables made with `CREATE ... FROM` or `COPY STRUCTURE`).
  A table made in the interactive `CREATE` screen has zeros there.
- `.DBT` block 0 holds the next free block in bytes 0-3; bytes 4-511 are
  leftover memory, not zeros.
- In an `.NDX`, the header after the key expression's NUL, the slots after a
  page's last entry, page bytes 2-3 and key bytes past the key length are
  leftover memory or stale entries.
- Pages emptied by key deletions are never reused or freed; they stay in the
  file, counted in the header's page count, but no longer in the tree.

`TEST/CHKDBF.PY --strict` accepts every file here, and
`TEST/DBASE/chkbad.py` checks that it rejects damaged copies.
