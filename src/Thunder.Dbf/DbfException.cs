//        _______
//       /  ___  \       ProjectThunder :: Thunder.Dbf
//      |  /   \  |      When something goes wrong, say what, which file,
//      |  \___/  |      why it matters, and how to fix it. Every time.
//      |   ___   |
//      |  |___|  |      DbfException.Code maps cleanly onto HTTP status codes,
//       \_______/       so a web layer can pass errors through without guessing.

namespace Thunder.Dbf;

/// <summary>What went wrong, in a form code can switch on.</summary>
public enum DbfErrorCode
{
    /// <summary>The folder or connection string doesn't point anywhere usable.</summary>
    FolderNotFound,
    /// <summary>The connection string can't be understood.</summary>
    InvalidConnectionString,
    /// <summary>There is no table with that name in the folder.</summary>
    TableNotFound,

    /// <summary>Creating a table would overwrite or adopt files that already exist.</summary>
    TableAlreadyExists,

    /// <summary>The file exists but isn't a DBF table Thunder.Dbf can read.</summary>
    NotADbfFile,
    /// <summary>The file is a DBF table, but of a kind or version Thunder.Dbf doesn't support.</summary>
    UnsupportedFormat,

    /// <summary>The operating system refused access to the file.</summary>
    AccessDenied,

    /// <summary>Another program holds the file open in a way that blocks this request.</summary>
    FileInUse,

    /// <summary>The file or the folder it's in can't be written (read-only attribute, share or volume).</summary>
    ReadOnlyLocation,

    /// <summary>A table name tried to reach outside the connected folder.</summary>
    PathEscapesFolder,

    /// <summary>dBASE IV encrypted the table; its records are unreadable without dBASE's PROTECT key.</summary>
    EncryptedTable,

    /// <summary>dBASE IV marked a transaction as unfinished; writing on top could corrupt it further.</summary>
    IncompleteTransaction,

    /// <summary>The table has memo fields but its .dbt or .fpt file is missing.</summary>
    MemoFileMissing,
    /// <summary>The memo file can't be read: a pointer or length in it is out of range.</summary>
    MemoFileDamaged,
    /// <summary>The index file can't be read: its structure is damaged or isn't what its header says.</summary>
    IndexDamaged,

    /// <summary>Writing would leave a production .mdx/.cdx index out of date.</summary>
    IndexWouldGoStale,

    /// <summary>The table or database was opened read-only.</summary>
    ReadOnlyConnection,

    /// <summary>Another program holds a lock on the record being changed.</summary>
    RecordLocked,

    /// <summary>Another program holds a lock on the whole table (or is adding records).</summary>
    TableLocked,

    /// <summary>The platform or file system can't take byte-range locks, so shared writes aren't safe.</summary>
    LockingUnsupported,

    /// <summary>A CSV (or other input) is malformed: no header, an unclosed quote, undecodable bytes.</summary>
    InvalidCsv,

    /// <summary>A SQL statement uses something Thunder.Dbf's small SQL dialect doesn't support, or is malformed.</summary>
    UnsupportedSql,

    /// <summary>A value can't be stored in its field: too wide, wrong type, or a character the code page lacks.</summary>
    InvalidValue,

    /// <summary>A command ran longer than its time limit.</summary>
    Timeout,

    /// <summary>A query would need more memory than the library allows it (for example sorting a huge result).</summary>
    QueryTooLarge,

    /// <summary>The operation (PACK, a rebuild, INDEX ON) needs the table opened exclusively, as dBASE and FoxPro require.</summary>
    ExclusiveUseRequired,
}

/// <summary>
/// A failure Thunder.Dbf can explain: <see cref="Code"/> says what kind and <see cref="Hint"/> says what
/// to do about it.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> and <see cref="Hint"/> are safe to show to anyone: they never contain
/// file paths, account names or lists of tables. Those details live in <see cref="FilePath"/> and
/// <see cref="Diagnostic"/>, for logs and administrators only.
/// </remarks>
public sealed class DbfException : IOException
{
    /// <summary>A failure with a safe message and a hint.</summary>
    /// <param name="code">What kind of failure it is.</param>
    /// <param name="message">What happened, safe to show to anyone: no paths, names or table lists.</param>
    /// <param name="hint">What to do about it, equally safe to show.</param>
    /// <param name="filePath">The file involved, for logs only.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    /// <param name="diagnostic">Extra detail for logs and administrators only.</param>
    public DbfException(DbfErrorCode code, string message, string hint, string? filePath = null, Exception? inner = null, string? diagnostic = null)
        : base($"{message} {hint}", inner)
    {
        Code = code;
        Hint = hint;
        FilePath = filePath;
        DiagnosticDetail = diagnostic;
    }

    /// <summary>What kind of failure this is.</summary>
    public DbfErrorCode Code { get; }
    /// <summary>What to do about it. Safe to show to anyone.</summary>
    public string Hint { get; }

    /// <summary>The file involved. Diagnostic only: don't send it to untrusted clients.</summary>
    public string? FilePath { get; }

    private string? DiagnosticDetail { get; }

    /// <summary>Everything known about the failure, including paths, account and inner errors. For logs only.</summary>
    public string Diagnostic
    {
        get
        {
            var text = new System.Text.StringBuilder($"{Code}: {Message}");
            if (FilePath is not null)
                text.Append($" File: {FilePath}.");
            if (DiagnosticDetail is not null)
                text.Append($" {DiagnosticDetail}");
            if (InnerException is not null)
                text.Append($" Cause: {InnerException.GetType().Name}: {InnerException.Message}");
            return text.ToString();
        }
    }

    /// <summary>True for refusals made to protect data or access rules, as opposed to missing or damaged files.</summary>
    public bool IsSecurityRelated => Code is DbfErrorCode.AccessDenied or DbfErrorCode.PathEscapesFolder
        or DbfErrorCode.EncryptedTable or DbfErrorCode.ReadOnlyConnection or DbfErrorCode.ReadOnlyLocation;

    /// <summary>The HTTP status a web layer should answer with.</summary>
    public int HttpStatus => Code switch
    {
        DbfErrorCode.TableNotFound or DbfErrorCode.FolderNotFound => 404,
        DbfErrorCode.AccessDenied or DbfErrorCode.PathEscapesFolder or DbfErrorCode.EncryptedTable => 403,
        DbfErrorCode.FileInUse or DbfErrorCode.ExclusiveUseRequired or DbfErrorCode.TableAlreadyExists or DbfErrorCode.IndexWouldGoStale or DbfErrorCode.IncompleteTransaction
            or DbfErrorCode.ReadOnlyConnection or DbfErrorCode.ReadOnlyLocation => 409,
        DbfErrorCode.InvalidConnectionString or DbfErrorCode.InvalidCsv or DbfErrorCode.UnsupportedSql => 400,
        DbfErrorCode.InvalidValue => 422,
        DbfErrorCode.RecordLocked or DbfErrorCode.TableLocked => 423,
        DbfErrorCode.QueryTooLarge => 413,
        DbfErrorCode.Timeout => 504,
        _ => 500,
    };

    // ---- translating operating-system failures ---------------------------------------------

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>Opens a file, turning operating-system failures into explained <see cref="DbfException"/>s.</summary>
    internal static FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize = 4096)
    {
        try
        {
            return new FileStream(path, mode, access, share, bufferSize);
        }
        catch (Exception ex) when (Translate(ex, path, access) is { } translated)
        {
            throw translated;
        }
    }

    internal static DbfException? Translate(Exception ex, string path, FileAccess access)
    {
        var writing = access != FileAccess.Read;
        switch (ex)
        {
            case DbfException:
                return null;

            case FileNotFoundException:
                return new DbfException(DbfErrorCode.TableNotFound, "The file doesn't exist.",
                    "Check the table name and that the folder is the legacy app's data folder.", path, ex);

            case DirectoryNotFoundException:
                return new DbfException(DbfErrorCode.FolderNotFound, "The folder doesn't exist or isn't reachable.",
                    "Check the path, that the network share is mounted, and that this machine can reach the server.", path, ex);

            case UnauthorizedAccessException when writing && IsReadOnlyFile(path):
                return new DbfException(DbfErrorCode.ReadOnlyLocation, "The file is marked read-only.",
                    "Clear the file's read-only attribute, or open the table read-only.", path, ex);

            case UnauthorizedAccessException:
                return new DbfException(DbfErrorCode.AccessDenied,
                    writing ? "The operating system refused write access." : "The operating system refused read access.",
                    $"Grant the account running this program {(writing ? "modify" : "read")} permission on the file " +
                    "and, for a network share, on the share itself. On macOS, also check Files and Folders or Full Disk Access in Privacy & Security.",
                    path, ex, diagnostic: $"Account: {Environment.UserName}.");

            case IOException io when io.HResult is SharingViolation or LockViolation || io.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase):
                return new DbfException(DbfErrorCode.FileInUse,
                    "Another program has the file open in a way that blocks this request.",
                    writing
                        ? "The legacy app (or another Thunder.Dbf process) probably has it open for writing. Open read-only, or close the other program first."
                        : "The other program opened it exclusively. Close it, or ask its users to exit, then try again.",
                    path, ex);

            case IOException io when IsReadOnlyVolume(io):
                return new DbfException(DbfErrorCode.ReadOnlyLocation, "The folder's volume or share is read-only.",
                    "Ask for write access to the share, or open the table read-only.", path, ex);

            default:
                return null;
        }
    }

    private static bool IsReadOnlyFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).IsReadOnly;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // EROFS on Unix, ERROR_WRITE_PROTECT on Windows.
    private static bool IsReadOnlyVolume(IOException io) =>
        io.HResult == unchecked((int)0x80070013)
        || io.Message.Contains("read-only file system", StringComparison.OrdinalIgnoreCase);
}
