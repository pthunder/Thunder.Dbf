# Changing a table's structure: the transaction

```
   P  partial   -->  R  ready  -->  decision  -->  files in place  -->  X  retired  -->  gone
   (building)        (staged)       (in B)          (rolled forward)     (being deleted)
                        \__ undo __/                                          ProjectThunder :: Thunder.Dbf
                                                                              Every state, what it means, and what
                                                                              an exclusive open does on finding it.
```

`ModifyStructure` replaces a table's `.dbf`, memo file and index with new ones. This page is the protocol it follows, so that a process killed (or a file move failing) at any step leaves files the next writable exclusive open can always finish or undo. The user-facing summary is in [Reading and writing](../reading-and-writing.md#what-a-crash-can-leave); what a power cut can do differently is in [Durability](../durability.md).

## The folders

For a table `F/PEOPLE.DBF` (the name as it is on disk; any extension):

| Folder | Holds | Read by |
|---|---|---|
| `P` = `PEOPLE.DBF.restructure-partial` | the new table while it's written | nobody: deleted by the next change or exclusive open |
| `B` = `PEOPLE.DBF.restructured-<UTC time>` | the transaction's record (`restructure.lock`, `restructure.manifest`, the decision `restructure.commit`) and the old files moved aside | recovery, under its lock; afterwards it's the user's backup |
| `R` = `PEOPLE.DBF.restructuring` | the complete new files and `restructure.id`, naming `B` | recovery, under `B`'s lock; its existence turns writers away |
| `X` = `PEOPLE.DBF.restructure-retired` | `R` after the change was finished or undone | nobody: deleted by whoever renamed it, or the next change or exclusive open |

Every one of them must be a real folder, not a link or reparse point, and every file moved out of `R` or `B` a real file: anything else stops the open with `PathEscapesFolder` before it's entered.

`B` is the transaction's identity. It's created first, under a unique name, and its lock is held by whoever is moving files: the change itself from before `P` is written until the reopened table is handed back, or an open finishing it. The lock lives in `B`, which is never renamed, so it can be held while `R` is renamed away. Recovery reads `R/restructure.id`, takes `B`'s lock, and then checks that `R` still names `B`; if it doesn't, the transaction it read about is over and it starts again.

**The manifest** (`B/restructure.manifest`, forced to disk before `P` is renamed) names every file the change moves, as plain names in `F`:

| Line | Meaning |
|---|---|
| `table=` | the `.dbf`'s name (staged under the same name in `R`) |
| `memo=` | the new memo file's name: the old memo file's own name when the table had one (with the format's extension, `.dbt` or `.fpt`), else the table's name with that extension; empty when the new table has no memo fields |
| `memo.original=` | the old memo file, found as the old table's format says (`.fpt` for a FoxPro table, `.dbt` for dBASE): a stray file with the other extension is never touched |
| `index=`, `index.original=` | the same for the production `.mdx` or structural `.cdx` |

Nothing in recovery picks a file by looking for whatever exists; it moves only what the manifest names.

## The states

Each file moves in two renames: the file at its destination into `B`, then the staged one into the destination. A file is therefore always in exactly one of three places (in place, staged in `R`, or in `B`), and each step below can be repeated.

| # | State | Files | Writers | Readers | An exclusive open |
|---|---|---|---|---|---|
| 0 | Building | `B` (lock, manifest), `P` | as before | as before | (can't happen: the change holds the table) after a kill: deletes `P`, and `B` once it holds only bookkeeping |
| 1 | Staged | `R` complete; nothing moved | turned away | old table | undoes: retires `R`, deletes `B` |
| 2 | Old memo aside / new memo in | the old memo file in `B`; the new one staged or in place | turned away | told a change is under way while the memo is between renames | undoes: the old memo comes back from `B` over the new one, then as 1 |
| 3 | Publishing the decision | `B/restructure.commit.tmp` written and forced to disk, then renamed `restructure.commit` | turned away | as 2 | decided only if `restructure.commit` exists; the `.tmp` never counts |
| 4 | Decided | `restructure.commit` in `B`; each of `.dbf`, index and new memo file in one of its three places; an unused old memo file in place or in `B` | turned away | old or new table, or told a change is under way while the `.dbf` is between renames | rolls forward: moves every file still staged, puts an unused memo file in `B`, then retires `R` |
| 5 | Retiring | `R` renamed `X`; `X` being emptied | as after | as after | deletes `X` |
| 6 | Done | `B` holds the old files (and its bookkeeping until it's cleared) | the new table | the new table | clears `B`'s bookkeeping once nobody holds its lock |

**Undoing** (states 1 to 3): for each file the manifest names, if `B` holds the file that was at its destination, that file goes back over whatever is there now; otherwise, if the staged file is no longer in `R` but something is at the destination, the change put it there (a memo file or index the table didn't have), and it goes back into `R`. Then `R` is retired and `B`, holding only bookkeeping, deleted. The old table is byte for byte as it was, and nothing new is left beside it.

**Rolling forward** (state 4): for each of `.dbf`, index and new memo file, if it's still staged, the file at its destination (if any) goes into `B` and the staged one into place. Then the old memo file the new table doesn't use goes into `B`.

**The decision's publication can fail.** Writing or forcing `restructure.commit.tmp` failing leaves the change undecided: it's undone. Renaming it failing leaves it uncertain: if `restructure.commit` then exists, the change is decided and rolls forward; if it plainly doesn't, it's undone; if even that can't be told, nothing more is touched and the next exclusive open decides from what it finds.

**What the change reports.** Failing before the decision: the error says the table was left as it was (after undoing). Failing after it: `IncompleteTransaction`, saying the new structure is decided and the next exclusive open finishes it. Failing only to retire `R`: `IncompleteTransaction`, saying the new structure is in place and the next exclusive open clears the folder.

## Recovery killed, or running twice

- **Killed anywhere.** It moves files only under `B`'s lock, in the steps above, each of which it repeats from wherever the kill left it. The decision doesn't change while it runs, so the next open picks up the same direction.
- **Two at once.** The second waits for `B`'s lock, finds `R` gone (or naming another `B`), and does nothing. A later change can't start while `R` exists (it's refused with `IncompleteTransaction`), so `R` only ever names one `B` until that transaction is over.
- **Paused, then resumed after a new change began.** It read `R/restructure.id` for the old `B`. When it resumes, either the old `B` is gone (its lock can't be opened: it starts again), or it takes the old `B`'s lock and finds `R` naming the new one (it starts again and waits for the new `B`'s lock, which the new change holds). It never moves files for a transaction whose lock it doesn't hold.

## Opens

Before a writable exclusive open touches the table's files, it runs the recovery above; with no `R`, it deletes `P` and `X` and clears finished `B`s' bookkeeping. Any open that finds `R` turns a writer away (`IncompleteTransaction`). After opening the `.dbf`, it checks that the handle is the file now at the path, by device and inode (Linux and macOS, read with the C library's `fstat` and `stat`); a mismatch reopens, and three in a row refuse the open. On Windows the check isn't needed: a file .NET has open can't be renamed, so the change's move fails and it's undone. Where the inode can't be read reliably (32-bit processes, glibc before 2.33, any process whose `stat` layout doesn't check out against the file's size), there's no check, and a writer that opens the `.dbf` in the moment between the change's last look and its move can write into the backup: keep other Thunder.Dbf writers away from a table while its structure changes there.

`DbfDatabase` finds a table whose `.dbf` is in `B` between its two renames through `R`, so a writable exclusive database recovers it too.
