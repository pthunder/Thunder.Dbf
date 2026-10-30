# Thunder.Dbf

A pure C# engine for xBase files (dBASE, FoxPro, Clipper) that runs on Windows, macOS and Linux with no native dependencies. It's aimed at upgrading, migrating and bridging legacy apps.

## Status

Early. The DBF header and field descriptors can be read today.

## Roadmap

1. DBF records: read, write, append, delete flags, code pages
2. DBT memo files (dBASE III and IV)
3. MDX indexes (dBASE IV): read, seek, ordered scans
4. MDX writes that keep every tag in sync
5. dBASE-compatible record and file locking
6. FoxPro (`.cdx` / `.fpt`) and Clipper (`.ntx`) formats
7. Tools: a migrator to SQLite/SQL Server/Postgres, a live bridge, and an ADO.NET provider

## Build

```bash
dotnet test
```

## License

MIT
