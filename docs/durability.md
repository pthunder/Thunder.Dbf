# Durability

```
   memo -> records -> header -> index        ProjectThunder :: Thunder.Dbf
     |        |         |         |          Each step reaches the disk before the next
   disk     disk      disk      disk         starts, so a surviving count never points at
                                             data that didn't make it, and an index never
                                             points at a record that isn't there.
```

## What Thunder.Dbf guarantees

Every write commits in this order, each flushed to the disk itself (not just the operating system's cache) before the next:

1. new memo blocks
2. the record bytes
3. the table header, whose record count makes new records visible
4. the production `.mdx` index

So after a crash or a power cut:

- **A completed write** is on disk.
- **An append that was cut short** is invisible: the header still has the old count, and dBASE ignores bytes past it.
- **A memo** written before the cut, with no record yet pointing at it, is wasted space, never corruption.
- **An index** can at worst be missing the entries for the last write. It never points at a record that doesn't exist. `table.RebuildIndex()` puts the missing entries back, and `Check()` reports an index it can't read.

If an index update itself fails (a damaged `.mdx`, say), the changes already made to that index are undone and the write fails with `IndexDamaged`, naming the tag.

### Turning forced flushes off

Forcing each step to the disk (FlushFileBuffers on Windows, fsync elsewhere) is most of what an append or update costs: several forced writes per record, against none in Visual FoxPro or its OLE DB driver. `table.FlushWritesToDisk = false` (`DbfDatabaseOptions.FlushWritesToDisk`, `Flush Writes To Disk=false`) leaves those writes to the operating system, which then writes them out in its own time. They still leave Thunder.Dbf in the same order, so:

- **A killed process** (a crash, Task Manager, a lost network session on this side) loses nothing more than with forced flushes: every step it finished is in the operating system's hands, in order, and the crash-safe key order above still holds.
- **A power cut or an operating-system crash** can lose the last writes, and the disk may have taken them in another order, so after one the table and its indexes can disagree: `Check()` and a rebuild (`RebuildIndex()`, or REINDEX in FoxPro) put that right. That's the same risk Visual FoxPro's own writes carry.

The journal (`UseJournal`) and PACK force their writes whatever this says. It's on by default.

`AppendRange` commits every 10,000 rows (configurable) and at the end. If it fails, the rows before the failure are committed, `RecordCount` says how many, and the original error is what you get back.

Imports (`DbfImport.FromCsv`) build the new table under temporary names and move it into place only when everything succeeded, so a failed import leaves nothing behind. Exports write a temporary file and rename it.

## What it doesn't guarantee

- **No transactions.** A crash between writing the record and updating the index can leave the index missing that change. dBASE has the same window. Two cases of it are caught:
  - **An append cut off after its record but before the header's count:** whole records sit past the count, which Visual FoxPro reports as a corrupted table (error 2091). `Check()` reports them. A shared writer can't tell them from another program's append in progress, so it leaves them; opening the table with `writable: true, exclusive: true` takes them off and rebuilds the indexes it keeps.
  - **An append cut off after the count but before the keys:** a `.cdx` tag is short of the table. On open, a tag with no FOR filter and no UNIQUE must hold one key per record, and the last two records must have their keys in every tag that includes them (UNIQUE tags aside, FOR tags included); otherwise the index is refused (`IndexWouldGoStale`, with the reason in `Diagnostic`). Open the table with `writable: true, exclusive: true, maintainFoxProIndexes: true` and call `MaintainIndex(path, rebuild: true)`, or REINDEX in FoxPro. The check reads the tree without holding Visual FoxPro's index lock, then confirms under it that the update counter and record count didn't move, and repeats if they did.

  An update, delete or recall that changes keys writes the new keys first, then the record, then removes the old keys, each forced to disk before the next. Cut off between those steps, it leaves the record with an extra key, never without one. Two exceptions: UNIQUE tags, where a key belongs to one record, change after the record (old key out, new key in), so a cut-off write can leave one without its key; and a record cut off *while* being written can hold neither its old nor its new values. Appends write the record, the count, then the keys (an append cut off is caught when the table opens, above). Without the journal, this costs one extra forced write of the indexes per key change that adds a key.

  With the opt-in **journal** (`table.UseJournal = true`, `DbfDatabaseOptions.Journal`, `Journal=true`), each update, delete or recall that changes keys first notes its record and its old and new keys, tag by tag, in `TABLE.DBF.journal`, and empties it once everything is committed: two more forced writes per such write. Appends are noted too, once per batch (each `Append`, or each `commitEvery` rows of `AppendRange`): where the batch starts and which indexes the writer kept. A batch cut off before the header counted it is taken off by the next write, shared or not, since the note proves those records were never counted; one cut off after the count gets the keys it's missing. The next write to the table, in any Thunder.Dbf process keeping the same indexes, finishes a change that was cut off, under the header lock and the record's lock, so every noted tag ends up holding exactly the key the record has (UNIQUE tags included); a key the record has is never removed on the way. A torn note was cut off before any index change and is dropped. A note naming an index the table isn't keeping stops writes until the table is opened with that index or every index is rebuilt; so does a note in another version of the format (`RebuildIndex()` on a table opened exclusively clears it). A `.cdx` the cut-off write left out of step is attached on probation while the note waits, and checked again once it's finished. Two limits: when a UNIQUE tag hands a key from one record to another (dBASE gives it to the lowest record left with it), that hand-over isn't noted, so a cut-off there can still leave the key without a holder until a rebuild; and the forced writes cover every index the table keeps, not only the ones a write changed. Other programs ignore the file. `Check()` reports a note that's waiting.
- **FoxPro `.cdx` pages mid-change.** A `.cdx` change that touches several pages (a split, an emptied page, a rebuild) isn't atomic either, but its pages are written in an order that loses no key: new pages before the page that links them in, and a page goes on the free list only once nothing points at it. A crash in the middle can leave a few keys listed twice, a parent's last key out of step with its child's, or unused pages; a rebuild writes its new root over the old one in one page write, so a crash leaves the old tree or the new one. `RebuildIndex()` (or REINDEX in FoxPro) tidies most of it; a crash mid-split can leave sibling links Thunder.Dbf's checks refuse, which only FoxPro's REINDEX repairs today. Details: [cdx format notes](formats/cdx.md#what-a-crash-can-leave).
- **No protection from other programs.** Locks coordinate well-behaved programs (see [Locking](locking.md)); they can't stop a program that ignores them.
- **Network shares.** "Flushed to disk" means the file server acknowledged the write. Opportunistic locking and client caching can still delay it; turn oplocks off for data files (see [Locking](locking.md)).

## Backups

The [web service](web-service.md) copies each table before its first write after startup. For anything else, back up the whole data folder (tables, memos and indexes together) before letting new code write to it.
