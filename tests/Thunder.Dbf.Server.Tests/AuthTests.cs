//        .---.
//       /     \       ProjectThunder :: Thunder.Dbf server tests
//      | () () |      Who gets in, who gets a 401, a 403 or a 404,
//       \  ^  /       and that none of them learn where the files live.
//        |||||

using System.Net;

namespace Thunder.Dbf.Server.Tests;

public sealed class AuthTests : IDisposable
{
    private readonly ServerHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Health_needs_no_key()
    {
        var response = await _harness.Client().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.Json()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task No_key_is_a_401_problem_with_a_challenge()
    {
        var response = await _harness.Client().GetAsync("/tables/people/records");

        var problem = await response.Problem(401, _harness);
        Assert.Equal("Unauthorized", problem.GetProperty("code").GetString());
        Assert.Contains("X-Api-Key", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("admin-key-0123456789-abcdefghijklmnoq")] // one character off
    public async Task Unknown_keys_are_401s(string key)
    {
        var response = await _harness.Client(key).GetAsync("/tables/people");
        await response.Problem(401, _harness);
    }

    [Fact]
    public async Task A_hash_in_place_of_the_key_doesnt_work()
    {
        // Config holds hashes; someone who reads appsettings.json still can't call the API.
        var response = await _harness.Client(ApiKeyHasher.HashHex(ServerHarness.AdminKey)).GetAsync("/tables/people");
        await response.Problem(401, _harness);
    }

    [Fact]
    public async Task Listing_tables_needs_permission()
    {
        var response = await _harness.Client(ServerHarness.ReaderKey).GetAsync("/tables");
        await response.Problem(403, _harness);
    }

    [Fact]
    public async Task Listing_shows_only_readable_tables_and_what_can_be_written()
    {
        var all = await (await _harness.Client(ServerHarness.AdminKey).GetAsync("/tables")).Json();
        var tables = all.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("canWrite").GetBoolean());

        Assert.Equal(["CHANGELOG", "client", "people", "STAFF"], tables.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(tables["STAFF"]);
        Assert.False(tables["people"]);
    }

    [Fact]
    public async Task A_table_the_key_cant_read_looks_exactly_like_one_that_doesnt_exist()
    {
        var client = _harness.Client(ServerHarness.ReaderKey);

        var hidden = await (await client.GetAsync("/tables/client")).Problem(404, _harness);
        var missing = await (await client.GetAsync("/tables/nosuchtable")).Problem(404, _harness);

        Assert.Equal(missing.GetProperty("detail").GetString(), hidden.GetProperty("detail").GetString());
        Assert.Equal(missing.GetProperty("hint").GetString(), hidden.GetProperty("hint").GetString());
        Assert.DoesNotContain("CHANGELOG", hidden.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("people", hidden.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Keys_that_may_list_tables_get_the_names_as_a_hint()
    {
        var problem = await (await _harness.Client(ServerHarness.AdminKey).GetAsync("/tables/nosuchtable")).Problem(404, _harness);
        Assert.Contains("people", problem.GetProperty("hint").GetString());
    }

    [Theory]
    [InlineData("..%2Fpeople")]
    [InlineData("..%5Cpeople")]
    [InlineData("%2E%2E%2F%2E%2E%2Fetc%2Fpasswd")]
    [InlineData("..%5C..%5Cdata%5Cpeople")]
    [InlineData("C:%5Cdata%5Cpeople")]
    [InlineData("%2Fetc%2Fpasswd")]
    [InlineData("people.dbt")]
    [InlineData("CHANGELOG.MDX")]
    [InlineData("%2E%2E")]
    [InlineData("..")]
    public async Task Path_escapes_in_table_names_are_refused_without_leaking_paths(string name)
    {
        var response = await _harness.Client(ServerHarness.AdminKey).GetAsync($"/tables/{name}/records");

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest,
            $"{name}: {(int)response.StatusCode} {text}");
        Http.AssertNoLeaks(text, _harness);
        Assert.DoesNotContain("\"_recno\"", text);
    }

    [Fact]
    public async Task Keys_are_rate_limited_per_minute()
    {
        var client = _harness.Client(ServerHarness.ReaderKey, s => s["Thunder:RateLimit:PerKeyPerMinute"] = "2");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/tables/people")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/tables/people")).StatusCode);
        var limited = await client.GetAsync("/tables/people");

        var problem = await limited.Problem(429, _harness);
        Assert.Equal("RateLimited", problem.GetProperty("code").GetString());
        Assert.True(limited.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task The_OpenAPI_document_describes_the_routes_and_the_key_header()
    {
        var response = await _harness.Client().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("/tables/{name}/records/{recno}", text);
        Assert.Contains("/tables/{name}/seek", text);
        Assert.Contains("X-Api-Key", text);
        Http.AssertNoLeaks(text, _harness);
    }

    [Fact]
    public void Bad_config_stops_the_server_with_a_plain_explanation()
    {
        var error = Assert.ThrowsAny<Exception>(() => _harness.Client(adjust: s => s["Thunder:ApiKeys:0:Sha256"] = "not-a-hash"));
        Assert.Contains("hash-key", error.ToString());
    }

    [Fact]
    public void Hash_key_makes_a_key_and_its_hash()
    {
        var output = new StringWriter();
        Assert.Equal(0, HashKeyCommand.Run([], TextReader.Null, output, TextWriter.Null));

        var lines = output.ToString().Split('\n');
        var key = lines[0]["key:".Length..].Trim();
        var hash = lines[1]["sha256:".Length..].Trim();
        Assert.True(key.Length >= 40);
        Assert.Equal(ApiKeyHasher.HashHex(key), hash);

        var piped = new StringWriter();
        Assert.Equal(0, HashKeyCommand.Run(["-"], new StringReader(key + "\n"), piped, TextWriter.Null));
        Assert.Equal(hash, piped.ToString().Trim());
    }

    [Theory]
    [InlineData("/tables/x\n2026-10-30 ERROR forged line", "/tables/x?2026-10-30 ERROR forged line")]
    [InlineData("/tables/\r\u001b[31mred", "/tables/??[31mred")]
    [InlineData(null, "")]
    public void Request_details_cant_forge_log_lines(string? path, string logged) =>
        Assert.Equal(logged, ProblemExceptionHandler.ForLog(path));

    [Fact]
    public void Very_long_paths_are_cut_short_in_the_log() =>
        Assert.Equal(303, ProblemExceptionHandler.ForLog(new string('a', 5000)).Length);
}
