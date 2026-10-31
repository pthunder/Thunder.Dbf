# Thunder.Dbf

```
       .-----.
      /     /      ProjectThunder :: Thunder.Dbf
     /     /__     dBASE . FoxPro . Clipper, in pure .NET
    /__      /
      /    /
     /   /
    //
   '
```

**Put a safe, modern app on top of a legacy dBASE or FoxPro system, while the legacy app keeps running on the same files.**

Thunder.Dbf reads and writes xBase tables, memos and indexes exactly as the old apps expect, takes the same locks they do, and never needs ODBC, OLE DB, Jet or a 32-bit process. It has no dependencies and runs on Windows, Linux and macOS.

```bash
dotnet add package Thunder.Dbf
```

```csharp
using Thunder.Dbf;

using var db = DbfDatabase.Open(@"\\server\goldmine\data");   // read-only unless you say otherwise
Console.Write(db.Check());                                      // what's readable, writable, locked or damaged

var contacts = db["CONTACT1"];
using var index = contacts.OpenProductionIndex()!;
var acme = contacts.Seek(index["CONTCOMP"], "ACME");            // like dBASE's SEEK, through the app's own index
```

## What it does

- **Tables:** dBASE III and IV, FoxPro 2 and Visual FoxPro, including nulls, autoincrement and over 60 code pages.
- **Memos:** `.dbt` and `.fpt`.
- **Indexes:** dBASE IV `.mdx` read and kept exact on every write; `.ndx`, `.ntx` and `.cdx`/`.idx` read and seek.
- **Locking:** record and table locks at dBASE, Clipper or Visual FoxPro offsets, so writes coexist with the legacy app.
- **ADO.NET provider:** `DbfConnection`, `DbfCommand` and `DbfDataReader` with parameterized SQL.
- **CSV:** import with the table's shape worked out from the data, and spreadsheet-safe export.
- **Safe by default:** read-only unless you ask, errors that never leak paths, and hostile files that can't crash or hang the process.

## Learn more

- [Getting started](https://github.com/pthunder/Thunder.Dbf/blob/main/docs/getting-started.md)
- [All the guides](https://github.com/pthunder/Thunder.Dbf/blob/main/docs/README.md): connecting, reading and writing, indexes, locking, durability, CSV, ADO.NET, security
- [Source, samples and issues](https://github.com/pthunder/Thunder.Dbf)
- Related packages: [Thunder.Dbf.Sql](https://www.nuget.org/packages/Thunder.Dbf.Sql) for SQL migration and live sync, and the [thunder-dbf](https://www.nuget.org/packages/thunder-dbf) command-line tool

MIT licensed. Made by [ProjectThunder](https://projectthunder.com).
