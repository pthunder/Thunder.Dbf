//      +----+-----------+------------+       ProjectThunder :: Thunder.Dbf server tests
//      | #  | NAME      | HIRED      |       Reading: schemas, pages, single records,
//      +----+-----------+------------+       index seeks and field allow-lists, as JSON.
//      | 1  | Ada       | 1997-04-12 |
//      | 2  | Grace     | 2001-09-01 |
//      +----+-----------+------------+

using System.Net;
using System.Text.Json;

namespace Thunder.Dbf.Server.Tests;

public sealed class ReadTests : IDisposable
{
    private readonly ServerHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Schema_lists_fields_types_widths_and_index_tags()
    {
        var client = _harness.Client(ServerHarness.AdminKey);

        var staff = await (await client.GetAsync("/tables/staff")).Json();
        Assert.Equal("STAFF", staff.GetProperty("name").GetString());
        Assert.Equal(2, staff.GetProperty("recordCount").GetInt32());
        Assert.True(staff.GetProperty("canWrite").GetBoolean());
        var salary = staff.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "SALARY");
        Assert.Equal("N", salary.GetProperty("type").GetString());
        Assert.Equal(8, salary.GetProperty("length").GetInt32());
        Assert.Equal(2, salary.GetProperty("decimals").GetInt32());
        Assert.Equal("number", salary.GetProperty("jsonType").GetString());

        var changelog = await (await client.GetAsync("/tables/CHANGELOG.dbf")).Json();
        var tags = changelog.GetProperty("indexTags").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("ID_TAG", tags);
        Assert.Contains("USER_TAG", tags);
        Assert.False(changelog.GetProperty("canWrite").GetBoolean());
    }

    [Fact]
    public async Task Records_are_json_objects_with_recno_deleted_and_typed_values()
    {
        var record = await (await _harness.Client(ServerHarness.AdminKey).GetAsync("/tables/STAFF/records/1")).Json();

        Assert.Equal(1, record.GetProperty("_recno").GetInt32());
        Assert.False(record.GetProperty("_deleted").GetBoolean());
        Assert.Equal("Ada", record.GetProperty("NAME").GetString());
        Assert.Equal(JsonValueKind.Number, record.GetProperty("SALARY").ValueKind);
        Assert.Equal(1234.5m, record.GetProperty("SALARY").GetDecimal());
        Assert.Equal("1997-04-12", record.GetProperty("HIRED").GetString());
        Assert.True(record.GetProperty("ACTIVE").GetBoolean());
        Assert.Equal("First hire.", record.GetProperty("NOTES").GetString());

        var grace = await (await _harness.Client(ServerHarness.AdminKey).GetAsync("/tables/STAFF/records/2")).Json();
        Assert.Equal(JsonValueKind.Null, grace.GetProperty("NOTES").ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task Record_numbers_outside_the_table_are_404s(int recno)
    {
        var response = await _harness.Client(ServerHarness.AdminKey).GetAsync($"/tables/STAFF/records/{recno}");
        await response.Problem(404, _harness);
    }

    [Fact]
    public async Task Pages_follow_skip_and_take_and_link_to_the_next()
    {
        var client = _harness.Client(ServerHarness.AdminKey);

        var first = await (await client.GetAsync("/tables/client/records")).Json();
        Assert.Equal(2, first.GetProperty("take").GetInt32()); // DefaultTake
        Assert.Equal(8, first.GetProperty("total").GetInt32());
        Assert.Equal([1, 2], first.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("_recno").GetInt32()));
        Assert.Equal("/tables/client/records?skip=2&take=2", first.GetProperty("next").GetString());

        var last = await (await client.GetAsync("/tables/client/records?skip=6&take=3")).Json();
        Assert.Equal([7, 8], last.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("_recno").GetInt32()));
        Assert.Equal(JsonValueKind.Null, last.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task A_page_too_big_for_the_response_budget_ends_early_with_a_next_link()
    {
        var client = _harness.Client(ServerHarness.AdminKey, s => s["Thunder:Paging:MaxResponseBytes"] = "1");

        var page = await (await client.GetAsync("/tables/client/records?take=3")).Json();

        // A page always holds at least one record, however small the budget.
        Assert.Equal([1], page.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("_recno").GetInt32()));
        Assert.Equal("/tables/client/records?skip=1&take=3", page.GetProperty("next").GetString());
    }

    [Fact]
    public async Task Take_is_capped()
    {
        var page = await (await _harness.Client(ServerHarness.AdminKey).GetAsync("/tables/client/records?take=100000")).Json();

        Assert.Equal(3, page.GetProperty("take").GetInt32()); // MaxTake
        Assert.Equal(3, page.GetProperty("records").GetArrayLength());
    }

    [Theory]
    [InlineData("skip=-1")]
    [InlineData("take=0")]
    [InlineData("take=lots")]
    public async Task Bad_paging_is_a_400(string query)
    {
        var response = await _harness.Client(ServerHarness.AdminKey).GetAsync($"/tables/client/records?{query}");
        await response.Problem(400, _harness);
    }

    [Fact]
    public async Task Field_allow_lists_hide_fields_from_records_and_schemas()
    {
        var client = _harness.Client(ServerHarness.LimitedKey);

        var record = await (await client.GetAsync("/tables/STAFF/records/1")).Json();
        Assert.Equal(["_recno", "_deleted", "NAME", "HIRED"], record.EnumerateObject().Select(p => p.Name));

        var schema = await (await client.GetAsync("/tables/STAFF")).Json();
        Assert.Equal(["NAME", "HIRED"], schema.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));

        // Index tags over hidden fields are hidden too: their expressions and seeks would reveal the field.
        var changelog = await (await client.GetAsync("/tables/CHANGELOG")).Json();
        Assert.Equal(["ID_TAG", "USER_TAG"], changelog.GetProperty("indexTags").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order());
        var seek = await client.GetAsync("/tables/CHANGELOG/seek?tag=ACTION_TAG&key=1");
        await seek.Problem(404, _harness);
    }

    [Fact]
    public async Task Seek_finds_records_through_the_mdx_index()
    {
        var client = _harness.Client(ServerHarness.AdminKey);
        var second = await (await client.GetAsync("/tables/CHANGELOG/records/2")).Json();
        var id = second.GetProperty("ID").GetDecimal();
        var user = second.GetProperty("USER").GetString()!;

        var byId = await (await client.GetAsync($"/tables/CHANGELOG/seek?tag=ID_TAG&key={id}")).Json();
        var found = byId.GetProperty("records").EnumerateArray().Single();
        Assert.Equal(2, found.GetProperty("_recno").GetInt32());

        var byUser = await (await client.GetAsync($"/tables/CHANGELOG/seek?tag=user_tag&key={Uri.EscapeDataString(user[..2])}")).Json();
        Assert.Contains(byUser.GetProperty("records").EnumerateArray(), r => r.GetProperty("_recno").GetInt32() == 2);

        var none = await (await client.GetAsync("/tables/CHANGELOG/seek?tag=ID_TAG&key=-987654")).Json();
        Assert.Equal(0, none.GetProperty("records").GetArrayLength());
    }

    [Theory]
    [InlineData("tag=NOPE&key=1", 404)]
    [InlineData("tag=ID_TAG&key=abc", 400)]
    [InlineData("tag=ID_TAG", 400)]
    public async Task Bad_seeks_explain_themselves(string query, int status)
    {
        var response = await _harness.Client(ServerHarness.AdminKey).GetAsync($"/tables/CHANGELOG/seek?{query}");
        await response.Problem(status, _harness);
    }

    [Fact]
    public async Task Seeking_a_table_without_an_index_is_a_404()
    {
        var response = await _harness.Client(ServerHarness.AdminKey).GetAsync("/tables/people/seek?tag=X&key=1");
        await response.Problem(404, _harness);
    }

    [Fact]
    public async Task Unmatched_routes_are_problems_too()
    {
        var response = await _harness.Client(ServerHarness.AdminKey).GetAsync("/nothing/here");
        var problem = await response.Problem(404, _harness);
        Assert.Equal("NotFound", problem.GetProperty("code").GetString());
    }
}
