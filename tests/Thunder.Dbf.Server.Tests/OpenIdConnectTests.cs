//       _______
//      | JWT   |~~.      ProjectThunder :: Thunder.Dbf server tests
//      | sub   |  |      Signing in with a bearer token: a throwaway RSA key and issuer made
//      | roles |  |      for each test run, so no working key or token ever lands in the repo,
//      |_______|  |      and the server never goes to the network for its keys.
//        '--------'

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Thunder.Dbf.Server.Tests;

/// <summary>A pretend OpenID Connect provider: an issuer, an audience and an RSA key generated at run time.</summary>
public sealed class TestIdentityProvider : IDisposable
{
    public const string Issuer = "https://login.thunder.test/realms/main";
    public const string Audience = "api://thunder-dbf-tests";

    private readonly RSA _rsa = RSA.Create(2048);

    public TestIdentityProvider() => Key = new RsaSecurityKey(_rsa) { KeyId = "test-" + Guid.NewGuid().ToString("N") };

    public RsaSecurityKey Key { get; }

    /// <summary>
    /// Turns OpenID Connect on with three profiles. The rules are in order: admins first, then readers,
    /// then a group limited to two STAFF fields, then a scope.
    /// </summary>
    public static void Settings(Dictionary<string, string?> s)
    {
        s["Thunder:OpenIdConnect:Authority"] = Issuer;
        s["Thunder:OpenIdConnect:Audience"] = Audience;

        s["Thunder:OpenIdConnect:Profiles:everything:CanListTables"] = "true";
        s["Thunder:OpenIdConnect:Profiles:everything:Read:0"] = "*";
        s["Thunder:OpenIdConnect:Profiles:everything:Write:0"] = "*";

        s["Thunder:OpenIdConnect:Profiles:readers:Read:0"] = "people";
        s["Thunder:OpenIdConnect:Profiles:readers:Read:1"] = "STAFF";

        s["Thunder:OpenIdConnect:Profiles:limited:Read:0"] = "STAFF";
        s["Thunder:OpenIdConnect:Profiles:limited:Write:0"] = "STAFF";
        s["Thunder:OpenIdConnect:Profiles:limited:Fields:STAFF:0"] = "NAME";
        s["Thunder:OpenIdConnect:Profiles:limited:Fields:STAFF:1"] = "HIRED";

        s["Thunder:OpenIdConnect:Map:0:Role"] = "Thunder.Admin";
        s["Thunder:OpenIdConnect:Map:0:Profile"] = "everything";
        s["Thunder:OpenIdConnect:Map:1:Role"] = "Thunder.Reader";
        s["Thunder:OpenIdConnect:Map:1:Profile"] = "readers";
        s["Thunder:OpenIdConnect:Map:2:Group"] = "staff-clerks";
        s["Thunder:OpenIdConnect:Map:2:Profile"] = "limited";
        s["Thunder:OpenIdConnect:Map:3:Claim"] = "scope";
        s["Thunder:OpenIdConnect:Map:3:Value"] = "dbf.read";
        s["Thunder:OpenIdConnect:Map:3:Profile"] = "readers";
    }

    /// <summary>
    /// Hands the JwtBearer handler this provider's metadata directly, as if it had fetched
    /// /.well-known/openid-configuration, so validation runs exactly as in production but offline.
    /// </summary>
    public void Services(IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(OidcAuthentication.SchemeName, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(Key);
            options.Configuration = configuration;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        });

    public string Token(string? subject = "alice", string[]? roles = null, string[]? groups = null, string? audience = Audience,
        TimeSpan? expiresIn = null, SecurityKey? signingKey = null, IDictionary<string, object>? extra = null, string tokenType = "at+jwt")
    {
        var claims = new Dictionary<string, object>(extra ?? new Dictionary<string, object>());
        if (subject is not null)
            claims["sub"] = subject;
        if (roles is not null)
            claims["roles"] = roles;
        if (groups is not null)
            claims["groups"] = groups;

        var expires = DateTime.UtcNow + (expiresIn ?? TimeSpan.FromMinutes(10));
        var issued = expires < DateTime.UtcNow ? expires - TimeSpan.FromMinutes(30) : DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = issued,
            NotBefore = issued,
            Expires = expires,
            SigningCredentials = new SigningCredentials(signingKey ?? Key, SecurityAlgorithms.RsaSha256),
            TokenType = tokenType,
        };
        return new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateEncodedJwt(descriptor);
    }

    public void Dispose() => _rsa.Dispose();
}

public sealed class OpenIdConnectTests : IDisposable
{
    private readonly ServerHarness _harness = new();
    private readonly TestIdentityProvider _provider = new();

    public void Dispose()
    {
        _harness.Dispose();
        _provider.Dispose();
    }

    private HttpClient Client(string? token, Action<Dictionary<string, string?>>? adjust = null, string? apiKey = null)
    {
        var client = _harness.Client(apiKey, s =>
        {
            TestIdentityProvider.Settings(s);
            adjust?.Invoke(s);
        }, services: _provider.Services);
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task A_valid_token_with_a_mapped_role_reads()
    {
        var response = await Client(_provider.Token(roles: ["Thunder.Reader"])).GetAsync("/tables/people/records");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty((await response.Json()).GetProperty("records").EnumerateArray());
    }

    [Fact]
    public async Task A_valid_token_that_maps_to_no_profile_is_a_clear_403()
    {
        var response = await Client(_provider.Token(roles: ["Some.Other.Role"], groups: ["not-mapped"])).GetAsync("/tables/people/records");

        var problem = await response.Problem(403, _harness);
        Assert.Equal("Forbidden", problem.GetProperty("code").GetString());
        Assert.Contains("access profile", problem.GetProperty("detail").GetString());
    }

    // ---- from the Codex security review ------------------------------------------------------

    [Fact]
    public async Task An_ID_token_is_refused_even_when_its_audience_and_role_would_pass()
    {
        // ID tokens carry typ JWT and a nonce; both are refused on their own.
        var plainType = _provider.Token(roles: ["Thunder.Admin"], tokenType: "JWT");
        await (await Client(plainType).GetAsync("/tables/people")).Problem(401, _harness);

        var withNonce = _provider.Token(roles: ["Thunder.Admin"], extra: new Dictionary<string, object> { ["nonce"] = "abc" });
        await (await Client(withNonce).GetAsync("/tables/people")).Problem(401, _harness);
    }

    [Fact]
    public async Task An_audience_that_differs_only_by_a_trailing_slash_is_refused()
    {
        var token = _provider.Token(roles: ["Thunder.Admin"], audience: TestIdentityProvider.Audience + "/");
        await (await Client(token).GetAsync("/tables/people")).Problem(401, _harness);
    }

    [Fact]
    public async Task An_expired_token_is_a_401_that_says_so()
    {
        var response = await Client(_provider.Token(roles: ["Thunder.Admin"], expiresIn: TimeSpan.FromMinutes(-10))).GetAsync("/tables/people");

        var problem = await response.Problem(401, _harness);
        Assert.Contains("expired", problem.GetProperty("title").GetString());
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_token_for_another_audience_is_a_401()
    {
        var response = await Client(_provider.Token(roles: ["Thunder.Admin"], audience: "api://some-other-app")).GetAsync("/tables/people");
        await response.Problem(401, _harness);
    }

    [Fact]
    public async Task A_token_signed_by_another_key_is_a_401()
    {
        using var other = RSA.Create(2048);
        var forged = _provider.Token(roles: ["Thunder.Admin"], signingKey: new RsaSecurityKey(other) { KeyId = _provider.Key.KeyId });

        var response = await Client(forged).GetAsync("/tables/people");
        await response.Problem(401, _harness);
    }

    [Fact]
    public async Task A_token_without_a_subject_is_a_401()
    {
        var response = await Client(_provider.Token(subject: null, roles: ["Thunder.Admin"])).GetAsync("/tables/people");
        await response.Problem(401, _harness);
    }

    [Fact]
    public async Task Garbage_in_the_bearer_header_is_a_401_not_a_500()
    {
        var response = await Client("not.a.jwt").GetAsync("/tables/people");
        await response.Problem(401, _harness);
    }

    [Fact]
    public async Task Sending_a_key_and_a_token_is_a_400()
    {
        var response = await Client(_provider.Token(roles: ["Thunder.Reader"]), apiKey: ServerHarness.AdminKey).GetAsync("/tables/people");

        var problem = await response.Problem(400, _harness);
        Assert.Equal("TwoCredentials", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Write_permission_comes_from_the_profile_and_the_journal_names_the_user()
    {
        var reader = Client(_provider.Token(subject: "bob", roles: ["Thunder.Reader"]));
        var refused = await reader.PostAsJsonAsync("/tables/STAFF/records", new Dictionary<string, object?> { ["NAME"] = "Linus" });
        await refused.Problem(403, _harness);

        var admin = Client(_provider.Token(subject: "alice", roles: ["Thunder.Admin"]));
        var created = await admin.PostAsJsonAsync("/tables/STAFF/records", new Dictionary<string, object?> { ["NAME"] = "Linus" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var lines = await File.ReadAllLinesAsync(_harness.JournalPath);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal("oidc:alice", JsonDocument.Parse(line).RootElement.GetProperty("keyId").GetString()));
        Assert.All(lines, line => Assert.DoesNotContain("eyJ", line)); // no token, or any piece of one
    }

    [Fact]
    public async Task Field_allow_lists_in_the_profile_hide_fields()
    {
        var client = Client(_provider.Token(groups: ["staff-clerks"]));

        var record = await (await client.GetAsync("/tables/STAFF/records/1")).Json();
        Assert.Equal(["_recno", "_deleted", "NAME", "HIRED"], record.EnumerateObject().Select(p => p.Name));

        var write = await client.PostAsJsonAsync("/tables/STAFF/records", new Dictionary<string, object?> { ["SALARY"] = 1 });
        var problem = await write.Problem(400, _harness);
        Assert.Equal("The table has no field by that name.", problem.GetProperty("errors").GetProperty("SALARY")[0].GetString());

        await (await client.GetAsync("/tables/people")).Problem(404, _harness);
    }

    [Fact]
    public async Task A_scope_claim_can_map_to_a_profile()
    {
        var token = _provider.Token(extra: new Dictionary<string, object> { ["scope"] = "openid dbf.read profile" });
        Assert.Equal(HttpStatusCode.OK, (await Client(token).GetAsync("/tables/people")).StatusCode);
    }

    [Fact]
    public async Task The_first_rule_that_matches_wins()
    {
        // Admin is rule 0, so a user who is also a reader gets the admin profile, never a blend.
        var client = Client(_provider.Token(roles: ["Thunder.Reader", "Thunder.Admin"]));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/tables")).StatusCode);

        var clerk = Client(_provider.Token(roles: ["Thunder.Reader"], groups: ["staff-clerks"]));
        var record = await (await clerk.GetAsync("/tables/STAFF/records/1")).Json();
        Assert.True(record.TryGetProperty("SALARY", out _)); // the readers profile, which has no allow-list
    }

    [Fact]
    public async Task A_token_cant_claim_to_be_an_api_key()
    {
        var token = _provider.Token(extra: new Dictionary<string, object> { [ApiKeyAuthentication.KeyIdClaim] = "admin" });

        var response = await Client(token).GetAsync("/tables/people");
        await response.Problem(403, _harness);
    }

    [Fact]
    public async Task Rate_limits_count_each_user_separately()
    {
        var alice = Client(_provider.Token(subject: "alice", roles: ["Thunder.Reader"]), s => s["Thunder:RateLimit:PerKeyPerMinute"] = "2");

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/tables/people")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/tables/people")).StatusCode);
        await (await alice.GetAsync("/tables/people")).Problem(429, _harness);

        // Same server, same address, another user: a budget of their own.
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _provider.Token(subject: "bob", roles: ["Thunder.Reader"]));
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/tables/people")).StatusCode);
    }

    [Fact]
    public async Task Without_a_credential_the_challenge_offers_both()
    {
        var response = await Client(token: null).GetAsync("/tables/people");

        var problem = await response.Problem(401, _harness);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("X-Api-Key", challenge);
        Assert.Contains("Bearer", challenge);
        Assert.Contains("bearer token", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Api_keys_still_work_beside_tokens()
    {
        var response = await Client(token: null, apiKey: ServerHarness.ReaderKey).GetAsync("/tables/people");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task With_OpenID_Connect_off_a_token_gets_nowhere()
    {
        var client = _harness.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _provider.Token(roles: ["Thunder.Admin"]));

        var response = await client.GetAsync("/tables/people");
        await response.Problem(401, _harness);
        Assert.DoesNotContain("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task The_OpenAPI_document_offers_bearer_tokens_without_naming_the_provider()
    {
        var text = await Client(token: null).GetStringAsync("/openapi/v1.json");

        Assert.Contains("\"bearer\"", text);
        Assert.DoesNotContain("login.thunder.test", text);
    }

    [Theory]
    [InlineData("Thunder:OpenIdConnect:Map:0:Profile", "nosuch", "isn't one of")]
    [InlineData("Thunder:OpenIdConnect:Audience", "", "Audience is required")]
    [InlineData("Thunder:OpenIdConnect:Authority", "http://login.thunder.test/realms/main", "must use https")]
    [InlineData("Thunder:OpenIdConnect:Authority", "not a url", "issuer URL")]
    [InlineData("Thunder:OpenIdConnect:Map:1:Group", "both-role-and-group", "exactly one of")]
    [InlineData("Thunder:ApiKeys:0:Id", "oidc:admin", "can't start with")]
    public void Bad_OpenID_Connect_settings_stop_the_server_with_a_plain_explanation(string setting, string value, string expected)
    {
        var error = Assert.ThrowsAny<Exception>(() => Client(token: null, s => s[setting] = value));
        Assert.Contains(expected, error.ToString());
    }

    [Fact]
    public async Task Plain_http_metadata_is_allowed_only_when_asked_for()
    {
        var client = Client(_provider.Token(roles: ["Thunder.Reader"]), s =>
        {
            s["Thunder:OpenIdConnect:Authority"] = "http://localhost:8080/realms/main";
            s["Thunder:OpenIdConnect:RequireHttpsMetadata"] = "false";
        });
        // The issuer in the token is still the provider's own, so the metadata handed over in the test wins.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/tables/people")).StatusCode);
    }
}
