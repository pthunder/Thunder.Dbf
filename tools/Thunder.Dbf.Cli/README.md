# thunder-dbf

```
       .-----.
      /     /      ProjectThunder :: thunder-dbf
     /     /__     dBASE and FoxPro data from a terminal, no code needed.
    /__      /
      /    /
     //
    '
```

A .NET tool to check, inspect, import, export and migrate dBASE and FoxPro data. Read-only unless a command creates something, and it never shows record data unless you export it.

```bash
dotnet tool install --global thunder-dbf
```

| Command | Does |
|---|---|
| `thunder-dbf check <folder>` | Every table's readability, writability, locks, memos and indexes. Exit code 2 when there are problems. |
| `thunder-dbf info <table.dbf>` | Fields, record count, encoding, memo file and index tags. No data, so it's safe to paste into a bug report. |
| `thunder-dbf import <file.csv> [table.dbf]` | Creates a table from a CSV, with its shape worked out from the data. |
| `thunder-dbf export <table.dbf> [file.csv]` | Writes a table as UTF-8 CSV, spreadsheet-safe. |
| `thunder-dbf migrate <folder> <file.db>` | Copies every table into a SQLite database. |

## Learn more

- [Command-line guide](https://github.com/pthunder/Thunder.Dbf/blob/main/docs/cli.md)
- The [Thunder.Dbf](https://www.nuget.org/packages/Thunder.Dbf) library behind it, and [all the docs](https://github.com/pthunder/Thunder.Dbf/blob/main/docs/README.md)
- [Source and issues](https://github.com/pthunder/Thunder.Dbf)

MIT licensed. Made by [ProjectThunder](https://projectthunder.com).
