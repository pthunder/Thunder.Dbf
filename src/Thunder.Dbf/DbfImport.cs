//     _____________          _____________
//    | a,b,c       |        | A | B  | C   |     ProjectThunder :: Thunder.Dbf
//    | 1,x,1997-04 |  --->  |---|----|-----|     Hand it a CSV, get back a table with
//    | 2,y,2001-01 |        | 1 | x  | ... |     the right shape. No Excel round trip.
//    |_____________|        |___|____|_____|
//
//   var plan = DbfImport.Plan("contacts.csv");          // look before you leap
//   DbfImport.FromCsv("contacts.csv", "CONTACTS.DBF");  // shape + rows
//   DbfImport.FromCsv("contacts.csv", "EMPTY.DBF", new() { ShapeOnly = true });

using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

/// <summary>How to read the CSV and shape the table.</summary>
public sealed record DbfImportOptions
{
    /// <summary>The field separator. Null detects comma, semicolon, tab or pipe from the header line.</summary>
    public char? Delimiter { get; init; }

    /// <summary>The CSV's encoding. Null means UTF-8, unless the file starts with a different byte-order mark.</summary>
    public Encoding? CsvEncoding { get; init; }

    /// <summary>Language driver for the new table: 0x57 (Windows-1252) by default; 0x01 is DOS 437, 0x02 is DOS 850.</summary>
    public byte LanguageDriver { get; init; } = 0x57;

    /// <summary>Create the empty table only (the "basic shape"), without loading rows.</summary>
    public bool ShapeOnly { get; init; }

    /// <summary>Date formats to recognise, tried in order. ISO dates and yyyyMMdd are always recognised.</summary>
    public IReadOnlyList<string> DateFormats { get; init; } = ["M/d/yyyy", "MM/dd/yyyy"];

    /// <summary>Force a column's type, by CSV header name: for example a ZIP column that must stay text.</summary>
    public IReadOnlyDictionary<string, DbfFieldDefinition>? Overrides { get; init; }
}

/// <summary>One CSV column and the field it becomes.</summary>
public sealed record DbfImportColumn(string CsvName, DbfFieldDefinition Field, string Reason);

/// <summary>What an import will create, before it creates anything.</summary>
public sealed record DbfImportPlan(IReadOnlyList<DbfImportColumn> Columns, int RowCount, char Delimiter, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<DbfFieldDefinition> Fields => Columns.Select(c => c.Field).ToList();

    public override string ToString()
    {
        var text = new StringBuilder($"{Columns.Count} columns, {RowCount} rows, delimiter '{(Delimiter == '\t' ? "\\t" : Delimiter.ToString())}'\n");
        foreach (var column in Columns)
        {
            var f = column.Field;
            var shape = f.Type switch { 'N' => $"N({f.Length},{f.DecimalCount})", 'C' => $"C({f.Length})", _ => f.Type.ToString() };
            text.AppendLine($"  {column.CsvName,-24} -> {f.Name,-10} {shape,-8} {column.Reason}");
        }
        foreach (var warning in Warnings)
            text.AppendLine($"  note: {warning}");
        return text.ToString();
    }
}

/// <summary>Creates DBF tables from CSV files, working out field types and widths from the data.</summary>
public static class DbfImport
{
    private const int MaxCharacterWidth = 254;
    private const int MaxNumericWidth = 20;
    private static readonly string[] TrueWords = ["T", "TRUE", "Y", "YES"];
    private static readonly string[] FalseWords = ["F", "FALSE", "N", "NO"];

    /// <summary>Reads the CSV and works out the table it would become, without creating anything.</summary>
    public static DbfImportPlan Plan(string csvPath, DbfImportOptions? options = null)
    {
        options ??= new DbfImportOptions();
        return Shape(csvPath, options);
    }

    /// <summary>
    /// Creates <paramref name="dbfPath"/> from the CSV: the shape always, the rows unless
    /// <see cref="DbfImportOptions.ShapeOnly"/>. Streams the file twice (once to shape, once to
    /// load), so memory stays flat however big the CSV is.
    /// </summary>
    public static DbfImportPlan FromCsv(string csvPath, string dbfPath, DbfImportOptions? options = null)
    {
        options ??= new DbfImportOptions();
        var plan = Shape(csvPath, options);

        using var table = DbfTable.Create(dbfPath, plan.Fields, options.LanguageDriver);
        if (options.ShapeOnly)
            return plan;

        var line = new int[1]; // the CSV line being loaded, for error messages
        try
        {
            table.AppendRange(Rows(csvPath, options, plan, line));
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException && ex is not DbfException)
        {
            // The table names its field; name the CSV column too, since that's what the user sees.
            var column = plan.Columns.FirstOrDefault(c => ex.Message.Contains($"Field {c.Field.Name} ", StringComparison.Ordinal));
            var where = column is null ? "" : $", column '{column.CsvName}'";
            throw new DbfException(DbfErrorCode.NotADbfFile, $"Row {line[0]}{where}: {ex.Message}",
                "Fix the value in the CSV, or widen the column with DbfImportOptions.Overrides.", csvPath, ex);
        }
        return plan;
    }

    private static IEnumerable<object?[]> Rows(string csvPath, DbfImportOptions options, DbfImportPlan plan, int[] currentLine)
    {
        var line = 1;
        foreach (var record in ReadRecords(csvPath, options, plan.Delimiter).Skip(1))
        {
            currentLine[0] = ++line;
            if (IsBlankLine(record))
                continue;

            var values = new object?[plan.Columns.Count];
            for (var c = 0; c < values.Length; c++)
            {
                var text = c < record.Count ? record[c] : "";
                try
                {
                    values[c] = Convert(text, plan.Columns[c].Field, options);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    throw new DbfException(DbfErrorCode.NotADbfFile,
                        $"Row {line}, column '{plan.Columns[c].CsvName}': '{text}' doesn't fit {plan.Columns[c].Field.Name} ({plan.Columns[c].Reason}).",
                        "Fix the value in the CSV, or force the column's type with DbfImportOptions.Overrides.", csvPath, ex);
                }
            }
            yield return values;
        }
    }

    // ---- shaping --------------------------------------------------------------------------

    /// <summary>Running facts about one column, so shaping never holds the data in memory.</summary>
    private sealed class ColumnStats
    {
        public int NonEmpty;
        public bool CouldBeLogical = true, CouldBeDate = true, CouldBeNumber = true;
        public bool LeadingZero, Negative;
        public int MaxWhole, MaxDecimals, MaxBytes;

        public void Add(string raw, Encoding encoding, DbfImportOptions options)
        {
            MaxBytes = Math.Max(MaxBytes, encoding.GetByteCount(raw.TrimEnd()));
            var v = raw.Trim();
            if (v.Length == 0)
                return;

            NonEmpty++;
            CouldBeLogical &= IsWord(v, TrueWords) || IsWord(v, FalseWords);
            CouldBeDate &= ParseDate(v, options) is not null;

            // Leading zeros (ZIP codes, account numbers) are text, not numbers.
            var unsigned = v.TrimStart('-');
            LeadingZero |= unsigned.Length > 1 && unsigned[0] == '0' && !unsigned.StartsWith("0.", StringComparison.Ordinal);
            if (CouldBeNumber && decimal.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
            {
                Negative |= v.StartsWith('-');
                MaxDecimals = Math.Max(MaxDecimals, v.Contains('.') ? v.Length - v.IndexOf('.') - 1 : 0);
                MaxWhole = Math.Max(MaxWhole, unsigned.Split('.')[0].TrimStart('0').Length);
            }
            else
                CouldBeNumber = false;
        }
    }

    private static DbfImportPlan Shape(string csvPath, DbfImportOptions options)
    {
        var delimiter = options.Delimiter ?? DetectDelimiter(csvPath, options);
        var encoding = DbfEncoding.For(options.LanguageDriver);
        List<string>? header = null;
        ColumnStats[] stats = [];
        var rowCount = 0;
        var ragged = false;

        foreach (var record in ReadRecords(csvPath, options, delimiter))
        {
            if (header is null)
            {
                header = record;
                stats = record.Select(_ => new ColumnStats()).ToArray();
                continue;
            }
            if (IsBlankLine(record))
                continue;

            rowCount++;
            ragged |= record.Count > header.Count;
            for (var c = 0; c < stats.Length; c++)
                stats[c].Add(c < record.Count ? record[c] : "", encoding, options);
        }

        if (header is null || header.All(string.IsNullOrWhiteSpace))
            throw new DbfException(DbfErrorCode.NotADbfFile, "The CSV has no header row.",
                "The first line must name the columns, like Name,City,Balance.", csvPath);

        var warnings = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<DbfImportColumn>();
        for (var c = 0; c < header.Count; c++)
        {
            var csvName = header[c];
            var name = FieldName(csvName, c, names);
            if (!string.Equals(name, csvName.Trim(), StringComparison.OrdinalIgnoreCase))
                warnings.Add($"'{csvName}' became {name} (dBASE field names are up to 10 letters, digits and underscores).");

            columns.Add(options.Overrides?.TryGetValue(csvName, out var forced) == true
                ? new(csvName, forced with { Name = name }, "set by Overrides")
                : Infer(csvName, name, stats[c]));
        }

        if (ragged)
            warnings.Add($"Some rows have more values than the {header.Count} headers; the extras are ignored.");
        if (columns.Sum(col => col.Field.Length) + 1 > 4000)
            warnings.Add("The fields add up to more than dBASE IV's 4,000-byte record limit; use Overrides to narrow wide text columns.");

        return new DbfImportPlan(columns, rowCount, delimiter, warnings);
    }

    private static DbfImportColumn Infer(string csvName, string name, ColumnStats s)
    {
        if (s.NonEmpty == 0)
            return new(csvName, DbfFieldDefinition.Character(name, 1), "always empty, kept as C(1)");
        if (s.CouldBeLogical)
            return new(csvName, DbfFieldDefinition.Logical(name), "only yes/no values");
        if (s.CouldBeDate)
            return new(csvName, DbfFieldDefinition.Date(name), "every value is a date");

        if (s.CouldBeNumber && !s.LeadingZero)
        {
            var width = Math.Max(1, s.MaxWhole) + (s.Negative ? 1 : 0) + (s.MaxDecimals > 0 ? s.MaxDecimals + 1 : 0);
            if (width <= MaxNumericWidth && s.MaxDecimals <= 15)
                return new(csvName, DbfFieldDefinition.Numeric(name, width, s.MaxDecimals), $"numbers up to {width} wide, {s.MaxDecimals} decimals");
        }

        if (s.MaxBytes > MaxCharacterWidth)
            return new(csvName, DbfFieldDefinition.Memo(name), $"text up to {s.MaxBytes} bytes, too long for a character field");

        var reason = s.LeadingZero && s.CouldBeNumber ? "text (values have leading zeros, like ZIP codes)" : $"text up to {s.MaxBytes} bytes";
        return new(csvName, DbfFieldDefinition.Character(name, Math.Max(1, s.MaxBytes)), reason);
    }

    private static object? Convert(string text, DbfFieldDefinition field, DbfImportOptions options)
    {
        var value = text.Trim();
        if (value.Length == 0)
            return null;

        return field.Type switch
        {
            'L' => IsWord(value, TrueWords) ? true : IsWord(value, FalseWords) ? false : throw new FormatException(),
            'D' => ParseDate(value, options) ?? throw new FormatException(),
            'N' or 'F' => decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
            _ => text.TrimEnd(),
        };
    }

    private static bool IsWord(string value, string[] words) => words.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool IsBlankLine(List<string> record) => record.Count == 1 && record[0].Length == 0;

    private static DateOnly? ParseDate(string value, DbfImportOptions options)
    {
        string[] formats = ["yyyy-MM-dd", "yyyyMMdd", .. options.DateFormats];
        return DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    /// <summary>A dBASE field name: up to 10 ASCII letters, digits and underscores, starting with a letter, unique.</summary>
    private static string FieldName(string csvName, int index, HashSet<string> taken)
    {
        var cleaned = new StringBuilder();
        foreach (var ch in csvName.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(ch))
                cleaned.Append(char.ToUpperInvariant(ch));
            else if ((char.IsWhiteSpace(ch) || ch is '_' or '-' or '.') && cleaned.Length > 0 && cleaned[^1] != '_')
                cleaned.Append('_');
        }

        var name = cleaned.ToString().Trim('_');
        if (name.Length == 0 || !char.IsAsciiLetter(name[0]))
            name = "F" + (name.Length == 0 ? (index + 1).ToString(CultureInfo.InvariantCulture) : name);
        if (name.Length > 10)
            name = name[..10];

        var candidate = name;
        for (var n = 2; !taken.Add(candidate); n++)
        {
            var suffix = n.ToString(CultureInfo.InvariantCulture);
            candidate = name[..Math.Min(name.Length, 10 - suffix.Length)] + suffix;
        }
        return candidate;
    }

    // ---- CSV, streamed --------------------------------------------------------------------

    private static StreamReader OpenCsv(string path, DbfImportOptions options)
    {
        try
        {
            return new StreamReader(path, options.CsvEncoding ?? new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        }
        catch (Exception ex) when (DbfException.Translate(ex, path, FileAccess.Read) is { } translated)
        {
            throw translated;
        }
    }

    private static char DetectDelimiter(string path, DbfImportOptions options)
    {
        using var reader = OpenCsv(path, options);
        var firstLine = reader.ReadLine() ?? "";
        char[] candidates = [',', ';', '\t', '|'];
        return candidates.MaxBy(c => firstLine.Count(ch => ch == c));
    }

    // RFC 4180, one record at a time: quoted fields may hold delimiters, newlines and doubled quotes.
    private static IEnumerable<List<string>> ReadRecords(string path, DbfImportOptions options, char delimiter)
    {
        using var reader = OpenCsv(path, options);
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;

        int next;
        while ((next = reader.Read()) >= 0)
        {
            var c = (char)next;
            if (quoted)
            {
                if (c == '"' && reader.Peek() == '"')
                {
                    field.Append('"');
                    reader.Read();
                }
                else if (c == '"')
                    quoted = false;
                else
                {
                    if (c == '\n')
                        line++;
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
                quoted = true;
            else if (c == delimiter)
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n')
                    reader.Read();
                line++;
                record.Add(field.ToString());
                field.Clear();
                yield return record;
                record = [];
            }
            else
                field.Append(c);
        }

        if (quoted)
            throw new DbfException(DbfErrorCode.NotADbfFile, $"A quoted value starting before line {line} never closes.",
                "Check for a missing closing quote (\") in the CSV.", path);
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }
}
