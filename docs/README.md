# Thunder.Dbf documentation

```
       .-----.
      /     /      ProjectThunder :: Thunder.Dbf
     /     /__     Everything you need to put legacy dBASE and FoxPro data
    /__      /     to work, without breaking the app that still uses it.
      /    /
     /   /
    /  /
   //
  '
```

## Start here

- [Getting started](getting-started.md): install, connect, read, write, in five minutes.
- [Use cases](use-cases.md): what people build with it, from GoldMine web portals to Clipper migrations.

## Guides

| Guide | Covers |
|---|---|
| [Connecting](connecting.md) | Folders, connection strings, the `Check()` handshake, and every error code with its fix |
| [Reading and writing](reading-and-writing.md) | Records, field types, memos, nulls, autoincrement, bulk loads |
| [Indexes](indexes.md) | Reading and seeking with `.mdx`, writes that keep every tag in sync, rebuilding |
| [Locking](locking.md) | Sharing files safely with a running dBASE app |
| [Durability](durability.md) | What survives a crash or power cut, and what doesn't |
| [CSV import and export](csv.md) | A table's shape from a CSV, and spreadsheet-safe export |
| [ADO.NET provider](ado-net.md) | `DbfConnection`, parameterized SQL, readers, index-assisted lookups |
| [SQL: migrate and bridge](sql.md) | One-shot copies to SQLite, SQL Server or PostgreSQL, and live two-way sync |
| [Web service](web-service.md) | A safe JSON API over the legacy folder |
| [Command line](cli.md) | `thunder-dbf check`, `info`, `import`, `export`, `migrate` |
| [Security](security.md) | The threat model: what's protected, and what isn't |
| [Testing](testing.md) | Running the tests, and testing against your own private data |
| [FAQ](faq.md) | Quick answers |

## Reference

- File formats: [MDX](formats/mdx.md), [NDX](formats/ndx.md), [NTX](formats/ntx.md) and [CDX](formats/cdx.md) indexes.
- [Samples](../samples): runnable programs for each guide.

## For maintainers

- [Editorial guidelines](maintainers/editorial-guidelines.md): what every change must meet before it's published.
- [Maintaining](maintainers/maintaining.md): the weekly routine, releases, and GitHub settings.

---

Thunder.Dbf is made by [ProjectThunder](https://projectthunder.com).
