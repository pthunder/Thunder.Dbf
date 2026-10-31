//      Table_STAFF:                         ProjectThunder :: Thunder.Dbf server tests
//        NAME:   string, maxLength 10       A schema per table, typed from the DBF header, served
//        SALARY: number|null, 0.01          only to callers who may read the table, with hidden
//        HIRED:  string|null, date          fields left out and other tables never named.

using System.Net;
using System.Text.Json;
using Microsoft.OpenApi;

namespace Thunder.Dbf.Server.Tests;

public sealed class TableOpenApiTests : IDisposable
{
    private readonly ServerHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static JsonElement Schemas(JsonElement document) => document.GetProperty("components").GetProperty("schemas");

    private static string[] Types(JsonElement property) => property.GetProperty("type").ValueKind == JsonValueKind.Array
        ? property.GetProperty("type").EnumerateArray().Select(t => t.GetString()!).Order().ToArray()
        : [property.GetProperty("type").GetString()!];

    [Fact]
    public async Task The_public_document_names_no_tables()
    {
        var text = await _harness.Client().GetStringAsync("/openapi/v1.json");

        Assert.DoesNotContain(TableOpenApi.SchemaPrefix, text);
        Assert.DoesNotContain("CHANGELOG", text);
        Assert.Contains("/openapi/tables.json", text);
    }

    [Fact]
    public async Task The_table_documents_need_a_caller()
    {
        await (await _harness.Client().GetAsync("/openapi/tables.json")).Problem(401, _harness);
        await (await _harness.Client().GetAsync("/tables/STAFF/openapi")).Problem(401, _harness);
    }

    [Fact]
    public async Task Every_readable_table_gets_a_typed_schema()
    {
        var response = await _harness.Client(ServerHarness.AdminKey).GetAsync("/openapi/tables.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Http.AssertNoLeaks(text, _harness);
        var document = JsonDocument.Parse(text).RootElement;

        var schemas = Schemas(document);
        foreach (var table in new[] { "STAFF", "people", "client", "CHANGELOG" })
            Assert.True(schemas.TryGetProperty("Table_" + table, out _), table);

        var staff = schemas.GetProperty("Table_STAFF");
        Assert.Equal("STAFF", staff.GetProperty("title").GetString());
        Assert.False(staff.GetProperty("additionalProperties").GetBoolean());
        var properties = staff.GetProperty("properties");
        Assert.Equal(["_recno", "_deleted", "NAME", "SALARY", "HIRED", "ACTIVE", "NOTES"], properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["_recno", "_deleted", "NAME", "SALARY", "HIRED", "ACTIVE", "NOTES"],
            staff.GetProperty("required").EnumerateArray().Select(r => r.GetString()));

        Assert.True(properties.GetProperty("_recno").GetProperty("readOnly").GetBoolean());
        Assert.Equal(["integer"], Types(properties.GetProperty("_recno")));
        Assert.Equal(["boolean"], Types(properties.GetProperty("_deleted")));

        var name = properties.GetProperty("NAME");
        Assert.Equal(["string"], Types(name)); // a blank character field reads as "", never null
        Assert.Equal(10, name.GetProperty("maxLength").GetInt32());

        var salary = properties.GetProperty("SALARY");
        Assert.Equal(["null", "number"], Types(salary));
        Assert.Equal(0.01m, salary.GetProperty("multipleOf").GetDecimal());

        var hired = properties.GetProperty("HIRED");
        Assert.Equal(["null", "string"], Types(hired));
        Assert.Equal("date", hired.GetProperty("format").GetString());

        Assert.Equal(["boolean", "null"], Types(properties.GetProperty("ACTIVE")));
        Assert.Equal(["null", "string"], Types(properties.GetProperty("NOTES")));
        Assert.False(properties.GetProperty("NOTES").TryGetProperty("format", out _));

        // The routes and security schemes are still there: it's the whole document, plus the tables.
        Assert.True(document.GetProperty("paths").TryGetProperty("/tables/{name}/records", out _));
        Assert.True(document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("ApiKey", out _));
    }

    [Fact]
    public async Task Serving_a_table_document_leaves_the_public_one_generic()
    {
        var client = _harness.Client(ServerHarness.AdminKey);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/tables.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/tables/STAFF/openapi")).StatusCode);

        Assert.DoesNotContain(TableOpenApi.SchemaPrefix, await client.GetStringAsync("/openapi/v1.json"));
    }

    [Fact]
    public async Task The_document_of_all_tables_needs_permission_to_list_them()
    {
        var problem = await (await _harness.Client(ServerHarness.ReaderKey).GetAsync("/openapi/tables.json")).Problem(403, _harness);
        Assert.DoesNotContain("people", problem.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_table_document_names_only_that_table()
    {
        var document = await (await _harness.Client(ServerHarness.ReaderKey).GetAsync("/tables/staff/openapi")).Json();

        var tables = Schemas(document).EnumerateObject().Select(p => p.Name).Where(n => n.StartsWith(TableOpenApi.SchemaPrefix)).ToList();
        Assert.Equal(["Table_STAFF"], tables);
        Assert.DoesNotContain("people", document.ToString());
    }

    [Fact]
    public async Task Hidden_fields_are_left_out()
    {
        var document = await (await _harness.Client(ServerHarness.LimitedKey).GetAsync("/tables/STAFF/openapi")).Json();

        var staff = Schemas(document).GetProperty("Table_STAFF");
        Assert.Equal(["_recno", "_deleted", "NAME", "HIRED"], staff.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("SALARY", document.ToString());
        Assert.DoesNotContain("NOTES", document.ToString());
    }

    [Fact]
    public async Task A_table_the_caller_cant_read_looks_exactly_like_one_that_doesnt_exist()
    {
        var client = _harness.Client(ServerHarness.LimitedKey);

        var hidden = await (await client.GetAsync("/tables/client/openapi")).Problem(404, _harness);
        var missing = await (await client.GetAsync("/tables/nosuchtable/openapi")).Problem(404, _harness);

        Assert.Equal(missing.GetProperty("detail").GetString(), hidden.GetProperty("detail").GetString());
        Assert.Equal(missing.GetProperty("hint").GetString(), hidden.GetProperty("hint").GetString());
    }

    [Theory]
    [InlineData('I', 4, 0, 0, "integer", "int32")]
    [InlineData('N', 18, 0, 0, "integer", "int64")]
    [InlineData('N', 20, 0, 0, "number", null)]
    [InlineData('F', 20, 4, 0, "number", null)]
    [InlineData('Y', 8, 4, 0, "number", null)]
    [InlineData('B', 8, 2, 0, "number", "double")]
    [InlineData('B', 10, 0, 0, "string", "byte")]   // dBASE binary memo
    [InlineData('M', 4, 0, 0x04, "string", "byte")] // Visual FoxPro binary memo
    [InlineData('G', 4, 0, 0, "string", "byte")]
    [InlineData('T', 8, 0, 0, "string", "date-time")]
    public void Each_DBF_type_gets_its_JSON_type(char type, int length, int decimals, byte flags, string json, string? format)
    {
        var schema = TableOpenApi.FieldSchema(new DbfField("F", type, length, decimals, 1) { Flags = flags });

        Assert.True(schema.Type!.Value.HasFlag(Enum.Parse<JsonSchemaType>(json, ignoreCase: true)), $"{type}: {schema.Type}");
        Assert.Equal(format, schema.Format);
    }

    [Fact]
    public void Numbers_say_how_many_decimals_they_keep()
    {
        Assert.Equal(0.001m, TableOpenApi.FieldSchema(new DbfField("N", 'N', 10, 3, 1)).MultipleOf);
        Assert.Equal(0.0001m, TableOpenApi.FieldSchema(new DbfField("Y", 'Y', 8, 4, 1)).MultipleOf);
        Assert.Null(TableOpenApi.FieldSchema(new DbfField("N", 'N', 5, 0, 1)).MultipleOf);
    }

    [Fact]
    public void Visual_FoxPro_flags_become_nullable_and_read_only()
    {
        var integer = TableOpenApi.FieldSchema(new DbfField("I", 'I', 4, 0, 1));
        Assert.Equal(JsonSchemaType.Integer, integer.Type); // VFP integers have no blank

        var nullable = TableOpenApi.FieldSchema(new DbfField("I", 'I', 4, 0, 1) { Flags = 0x02 });
        Assert.Equal(JsonSchemaType.Integer | JsonSchemaType.Null, nullable.Type);

        var text = TableOpenApi.FieldSchema(new DbfField("C", 'C', 20, 0, 1) { Flags = 0x02 });
        Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, text.Type);

        var counter = TableOpenApi.FieldSchema(new DbfField("ID", 'I', 4, 0, 1) { Flags = 0x08 });
        Assert.True(counter.ReadOnly);
        Assert.False(integer.ReadOnly);
    }

    [Theory]
    [InlineData("STAFF", "Table_STAFF")]
    [InlineData("my table", "Table_my_table")]
    [InlineData("café", "Table_caf_")]
    public void Component_names_are_always_valid(string table, string expected) =>
        Assert.Equal(expected, TableOpenApi.ComponentName(table, []));

    [Fact]
    public void Component_names_never_clash()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Table_a_b", TableOpenApi.ComponentName("a b", used));
        Assert.Equal("Table_a_b_2", TableOpenApi.ComponentName("a_b", used));
        Assert.Equal("Table_A_B_3", TableOpenApi.ComponentName("A_B", used)); // names differing only in case clash too
    }
}
