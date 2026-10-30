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
    InvalidConnectionString,
    TableNotFound,

    /// <summary>Creating a table would overwrite or adopt files that already exist.</summary>
    TableAlreadyExists,

    /// <summary>The file exists but isn't a DBF table Thunder.Dbf can read.</summary>
    NotADbfFile,
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

    MemoFileMissing,
    MemoFileDamaged,
    IndexDamaged,

    /// <summary>Writing would leave a production .mdx/.cdx index out of date.</summary>
    IndexWouldGoStale,

    /// <summary>The table or database was opened read-only.</summary>
    ReadOnlyConnection,
}

/// <summary>
/// A failure Thunder.Dbf can explain: <see cref="Code"/> says what kind, <see cref="FilePath"/> says
/// which file, and <see cref="Hint"/> says what to do about it.
/// </summary>
public sealed class DbfException : IOException
{
    public DbfException(DbfErrorCode code, string message, string hint, string? filePath = null, Exception? inner = null)
        : base(Compose(message, hint, filePath), inner)
    {
        Code = code;
        Hint = hint;
        FilePath = filePath;
    }

    public DbfErrorCode Code { get; }
    public string Hint { get; }
    public string? FilePath { get; }

    /// <summary>True for refusals made to protect data or access rules, as opposed to missing or damaged files.</summary>
    public bool IsSecurityRelated => Code is DbfErrorCode.AccessDenied or DbfErrorCode.PathEscapesFolder
        or DbfErrorCode.EncryptedTable or DbfErrorCode.ReadOnlyConnection or DbfErrorCode.ReadOnlyLocation;

    /// <summary>The HTTP status a web layer should answer with.</summary>
    public int HttpStatus => Code switch
    {
        DbfErrorCode.TableNotFound or DbfErrorCode.FolderNotFound => 404,
        DbfErrorCode.AccessDenied or DbfErrorCode.PathEscapesFolder or DbfErrorCode.EncryptedTable => 403,
        DbfErrorCode.FileInUse or DbfErrorCode.TableAlreadyExists or DbfErrorCode.IndexWouldGoStale or DbfErrorCode.IncompleteTransaction
            or DbfErrorCode.ReadOnlyConnection or DbfErrorCode.ReadOnlyLocation => 409,
        DbfErrorCode.InvalidConnectionString => 400,
        _ => 500,
    };

    private static string Compose(string message, string hint, string? filePath) =>
        filePath is null ? $"{message} {hint}" : $"{message} ({filePath}) {hint}";

    // ---- translating operating-system failures ---------------------------------------------

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>Opens a file, turning operating-system failures into explained <see cref="DbfException"/>s.</summary>
    internal static FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        try
        {
            return new FileStream(path, mode, access, share);
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
                    $"Grant the account running this program ({Environment.UserName}) {(writing ? "modify" : "read")} permission on the file " +
                    "and, for a network share, on the share itself. On macOS, also check Files and Folders or Full Disk Access in Privacy & Security.",
                    path, ex);

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
