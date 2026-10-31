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

`AppendRange` commits every 10,000 rows (configurable) and at the end. If it fails, the rows before the failure are committed, `RecordCount` says how many, and the original error is what you get back.

Imports (`DbfImport.FromCsv`) build the new table under temporary names and move it into place only when everything succeeded, so a failed import leaves nothing behind. Exports write a temporary file and rename it.

## What it doesn't guarantee

- **No transactions.** A crash between writing the record and updating the index can leave the index missing that change. dBASE has the same window. `table.RebuildIndex()` repairs it, and a [`LocalDataTests`](testing.md)-style check confirms it.
- **FoxPro `.cdx` pages mid-change.** A `.cdx` change that touches several pages (a split, an emptied page, a rebuild) isn't atomic either, but its pages are written in an order that loses no key: new pages before the page that links them in, and a page goes on the free list only once nothing points at it. A crash in the middle can leave a few keys listed twice, a parent's last key out of step with its child's, or unused pages; a rebuild switches to its new tree in one header write, so a crash leaves the old tree or the new one. `RebuildIndex()` (or REINDEX in FoxPro) tidies any of it. Details: [cdx format notes](formats/cdx.md#what-a-crash-can-leave).
- **No protection from other programs.** Locks coordinate well-behaved programs (see [Locking](locking.md)); they can't stop a program that ignores them.
- **Network shares.** "Flushed to disk" means the file server acknowledged the write. Opportunistic locking and client caching can still delay it; turn oplocks off for data files (see [Locking](locking.md)).

## Backups

The [web service](web-service.md) copies each table before its first write after startup. For anything else, back up the whole data folder (tables, memos and indexes together) before letting new code write to it.
