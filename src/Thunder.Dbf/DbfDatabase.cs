//      ____________________________
//     | \\server\goldmine\data     |     ProjectThunder :: Thunder.Dbf
//     |____________________________|     Point it at the folder. That's it.
//        |   |   |   |   |   |
//      CONTACT1 CONTACT2 CAL HISTORY     Tables, memos, indexes and code pages
//        .dbf .dbt .mdx .cpg ...          are found and matched up for you.
//
//   using var db = DbfDatabase.Open(@"\\server\goldmine\data");   // read-only by default
//   var report = db.Check();                                       // the "Test connection" button
//   foreach (var row in db["CONTACT1"].ReadAll()) { ... }

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Thunder.Dbf;

/// <summary>How to open a folder of tables. The defaults are the safe ones.</summary>
public sealed record DbfDatabaseOptions
{
    /// <summary>Read-only unless you say otherwise, so pointing a tool at live data can't change it.</summary>
    public bool ReadOnly { get; init; } = true;

    /// <summary>Text encoding for every table, overriding what each table's header or .cpg says.</summary>
    public Encoding? Encoding { get; init; }

    public bool IgnoreMissingMemo { get; init; }
    public bool AllowUnmaintainedIndexes { get; init; }
}

/// <summary>A folder of DBF tables, opened as one database.</summary>
public sealed class DbfDatabase : IDisposable
{
    private static readonly string[] CompanionExtensions = [".dbt", ".fpt", ".mdx", ".cdx", ".cpg"];

    private readonly Dictionary<string, DbfTable> _open = new(StringComparer.OrdinalIgnoreCase);

    private DbfDatabase(string folder, DbfDatabaseOptions options)
    {
        Folder = folder;
        Options = options;
    }

    public string Folder { get; }
    public DbfDatabaseOptions Options { get; }
    public bool ReadOnly => Options.ReadOnly;

    /// <summary>
    /// Opens a folder of tables. <paramref name="folderOrConnectionString"/> is either a path or a
    /// connection string such as <c>Data Source=C:\Data;Read Only=false;Code Page=1252</c>.
    /// </summary>
    public static DbfDatabase Open(string folderOrConnectionString, DbfDatabaseOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(folderOrConnectionString))
            throw new DbfException(DbfErrorCode.InvalidConnectionString, "No folder or connection string was given.",
                @"Pass the data folder, like C:\GoldMine\Data or \\server\share\data.");

        var (folder, parsed) = LooksLikeConnectionString(folderOrConnectionString)
            ? ParseConnectionString(folderOrConnectionString, options ?? new DbfDatabaseOptions())
            : (folderOrConnectionString, options ?? new DbfDatabaseOptions());

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DbfException(DbfErrorCode.InvalidConnectionString, $"'{folder}' isn't a valid folder path.",
                "Check for stray quotes or characters the file system doesn't allow.", inner: ex);
        }

        if (!Directory.Exists(fullPath))
            throw new DbfException(DbfErrorCode.FolderNotFound, "The folder doesn't exist or isn't reachable.",
                "Check the path, that the network share is mounted, and that this account can see it.", fullPath);

        try
        {
            _ = Directory.EnumerateFiles(fullPath).FirstOrDefault();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DbfException(DbfErrorCode.AccessDenied, "The operating system refused to list the folder.",
                $"Grant the account running this program ({Environment.UserName}) list and read permission on the folder.", fullPath, ex);
        }

        return new DbfDatabase(fullPath, parsed);
    }

    /// <summary>Every table in the folder, by name without the extension.</summary>
    public IReadOnlyList<string> TableNames =>
        Directory.EnumerateFiles(Folder)
            .Where(p => Path.GetExtension(p).Equals(".dbf", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList()!;

    /// <summary>The table called <paramref name="name"/> (with or without ".dbf"), opened once and kept until the database is disposed.</summary>
    public DbfTable this[string name]
    {
        get
        {
            var path = ResolveTablePath(name);
            var key = Path.GetFileNameWithoutExtension(path);
            if (!_open.TryGetValue(key, out var table))
            {
                table = OpenTableAt(path);
                _open[key] = table;
            }
            return table;
        }
    }

    /// <summary>Opens a separate instance of a table that the caller owns and disposes.</summary>
    public DbfTable OpenTable(string name) => OpenTableAt(ResolveTablePath(name));

    /// <summary>
    /// The handshake: reads every table's header (never its records), and reports what's readable,
    /// writable, locked, encrypted or damaged, with a hint for each problem.
    /// </summary>
    public DbfCheckReport Check()
    {
        var timer = Stopwatch.StartNew();
        var reports = TableNames.Select(CheckTable).ToList();
        return new DbfCheckReport(Folder, ReadOnly, timer.Elapsed, reports);
    }

    public void Dispose()
    {
        foreach (var table in _open.Values)
            table.Dispose();
        _open.Clear();
    }

    // ---- names and paths ------------------------------------------------------------------

    /// <summary>
    /// Turns a table name into a path inside the folder, refusing anything that could reach outside it:
    /// separators, "..", drive letters, invalid characters, and links that point elsewhere.
    /// </summary>
    internal string ResolveTablePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.IndexOfAny(['/', '\\', ':']) >= 0
            || name.Contains("..", StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || Path.IsPathRooted(name))
        {
            throw new DbfException(DbfErrorCode.PathEscapesFolder, $"'{name}' isn't a plain table name.",
                "Use just the table's name, like CONTACT1 or CONTACT1.DBF. Paths, '..' and drive letters are refused so a request can't reach files outside the data folder.");
        }

        var extension = Path.GetExtension(name);
        if (extension.Length > 0 && !extension.Equals(".dbf", StringComparison.OrdinalIgnoreCase))
            throw new DbfException(DbfErrorCode.TableNotFound, $"'{name}' isn't a table name.",
                "Tables end in .dbf. Memo (.dbt/.fpt) and index (.mdx/.cdx) files are opened with their table automatically.");

        var wanted = Path.GetFileNameWithoutExtension(name);
        var path = Directory.EnumerateFiles(Folder)
            .FirstOrDefault(p => Path.GetExtension(p).Equals(".dbf", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(p).Equals(wanted, StringComparison.OrdinalIgnoreCase));

        if (path is null)
        {
            var available = TableNames;
            var listed = available.Count == 0 ? "The folder has no .dbf tables." : $"Tables here: {string.Join(", ", available.Take(12))}{(available.Count > 12 ? ", ..." : "")}.";
            throw new DbfException(DbfErrorCode.TableNotFound, $"There's no table called '{wanted}'.", listed, Folder);
        }

        EnsureStaysInFolder(path);
        foreach (var companion in Companions(path))
            EnsureStaysInFolder(companion);
        return path;
    }

    private IEnumerable<string> Companions(string tablePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(tablePath);
        return Directory.EnumerateFiles(Folder).Where(p =>
            Path.GetFileNameWithoutExtension(p).Equals(baseName, StringComparison.OrdinalIgnoreCase)
            && CompanionExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase));
    }

    private void EnsureStaysInFolder(string path)
    {
        var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        var folder = Folder.EndsWith(Path.DirectorySeparatorChar) ? Folder : Folder + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(target).StartsWith(folder, comparison))
            throw new DbfException(DbfErrorCode.PathEscapesFolder, $"'{Path.GetFileName(path)}' is a link to a file outside the data folder.",
                "Replace the link with the real file, or connect to the folder the link points into.", path);
    }

    private DbfTable OpenTableAt(string path) =>
        DbfTable.Open(path, writable: !Options.ReadOnly, Options.Encoding, Options.IgnoreMissingMemo, Options.AllowUnmaintainedIndexes);

    // ---- the handshake --------------------------------------------------------------------

    private DbfTableReport CheckTable(string name)
    {
        var problems = new List<DbfException>();
        var warnings = new List<string>();
        int? records = null;
        string? format = null, encoding = null, memo = null;
        IReadOnlyList<string> tags = [];
        var canRead = false;
        var canWrite = false;

        try
        {
            var path = ResolveTablePath(name);
            using var table = DbfTable.Open(path, writable: false, Options.Encoding, Options.IgnoreMissingMemo);
            canRead = true;
            records = table.RecordCount;
            format = DescribeFormat(table.Header);
            encoding = $"{table.Encoding.WebName} (code page {table.Encoding.CodePage})";
            memo = table.MemoFormat?.ToString();

            if (Options.Encoding is null && DbfEncoding.CodePageFor(table.Header.LanguageDriver) is null
                && !File.Exists(Path.ChangeExtension(path, ".cpg")))
            {
                warnings.Add($"The table doesn't record its code page (byte 29 is 0x{table.Header.LanguageDriver:X2}), so text is read as {table.Encoding.WebName}. " +
                    "If accented letters look wrong, set DbfDatabaseOptions.Encoding.");
            }
            if (table.Header.HasIncompleteTransaction)
                warnings.Add("dBASE IV marked a transaction as unfinished; writes will be refused until the legacy app rolls it back.");

            if (table.Header.HasProductionMdx)
            {
                try
                {
                    using var index = table.OpenProductionIndex();
                    if (index is null)
                        warnings.Add("The header says there's a production .mdx index, but no .mdx file is next to the table.");
                    else
                        tags = index.Tags.Select(t => $"{t.Name} ({t.Expression})").ToList();
                }
                catch (Exception ex) when (ex is InvalidDataException or DbfException)
                {
                    problems.Add(ex as DbfException ?? new DbfException(DbfErrorCode.IndexDamaged, $"The .mdx index can't be read: {ex.Message}",
                        "Rebuild it in the legacy app (REINDEX) or restore it from a backup.", Path.ChangeExtension(path, ".mdx"), ex));
                }
            }
            if (table.Header.HasStructuralCdx)
                warnings.Add("The table has a FoxPro .cdx index. Reading it isn't supported yet, and writes are refused so it can't go stale.");

            if (!Options.ReadOnly)
                canWrite = ProbeWrite(path, problems);
        }
        catch (DbfException ex)
        {
            problems.Add(ex);
        }

        return new DbfTableReport(name, canRead, canWrite, records, format, encoding, memo, tags, problems, warnings);
    }

    // Opens for writing and closes again without writing a byte, to learn whether writes would work.
    private static bool ProbeWrite(string path, List<DbfException> problems)
    {
        try
        {
            using var _ = DbfException.OpenFile(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch (DbfException ex)
        {
            problems.Add(ex);
            return false;
        }
    }

    private static string DescribeFormat(DbfHeader header) => header.Version switch
    {
        0x03 => "dBASE III/IV",
        0x83 => "dBASE III with memo",
        0x8B => "dBASE IV with memo",
        0xCB => "dBASE IV SQL table with memo",
        0xF5 => "FoxPro 2 with memo",
        0xFB => "FoxBASE",
        0x30 => "Visual FoxPro",
        0x31 => "Visual FoxPro with autoincrement",
        0x32 => "Visual FoxPro with varchar/varbinary",
        _ => $"version 0x{header.Version:X2}",
    };

    // ---- connection strings ---------------------------------------------------------------

    private static bool LooksLikeConnectionString(string text) =>
        text.Contains('=') && (text.Contains(';') || text.TrimStart().StartsWith("data source", StringComparison.OrdinalIgnoreCase)
            || text.TrimStart().StartsWith("folder", StringComparison.OrdinalIgnoreCase));

    private static (string Folder, DbfDatabaseOptions Options) ParseConnectionString(string text, DbfDatabaseOptions options)
    {
        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = text;
        }
        catch (ArgumentException ex)
        {
            throw new DbfException(DbfErrorCode.InvalidConnectionString, $"The connection string can't be parsed: {ex.Message}",
                @"Use key=value pairs separated by semicolons, like Data Source=C:\Data;Read Only=true.", inner: ex);
        }

        string? folder = null;
        foreach (string key in builder.Keys)
        {
            var value = Convert.ToString(builder[key], CultureInfo.InvariantCulture) ?? "";
            switch (key.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant())
            {
                case "datasource" or "folder" or "path":
                    folder = value;
                    break;
                case "readonly":
                    options = options with { ReadOnly = ParseBool(key, value) };
                    break;
                case "mode":
                    options = options with { ReadOnly = !value.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase) };
                    break;
                case "codepage" or "encoding":
                    options = options with { Encoding = ParseEncoding(value) };
                    break;
                case "ignoremissingmemo":
                    options = options with { IgnoreMissingMemo = ParseBool(key, value) };
                    break;
                case "allowunmaintainedindexes":
                    options = options with { AllowUnmaintainedIndexes = ParseBool(key, value) };
                    break;
                default:
                    throw new DbfException(DbfErrorCode.InvalidConnectionString, $"The connection string has an unknown setting '{key}'.",
                        "Known settings: Data Source, Read Only, Mode, Code Page, Ignore Missing Memo, Allow Unmaintained Indexes.");
            }
        }

        return folder is null
            ? throw new DbfException(DbfErrorCode.InvalidConnectionString, "The connection string doesn't say which folder to open.",
                @"Add Data Source=, like Data Source=\\server\share\data.")
            : (folder, options);
    }

    private static bool ParseBool(string key, string value) => value.ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "on" => true,
        "false" or "no" or "0" or "off" => false,
        _ => throw new DbfException(DbfErrorCode.InvalidConnectionString, $"'{key}' needs true or false, not '{value}'.", "Use true or false."),
    };

    private static Encoding ParseEncoding(string value)
    {
        try
        {
            return int.TryParse(value, out var codePage) ? DbfEncoding.GetEncoding(codePage) : DbfEncoding.GetEncodingByName(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new DbfException(DbfErrorCode.InvalidConnectionString, $"'{value}' isn't an encoding .NET knows.",
                "Use a code page number like 1252, 437 or 850, or a name like utf-8.", inner: ex);
        }
    }
}

/// <summary>What the handshake learned about one table.</summary>
public sealed record DbfTableReport(
    string Name,
    bool CanRead,
    bool CanWrite,
    int? RecordCount,
    string? Format,
    string? Encoding,
    string? Memo,
    IReadOnlyList<string> IndexTags,
    IReadOnlyList<DbfException> Problems,
    IReadOnlyList<string> Warnings);

/// <summary>The result of <see cref="DbfDatabase.Check"/>.</summary>
public sealed record DbfCheckReport(string Folder, bool ReadOnly, TimeSpan Elapsed, IReadOnlyList<DbfTableReport> Tables)
{
    public bool Ok => Tables.All(t => t.Problems.Count == 0);

    public IEnumerable<DbfException> Problems => Tables.SelectMany(t => t.Problems);

    /// <summary>A plain-text summary for logs or a "Test connection" dialog.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        text.AppendLine($"{Folder}: {Tables.Count} tables, {(ReadOnly ? "read-only" : "read/write")}, checked in {Elapsed.TotalMilliseconds:F0} ms. {(Ok ? "All good." : $"{Problems.Count()} problem(s).")}");
        foreach (var table in Tables)
        {
            var status = table.Problems.Count > 0 ? "PROBLEM" : table.Warnings.Count > 0 ? "warning" : "ok";
            text.AppendLine($"  [{status}] {table.Name}: {table.Format ?? "?"}, {table.RecordCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} records" +
                (table.Memo is null ? "" : $", {table.Memo} memos") + (table.IndexTags.Count == 0 ? "" : $", {table.IndexTags.Count} index tags"));
            foreach (var problem in table.Problems)
                text.AppendLine($"      {problem.Code}: {problem.Message}");
            foreach (var warning in table.Warnings)
                text.AppendLine($"      note: {warning}");
        }
        return text.ToString();
    }
}
