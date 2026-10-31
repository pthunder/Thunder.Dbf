//        .-----.
//       /     /      ProjectThunder :: thunder-dbf
//      /     /__     -------------------------------
//     /__      /     The whole library from a terminal:
//       /    /         thunder-dbf check  \\server\goldmine\data
//      /   /           thunder-dbf import contacts.csv
//     /  /             thunder-dbf info   CONTACT1.DBF
//    / /               thunder-dbf export CONTACT1.DBF contacts.csv
//   //
//   '

using System.Globalization;
using Microsoft.Data.Sqlite;
using Thunder.Dbf;
using Thunder.Dbf.Sql;

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        thunder-dbf: dBASE and FoxPro tables from the command line

          thunder-dbf check  <folder>                 Test a data folder: what's readable, writable, locked or damaged
          thunder-dbf info   <table.dbf>              Show a table's fields, memo file and index tags
          thunder-dbf import <file.csv> [table.dbf]   Create a table from a CSV (types and widths worked out for you)
                 --plan                                 Only show the shape it would create
                 --shape-only                           Create the empty table, without rows
                 --delimiter <c>                        Field separator (detected if omitted)
                 --code-page <n>                        Table code page: 1252 (default), 437, 850, ...
          thunder-dbf migrate <folder> <file.db>      Copy every table into a SQLite database (the folder is only read)
                 --include-deleted                      Copy records marked deleted too
                 --replace                              Recreate SQL tables that already exist
          thunder-dbf export <table.dbf> [file.csv]   Write a table out as CSV (UTF-8)
                 --include-deleted                      Include records marked deleted
                 --raw                                  Don't protect spreadsheet users from formula text (=, +, -, @)
        """;

    public static int Run(string[] args)
    {
        if (args.Length < 2 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 || args[0] is "-h" or "--help" or "help" ? 0 : 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "check" => Check(args[1]),
                "info" => Info(args[1]),
                "import" => Import(args),
                "export" => Export(args),
                "migrate" when args.Length > 2 => Migrate(args),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (DbfException ex)
        {
            Console.Error.WriteLine($"error [{ex.Code}]: {ex.Message}");
            return 2;
        }
    }

    private static int Check(string folder)
    {
        using var db = DbfDatabase.Open(folder);
        var report = db.Check();
        Console.Write(report);
        return report.Ok ? 0 : 2;
    }

    private static int Info(string path)
    {
        using var table = DbfTable.Open(path, ignoreMissingMemo: true);
        Console.WriteLine($"{Path.GetFileName(path)}: {table.RecordCount:N0} records, version 0x{table.Header.Version:X2}, " +
            $"{table.Encoding.WebName}, last updated {table.Header.LastUpdate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?"}");
        foreach (var field in table.Fields)
        {
            var shape = field.Type is 'N' or 'F' ? $"{field.Type}({field.Length},{field.DecimalCount})" : $"{field.Type}({field.Length})";
            Console.WriteLine($"  {field.Name,-11} {shape}");
        }
        if (table.MemoFormat is { } memo)
            Console.WriteLine($"  memo file: {memo}");

        using var index = table.OpenProductionIndex();
        foreach (var tag in index?.Tags ?? [])
            Console.WriteLine($"  index tag {tag.Name,-10} {tag.KeyType}  {tag.Expression}" + (tag.ForExpression is { } f ? $"  FOR {f}" : ""));
        return 0;
    }

    private static int Import(string[] args)
    {
        var csv = args[1];
        var table = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : Path.ChangeExtension(csv, ".dbf");
        var options = new DbfImportOptions
        {
            ShapeOnly = args.Contains("--shape-only"),
            Delimiter = Option(args, "--delimiter") is { } d ? (d == "\\t" ? '\t' : d[0]) : null,
            LanguageDriver = Option(args, "--code-page") is { } cp ? DriverFor(int.Parse(cp, CultureInfo.InvariantCulture)) : (byte)0x57,
        };

        if (args.Contains("--plan"))
        {
            Console.Write(DbfImport.Plan(csv, options));
            return 0;
        }

        var plan = DbfImport.FromCsv(csv, table, options);
        Console.Write(plan);
        Console.WriteLine(options.ShapeOnly ? $"Created {table} (empty)." : $"Created {table} with {plan.RowCount:N0} rows.");
        return 0;
    }

    private static int Export(string[] args)
    {
        var path = args[1];
        var csv = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : Path.ChangeExtension(path, ".csv");
        using var table = DbfTable.Open(path);
        var rows = DbfExport.ToCsv(table, csv, new DbfExportOptions
        {
            IncludeDeleted = args.Contains("--include-deleted"),
            SpreadsheetSafe = !args.Contains("--raw"),
        });
        Console.WriteLine($"Wrote {rows:N0} rows to {csv}.");
        return 0;
    }

    private static int Migrate(string[] args)
    {
        using var db = DbfDatabase.Open(args[1]);
        using var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[2] }.ToString());
        sqlite.Open();
        var result = DbfMigrator.Migrate(db, sqlite, SqlDialect.Sqlite, new DbfMigrationOptions
        {
            IncludeDeleted = args.Contains("--include-deleted"),
            ReplaceExisting = args.Contains("--replace"),
        });
        foreach (var table in result.Tables)
            Console.WriteLine($"  {table.Table,-16} {table.Rows,10:N0} rows" + (table.SkippedDeleted > 0 ? $"  ({table.SkippedDeleted:N0} deleted left out)" : ""));
        Console.WriteLine($"Migrated {result.Rows:N0} rows from {result.Tables.Count} tables in {result.Elapsed.TotalSeconds:0.0} s.");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static byte DriverFor(int codePage) => codePage switch
    {
        437 => 0x01,
        850 => 0x02,
        1252 => 0x57,
        852 => 0x64,
        866 => 0x65,
        1250 => 0xC8,
        1251 => 0xC9,
        _ => throw new DbfException(DbfErrorCode.InvalidConnectionString, $"Code page {codePage} isn't one the import can mark in a table header.",
            "Use 1252, 437, 850, 852, 866, 1250 or 1251."),
    };

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
