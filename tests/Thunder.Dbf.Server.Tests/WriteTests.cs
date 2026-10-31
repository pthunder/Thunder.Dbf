//       ______
//      |  __  |__      ProjectThunder :: Thunder.Dbf server tests
//      | |__| |  |     Writing: refused unless every switch says yes, checked against the
//      |  __  |__|     schema, backed up first, and journaled before and after. Every time.
//      |_|  |_|

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Thunder.Dbf.Server.Tests;

public sealed class WriteTests : IDisposable
{
    private readonly ServerHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private int StaffCount()
    {
        using var table = DbfTable.Open(Path.Combine(_harness.DataFolder, "STAFF.DBF"));
        return table.RecordCount;
    }

    [Fact]
    public async Task A_write_round_trip_is_backed_up_and_journaled()
    {
        var client = _harness.Client(ServerHarness.AdminKey);

        // Insert
        var created = await client.PostAsJsonAsync("/tables/staff/records",
            new { NAME = "Linus", SALARY = 4000.25, HIRED = "2010-01-31", ACTIVE = true, NOTES = "Kernel." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/tables/STAFF/records/3", created.Headers.Location?.OriginalString);
        var inserted = await created.Json();
        Assert.Equal(3, inserted.GetProperty("_recno").GetInt32());
        Assert.Equal(4000.25m, inserted.GetProperty("SALARY").GetDecimal());
        Assert.Equal("Kernel.", inserted.GetProperty("NOTES").GetString());

        // Partial update: only SALARY changes
        var patched = await client.PatchAsJsonAsync("/tables/staff/records/3", new { SALARY = 4100 });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var updated = await patched.Json();
        Assert.Equal(4100m, updated.GetProperty("SALARY").GetDecimal());
        Assert.Equal("Linus", updated.GetProperty("NAME").GetString());
        Assert.Equal("2010-01-31", updated.GetProperty("HIRED").GetString());
        Assert.Equal("Kernel.", updated.GetProperty("NOTES").GetString());

        // Delete marks the record; it's still readable and hidden from pages by default
        var deleted = await (await client.DeleteAsync("/tables/staff/records/3")).Json();
        Assert.True(deleted.GetProperty("_deleted").GetBoolean());
        var page = await (await client.GetAsync("/tables/staff/records?take=3")).Json();
        Assert.Equal(2, page.GetProperty("records").GetArrayLength());
        var withDeleted = await (await client.GetAsync("/tables/staff/records?take=3&includeDeleted=true")).Json();
        Assert.Equal(3, withDeleted.GetProperty("records").GetArrayLength());

        // The legacy app sees the same thing
        using (var table = DbfTable.Open(Path.Combine(_harness.DataFolder, "STAFF.DBF")))
        {
            var record = table.Read(2);
            Assert.True(record.IsDeleted);
            Assert.Equal(4100m, record["SALARY"]);
            Assert.Equal("Kernel.", record["NOTES"]);
        }

        // Two journal lines per write, the intent and then what happened, naming the key by id and
        // never the key itself
        var journal = await File.ReadAllLinesAsync(_harness.JournalPath);
        Assert.Equal(6, journal.Length);
        var lines = journal.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(["intent", "done", "intent", "done", "intent", "done"], lines.Select(e => e.GetProperty("status").GetString()));
        Assert.Equal(4100m, lines[2].GetProperty("after").GetProperty("SALARY").GetDecimal()); // the update's intent says what was asked
        var entries = lines.Where(e => e.GetProperty("status").GetString() == "done").ToList();
        Assert.Equal(["insert", "update", "delete"], entries.Select(e => e.GetProperty("operation").GetString()));
        Assert.All(entries, e =>
        {
            Assert.Equal("admin", e.GetProperty("keyId").GetString());
            Assert.Equal("STAFF", e.GetProperty("table").GetString());
            Assert.Equal(3, e.GetProperty("recno").GetInt32());
            Assert.True(DateTimeOffset.TryParse(e.GetProperty("time").GetString(), out _));
        });
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("before").ValueKind);
        Assert.Equal(4000.25m, entries[1].GetProperty("before").GetProperty("SALARY").GetDecimal());
        Assert.Equal(4100m, entries[1].GetProperty("after").GetProperty("SALARY").GetDecimal());
        Assert.True(entries[2].GetProperty("after").GetProperty("_deleted").GetBoolean());
        Assert.All(journal, line => Assert.DoesNotContain(ServerHarness.AdminKey, line));

        // One backup, taken before the first write, holding the table as it was
        var backup = Assert.Single(Directory.GetDirectories(_harness.BackupFolder));
        Assert.Equal(["STAFF.DBF", "STAFF.dbt"], Directory.GetFiles(backup).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        using var original = DbfTable.Open(Path.Combine(backup, "STAFF.DBF"));
        Assert.Equal(2, original.RecordCount);
    }

    [Fact]
    public async Task Patching_keeps_the_bytes_of_fields_it_doesnt_name()
    {
        // Another app wrote SALARY left-padded with zeros; a PATCH of NAME must leave that alone.
        var path = Path.Combine(_harness.DataFolder, "STAFF.DBF");
        int salaryOfSecondRecord;
        using (var table = DbfTable.Open(path))
            salaryOfSecondRecord = table.Header.HeaderLength + table.Header.RecordLength + table.Fields.Single(f => f.Name == "SALARY").Offset;
        var bytes = await File.ReadAllBytesAsync(path);
        "00099.99"u8.CopyTo(bytes.AsSpan(salaryOfSecondRecord, 8));
        await File.WriteAllBytesAsync(path, bytes);

        var response = await _harness.Client(ServerHarness.AdminKey).PatchAsJsonAsync("/tables/STAFF/records/2", new { NAME = "Hopper", SALARY = 99.99 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await File.ReadAllBytesAsync(path);
        Assert.Equal("00099.99", System.Text.Encoding.ASCII.GetString(after, salaryOfSecondRecord, 8));
        Assert.Equal("Hopper", (await response.Json()).GetProperty("NAME").GetString());
    }

    [Fact]
    public async Task Bad_values_are_400s_with_an_error_per_field_and_nothing_is_written()
    {
        var response = await _harness.Client(ServerHarness.AdminKey).PostAsJsonAsync("/tables/STAFF/records", new Dictionary<string, object?>
        {
            ["NAME"] = "Much too long for ten",
            ["SALARY"] = 1.005,
            ["HIRED"] = "12/01/1999",
            ["ACTIVE"] = "yes",
            ["NOTES"] = 42,
            ["SHOE_SIZE"] = 9,
        });

        var problem = await response.Problem(400, _harness);
        Assert.Equal("ValidationFailed", problem.GetProperty("code").GetString());
        var errors = problem.GetProperty("errors");
        foreach (var field in new[] { "NAME", "SALARY", "HIRED", "ACTIVE", "NOTES", "SHOE_SIZE" })
            Assert.True(errors.TryGetProperty(field, out var messages) && messages.GetArrayLength() == 1, $"no error for {field}: {errors}");
        Assert.Equal(2, StaffCount());
    }

    [Theory]
    [InlineData("{\"SALARY\": 123456.78}")] // 9 characters in an N(8,2)
    [InlineData("{\"SALARY\": \"12\"}")]   // a string, not a number
    [InlineData("{\"NAME\": \"Ωmega\"}")]  // not in code page 1252
    [InlineData("[1, 2]")]
    [InlineData("not json")]
    public async Task More_bad_bodies_are_400s(string body)
    {
        var response = await _harness.Client(ServerHarness.AdminKey).PatchAsync("/tables/STAFF/records/1",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        await response.Problem(400, _harness);
    }

    [Fact]
    public async Task Read_only_servers_dont_offer_writes_at_all()
    {
        var client = _harness.Client(ServerHarness.AdminKey, readOnly: true);

        var post = await client.PostAsJsonAsync("/tables/STAFF/records", new { NAME = "Nope" });
        await post.Problem(405, _harness);
        var delete = await client.DeleteAsync("/tables/STAFF/records/1");
        await delete.Problem(405, _harness);

        Assert.Equal(2, StaffCount());
        Assert.False(Directory.Exists(_harness.BackupFolder));
        var schema = await (await client.GetAsync("/tables/STAFF")).Json();
        Assert.False(schema.GetProperty("canWrite").GetBoolean());
    }

    [Fact]
    public async Task Tables_without_AllowWrites_are_read_only()
    {
        var response = await _harness.Client(ServerHarness.AdminKey).PatchAsJsonAsync("/tables/people/records/1", new { NAME = "Changed" });

        var problem = await response.Problem(403, _harness);
        Assert.Contains("read-only", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Keys_without_write_permission_get_403()
    {
        var response = await _harness.Client(ServerHarness.ReaderKey).DeleteAsync("/tables/STAFF/records/1");
        await response.Problem(403, _harness);
        Assert.False(Directory.Exists(_harness.BackupFolder));
    }

    [Fact]
    public async Task Hidden_fields_cant_be_written_and_dont_confirm_they_exist()
    {
        var client = _harness.Client(ServerHarness.LimitedKey);

        var hidden = await (await client.PatchAsJsonAsync("/tables/STAFF/records/1", new Dictionary<string, int> { ["SALARY"] = 1 })).Problem(400, _harness);
        var unknown = await (await client.PatchAsJsonAsync("/tables/STAFF/records/1", new Dictionary<string, int> { ["BONUS"] = 1 })).Problem(400, _harness);
        Assert.Equal(unknown.GetProperty("errors").GetProperty("BONUS")[0].GetString(), hidden.GetProperty("errors").GetProperty("SALARY")[0].GetString());

        var ok = await client.PatchAsJsonAsync("/tables/STAFF/records/1", new { NAME = "Lovelace" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(["_recno", "_deleted", "NAME", "HIRED"], (await ok.Json()).EnumerateObject().Select(p => p.Name));

        using var table = DbfTable.Open(Path.Combine(_harness.DataFolder, "STAFF.DBF"));
        Assert.Equal(1234.5m, table.Read(0)["SALARY"]);
    }

    [Fact]
    public async Task Indexed_tables_keep_their_index_up_to_date()
    {
        var client = _harness.Client(ServerHarness.AdminKey, s => s["Thunder:Tables:CHANGELOG:AllowWrites"] = "true");

        var response = await client.PatchAsJsonAsync("/tables/CHANGELOG/records/1", new { LEVEL = 42 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The production .mdx moved the record to its new key, so a seek on it finds the record.
        var found = await (await client.GetAsync("/tables/CHANGELOG/seek?tag=LEVEL_TAG&key=42")).Json();
        Assert.Contains(found.GetProperty("records").EnumerateArray(), r => r.GetProperty("_recno").GetInt32() == 1);
    }

    [Fact]
    public async Task Tables_whose_index_is_missing_refuse_writes_that_would_leave_it_stale()
    {
        File.Delete(Path.Combine(_harness.DataFolder, "CHANGELOG.MDX"));
        var client = _harness.Client(ServerHarness.AdminKey, s => s["Thunder:Tables:CHANGELOG:AllowWrites"] = "true");

        var response = await client.PatchAsJsonAsync("/tables/CHANGELOG/records/1", new { LEVEL = 2 });

        var problem = await response.Problem(409, _harness);
        Assert.Equal("IndexWouldGoStale", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("hint").GetString()));
        Assert.False(File.Exists(_harness.JournalPath) && File.ReadAllText(_harness.JournalPath).Length > 0);
        Assert.False(Directory.Exists(_harness.BackupFolder)); // refused before the backup, not after
    }

    [Fact]
    public async Task Indexed_tables_can_opt_in_to_unmaintained_index_writes()
    {
        var client = _harness.Client(ServerHarness.AdminKey, s =>
        {
            s["Thunder:Tables:CHANGELOG:AllowWrites"] = "true";
            s["Thunder:Tables:CHANGELOG:AllowUnmaintainedIndexes"] = "true";
        });

        var response = await client.PatchAsJsonAsync("/tables/CHANGELOG/records/1", new { LEVEL = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Json()).GetProperty("LEVEL").GetInt32());
        var backup = Assert.Single(Directory.GetDirectories(_harness.BackupFolder));
        Assert.Equal(3, Directory.GetFiles(backup).Length); // .DBF, .DBT and .MDX
    }

    [Fact]
    public async Task Concurrent_writes_to_one_table_take_turns()
    {
        var client = _harness.Client(ServerHarness.AdminKey);

        var responses = await Task.WhenAll(Enumerable.Range(0, 25).Select(i =>
            client.PostAsJsonAsync("/tables/STAFF/records", new { NAME = $"Temp{i}", SALARY = i })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var recnos = await Task.WhenAll(responses.Select(async r => (await r.Json()).GetProperty("_recno").GetInt32()));
        Assert.Equal(Enumerable.Range(3, 25), recnos.Order());
        Assert.Equal(27, StaffCount());
        Assert.Equal(50, (await File.ReadAllLinesAsync(_harness.JournalPath)).Length); // an intent and a done line each
    }

    [Fact]
    public async Task Writing_a_record_that_doesnt_exist_is_a_404()
    {
        var response = await _harness.Client(ServerHarness.AdminKey).PatchAsJsonAsync("/tables/STAFF/records/99", new { NAME = "Ghost" });
        await response.Problem(404, _harness);
    }
}
