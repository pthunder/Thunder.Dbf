# Command line: `thunder-dbf`

```
       .-----.
      /     /      ProjectThunder :: thunder-dbf
     /     /__     The library from a terminal, no code needed.
    /__      /
      /    /
     /   /
    /  /
   //
  '
```

From 0.1.0, the first release on nuget.org (coming soon), install it as a global .NET tool:

```bash
dotnet tool install -g thunder-dbf
```

Until then, run it from the repository:

```bash
dotnet run --project tools/Thunder.Dbf.Cli -- <command>
```

Or install your own build as a .NET tool:

```bash
dotnet pack tools/Thunder.Dbf.Cli -o nupkg
```

```bash
dotnet tool install --global --add-source nupkg thunder-dbf
```

| Command | Does |
|---|---|
| `thunder-dbf check <folder>` | The handshake: every table's readability, writability, locks, encryption, memos and indexes. Exit code 2 when there are problems. |
| `thunder-dbf info <table.dbf>` | Fields, record count, encoding, memo file and index tags. It shows no data, so it's safe to paste into a bug report. |
| `thunder-dbf import <file.csv> [table.dbf]` | Creates a table from a CSV. Add `--plan` to only show the shape, `--shape-only` to create the empty table, plus `--delimiter <c>` and `--code-page <n>`. |
| `thunder-dbf export <table.dbf> [file.csv]` | Writes a table as UTF-8 CSV, spreadsheet-safe. Add `--include-deleted` for deleted records, or `--raw` to turn off formula protection. |
| `thunder-dbf migrate <folder> <file.db>` | Copies every table into a SQLite database. Add `--include-deleted` or `--replace`. |

Errors print their code and fix, and exit with code 2:

```
error [TableNotFound]: There's no table called 'CONTACT9'. Check the spelling; TableNames lists the tables in the folder.
```
