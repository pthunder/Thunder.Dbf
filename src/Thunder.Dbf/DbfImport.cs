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

    /// <summary>
    /// The CSV's encoding. Null means UTF-8, unless the file starts with a different byte-order mark.
    /// Bytes that aren't valid in it are an error, never silently replaced.
    /// </summary>
    public Encoding? CsvEncoding { get; init; }

    /// <summary>The new table's dialect: dBASE IV by default.</summary>
    public DbfTableFormat Format { get; init; } = DbfTableFormat.DbaseIV;

    /// <summary>
    /// Language driver for the new table: 0x01 is DOS 437, 0x02 is DOS 850. Unless set, Windows-1252 as
    /// the format marks it: 0x57 for dBASE, 0x03 for the FoxPros.
    /// </summary>
    public byte LanguageDriver
    {
        get => _languageDriver ?? (Format is DbfTableFormat.FoxPro2 or DbfTableFormat.VisualFoxPro ? (byte)0x03 : (byte)0x57);
        init => _languageDriver = value;
    }

    private readonly byte? _languageDriver;

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
    /// <summary>The fields the new table will have, in column order.</summary>
    public IReadOnlyList<DbfFieldDefinition> Fields => Columns.Select(c => c.Field).ToList();

    /// <inheritdoc />
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
    private static readonly string[] CompanionExtensions = [".dbf", ".dbt", ".fpt", ".mdx", ".cdx", ".cpg"];

    /// <summary>Reads the CSV and works out the table it would become, without creating anything.</summary>
    public static DbfImportPlan Plan(string csvPath, DbfImportOptions? options = null) =>
        Shape(csvPath, options ?? new DbfImportOptions());

    /// <summary>
    /// Creates <paramref name="dbfPath"/> from the CSV: the shape always, the rows unless
    /// <see cref="DbfImportOptions.ShapeOnly"/>. The CSV is streamed twice (once to shape, once to
    /// load), so memory stays flat however big it is. The table is built under temporary names and
    /// only moved into place when everything succeeded, so a failed import leaves nothing behind.
    /// </summary>
    public static DbfImportPlan FromCsv(string csvPath, string dbfPath, DbfImportOptions? options = null)
    {
        options ??= new DbfImportOptions();
        var fingerprint = Fingerprint(csvPath);
        var plan = Shape(csvPath, options);

        var finalPaths = plan.Fields.Any(f => f.Type == 'M')
            ? new[] { dbfPath, Path.ChangeExtension(dbfPath, options.Format is DbfTableFormat.FoxPro2 or DbfTableFormat.VisualFoxPro ? ".fpt" : ".dbt") }
            : [dbfPath];
        RefuseExisting(dbfPath);

        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbfPath))!, $".{Path.GetFileNameWithoutExtension(dbfPath)}.importing-{Guid.NewGuid():N}");
        var tempPaths = finalPaths.Select(p => temporary + Path.GetExtension(p)).ToArray();
        try
        {
            using (var stream = new FileStream(tempPaths[0], FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var memo = tempPaths.Length > 1 ? new FileStream(tempPaths[1], FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None) : null)
            using (var table = DbfTable.Create(stream, options.Format, plan.Fields, options.LanguageDriver, leaveOpen: true, memo, Path.GetFileName(dbfPath)))
            {
                if (!options.ShapeOnly)
                    Load(table, csvPath, options, plan);
            }

            // The CSV must not have changed between the shaping pass and the loading pass.
            if (!fingerprint.AsSpan().SequenceEqual(Fingerprint(csvPath)))
                throw new DbfException(DbfErrorCode.InvalidCsv, "The CSV changed while it was being imported.",
                    "Make sure nothing is writing to the file, then import again.", csvPath);

            // Publish the memo file before the table, so the table never appears without its memos.
            // If a move fails, take back whatever this import already published.
            RefuseExisting(dbfPath);
            var published = new List<string>();
            try
            {
                for (var i = tempPaths.Length - 1; i >= 0; i--)
                {
                    File.Move(tempPaths[i], finalPaths[i]);
                    published.Add(finalPaths[i]);
                }
            }
            catch
            {
                published.ForEach(File.Delete);
                throw;
            }
        }
        finally
        {
            foreach (var temp in tempPaths)
                File.Delete(temp);
        }
        return plan;
    }

    private static void Load(DbfTable table, string csvPath, DbfImportOptions options, DbfImportPlan plan)
    {
        var line = new int[1]; // the CSV line being loaded, for error messages
        try
        {
            // A Visual FoxPro table with nullable fields ends with the hidden _NullFlags, which takes a null.
            var rows = Rows(csvPath, options, plan, line);
            if (table.Fields.Count > plan.Columns.Count)
                rows = rows.Select(r => (object?[])[.. r, .. new object?[table.Fields.Count - r.Length]]);
            table.AppendRange(rows);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException && ex is not DbfException)
        {
            // The table names its field; name the CSV column too, since that's what the user sees.
            var column = plan.Columns.FirstOrDefault(c => ex.Message.Contains($"Field {c.Field.Name} ", StringComparison.Ordinal));
            var where = column is null ? "" : $", column '{column.CsvName}'";
            throw new DbfException(DbfErrorCode.InvalidValue, $"Row {line[0]}{where}: {ex.Message}",
                "Fix the value in the CSV, or widen the column with DbfImportOptions.Overrides.", csvPath, ex);
        }
    }

    private static void RefuseExisting(string dbfPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbfPath))!;
        var baseName = Path.GetFileNameWithoutExtension(dbfPath);
        var existing = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).FirstOrDefault(p =>
                Path.GetFileNameWithoutExtension(p).Equals(baseName, StringComparison.OrdinalIgnoreCase)
                && CompanionExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            : null;
        if (existing is not null)
            throw new DbfException(DbfErrorCode.TableAlreadyExists, $"'{Path.GetFileName(existing)}' already exists.",
                "Pick another table name, or move the old files away first.", existing);
    }

    private static byte[] Fingerprint(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return System.Security.Cryptography.SHA256.HashData(stream);
        }
        catch (Exception ex) when (DbfException.Translate(ex, path, FileAccess.Read) is { } translated)
        {
            throw translated;
        }
    }

    private static IEnumerable<object?[]> Rows(string csvPath, DbfImportOptions options, DbfImportPlan plan, int[] currentLine)
    {
        foreach (var record in ReadRecords(csvPath, options, plan.Delimiter).Skip(1))
        {
            currentLine[0] = record.Line;
            if (record.IsBlankLine)
                continue;

            var values = new object?[plan.Columns.Count];
            for (var c = 0; c < values.Length; c++)
            {
                var text = c < record.Fields.Count ? record.Fields[c] : "";
                try
                {
                    values[c] = Convert(text, plan.Columns[c].Field, options);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    throw new DbfException(DbfErrorCode.InvalidValue,
                        $"Row {record.Line}, column '{plan.Columns[c].CsvName}': '{text}' doesn't fit {plan.Columns[c].Field.Name} ({plan.Columns[c].Reason}).",
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

        public void Add(string raw, Encoding strict, DbfImportOptions options)
        {
            // Text is stored as written (only trailing spaces go, as the field pads with spaces anyway).
            MaxBytes = Math.Max(MaxBytes, strict.GetByteCount(raw.TrimEnd(' ')));
            var v = raw.Trim();
            if (v.Length == 0)
                return;

            NonEmpty++;
            CouldBeLogical &= IsWord(v, TrueWords) || IsWord(v, FalseWords);
            CouldBeDate &= ParseDate(v, options) is not null;

            // One optional sign, then the digits. Leading zeros (ZIP codes, account numbers like
            // +0007) mean an identifier, which stays text.
            var unsigned = v[0] is '+' or '-' ? v[1..] : v;
            LeadingZero |= unsigned.Length > 1 && unsigned[0] == '0' && !unsigned.StartsWith("0.", StringComparison.Ordinal);
            if (CouldBeNumber && unsigned.Length > 0 && char.IsAsciiDigit(unsigned[0])
                && decimal.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
            {
                Negative |= v[0] == '-';
                MaxDecimals = Math.Max(MaxDecimals, unsigned.Contains('.') ? unsigned.Length - unsigned.IndexOf('.') - 1 : 0);
                MaxWhole = Math.Max(MaxWhole, unsigned.Split('.')[0].TrimStart('0').Length);
            }
            else
                CouldBeNumber = false;
        }
    }

    private static DbfImportPlan Shape(string csvPath, DbfImportOptions options)
    {
        var delimiter = options.Delimiter ?? DetectDelimiter(csvPath, options);
        var strict = DbfEncoding.Strict(DbfEncoding.For(options.LanguageDriver));
        List<string>? header = null;
        ColumnStats[] stats = [];
        var rowCount = 0;
        var ragged = false;

        foreach (var record in ReadRecords(csvPath, options, delimiter))
        {
            if (header is null)
            {
                header = record.Fields;
                stats = header.Select(_ => new ColumnStats()).ToArray();
                continue;
            }
            if (record.IsBlankLine)
                continue;

            rowCount++;
            ragged |= record.Fields.Count > header.Count;
            for (var c = 0; c < stats.Length; c++)
            {
                var raw = c < record.Fields.Count ? record.Fields[c] : "";
                try
                {
                    stats[c].Add(raw, strict, options);
                }
                catch (EncoderFallbackException ex)
                {
                    throw new DbfException(DbfErrorCode.InvalidValue,
                        $"Row {record.Line}, column '{header[c]}': '{ex.CharUnknown}' can't be stored in the table's code page ({strict.WebName}).",
                        "Pick a code page that has it with LanguageDriver (or --code-page), or change the text.", csvPath, ex);
                }
            }
        }

        if (header is null || header.All(string.IsNullOrWhiteSpace))
            throw new DbfException(DbfErrorCode.InvalidCsv, "The CSV has no header row.",
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
                : Infer(csvName, name, stats[c], options.Format == DbfTableFormat.DbaseIII ? MaxNumericWidth - 1 : MaxNumericWidth));
        }

        if (ragged)
            warnings.Add($"Some rows have more values than the {header.Count} headers; the extras are ignored.");
        if (columns.Sum(col => col.Field.Length) + 1 > 4000)
            warnings.Add("The fields add up to more than dBASE IV's 4,000-byte record limit; use Overrides to narrow wide text columns.");

        return new DbfImportPlan(columns, rowCount, delimiter, warnings);
    }

    private static DbfImportColumn Infer(string csvName, string name, ColumnStats s, int maxNumericWidth)
    {
        if (s.NonEmpty == 0)
            return new(csvName, DbfFieldDefinition.Character(name, Math.Max(1, s.MaxBytes)), "always empty, kept as text");
        if (s.CouldBeLogical)
            return new(csvName, DbfFieldDefinition.Logical(name), "only yes/no values");
        if (s.CouldBeDate)
            return new(csvName, DbfFieldDefinition.Date(name), "every value is a date");

        if (s.CouldBeNumber && !s.LeadingZero)
        {
            var width = Math.Max(1, s.MaxWhole) + (s.Negative ? 1 : 0) + (s.MaxDecimals > 0 ? s.MaxDecimals + 1 : 0);
            if (width <= maxNumericWidth && s.MaxDecimals <= 15)
                return new(csvName, DbfFieldDefinition.Numeric(name, width, s.MaxDecimals), $"numbers up to {width} wide, {s.MaxDecimals} decimals");
        }

        if (s.MaxBytes > MaxCharacterWidth)
            return new(csvName, DbfFieldDefinition.Memo(name), $"text up to {s.MaxBytes} bytes, too long for a character field");

        var reason = s.LeadingZero && s.CouldBeNumber ? "text (values have leading zeros, like ZIP codes)" : $"text up to {s.MaxBytes} bytes";
        return new(csvName, DbfFieldDefinition.Character(name, Math.Max(1, s.MaxBytes)), reason);
    }

    private static object? Convert(string text, DbfFieldDefinition field, DbfImportOptions options)
    {
        // Character and memo fields keep the text exactly (tabs, line breaks, inner spaces).
        if (field.Type is 'C' or 'M')
            return text.Length == 0 ? null : text.TrimEnd(' ');

        var value = text.Trim();
        if (value.Length == 0)
            return null;

        return field.Type switch
        {
            'L' => IsWord(value, TrueWords) ? true : IsWord(value, FalseWords) ? false : throw new FormatException(),
            'D' => ParseDate(value, options) ?? throw new FormatException(),
            'N' or 'F' => decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
            'Y' => decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
            'I' => int.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
            'B' => double.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
            'T' => ParseDateTime(value, options) ?? throw new FormatException(),
            _ => value,
        };
    }

    private static bool IsWord(string value, string[] words) => words.Contains(value, StringComparer.OrdinalIgnoreCase);

    // ISO date-times ("2026-10-31T14:30:00", "2026-10-31 14:30", with seconds or milliseconds), or any date alone.
    private static DateTime? ParseDateTime(string value, DbfImportOptions options)
    {
        string[] formats = ["yyyy-MM-ddTHH:mm:ss.FFF", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss.FFF", "yyyy-MM-dd HH:mm"];
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
            return moment;
        return ParseDate(value, options)?.ToDateTime(TimeOnly.MinValue);
    }

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

    /// <summary>One CSV record, the line it started on, and whether it was a bare empty line (not "").</summary>
    private sealed record CsvRecord(List<string> Fields, int Line, bool IsBlankLine);

    private static StreamReader OpenCsv(string path, DbfImportOptions options)
    {
        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        if (options.CsvEncoding is { } chosen)
        {
            encoding = (Encoding)chosen.Clone();
            encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        }
        try
        {
            return new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
        }
        catch (Exception ex) when (DbfException.Translate(ex, path, FileAccess.Read) is { } translated)
        {
            throw translated;
        }
    }

    // Counts candidate separators outside quotes, across the whole first record (which may span lines).
    private static char DetectDelimiter(string path, DbfImportOptions options)
    {
        char[] candidates = [',', ';', '\t', '|'];
        var counts = new int[candidates.Length];
        using var reader = OpenCsv(path, options);
        var quoted = false;
        int next;
        while ((next = Read(reader, path)) >= 0)
        {
            var c = (char)next;
            if (c == '"')
                quoted = !quoted;
            else if (!quoted && c is '\r' or '\n')
                break;
            else if (!quoted && Array.IndexOf(candidates, c) is var at and >= 0)
                counts[at]++;
        }
        var best = Array.IndexOf(counts, counts.Max());
        return candidates[best];
    }

    // RFC 4180, one record at a time: quoted fields may hold delimiters, newlines and doubled quotes.
    // Hard limits, so one hostile line can't exhaust memory: dBASE tables hold at most 255 fields,
    // and no field can be bigger than the largest memo Thunder.Dbf will store.
    private const int MaxColumns = 1_024;

    private static IEnumerable<CsvRecord> ReadRecords(string path, DbfImportOptions options, char delimiter)
    {
        var maxFieldChars = DbfMemoFile.MaxMemoBytes;
        using var reader = OpenCsv(path, options);
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var started = false;       // anything (even "") seen in this record
        var line = 1;
        var recordLine = 1;

        int next;
        while ((next = Read(reader, path)) >= 0)
        {
            if (field.Length > maxFieldChars)
                throw new DbfException(DbfErrorCode.InvalidCsv, $"A value starting on line {recordLine} is longer than {maxFieldChars:N0} characters.",
                    "Check for a missing closing quote, or split the value; no field can hold that much.", path);
            if (record.Count > MaxColumns)
                throw new DbfException(DbfErrorCode.InvalidCsv, $"Line {recordLine} has more than {MaxColumns:N0} values.",
                    "Check the delimiter, or set DbfImportOptions.Delimiter; dBASE tables hold at most 255 fields.", path);

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
            {
                quoted = true;
                started = true;
            }
            else if (c == delimiter)
            {
                record.Add(field.ToString());
                field.Clear();
                started = true;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n')
                    reader.Read();
                record.Add(field.ToString());
                field.Clear();
                yield return new CsvRecord(record, recordLine, IsBlankLine: !started && record.Count == 1 && record[0].Length == 0);
                record = [];
                started = false;
                recordLine = ++line;
            }
            else
            {
                field.Append(c);
                started = true;
            }
        }

        if (quoted)
            throw new DbfException(DbfErrorCode.InvalidCsv, $"A quoted value starting on line {recordLine} never closes.",
                "Check for a missing closing quote (\") in the CSV.", path);
        if (started)
        {
            record.Add(field.ToString());
            yield return new CsvRecord(record, recordLine, IsBlankLine: false);
        }
    }

    private static int Read(StreamReader reader, string path)
    {
        try
        {
            return reader.Read();
        }
        catch (DecoderFallbackException ex)
        {
            throw new DbfException(DbfErrorCode.InvalidCsv, $"The CSV has bytes that aren't valid {reader.CurrentEncoding.WebName}.",
                "Save the CSV as UTF-8, or set DbfImportOptions.CsvEncoding to the encoding it was saved in (for example Windows-1252).", path, ex);
        }
    }
}
