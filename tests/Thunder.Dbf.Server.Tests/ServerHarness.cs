//      ___________
//     |  _______  |      ProjectThunder :: Thunder.Dbf server tests
//     | | TEST  | |      A throwaway data folder, three keys and a server in memory.
//     | |_______| |      Each test gets fresh copies, so writes never touch tests/fixtures.
//     |___________|
//       _|_____|_

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Thunder.Dbf.Server.Tests;

/// <summary>A data folder copied from the fixtures, plus a configured in-memory server over it.</summary>
public sealed class ServerHarness : IDisposable
{
    // Fresh random keys every run, so no working key is ever written down in the repo.
    public static readonly string AdminKey = NewKey();
    public static readonly string ReaderKey = NewKey();
    public static readonly string LimitedKey = NewKey();

    private static string NewKey() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static readonly string FixtureRoot = FindFixtureRoot();
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("thunder-server-");
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public ServerHarness()
    {
        Directory.CreateDirectory(DataFolder);
        File.Copy(Path.Combine(FixtureRoot, "dbfread", "people.dbf"), Path.Combine(DataFolder, "people.dbf"));
        foreach (var file in new[] { "client.dbf", "client.dbt" })
            File.Copy(Path.Combine(FixtureRoot, "dbfdatareader", file), Path.Combine(DataFolder, file));
        foreach (var file in new[] { "CHANGELOG.DBF", "CHANGELOG.DBT", "CHANGELOG.MDX" })
            File.Copy(Path.Combine(FixtureRoot, "siemens-step7", "siprom", file), Path.Combine(DataFolder, file));

        // A small table with one field of each everyday type, for writes and validation.
        using var staff = DbfTable.Create(Path.Combine(DataFolder, "STAFF.DBF"),
        [
            DbfFieldDefinition.Character("NAME", 10),
            DbfFieldDefinition.Numeric("SALARY", 8, 2),
            DbfFieldDefinition.Date("HIRED"),
            DbfFieldDefinition.Logical("ACTIVE"),
            DbfFieldDefinition.Memo("NOTES"),
        ]);
        staff.Append("Ada", 1234.5m, new DateOnly(1997, 4, 12), true, "First hire.");
        staff.Append("Grace", 99.99m, new DateOnly(2001, 9, 1), false, null);
    }

    public string Root => _root.FullName;
    public string DataFolder => Path.Combine(Root, "data");
    public string JournalPath => Path.Combine(Root, "journal", "writes.jsonl");
    public string BackupFolder => Path.Combine(Root, "backups");

    /// <summary>
    /// The settings every test starts from: writes on for STAFF only, an admin key that can do
    /// everything, a reader for two tables, and a key limited to two STAFF fields.
    /// </summary>
    public Dictionary<string, string?> BaseSettings(bool readOnly = false) => new()
    {
        ["Thunder:Data:Folder"] = DataFolder,
        ["Thunder:Data:ReadOnly"] = readOnly ? "true" : "false",
        ["Thunder:Tables:STAFF:AllowWrites"] = "true",
        ["Thunder:Journal:Path"] = JournalPath,
        ["Thunder:Backups:Folder"] = BackupFolder,
        ["Thunder:Paging:DefaultTake"] = "2",
        ["Thunder:Paging:MaxTake"] = "3",

        ["Thunder:ApiKeys:0:Id"] = "admin",
        ["Thunder:ApiKeys:0:Sha256"] = ApiKeyHasher.HashHex(AdminKey),
        ["Thunder:ApiKeys:0:CanListTables"] = "true",
        ["Thunder:ApiKeys:0:Read:0"] = "*",
        ["Thunder:ApiKeys:0:Write:0"] = "*",

        ["Thunder:ApiKeys:1:Id"] = "reader",
        ["Thunder:ApiKeys:1:Sha256"] = ApiKeyHasher.HashHex(ReaderKey),
        ["Thunder:ApiKeys:1:Read:0"] = "people",
        ["Thunder:ApiKeys:1:Read:1"] = "STAFF",

        ["Thunder:ApiKeys:2:Id"] = "limited",
        ["Thunder:ApiKeys:2:Sha256"] = ApiKeyHasher.HashHex(LimitedKey),
        ["Thunder:ApiKeys:2:Read:0"] = "STAFF",
        ["Thunder:ApiKeys:2:Read:1"] = "CHANGELOG",
        ["Thunder:ApiKeys:2:Write:0"] = "STAFF",
        ["Thunder:ApiKeys:2:Fields:STAFF:0"] = "NAME",
        ["Thunder:ApiKeys:2:Fields:STAFF:1"] = "HIRED",
        ["Thunder:ApiKeys:2:Fields:CHANGELOG:0"] = "ID",
        ["Thunder:ApiKeys:2:Fields:CHANGELOG:1"] = "USER",
    };

    /// <summary>A client for a server built from <see cref="BaseSettings"/>, adjusted by <paramref name="adjust"/>.</summary>
    public HttpClient Client(string? apiKey = null, Action<Dictionary<string, string?>>? adjust = null, bool readOnly = false)
    {
        var settings = BaseSettings(readOnly);
        adjust?.Invoke(settings);

        // Disposing the base factory also disposes the one WithWebHostBuilder derives from it, but not the other way round.
        var root = new WebApplicationFactory<Program>();
        _factories.Add(root);
        var factory = root.WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (apiKey is not null)
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
            factory.Dispose();
        _root.Delete(recursive: true);
    }

    private static string FindFixtureRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Couldn't find tests/fixtures above the test output folder.");
    }
}

/// <summary>Assertions shared by the server tests.</summary>
public static class Http
{
    public static async Task<JsonElement> Json(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Checks the RFC 7807 shape and that nothing private leaked, and returns the problem.</summary>
    public static async Task<JsonElement> Problem(this HttpResponseMessage response, int status, ServerHarness harness)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"Expected {status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertNoLeaks(text, harness);

        var problem = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("code").GetString()));
        return problem;
    }

    public static void AssertNoLeaks(string text, ServerHarness harness)
    {
        Assert.DoesNotContain(harness.Root, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('/', '\\'), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text, StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName.Length >= 3)
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at Thunder.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:line", text, StringComparison.Ordinal);
    }
}
