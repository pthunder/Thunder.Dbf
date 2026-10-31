//         ____
//        / __ \___________      ProjectThunder :: Thunder.Dbf
//       | |  | |__ __ __  |     X-Api-Key: in.   SHA-256: compared.   Key: forgotten.
//        \____/  |_||_|  |_|
//                               Config only ever holds hashes, compared in constant time,
//                               so a leaked appsettings.json doesn't leak a working key.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Thunder.Dbf.Server;

/// <summary>
/// Authenticates requests by the <c>X-Api-Key</c> header. Another scheme (OpenID Connect, say) can sit
/// beside it later: permissions hang off the <see cref="ApiKeyAuthentication.KeyIdClaim"/> claim, not the scheme.
/// </summary>
public sealed class ApiKeyAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccessRegistry registry,
    IProblemDetailsService problems)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string KeyIdClaim = "thunder:key_id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || values.Count == 0 || string.IsNullOrEmpty(values[0]))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (values.Count > 1)
            return Task.FromResult(AuthenticateResult.Fail("More than one X-Api-Key header."));

        var keyId = registry.Match(values[0]!);
        if (keyId is null)
        {
            // Never log the key itself, not even part of it. A guessing attack would otherwise write a
            // line per guess, so each address is logged at most once a minute, with a count.
            if (RejectionLog.ShouldLog(Context.Connection.RemoteIpAddress?.ToString() ?? "unknown", out var suppressed))
                Logger.LogWarning("Rejected an unknown API key from {RemoteIp} ({Suppressed} more from it in the last minute not logged).",
                    Context.Connection.RemoteIpAddress, suppressed);
            return Task.FromResult(AuthenticateResult.Fail("Unknown API key."));
        }

        var identity = new ClaimsIdentity([new Claim(KeyIdClaim, keyId), new Claim(ClaimTypes.Name, keyId)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{HeaderName}\"";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = Problems.Create(StatusCodes.Status401Unauthorized, "An API key is required.",
                $"Send a valid key in the {HeaderName} header.", "Unauthorized"),
        });
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = Problems.Create(StatusCodes.Status403Forbidden, "This key isn't allowed to do that.", null, "Forbidden"),
        });
    }
}

/// <summary>What one API key may do, resolved from configuration once at startup.</summary>
public sealed class KeyAccess
{
    private readonly bool _readAll;
    private readonly bool _writeAll;
    private readonly HashSet<string> _read;
    private readonly HashSet<string> _write;
    private readonly Dictionary<string, HashSet<string>> _fields;

    internal KeyAccess(ApiKeyOptions options, byte[] hash)
    {
        Id = options.Id;
        Hash = hash;
        CanListTables = options.CanListTables;
        _readAll = options.Read.Contains("*");
        _writeAll = options.Write.Contains("*");
        _read = new HashSet<string>(options.Read.Select(Bare), StringComparer.OrdinalIgnoreCase);
        _write = new HashSet<string>(options.Write.Select(Bare), StringComparer.OrdinalIgnoreCase);
        _fields = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, fields) in options.Fields)
            _fields[Bare(table)] = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
    }

    public string Id { get; }
    internal byte[] Hash { get; }
    public bool CanListTables { get; }

    public bool CanRead(string table) => _readAll || _read.Contains(table);

    /// <summary>Write permission on the key's side. The table must also accept writes.</summary>
    public bool CanWrite(string table) => CanRead(table) && (_writeAll || _write.Contains(table));

    /// <summary>True when the key may see (and, with write permission, change) <paramref name="field"/>.</summary>
    public bool CanSeeField(string table, string field) => !_fields.TryGetValue(table, out var allowed) || allowed.Contains(field);

    // "CLIENT.DBF" and "client" mean the same table.
    private static string Bare(string table) =>
        table.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) ? table[..^4] : table;
}

/// <summary>Every configured key, and the constant-time lookup from a presented key to its id.</summary>
public sealed class AccessRegistry
{
    private readonly IReadOnlyList<KeyAccess> _keys;

    public AccessRegistry(IOptions<ThunderOptions> options)
    {
        _keys = options.Value.ApiKeys
            .Select(k => new KeyAccess(k, ApiKeyHasher.TryParseHash(k.Sha256) ?? throw new InvalidOperationException($"API key '{k.Id}' has no valid hash.")))
            .ToList();
    }

    /// <summary>
    /// The id of the key whose hash matches, or null. Every configured hash is compared, with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>, so timing says nothing about which one was close.
    /// </summary>
    public string? Match(string presentedKey)
    {
        var hash = ApiKeyHasher.Hash(presentedKey);
        KeyAccess? found = null;
        foreach (var key in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, key.Hash))
                found ??= key;
        }
        return found?.Id;
    }

    /// <summary>The permissions of the authenticated caller. Endpoints only run for authenticated callers.</summary>
    public KeyAccess For(ClaimsPrincipal user)
    {
        var id = user.FindFirst(ApiKeyAuthentication.KeyIdClaim)?.Value;
        return _keys.FirstOrDefault(k => k.Id == id)
            ?? throw new ApiProblem(StatusCodes.Status401Unauthorized, "An API key is required.", null, "Unauthorized");
    }
}

/// <summary>Samples "unknown key" warnings: one line per address per minute, however many guesses.</summary>
internal static class RejectionLog
{
    private const int MaxAddresses = 10_000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Since, int Suppressed)> Seen = new();

    public static bool ShouldLog(string address, out int suppressed)
    {
        var now = Environment.TickCount64;
        if (Seen.Count > MaxAddresses)
            Seen.Clear(); // a spray from many addresses: start over rather than grow without limit

        var log = false;
        var count = 0;
        Seen.AddOrUpdate(address,
            _ => { log = true; return (now, 0); },
            (_, last) =>
            {
                if (now - last.Since >= 60_000)
                {
                    log = true;
                    count = last.Suppressed;
                    return (now, 0);
                }
                return (last.Since, last.Suppressed + 1);
            });
        suppressed = count;
        return log;
    }
}
