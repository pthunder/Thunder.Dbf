//       _____
//      /     \       ProjectThunder :: Thunder.Dbf
//     | () () |      RFC 7807 problems: what went wrong and how to fix it,
//      \  ^  /       without the path, the username or the stack trace.
//       |||||
//       |||||        The full story goes to the server log, tagged with the
//                    same traceId the client sees, so support can match them up.

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Thunder.Dbf.Server;

/// <summary>A refusal the API explains to the client, thrown from anywhere in a request.</summary>
public class ApiProblem(int status, string title, string? detail, string code, string? hint = null) : Exception(title)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public string Code { get; } = code;
    public string? Hint { get; } = hint;

    public static ApiProblem NotFound(string detail, string? hint = null) => new(StatusCodes.Status404NotFound, "Not found.", detail, "NotFound", hint);

    public static ApiProblem Forbidden(string detail, string? hint = null) => new(StatusCodes.Status403Forbidden, "Not allowed.", detail, "Forbidden", hint);

    public static ApiProblem BadRequest(string detail, string? hint = null) => new(StatusCodes.Status400BadRequest, "The request isn't valid.", detail, "BadRequest", hint);
}

/// <summary>Field-by-field validation failures, answered as a 400 with an <c>errors</c> map.</summary>
public sealed class ApiValidationProblem(IDictionary<string, string[]> errors)
    : ApiProblem(StatusCodes.Status400BadRequest, "Some fields aren't valid.", "See errors for what each field needs.", "ValidationFailed")
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>Another request held the table for longer than <see cref="ThunderOptions.LockTimeoutSeconds"/>.</summary>
public sealed class TableBusyProblem() : ApiProblem(StatusCodes.Status503ServiceUnavailable, "The table is busy.",
    "Another request has been using this table for a while.", "TableBusy", "Try again in a few seconds.");

/// <summary>Builds the ProblemDetails the API returns.</summary>
public static class Problems
{
    public static ProblemDetails Create(int status, string title, string? detail, string code, string? hint = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"https://httpstatuses.io/{status}",
        };
        problem.Extensions["code"] = code;
        if (hint is not null)
            problem.Extensions["hint"] = hint;
        return problem;
    }

    /// <summary>Friendly titles for the library's error codes.</summary>
    public static string TitleFor(DbfErrorCode code) => code switch
    {
        DbfErrorCode.TableNotFound or DbfErrorCode.FolderNotFound => "Not found.",
        DbfErrorCode.AccessDenied => "The server can't open the file.",
        DbfErrorCode.FileInUse => "The legacy app is using the file.",
        DbfErrorCode.ReadOnlyLocation or DbfErrorCode.ReadOnlyConnection => "The table can't be written.",
        DbfErrorCode.PathEscapesFolder => "Not a plain table name.",
        DbfErrorCode.EncryptedTable => "The table is encrypted.",
        DbfErrorCode.IncompleteTransaction => "The table has an unfinished transaction.",
        DbfErrorCode.IndexWouldGoStale => "Writing would leave the table's index out of date.",
        DbfErrorCode.MemoFileMissing or DbfErrorCode.MemoFileDamaged => "The table's memo file can't be used.",
        DbfErrorCode.IndexDamaged => "The table's index can't be read.",
        _ => "The table can't be used.",
    };
}

/// <summary>
/// Takes paths and account names out of text before it reaches a client. Library messages are written
/// for the person at the server, so they name files and accounts; web clients get the gist instead.
/// </summary>
public sealed partial class Scrubber
{
    private readonly List<(string Secret, string Replacement)> _known = [];

    public Scrubber(IEnumerable<string?> folders)
    {
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
        {
            _known.Add((folder!.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, ""));
            _known.Add((folder.TrimEnd('/', '\\'), "the data folder"));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1)
            _known.Add((home, "~"));
        // Longest first, so a folder's subfolder is replaced before the folder itself.
        _known.Sort((a, b) => b.Secret.Length.CompareTo(a.Secret.Length));
    }

    public string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        foreach (var (secret, replacement) in _known)
            text = text.Replace(secret, replacement, StringComparison.OrdinalIgnoreCase);

        // Whatever absolute path is left keeps only its file name: /a/b/C.DBF and C:\a\C.DBF become C.DBF.
        text = AbsolutePath().Replace(text, "");

        var user = Environment.UserName;
        if (user.Length >= 3)
            text = Regex.Replace(text, $@"\b{Regex.Escape(user)}\b", "the service account", RegexOptions.IgnoreCase);
        return text;
    }

    [GeneratedRegex(@"(?<![\w.~])(?:[A-Za-z]:[\\/]|\\\\|/|~[\\/])(?:[^\s'""(),;<>]+[\\/])+")]
    private static partial Regex AbsolutePath();
}

/// <summary>Turns every exception into a ProblemDetails response and logs what the client doesn't see.</summary>
internal sealed class ProblemExceptionHandler(IProblemDetailsService problems, Scrubber scrubber, ILogger<ProblemExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return true; // the client went away; there's no one to answer

        ProblemDetails problem;
        switch (exception)
        {
            case ApiValidationProblem invalid:
                problem = new HttpValidationProblemDetails(invalid.Errors)
                {
                    Status = invalid.Status,
                    Title = invalid.Title,
                    Detail = invalid.Detail,
                    Type = $"https://httpstatuses.io/{invalid.Status}",
                };
                problem.Extensions["code"] = invalid.Code;
                break;

            case TableBusyProblem busy:
                context.Response.Headers.RetryAfter = "5";
                problem = Problems.Create(busy.Status, busy.Title, busy.Detail, busy.Code, busy.Hint);
                break;

            case ApiProblem api:
                problem = Problems.Create(api.Status, api.Title, api.Detail, api.Code, api.Hint);
                break;

            case DbfException dbf:
                // The library explains itself to whoever runs the server; the log gets that version.
                var level = dbf.HttpStatus >= 500 ? LogLevel.Error : LogLevel.Warning;
                logger.Log(level, dbf, "{Code} on {Method} {Path}", dbf.Code, ForLog(context.Request.Method), ForLog(context.Request.Path.Value));
                problem = Problems.Create(dbf.HttpStatus, Problems.TitleFor(dbf.Code), scrubber.Scrub(MessageOf(dbf)),
                    dbf.Code.ToString(), scrubber.Scrub(dbf.Hint));
                break;

            case BadHttpRequestException bad:
                problem = Problems.Create(bad.StatusCode, "The request couldn't be read.", null, "BadRequest");
                break;

            default:
                logger.LogError(exception, "Unhandled error on {Method} {Path}", ForLog(context.Request.Method), ForLog(context.Request.Path.Value));
                problem = Problems.Create(StatusCodes.Status500InternalServerError, "Something went wrong on the server.",
                    "The details are in the server log under this traceId.", "ServerError");
                break;
        }

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }

    // The method and path come from the caller, and a path can carry an encoded line break, which
    // would let a request write a fake line into a plain-text log. Control characters become '?',
    // and very long paths are cut short.
    internal static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var text = value.Length > 300 ? value[..300] + "..." : value;
        return string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
        });
    }

    // DbfException.Message is "message (path) hint"; peel off the hint and the path to get the message alone.
    internal static string MessageOf(DbfException ex)
    {
        var text = ex.Message;
        if (text.EndsWith(ex.Hint, StringComparison.Ordinal))
            text = text[..^ex.Hint.Length].TrimEnd();
        if (ex.FilePath is not null && text.EndsWith($"({ex.FilePath})", StringComparison.Ordinal))
            text = text[..^(ex.FilePath.Length + 2)].TrimEnd();
        return text;
    }
}
