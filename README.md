# Thunder.Dbf

```
       .-----.
      /     /      T H U N D E R . D B F
     /     /__     a ProjectThunder joint
    /__      /
      /    /       dBASE  .  FoxPro  .  Clipper
     /   /         ...now on Windows AND Mac
    /  /
   / /
  //
  '
```

A pure C# engine for xBase files (dBASE, FoxPro, Clipper) that runs on Windows, macOS and Linux with no native dependencies. It's aimed at upgrading, migrating and bridging legacy apps.

## Status

Early. Today it can create DBF tables and read, append, update, delete and recall records. It handles character, numeric, float, date, logical and memo-pointer fields, plus Visual FoxPro's integer, currency, double and datetime fields. Text is decoded through the table's code page, or a shapefile-style `.cpg` file next to the table. Memo contents, indexes and locking come next.

It's tested against 28 real tables from dBASE III, dBASE IV, FoxPro 2, Visual FoxPro and the US Census Bureau (see [tests/fixtures/SOURCES.md](tests/fixtures/SOURCES.md)).

```csharp
using var table = DbfTable.Create("people.dbf",
[
    DbfFieldDefinition.Character("NAME", 20),
    DbfFieldDefinition.Numeric("BALANCE", 10, 2),
    DbfFieldDefinition.Date("HIRED"),
]);

table.Append("Smith", 1234.50m, new DateOnly(1997, 4, 12));

foreach (var record in table.ReadAll())
    Console.WriteLine($"{record["NAME"]} owes {record["BALANCE"]}");
```

## Roadmap

1. DBF records: read, write, append, delete flags, code pages
2. DBT memo files (dBASE III and IV)
3. MDX indexes (dBASE IV): read, seek, ordered scans
4. MDX writes that keep every tag in sync
5. dBASE-compatible record and file locking
6. FoxPro (`.cdx` / `.fpt`) and Clipper (`.ntx`) formats, including Visual FoxPro field flags (binary, nullable, autoincrement) and its two-digit-year headers
7. Tools: a migrator to SQLite/SQL Server/Postgres, a live bridge, and an ADO.NET provider

## Build

Needs the .NET 10 SDK.

```bash
dotnet test
```

## License

MIT
