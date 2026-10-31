# Locking

```
     ____                          ProjectThunder :: Thunder.Dbf
    |    |  record 3 is mine       The same byte-range locks dBASE IV and the Borland
    | () |  ...wait your turn      Database Engine take, so Thunder.Dbf and a running
    |____|                         dBASE app can share one set of files.
```

## The schemes

xBase programs lock bytes far past the end of the file, so locks never cover real data. Each family picked its own offsets, and a lock only protects anything when every program uses the same ones:

| Scheme | Header and append | Record *n* | Whole table |
|---|---|---|---|
| **dBASE** (dBASE III/IV, the Borland Database Engine) | 4,026,531,838 (header), 4,026,531,839 (append) | 4,026,531,838 − *n* | 3,026,531,838 for 1,000,000,000 bytes, plus header and append |
| **Clipper** (and Harbour's default) | 1,000,000,000 | 1,000,000,000 + *n* | 1,000,000,001 for 1,000,000,000 bytes |
| **Visual FoxPro**, no `.cdx` | 0x40000000 | 0x40000000 + the record's byte offset | 0x40000001 for 0x3FFFFFFD bytes |
| **Visual FoxPro**, with a `.cdx` | 0x7FFFFFFE | 0x7FFFFFFE − *n* | 0x77FFFFFF for 0x07FFFFFF bytes |

The Clipper and Visual FoxPro offsets follow Harbour's DBF driver (read, not copied).

`Auto`, the default, picks Visual FoxPro for FoxPro tables and dBASE for everything else. Clipper tables look like dBASE ones from the outside, so a Clipper app needs the scheme set explicitly:

```csharp
var db = DbfDatabase.Open(folder, new DbfDatabaseOptions { ReadOnly = false, LockScheme = DbfLockScheme.Clipper });
// or in a connection string:
var db2 = DbfDatabase.Open(@"Data Source=\\server\app\data;Read Only=false;Lock Scheme=Clipper");
// or per table:
table.LockScheme = DbfLockScheme.Clipper;
```

Inside one process, every `DbfTable` open on the same file shares one set of locks, so two connections in the same web app see each other's locks too (the operating system wouldn't tell them apart).

## What Thunder.Dbf locks

| Operation | Locks |
|---|---|
| `Append`, `AppendRange` | header + append, for the whole batch; the record count is re-read first |
| `Update`, `Delete`, `Recall` | that record + the header |
| `RebuildIndex` | the whole table |
| Reads | nothing, like dBASE |

Files are opened shared, as dBASE opens them in multi-user mode, so the legacy app keeps working. If another program holds a lock, a write waits up to `LockTimeout` (3 seconds by default), then fails with `RecordLocked` or `TableLocked` (HTTP 423).

For several steps that must happen together, hold the lock yourself:

```csharp
using (table.LockRecord(5))      // like RLOCK()
{
    var current = table.Read(5);
    table.Update(5, Adjusted(current));
}
using (table.LockTable()) { }    // like FLOCK()
```

Locks nest: a write inside your own lock on the same record doesn't wait for itself.

### What the locks don't cover

Locks are taken on the `.dbf` only, as dBASE does. The memo and index files have no locks of their own; they're protected because every program that writes them holds the table's header or record lock while it does. A program that changes a `.dbt` or `.mdx` without taking those locks (some old repair tools do) isn't held off.

Clipper and Harbour also lock an `.ntx` file itself while they change it (byte 1,000,000,000, or Harbour's 64-bit position for large-file indexes). Thunder.Dbf takes the same lock around every `.ntx` change, so the two programs never rewrite the same index page at once. dBASE III doesn't lock `.ndx` files; it relies on the table's locks, and so does Thunder.Dbf. Visual FoxPro's `.cdx` lock isn't taken yet, which is one reason `.cdx` writing is still a preview.

Single-file indexes (`.ndx`, `.ntx`, `.idx`) don't record which table they belong to. See [Which indexes are kept](indexes.md#which-indexes-are-kept).

## Where it works

| Platform | Locks |
|---|---|
| Windows, local or on a file share | Enforced |
| Linux, local or NFS | Enforced (POSIX record locks) |
| macOS | **Not enforced:** .NET can't lock part of a file on macOS, and macOS doesn't pass byte-range locks over SMB anyway |

`table.LocksEnforced` tells you which you have, and `Check()` warns when locks aren't enforced. Set `table.RequireLocks = true` to refuse writes rather than write uncoordinated. For anything that shares files with a live legacy app, run on Windows (on or next to the file server) or Linux.

## Windows file shares

Opportunistic locking ("oplocks") lets a client cache a file, which defeats byte-range locks between machines. It caused much of the classic dBASE and GoldMine corruption on busy networks. On the server that holds the data, turn it off for the data files:

- **Samba:** add `veto oplock files = /*.dbf/*.DBF/*.dbt/*.DBT/*.mdx/*.MDX/*.cdx/*.CDX/*.fpt/*.FPT/` to the share.
- **Windows Server:** disable oplocks or leasing for the share (see Microsoft's guidance for database files on SMB shares).

Better still, run the [web service](web-service.md) on the file server itself, so only one process writes and the network carries JSON rather than file locks.
