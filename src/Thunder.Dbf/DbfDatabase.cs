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

    /// <summary>Read memo fields as null when a table's memo file is missing, instead of failing.</summary>
    public bool IgnoreMissingMemo { get; init; }
    /// <summary>
    /// Allow writes to tables with a production .mdx or structural .cdx that Thunder.Dbf can't keep
    /// up to date. Off by default, because those indexes go stale until something rebuilds them.
    /// </summary>
    public bool AllowUnmaintainedIndexes { get; init; }

    /// <summary>
    /// Preview: keep FoxPro and Visual FoxPro .cdx and .idx indexes up to date on writes. Off by default
    /// until it has been checked against Visual FoxPro itself; without it, FoxPro tables with a .cdx refuse writes.
    /// </summary>
    public bool MaintainFoxProIndexes { get; init; }

    /// <summary>
    /// Open tables exclusively, like <c>USE ... EXCLUSIVE</c>: no other program can open them meanwhile, and
    /// opening fails while one has them open. PACK, rebuilds and INDEX ON need it. Off by default.
    /// </summary>
    public bool Exclusive { get; init; }

    /// <summary>Opt-in: keep a journal next to each table so a write cut off while changing keys is finished later (see DbfTable.UseJournal).</summary>
    public bool Journal { get; init; }

    /// <summary>Force each write through to the disk before the next step (see DbfTable.FlushWritesToDisk). On by default.</summary>
    public bool FlushWritesToDisk { get; init; } = true;

    /// <summary>Whose lock positions writes use: dBASE, Clipper or Visual FoxPro. Auto picks per table.</summary>
    public DbfLockScheme LockScheme { get; init; } = DbfLockScheme.Auto;
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

    /// <summary>The full path of the folder the tables live in.</summary>
    public string Folder { get; }
    /// <summary>The options the database was opened with.</summary>
    public DbfDatabaseOptions Options { get; }
    /// <summary>True unless the database was opened for writing.</summary>
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
            throw new DbfException(DbfErrorCode.InvalidConnectionString, "The folder path isn't valid.",
                "Check for stray quotes or characters the file system doesn't allow.", inner: ex, diagnostic: $"Path: {folder}");
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
                "Grant the account running this program list and read permission on the folder.", fullPath, ex,
                diagnostic: $"Account: {Environment.UserName}.");
        }

        return new DbfDatabase(RealPath(fullPath), parsed);
    }

    /// <summary>
    /// The folder with every symbolic link in its path resolved, so containment is judged against
    /// the real directory (macOS's /var is really /private/var, for example).
    /// </summary>
    private static string RealPath(string folder)
    {
        var resolved = new DirectoryInfo(folder);
        var parts = new Stack<string>();
        for (var dir = resolved; dir is not null; dir = dir.Parent)
            parts.Push(dir.Name);

        var current = parts.Pop(); // the root
        while (parts.Count > 0)
        {
            current = Path.Combine(current, parts.Pop());
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        }
        return current;
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

    /// <inheritdoc />
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
            // The list of tables is useful to an administrator and to an attacker, so it's diagnostic only.
            var available = TableNames;
            throw new DbfException(DbfErrorCode.TableNotFound, $"There's no table called '{wanted}'.",
                "Check the spelling; TableNames lists the tables in the folder.", Folder,
                diagnostic: available.Count == 0 ? "The folder has no .dbf tables." : $"Tables here: {string.Join(", ", available)}.");
        }

        EnsureNotLink(path);
        foreach (var companion in Companions(path))
            EnsureNotLink(companion);
        return path;
    }

    private IEnumerable<string> Companions(string tablePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(tablePath);
        return Directory.EnumerateFiles(Folder).Where(p =>
            Path.GetFileNameWithoutExtension(p).Equals(baseName, StringComparison.OrdinalIgnoreCase)
            && CompanionExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase));
    }

    // Tables are direct children of the real folder (they're found by listing it), so the only way
    // out is a link. Links are refused outright rather than followed and judged: a link's target can
    // pass through further links, and checking a target is a race against whoever can change it.
    // See SECURITY.md for what this does and doesn't protect against.
    internal static void EnsureNotLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new DbfException(DbfErrorCode.PathEscapesFolder, $"'{Path.GetFileName(path)}' is a link, and links aren't followed.",
                "Replace the link with the real file, or connect to the folder that holds the real file.", path);
    }

    private DbfTable OpenTableAt(string path)
    {
        var table = DbfTable.Open(path, writable: !Options.ReadOnly, Options.Encoding, Options.IgnoreMissingMemo, Options.AllowUnmaintainedIndexes,
            Options.MaintainFoxProIndexes, Options.Exclusive);
        table.RefuseLinks = true;
        table.UseJournal = Options.Journal;
        table.FlushWritesToDisk = Options.FlushWritesToDisk;
        table.LockScheme = Options.LockScheme;
        return table;
    }

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
        var indexDamaged = false;

        try
        {
            var path = ResolveTablePath(name);
            // A table this database already has open is looked at as it is: opening the file again
            // would collide with it when it's held exclusively. Otherwise the probe opens the way the
            // database would, so an exclusive open that would fail shows up here.
            var cached = _open.GetValueOrDefault(Path.GetFileNameWithoutExtension(path));
            // Held exclusively, the .dbf can't be opened a second time to test writing, so the probe
            // opens it for writing itself when writes are wanted.
            using var probe = cached is null
                ? DbfTable.Open(path, writable: Options.Exclusive && !Options.ReadOnly, Options.Encoding, Options.IgnoreMissingMemo,
                    allowUnmaintainedIndexes: true, exclusive: Options.Exclusive)
                : null;
            var table = cached ?? probe!;
            var heldTable = cached ?? (Options.Exclusive ? probe : null);
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
            if (!Options.ReadOnly && !DbfLocks.PlatformSupportsLocks)
                warnings.Add("This platform can't take byte-range locks, so writes here aren't coordinated with other programs using the table. Run on Windows or Linux to share the files safely.");
            if (table.HasPendingJournal)
                warnings.Add("The table's .journal holds a key change a write didn't finish (or one in flight). The next writable open or write, with the same indexes kept, finishes it.");
            if (table.RecordsInFile() < table.Header.RecordCount)
                problems.Add(new DbfException(DbfErrorCode.NotADbfFile,
                    $"The table's header counts {table.Header.RecordCount:N0} records, but the file holds only {table.RecordsInFile():N0}.",
                    "A power cut or an interrupted copy cut it short, and Visual FoxPro won't open it (error 2091). Opening it with exclusive: true (writable) cuts the count back to the records it holds, keeping the bytes of a part record beside it, and rebuilds its indexes; or restore it from a backup.", path));
            if (table.RecordsPastCount() is > 0 and var past)
                warnings.Add($"{past:N0} record(s) sit past the header's count: an append in progress, or one that was cut off. If it was cut off, Visual FoxPro reports the table as corrupted (error 2091); opening it here with exclusive: true (and writable) takes them off and rebuilds its indexes.");
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
                    problems.Add(ex as DbfException ?? new DbfException(DbfErrorCode.IndexDamaged, "The .mdx index can't be read.",
                        "Rebuild it in the legacy app (REINDEX) or restore it from a backup.", Path.ChangeExtension(path, ".mdx"), ex));
                    indexDamaged = true;
                }
            }
            var cdxMaintained = false;
            if (table.Header.HasStructuralCdx)
            {
                try
                {
                    if (CdxIndex.FindFor(path) is not { } cdxPath)
                        warnings.Add("The header says there's a structural .cdx index, but no .cdx file is next to the table.");
                    else if (CdxIndex.WhyNotMaintainable(cdxPath, table) is { } refusal)
                        warnings.Add($"The table's FoxPro .cdx index can be read but not kept up to date: {refusal}");
                    else if (!Options.MaintainFoxProIndexes)
                        warnings.Add("The table has a FoxPro .cdx index. Keeping it up to date is a preview; set Maintain FoxPro Indexes=true to write to this table.");
                    else
                        cdxMaintained = true;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
                {
                    warnings.Add("The table's FoxPro .cdx index can't be read, so writes would leave it out of date.");
                }
            }

            if (!Options.ReadOnly)
            {
                // Writable means the file system allows it AND the library would: no stale-index or
                // unfinished-transaction refusal, and the memo and index files are writable too.
                var mdx = table.Header.HasProductionMdx ? MdxIndex.FindFor(path) : null;
                var blocker = DbfTable.WriteBlocker(table.Header, Options.AllowUnmaintainedIndexes,
                    indexMaintained: mdx is not null && !indexDamaged, path, DbfTable.FindUnmaintainedIndexes(path, table.Header),
                    structuralCdxMaintained: cdxMaintained);
                if (blocker is not null)
                    warnings.Add($"Writes will be refused: {blocker.Message}");
                canWrite = blocker is null && !indexDamaged && (heldTable?.CanWrite ?? ProbeWrite(path, problems))
                    && Companions(path).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".dbt" or ".fpt" or ".mdx").All(p => ProbeWrite(p, problems));
            }
        }
        catch (DbfException ex)
        {
            problems.Add(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            problems.Add(DbfException.Translate(ex, Path.Combine(Folder, name), FileAccess.Read)
                ?? new DbfException(DbfErrorCode.NotADbfFile, "The table or one of its companion files can't be read.",
                    "Check the table's .dbt/.fpt, .mdx and .cpg files are intact and readable.", Path.Combine(Folder, name), ex));
        }

        return new DbfTableReport(name, canRead, canWrite, records, format, encoding, memo, tags, problems, warnings);
    }

    // Opens for writing and closes again without writing a byte, to learn whether writes would work.
    private static bool ProbeWrite(string path, List<DbfException> problems)
    {
        try
        {
            var probe = DbfException.OpenFile(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            DbfLocks.CloseWhenSafe(probe); // on Linux, a plain close would drop this process's locks on the file
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
                case "lockscheme":
                    options = options with
                    {
                        LockScheme = Enum.TryParse<DbfLockScheme>(value, ignoreCase: true, out var scheme)
                            ? scheme
                            : throw new DbfException(DbfErrorCode.InvalidConnectionString, $"'{value}' isn't a lock scheme.",
                                "Use Auto, Dbase, Clipper, VisualFoxPro or Jet."),
                    };
                    break;
                case "allowunmaintainedindexes":
                    options = options with { AllowUnmaintainedIndexes = ParseBool(key, value) };
                    break;
                case "maintainfoxproindexes":
                    options = options with { MaintainFoxProIndexes = ParseBool(key, value) };
                    break;
                case "exclusive":
                    options = options with { Exclusive = ParseBool(key, value) };
                    break;
                case "journal":
                    options = options with { Journal = ParseBool(key, value) };
                    break;
                case "flushwritestodisk":
                    options = options with { FlushWritesToDisk = ParseBool(key, value) };
                    break;
                default:
                    throw new DbfException(DbfErrorCode.InvalidConnectionString, $"The connection string has an unknown setting '{key}'.",
                        "Known settings: Data Source, Read Only, Mode, Code Page, Ignore Missing Memo, Allow Unmaintained Indexes, Maintain FoxPro Indexes, Exclusive, Journal, Flush Writes To Disk, Lock Scheme.");
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
    /// <summary>True when every table opened without a problem.</summary>
    public bool Ok => Tables.All(t => t.Problems.Count == 0);

    /// <summary>Every problem found, across all tables.</summary>
    public IEnumerable<DbfException> Problems => Tables.SelectMany(t => t.Problems);

    /// <summary>A plain-text summary for logs or a "Test connection" dialog.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        var notes = Tables.Sum(t => t.Warnings.Count);
        var verdict = !Ok ? $"{Problems.Count()} problem(s)."
            : notes == 0 ? "All good."
            : $"No problems; {notes} note(s) below.";
        text.AppendLine($"{Folder}: {Tables.Count} tables, {(ReadOnly ? "read-only" : "read/write")}, checked in {Elapsed.TotalMilliseconds:F0} ms. {verdict}");
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
