//     _____________          _____________
//    | A | B  | C   |       | a,b,c       |     ProjectThunder :: Thunder.Dbf
//    |---|----|-----|  ---> | 1,x,1997-04 |     And back out again: any table to
//    | 1 | x  | ... |       | 2,y,2001-01 |     a CSV that Excel and DbfImport read.
//    |___|____|_____|       |_____________|

using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

/// <summary>Writes tables out as CSV.</summary>
public static class DbfExport
{
    /// <summary>
    /// Writes every live record (deleted ones too, with <paramref name="includeDeleted"/>) as UTF-8 CSV:
    /// dates as yyyy-MM-dd, logicals as T/F, numbers with a '.' decimal point, memos in full, binary as base64.
    /// Returns the number of rows written.
    /// </summary>
    public static int ToCsv(DbfTable table, string csvPath, bool includeDeleted = false)
    {
        using var writer = new StreamWriter(csvPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return ToCsv(table, writer, includeDeleted);
    }

    public static int ToCsv(DbfTable table, TextWriter writer, bool includeDeleted = false)
    {
        var columns = table.Fields.Select((f, i) => (f, i)).Where(x => !x.f.IsSystem).ToList();
        writer.WriteLine(string.Join(',', columns.Select(c => Quote(c.f.Name))));

        var rows = 0;
        foreach (var record in table.ReadAll())
        {
            if (record.IsDeleted && !includeDeleted)
                continue;
            writer.WriteLine(string.Join(',', columns.Select(c => Quote(Format(record[c.i])))));
            rows++;
        }
        return rows;
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        bool b => b ? "T" : "F",
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    // RFC 4180: quote when the value holds a comma, quote, newline, or leading/trailing space.
    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || (value.Length > 0 && (value[0] == ' ' || value[^1] == ' '))
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
}
