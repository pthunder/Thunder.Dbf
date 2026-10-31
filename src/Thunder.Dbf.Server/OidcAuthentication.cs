//                    .-- X-Api-Key ---------> [ ApiKey ] --.
//     request ------<                                       >-- thunder:key_id --> AccessRegistry
//                    '-- Authorization: Bearer -> [ JWT ] --'    "website" or "oidc:<sub>"
//
//     ProjectThunder :: Thunder.Dbf
//     Two ways in, one way to permissions. The header picks the handler; both handlers name the
//     caller the same way, and a token's roles and groups pick an access profile shaped exactly
//     like an API key's. Sending both headers is refused: a caller is one thing or the other.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Thunder.Dbf.Server;

/// <summary>
/// OpenID Connect sign-in: bearer tokens from the configured provider, checked by the JwtBearer handler,
/// beside API keys. A policy scheme picks the handler from the request's headers.
/// </summary>
public static class OidcAuthentication
{
    /// <summary>The scheme that checks bearer tokens.</summary>
    public const string SchemeName = "Bearer";

    /// <summary>The default scheme: it looks at the headers and forwards to the API key or bearer scheme.</summary>
    public const string SelectorSchemeName = "Thunder";

    /// <summary>How signed-in users are named in logs, the journal and rate limits: <c>oidc:&lt;subject&gt;</c>.</summary>
    public const string CallerPrefix = "oidc:";

    // A subject goes into log lines and the journal, so it has to be short and printable.
    private const int MaxSubjectLength = 200;

    // Only asymmetric signatures: the provider publishes public keys, and a token "signed" with
    // HMAC using one of those public keys as the secret must never pass.
    private static readonly string[] AsymmetricAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    /// <summary>Registers both schemes behind the selector. The bearer scheme only ever runs when OpenID Connect is configured.</summary>
    public static void AddThunderAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(SelectorSchemeName)
            .AddPolicyScheme(SelectorSchemeName, null, options => options.ForwardDefaultSelector = SelectScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthentication>(ApiKeyAuthentication.SchemeName, null)
            .AddJwtBearer(SchemeName, _ => { });

        // Configured from ThunderOptions when first needed, so the settings are read (and validated)
        // the same way as every other setting, whichever configuration source they came from.
        services.AddOptions<JwtBearerOptions>(SchemeName).Configure<IOptions<ThunderOptions>>((jwt, thunder) => Configure(jwt, thunder.Value.OpenIdConnect));
    }

    /// <summary>Bearer tokens go to the JWT handler when OpenID Connect is on; everything else to the API key handler.</summary>
    internal static string SelectScheme(HttpContext http) =>
        IsBearer(http.Request) && http.RequestServices.GetRequiredService<IOptions<ThunderOptions>>().Value.OpenIdConnect.Enabled
            ? SchemeName
            : ApiKeyAuthentication.SchemeName;

    private static bool IsBearer(HttpRequest request) =>
        request.Headers.Authorization.Any(v => v is not null && v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Refuses a request that carries both an API key and a bearer token with 400. Guessing which one
    /// the caller meant could run the request with permissions it didn't expect.
    /// </summary>
    internal static async Task RefuseTwoCredentials(HttpContext http, RequestDelegate next)
    {
        if (http.Request.Headers.ContainsKey(ApiKeyAuthentication.HeaderName) && IsBearer(http.Request))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                ProblemDetails = Problems.Create(StatusCodes.Status400BadRequest, "Send one credential, not two.",
                    $"The request has both an {ApiKeyAuthentication.HeaderName} header and a bearer token.", "TwoCredentials",
                    "Send either the API key or the bearer token."),
            });
            return;
        }
        await next(http);
    }

    private static void Configure(JwtBearerOptions jwt, OidcOptions oidc)
    {
        if (!oidc.Enabled)
            return;

        jwt.Authority = oidc.Authority;
        jwt.Audience = oidc.Audience;
        jwt.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        jwt.MapInboundClaims = false; // keep "sub", "roles" and "groups" as the provider wrote them
        jwt.SaveToken = false;        // the token is never kept past the request
        jwt.IncludeErrorDetails = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidAudience = oidc.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = AsymmetricAlgorithms,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = ApiKeyAuthentication.KeyIdClaim,
            AuthenticationType = SchemeName,
        };
        jwt.Events = new JwtBearerEvents
        {
            OnTokenValidated = context => TokenValidated(context, oidc),
            OnChallenge = Challenge,
            OnForbidden = Forbidden,
        };
    }

    // Rebuilds the principal from the token's own claims, minus any that use the server's
    // "thunder:" names, plus the caller's name. A provider (or a user who can edit their own
    // claims there) can't hand themselves an API key's identity by putting thunder:key_id in a token.
    private static Task TokenValidated(TokenValidatedContext context, OidcOptions oidc)
    {
        var subject = context.Principal?.FindFirst(oidc.SubjectClaim)?.Value;
        if (string.IsNullOrEmpty(subject) || subject.Length > MaxSubjectLength || subject.Any(char.IsControl))
        {
            context.Fail("The token has no usable subject claim.");
            return Task.CompletedTask;
        }

        var claims = context.Principal!.Claims
            .Where(c => !c.Type.StartsWith("thunder:", StringComparison.OrdinalIgnoreCase))
            .Select(c => new Claim(c.Type, c.Value, c.ValueType, c.Issuer))
            .Append(new Claim(ApiKeyAuthentication.KeyIdClaim, CallerPrefix + subject));
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ApiKeyAuthentication.KeyIdClaim, roleType: null));
        return Task.CompletedTask;
    }

    // The handler's own challenge is a bare 401; ours is a ProblemDetails like every other refusal.
    // It says whether the token expired (the one case a client can fix by itself), never why else
    // it failed: the server log has that.
    private static async Task Challenge(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        http.Response.Headers.WWWAuthenticate = context.AuthenticateFailure is null ? SchemeName : $"{SchemeName} error=\"invalid_token\"";
        var expired = context.AuthenticateFailure is SecurityTokenExpiredException;
        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = Problems.Create(StatusCodes.Status401Unauthorized,
                expired ? "The bearer token has expired." : "The bearer token isn't valid.",
                "Sign in again to get a fresh token issued for this API.", "Unauthorized"),
        });
    }

    private static async Task Forbidden(ForbiddenContext context)
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = Problems.Create(StatusCodes.Status403Forbidden, "You aren't allowed to do that.", null, "Forbidden"),
        });
    }
}
