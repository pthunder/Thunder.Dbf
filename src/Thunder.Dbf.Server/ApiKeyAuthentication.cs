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
/// Authenticates requests by the <c>X-Api-Key</c> header. <see cref="OidcAuthentication"/> sits beside it
/// for bearer tokens; both name the caller with the <see cref="KeyIdClaim"/> claim, and
/// <see cref="AccessRegistry"/> turns the caller into permissions, so endpoints don't care which was used.
/// </summary>
public sealed class ApiKeyAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccessRegistry registry,
    IProblemDetailsService problems,
    IOptions<ThunderOptions> thunder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    /// <summary>Who is calling: the API key's id, or <c>oidc:&lt;subject&gt;</c> for a signed-in user.</summary>
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
        var bearer = thunder.Value.OpenIdConnect.Enabled;
        Response.Headers.WWWAuthenticate = bearer
            ? new Microsoft.Extensions.Primitives.StringValues([$"{SchemeName} header=\"{HeaderName}\"", OidcAuthentication.SchemeName])
            : $"{SchemeName} header=\"{HeaderName}\"";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = bearer
                ? Problems.Create(StatusCodes.Status401Unauthorized, "An API key or a bearer token is required.",
                    $"Send a valid key in the {HeaderName} header, or a token from the identity provider as Authorization: Bearer.", "Unauthorized")
                : Problems.Create(StatusCodes.Status401Unauthorized, "An API key is required.",
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

/// <summary>
/// What one caller may do: an API key, or a signed-in user with the permissions of their access
/// profile. Resolved from configuration once at startup.
/// </summary>
public sealed class KeyAccess
{
    private readonly bool _readAll;
    private readonly bool _writeAll;
    private readonly HashSet<string> _read;
    private readonly HashSet<string> _write;
    private readonly Dictionary<string, HashSet<string>> _fields;

    internal KeyAccess(string id, AccessOptions options)
    {
        Id = id;
        CanListTables = options.CanListTables;
        _readAll = options.Read.Contains("*");
        _writeAll = options.Write.Contains("*");
        _read = new HashSet<string>(options.Read.Select(Bare), StringComparer.OrdinalIgnoreCase);
        _write = new HashSet<string>(options.Write.Select(Bare), StringComparer.OrdinalIgnoreCase);
        _fields = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, fields) in options.Fields)
            _fields[Bare(table)] = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
    }

    // The same permissions under another caller's name: every user mapped to a profile shares its
    // rules, but the journal and logs must say which user it was.
    private KeyAccess(KeyAccess profile, string id)
    {
        Id = id;
        CanListTables = profile.CanListTables;
        _readAll = profile._readAll;
        _writeAll = profile._writeAll;
        _read = profile._read;
        _write = profile._write;
        _fields = profile._fields;
    }

    /// <summary>The caller's name in logs and the journal: the key id, or <c>oidc:&lt;subject&gt;</c>.</summary>
    public string Id { get; }

    public bool CanListTables { get; }

    internal KeyAccess As(string id) => new(this, id);

    public bool CanRead(string table) => _readAll || _read.Contains(table);

    /// <summary>Write permission on the key's side. The table must also accept writes.</summary>
    public bool CanWrite(string table) => CanRead(table) && (_writeAll || _write.Contains(table));

    /// <summary>True when the key may see (and, with write permission, change) <paramref name="field"/>.</summary>
    public bool CanSeeField(string table, string field) => !_fields.TryGetValue(table, out var allowed) || allowed.Contains(field);

    // "CLIENT.DBF" and "client" mean the same table.
    private static string Bare(string table) =>
        table.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) ? table[..^4] : table;
}

/// <summary>
/// Every configured key and access profile: the constant-time lookup from a presented key to its id,
/// and from an authenticated caller to what it may do.
/// </summary>
public sealed class AccessRegistry
{
    private readonly IReadOnlyList<(KeyAccess Access, byte[] Hash)> _keys;
    private readonly Dictionary<string, KeyAccess> _profiles;
    private readonly IReadOnlyList<ClaimMapOptions> _map;
    private readonly bool _oidcEnabled;
    private readonly ILogger<AccessRegistry> _logger;

    public AccessRegistry(IOptions<ThunderOptions> options, ILogger<AccessRegistry> logger)
    {
        _logger = logger;
        _keys = options.Value.ApiKeys
            .Select(k => (new KeyAccess(k.Id, k), ApiKeyHasher.TryParseHash(k.Sha256) ?? throw new InvalidOperationException($"API key '{k.Id}' has no valid hash.")))
            .ToList();

        var oidc = options.Value.OpenIdConnect;
        _oidcEnabled = oidc.Enabled;
        _profiles = oidc.Profiles.ToDictionary(p => p.Key, p => new KeyAccess(p.Key, p.Value), StringComparer.OrdinalIgnoreCase);
        _map = oidc.Map;
    }

    /// <summary>
    /// The id of the key whose hash matches, or null. Every configured hash is compared, with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>, so timing says nothing about which one was close.
    /// </summary>
    public string? Match(string presentedKey)
    {
        var hash = ApiKeyHasher.Hash(presentedKey);
        KeyAccess? found = null;
        foreach (var (access, keyHash) in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, keyHash))
                found ??= access;
        }
        return found?.Id;
    }

    /// <summary>
    /// The permissions of the authenticated caller. Endpoints only run for authenticated callers. A
    /// signed-in user whose token maps to no profile gets a 403 here, before any table is touched.
    /// </summary>
    public KeyAccess For(ClaimsPrincipal user)
    {
        var id = user.FindFirst(ApiKeyAuthentication.KeyIdClaim)?.Value;

        // Which scheme vouched for the caller decides where its permissions come from, so a claim
        // can never make a token look like an API key, or the other way round.
        switch (user.Identity?.AuthenticationType)
        {
            case ApiKeyAuthentication.SchemeName:
                foreach (var (access, _) in _keys)
                {
                    if (access.Id == id)
                        return access;
                }
                break;

            case OidcAuthentication.SchemeName when _oidcEnabled && id is not null:
                if (ProfileFor(user) is { } profile)
                    return profile.As(id);
                if (RejectionLog.ShouldLog(id, out var suppressed))
                    _logger.LogWarning("Signed-in user {Caller} has a valid token that no rule in Thunder:OpenIdConnect:Map matches ({Suppressed} more refusals not logged).",
                        id, suppressed);
                throw ApiProblem.Forbidden("You're signed in, but your account isn't mapped to any access profile on this server.",
                    "Ask the server's administrator to map one of your roles or groups to a profile.");
        }
        throw new ApiProblem(StatusCodes.Status401Unauthorized, "An API key or a bearer token is required.", null, "Unauthorized");
    }

    // The first rule that matches wins, so a user in two groups gets one profile's permissions,
    // never a mix of two that nobody wrote down.
    private KeyAccess? ProfileFor(ClaimsPrincipal user)
    {
        foreach (var rule in _map)
        {
            var matched =
                !string.IsNullOrEmpty(rule.Role) ? HasValue(user, rule.Role, "roles", "role") :
                !string.IsNullOrEmpty(rule.Group) ? HasValue(user, rule.Group, "groups") :
                !string.IsNullOrEmpty(rule.Claim) && rule.Value is not null && HasValue(user, rule.Value, rule.Claim);
            if (matched && _profiles.TryGetValue(rule.Profile, out var profile))
                return profile;
        }
        return null;
    }

    // OAuth scopes come as one space-separated claim ("scope": "dbf.read dbf.write"), so those are split.
    private static bool HasValue(ClaimsPrincipal user, string value, params string[] types) =>
        user.Claims.Any(c => types.Contains(c.Type, StringComparer.Ordinal)
            && (c.Type is "scope" or "scp"
                ? c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.Ordinal)
                : string.Equals(c.Value, value, StringComparison.Ordinal)));
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
