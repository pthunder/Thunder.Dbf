//      .------------------.
//      | appsettings.json |      ProjectThunder :: Thunder.Dbf
//      |  "Thunder": {    |      Server settings. Every default is the safe one:
//      |    "Data": ...   |      read-only, no keys, nothing listed, nothing written.
//      '------------------'
//              ||                A typo should fail at startup with a sentence that
//            .-''-.              says what to fix, never at 3 a.m. on the first write.
//           ( ZZZZ )

using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Thunder.Dbf.Server;

/// <summary>Everything under the <c>Thunder</c> section of the configuration.</summary>
public sealed class ThunderOptions
{
    public const string SectionName = "Thunder";

    /// <summary>The legacy app's data folder and how to open it.</summary>
    public DataOptions Data { get; set; } = new();

    /// <summary>Per-table switches, keyed by table name (without .dbf). Tables not listed are read-only.</summary>
    public Dictionary<string, TableOptions> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The API keys allowed in, each with its own permissions. Only SHA-256 hashes are stored.</summary>
    public List<ApiKeyOptions> ApiKeys { get; set; } = [];

    /// <summary>Optional sign-in with OpenID Connect bearer tokens, beside the API keys.</summary>
    public OidcOptions OpenIdConnect { get; set; } = new();

    public PagingOptions Paging { get; set; } = new();
    public JournalOptions Journal { get; set; } = new();
    public BackupOptions Backups { get; set; } = new();
    public RateLimitOptions RateLimit { get; set; } = new();

    /// <summary>How long a request waits for another request on the same table before giving up with 503.</summary>
    public int LockTimeoutSeconds { get; set; } = 30;

    /// <summary>True when the data folder is opened for writing at all; each table still has to opt in.</summary>
    public bool WritesEnabled => !Data.ReadOnly;

    /// <summary>True when this table accepts writes from keys that are allowed to write it.</summary>
    public bool TableAcceptsWrites(string table) => WritesEnabled && TableSettings(table) is { AllowWrites: true };

    public bool AllowsUnmaintainedIndexes(string table) => TableSettings(table) is { AllowUnmaintainedIndexes: true };

    // The configuration binder may hand back a case-sensitive dictionary, so look names up by hand.
    private TableOptions? TableSettings(string table) =>
        Tables.FirstOrDefault(t => string.Equals(t.Key, table, StringComparison.OrdinalIgnoreCase)).Value;
}

public sealed class DataOptions
{
    /// <summary>The folder holding the .dbf files, or a Thunder.Dbf connection string.</summary>
    public string Folder { get; set; } = "";

    /// <summary>The master switch. While true nothing is ever written, whatever the tables or keys say.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Overrides every table's code page, like 1252, 437 or 850. Leave empty to use each table's own.</summary>
    public int? CodePage { get; set; }

    /// <summary>Read memo fields as null when a table's .dbt/.fpt is missing, instead of failing.</summary>
    public bool IgnoreMissingMemo { get; set; }
}

public sealed class TableOptions
{
    /// <summary>Lets keys with write permission change this table (only when Data:ReadOnly is false).</summary>
    public bool AllowWrites { get; set; }

    /// <summary>
    /// Allows writes to a table whose index Thunder.Dbf can't keep up to date: a FoxPro .cdx, or a
    /// production .mdx the header promises but that's missing. (A present .mdx is always maintained.)
    /// Only turn this on if the legacy app reindexes before it relies on the index.
    /// </summary>
    public bool AllowUnmaintainedIndexes { get; set; }
}

/// <summary>
/// What a caller may touch: the shape shared by API keys and the access profiles OpenID Connect
/// users are mapped to, so both kinds of caller are checked by exactly the same code.
/// </summary>
public class AccessOptions
{
    /// <summary>Lets the caller call GET /tables and GET /openapi/tables.json, and see table names in 404 hints.</summary>
    public bool CanListTables { get; set; }

    /// <summary>Tables the caller may read, or "*" for every table in the folder.</summary>
    public List<string> Read { get; set; } = [];

    /// <summary>Tables the caller may write, or "*". A table must also allow writes in Tables.</summary>
    public List<string> Write { get; set; } = [];

    /// <summary>
    /// Optional field allow-lists per table. When a table is listed, only those fields are returned
    /// or accepted for this caller; the rest behave as if they didn't exist.
    /// </summary>
    public Dictionary<string, List<string>> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One API key: who it is, its hash, and what it may touch.</summary>
public sealed class ApiKeyOptions : AccessOptions
{
    /// <summary>A name for the key, used in logs and the write journal. Never the key itself.</summary>
    public string Id { get; set; } = "";

    /// <summary>The SHA-256 hash of the key, as 64 hex digits. Make one with <c>thunder-dbf-server hash-key</c>.</summary>
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// Sign-in with bearer tokens from an OpenID Connect provider (Entra ID, Auth0, Keycloak, ...), beside
/// API keys. Off while <see cref="Authority"/> is empty.
/// </summary>
public sealed class OidcOptions
{
    /// <summary>The provider's issuer URL. Its /.well-known/openid-configuration supplies the signing keys.</summary>
    public string Authority { get; set; } = "";

    /// <summary>The audience tokens must be issued for (this API's client id or app ID URI). Required.</summary>
    public string Audience { get; set; } = "";

    /// <summary>Refuses to fetch the provider's metadata and keys over plain HTTP. Only a local test provider needs false.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>The claim that names the user in logs, the journal and rate limits, as <c>oidc:&lt;value&gt;</c>.</summary>
    public string SubjectClaim { get; set; } = "sub";

    /// <summary>
    /// The token types (the JWT <c>typ</c> header) accepted as access tokens. The default is RFC 9068's
    /// <c>at+jwt</c>, which ID tokens never carry. Some providers (Entra ID, Keycloak) mark access tokens
    /// plain <c>JWT</c>: list that only when the API has its own audience that no app's ID token uses.
    /// </summary>
    public List<string> AccessTokenTypes { get; set; } = ["at+jwt", "application/at+jwt"];

    /// <summary>Named sets of permissions, each shaped like an API key's.</summary>
    public Dictionary<string, AccessOptions> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rules from a token's roles, groups or other claims to a profile. The first rule that matches wins.</summary>
    public List<ClaimMapOptions> Map { get; set; } = [];

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority);
}

/// <summary>
/// One rule of <see cref="OidcOptions.Map"/>: exactly one of <see cref="Role"/>, <see cref="Group"/> or
/// <see cref="Claim"/> (with <see cref="Value"/>), and the profile a match gets.
/// </summary>
public sealed class ClaimMapOptions
{
    /// <summary>A value of the token's <c>roles</c> claim.</summary>
    public string? Role { get; set; }

    /// <summary>A value of the token's <c>groups</c> claim.</summary>
    public string? Group { get; set; }

    /// <summary>Any other claim type, such as a namespaced Auth0 claim or <c>scope</c>.</summary>
    public string? Claim { get; set; }

    /// <summary>The value <see cref="Claim"/> must have. Compared exactly, case included.</summary>
    public string? Value { get; set; }

    /// <summary>The name of the profile in <see cref="OidcOptions.Profiles"/> a matching user gets.</summary>
    public string Profile { get; set; } = "";
}

public sealed class PagingOptions
{
    public int DefaultTake { get; set; } = 50;

    /// <summary>The most records one page can return, whatever <c>take</c> asks for.</summary>
    public int MaxTake { get; set; } = 500;

    /// <summary>A rough cap on one response's size; a page of records with big memos ends early, with a next link.</summary>
    public long MaxResponseBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>The most index entries one seek looks at, so a short key can't walk a whole huge index.</summary>
    public int MaxSeekScan { get; set; } = 10_000;
}

public sealed class JournalOptions
{
    /// <summary>The JSON Lines file every write is appended to. Relative paths start at the service's folder.</summary>
    public string Path { get; set; } = System.IO.Path.Combine("journal", "writes.jsonl");
}

public sealed class BackupOptions
{
    /// <summary>Where tables are copied before their first write after startup. Relative paths start at the service's folder.</summary>
    public string Folder { get; set; } = "backups";
}

public sealed class RateLimitOptions
{
    /// <summary>Requests each API key, or each signed-in user, may make per minute.</summary>
    public int PerKeyPerMinute { get; set; } = 600;

    /// <summary>Requests per minute from one address without a valid key (slows down key guessing).</summary>
    public int AnonymousPerMinute { get; set; } = 60;

    /// <summary>
    /// Requests per minute from one address, checked before the key is, so a flood of guesses is
    /// turned away before any hashing happens. Raise it if many users share one address (a proxy).
    /// </summary>
    public int PerAddressPerMinute { get; set; } = 1200;
}

/// <summary>Checks the settings at startup so mistakes stop the service with a plain explanation.</summary>
internal sealed class ThunderOptionsValidator : IValidateOptions<ThunderOptions>
{
    public ValidateOptionsResult Validate(string? name, ThunderOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Data.Folder))
            errors.Add("Set Thunder:Data:Folder to the legacy app's data folder (the one holding the .dbf files).");

        if (options.Paging.DefaultTake < 1 || options.Paging.MaxTake < 1 || options.Paging.DefaultTake > options.Paging.MaxTake)
            errors.Add("Thunder:Paging needs 1 <= DefaultTake <= MaxTake.");
        if (options.LockTimeoutSeconds < 1)
            errors.Add("Thunder:LockTimeoutSeconds must be at least 1.");
        if (options.RateLimit.PerKeyPerMinute < 1 || options.RateLimit.AnonymousPerMinute < 1 || options.RateLimit.PerAddressPerMinute < 1)
            errors.Add("Thunder:RateLimit limits must be at least 1 request per minute.");
        if (string.IsNullOrWhiteSpace(options.Journal.Path))
            errors.Add("Thunder:Journal:Path can't be empty; every write must be journaled.");
        if (string.IsNullOrWhiteSpace(options.Backups.Folder))
            errors.Add("Thunder:Backups:Folder can't be empty; tables are backed up before their first write.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < options.ApiKeys.Count; i++)
        {
            var key = options.ApiKeys[i];
            var where = $"Thunder:ApiKeys:{i}";
            if (string.IsNullOrWhiteSpace(key.Id))
                errors.Add($"{where}:Id is missing. Give every key a name; it's what logs and the journal show.");
            else if (!ids.Add(key.Id))
                errors.Add($"{where}:Id '{key.Id}' is used twice.");
            else if (key.Id.StartsWith(OidcAuthentication.CallerPrefix, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{where}:Id can't start with '{OidcAuthentication.CallerPrefix}'; that prefix names signed-in users in logs and the journal.");

            if (ApiKeyHasher.TryParseHash(key.Sha256) is null)
                errors.Add($"{where}:Sha256 must be 64 hex digits. Run 'thunder-dbf-server hash-key' to make one; never put the key itself in config.");
            else if (!hashes.Add(key.Sha256))
                errors.Add($"{where}:Sha256 is the same hash as another key. Give each client its own key.");
        }

        if (options.OpenIdConnect.Enabled)
            ValidateOpenIdConnect(options.OpenIdConnect, errors);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateOpenIdConnect(OidcOptions oidc, List<string> errors)
    {
        const string where = "Thunder:OpenIdConnect";
        if (!Uri.TryCreate(oidc.Authority, UriKind.Absolute, out var authority) || authority.Scheme is not ("https" or "http"))
            errors.Add($"{where}:Authority must be the provider's issuer URL, like https://login.example.com/realms/main.");
        else if (oidc.RequireHttpsMetadata && authority.Scheme != "https")
            errors.Add($"{where}:Authority must use https, or the signing keys could be swapped in transit. Only a local test provider may set RequireHttpsMetadata to false.");
        if (string.IsNullOrWhiteSpace(oidc.Audience))
            errors.Add($"{where}:Audience is required (this API's client id or app ID URI), so tokens issued for other apps aren't accepted.");
        if (oidc.AccessTokenTypes.Count == 0 || oidc.AccessTokenTypes.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{where}:AccessTokenTypes can't be empty: list the typ values your provider puts on access tokens, such as at+jwt.");
        if (string.IsNullOrWhiteSpace(oidc.SubjectClaim))
            errors.Add($"{where}:SubjectClaim can't be empty; it names the user in logs and the journal.");

        if (oidc.Profiles.Count == 0)
            errors.Add($"{where}:Profiles is empty. Add at least one, shaped like an API key's permissions (Read, Write, Fields, CanListTables).");
        if (oidc.Map.Count == 0)
            errors.Add($"{where}:Map is empty, so every signed-in user would get 403. Map a role, group or claim to a profile.");

        for (var i = 0; i < oidc.Map.Count; i++)
        {
            var rule = oidc.Map[i];
            var at = $"{where}:Map:{i}";
            var kinds = new[] { rule.Role, rule.Group, rule.Claim }.Count(v => !string.IsNullOrWhiteSpace(v));
            if (kinds != 1)
                errors.Add($"{at} needs exactly one of Role, Group or Claim.");
            else if (!string.IsNullOrWhiteSpace(rule.Claim) && string.IsNullOrEmpty(rule.Value))
                errors.Add($"{at} has a Claim but no Value. Say which value of the claim the rule matches.");
            if (string.IsNullOrWhiteSpace(rule.Claim) && !string.IsNullOrEmpty(rule.Value))
                errors.Add($"{at}:Value only goes with Claim; Role and Group rules carry their value themselves.");
            if (string.IsNullOrWhiteSpace(rule.Profile))
                errors.Add($"{at}:Profile is missing. Name the profile a matching user gets.");
            else if (!oidc.Profiles.Keys.Any(p => string.Equals(p, rule.Profile, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"{at}:Profile '{rule.Profile}' isn't one of {where}:Profiles.");
        }
    }
}

/// <summary>Hashes API keys the one way config stores them: SHA-256 of the UTF-8 bytes, as hex.</summary>
public static class ApiKeyHasher
{
    public static byte[] Hash(string key) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));

    public static string HashHex(string key) => Convert.ToHexStringLower(Hash(key));

    /// <summary>The 32 hash bytes, or null if <paramref name="hex"/> isn't 64 hex digits.</summary>
    public static byte[]? TryParseHash(string? hex)
    {
        if (hex is null || hex.Length != 64)
            return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A new random key: 32 bytes, base64url, about 43 characters.</summary>
    public static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
