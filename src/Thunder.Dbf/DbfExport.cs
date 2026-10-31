//     _____________          _____________
//    | A | B  | C   |       | a,b,c       |     ProjectThunder :: Thunder.Dbf
//    |---|----|-----|  ---> | 1,x,1997-04 |     And back out again: any table to
//    | 1 | x  | ... |       | 2,y,2001-01 |     a CSV that Excel and DbfImport read.
//    |___|____|_____|       |_____________|

using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

/// <summary>How to write a table out as CSV.</summary>
public sealed record DbfExportOptions
{
    /// <summary>Include records marked deleted.</summary>
    public bool IncludeDeleted { get; init; }

    /// <summary>
    /// Protect spreadsheet users: text starting with = + - @ or a tab/carriage return gets a leading
    /// apostrophe, so Excel shows it instead of running it as a formula. On by default; turn it off
    /// only when the CSV goes to another program, not a spreadsheet.
    /// </summary>
    public bool SpreadsheetSafe { get; init; } = true;
}

/// <summary>Writes tables out as CSV.</summary>
public static class DbfExport
{
    private static readonly string[] TableFileExtensions = [".dbf", ".dbt", ".fpt", ".mdx", ".cdx", ".ndx", ".ntx", ".idx", ".cpg"];

    /// <summary>
    /// Writes the table as UTF-8 CSV: dates as yyyy-MM-dd, logicals as T/F, numbers with a '.' decimal
    /// point, memos in full, binary as base64. The file is written under a temporary name and moved
    /// into place at the end, and never over a table, memo or index file. Returns the rows written.
    /// </summary>
    public static int ToCsv(DbfTable table, string csvPath, DbfExportOptions? options = null)
    {
        var destination = Path.GetFullPath(csvPath);
        if (TableFileExtensions.Contains(Path.GetExtension(destination), StringComparer.OrdinalIgnoreCase))
            throw new DbfException(DbfErrorCode.TableAlreadyExists, $"Refusing to export over '{Path.GetFileName(destination)}', which is a table, memo or index file.",
                "Export to a .csv file.", destination);

        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.exporting-{Guid.NewGuid():N}");
        try
        {
            int rows;
            using (var writer = new StreamWriter(temporary, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
                rows = ToCsv(table, writer, options);
            File.Move(temporary, destination, overwrite: true);
            return rows;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static int ToCsv(DbfTable table, TextWriter writer, DbfExportOptions? options = null)
    {
        options ??= new DbfExportOptions();
        var columns = table.Fields.Select((f, i) => (f, i)).Where(x => !x.f.IsSystem).ToList();
        writer.WriteLine(string.Join(',', columns.Select(c => Quote(Text(c.f.Name, options)))));

        var rows = 0;
        foreach (var record in table.ReadAll())
        {
            if (record.IsDeleted && !options.IncludeDeleted)
                continue;
            writer.WriteLine(string.Join(',', columns.Select(c => Quote(Format(record[c.i], options)))));
            rows++;
        }
        return rows;
    }

    private static string Format(object? value, DbfExportOptions options) => value switch
    {
        null => "",
        string s => Text(s, options),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        bool b => b ? "T" : "F",
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    // Only text is neutralised: numbers like -20.00 are numbers, not formulas.
    private static string Text(string s, DbfExportOptions options) =>
        options.SpreadsheetSafe && s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + s : s;

    // RFC 4180: quote when the value holds a comma, quote, newline, or leading/trailing space.
    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || (value.Length > 0 && (value[0] == ' ' || value[^1] == ' '))
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
}
