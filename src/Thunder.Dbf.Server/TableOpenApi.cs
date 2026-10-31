//      STAFF.DBF header                     components:
//      +--------+---+-----+-----+             schemas:
//      | NAME   | C |  10 |   0 |  ---->        Table_STAFF:
//      | SALARY | N |   8 |   2 |                NAME:   { type: string, maxLength: 10 }
//      | HIRED  | D |   8 |   0 |                SALARY: { type: [number, null], multipleOf: 0.01 }
//      +--------+---+-----+-----+                HIRED:  { type: [string, null], format: date }
//
//      ProjectThunder :: Thunder.Dbf
//      An OpenAPI schema per table, read from the DBF header at the moment it's asked for. Each
//      caller sees only the tables it may read and the fields it may see, so these documents are
//      served to authenticated callers only; /openapi/v1.json stays generic for everyone else.

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Thunder.Dbf.Server;

/// <summary>Serves OpenAPI documents with a component schema (<c>Table_&lt;NAME&gt;</c>) per table the caller may read.</summary>
public static partial class TableOpenApi
{
    public const string SchemaPrefix = "Table_";

    // The document AddOpenApi() registers by default, and the one /openapi/v1.json serves.
    private const string DocumentName = "v1";

    public static void MapTableOpenApi(this WebApplication app)
    {
        app.MapGet("/openapi/tables.json", AllTables)
            .RequireAuthorization()
            .WithTags("Tables")
            .WithSummary("The OpenAPI document plus a schema per table this caller may read. Needs CanListTables.")
            .Produces<string>(contentType: "application/json")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapGet("/tables/{name}/openapi", OneTable)
            .RequireAuthorization()
            .WithTags("Tables")
            .WithSummary("The OpenAPI document plus the schema of one table's records, as this caller sees them.")
            .Produces<string>(contentType: "application/json")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    // A document naming every readable table is a table list, so it needs the same permission as GET /tables.
    private static async Task<IResult> AllTables(HttpContext http, AccessRegistry registry, TableStore store, ILogger<TableStore> logger)
    {
        var key = registry.For(http.User);
        if (!key.CanListTables)
            throw ApiProblem.Forbidden("This key isn't allowed to list tables, so it can't have a document that names them all.",
                "Use GET /tables/{name}/openapi for each table you were given.");

        var tables = store.TableNames.Where(key.CanRead).ToList();
        if (tables.Count > MaxTablesPerDocument)
            throw ApiProblem.BadRequest($"This key can read {tables.Count} tables, more than one document describes ({MaxTablesPerDocument}).",
                "Use GET /tables/{name}/openapi for the tables you need.");

        // Building this document opens every table, so only a couple are built at a time, server-wide.
        if (!await AllTablesGate.WaitAsync(TimeSpan.FromSeconds(10), http.RequestAborted))
            throw new TableBusyProblem();
        try
        {
            return await AllTablesDocument(http, key, store, logger, tables);
        }
        finally
        {
            AllTablesGate.Release();
        }
    }

    private const int MaxTablesPerDocument = 500;
    private static readonly SemaphoreSlim AllTablesGate = new(2, 2);

    private static async Task<IResult> AllTablesDocument(HttpContext http, KeyAccess key, TableStore store, ILogger logger, List<string> tables)
    {
        var schemas = new List<(string Table, OpenApiSchema Schema)>();
        foreach (var table in tables)
        {
            try
            {
                schemas.Add((table, await store.ReadAsync(table, t => RecordSchema(t, table, key), http.RequestAborted)));
            }
            catch (Exception ex) when (ex is DbfException or IOException or InvalidDataException or FormatException)
            {
                // One damaged or locked table shouldn't hide the schemas of all the others.
                logger.LogWarning(ex, "Couldn't read the header of {Table}; it's left out of /openapi/tables.json.", table);
            }
        }
        return await DocumentAsync(http, schemas);
    }

    private static async Task<IResult> OneTable(string name, HttpContext http, AccessRegistry registry, TableStore store)
    {
        var (key, table) = TableEndpoints.Resolve(http, name, registry, store);
        var schema = await store.ReadAsync(table, t => RecordSchema(t, table, key), http.RequestAborted);
        return await DocumentAsync(http, [(table, schema)]);
    }

    // Starts from a freshly generated copy of the generic document, so routes, problems and security
    // schemes stay described in one place, then adds the table schemas to that copy only.
    private static async Task<IResult> DocumentAsync(HttpContext http, IReadOnlyList<(string Table, OpenApiSchema Schema)> schemas)
    {
        var provider = http.RequestServices.GetRequiredKeyedService<IOpenApiDocumentProvider>(DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(http.RequestAborted);

        document.Servers = [new OpenApiServer { Url = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}" }];
        document.Info.Description += " This copy adds a schema per table (" + SchemaPrefix + "<NAME>) for the tables and fields this caller may see.";
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();

        var used = new HashSet<string>(document.Components.Schemas.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (table, schema) in schemas)
            document.Components.Schemas[ComponentName(table, used)] = schema;

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        document.SerializeAs(OpenApiSpecVersion.OpenApi3_1, new OpenApiJsonWriter(writer));

        // The document depends on who's asking, and X-Api-Key isn't a header shared caches know to
        // respect, so say outright that no cache may keep it.
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.Vary = "Authorization, X-Api-Key";
        return Results.Text(writer.ToString(), "application/json; charset=utf-8");
    }

    /// <summary>
    /// The schema of one table's records as <see cref="RecordJson.ToJson"/> writes them for this caller:
    /// <c>_recno</c>, <c>_deleted</c>, then every field the caller may see. Hidden fields aren't mentioned.
    /// </summary>
    internal static OpenApiSchema RecordSchema(DbfTable table, string name, KeyAccess key)
    {
        var properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        {
            [RecordJson.RecnoProperty] = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Format = "int32",
                ReadOnly = true,
                Description = "The 1-based record number, dBASE's RECNO().",
            },
            [RecordJson.DeletedProperty] = new OpenApiSchema
            {
                Type = JsonSchemaType.Boolean,
                ReadOnly = true,
                Description = "Marked deleted. The record stays in the file until the legacy app packs the table.",
            },
        };
        foreach (var (_, field) in RecordJson.VisibleFields(table, f => key.CanSeeField(name, f)))
            properties[field.Name] = FieldSchema(field);

        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Title = name,
            Description = $"A record of {name}, as GET /tables/{name}/records returns it to this caller.",
            Properties = properties,
            Required = new HashSet<string>(properties.Keys, StringComparer.Ordinal), // every record carries every visible field
            AdditionalPropertiesAllowed = false,
        };
    }

    /// <summary>One field's JSON shape, matching what <see cref="RecordJson"/> reads and accepts.</summary>
    internal static OpenApiSchema FieldSchema(DbfField field)
    {
        var shape = $"{RecordJson.TypeName(field)}, {field.Type}({field.Length}{(field.DecimalCount > 0 ? "," + field.DecimalCount : "")})";
        var schema = new OpenApiSchema { Description = shape };
        switch (field.Type)
        {
            case 'C':
                schema.Type = JsonSchemaType.String;
                schema.MaxLength = field.Length; // bytes in the table's code page, so never fewer characters than this
                schema.Description = shape + ". Trailing spaces are trimmed; the width counts bytes in the table's code page.";
                break;
            case 'N' or 'F' when field.DecimalCount == 0 && field.Length <= 18:
                schema.Type = JsonSchemaType.Integer; // 18 digits always fit a 64-bit integer
                schema.Format = "int64";
                break;
            case 'N' or 'F':
                schema.Type = JsonSchemaType.Number;
                if (field.DecimalCount > 0)
                    schema.MultipleOf = 1m / Pow10(field.DecimalCount);
                break;
            case 'I':
                schema.Type = JsonSchemaType.Integer;
                schema.Format = "int32";
                break;
            case 'Y':
                schema.Type = JsonSchemaType.Number;
                schema.MultipleOf = 0.0001m; // Visual FoxPro currency keeps four decimal places
                break;
            case 'B' when field.Length == 8:
                schema.Type = JsonSchemaType.Number;
                schema.Format = "double";
                break;
            case 'D':
                schema.Type = JsonSchemaType.String;
                schema.Format = "date";
                break;
            case 'T':
                schema.Type = JsonSchemaType.String;
                schema.Format = "date-time";
                schema.Description = shape + ". Written as yyyy-MM-ddTHH:mm:ss.fff with no time zone, because DBF doesn't store one.";
                break;
            case 'L':
                schema.Type = JsonSchemaType.Boolean;
                break;
            case 'M' when !field.IsBinary:
                schema.Type = JsonSchemaType.String;
                break;
            case 'M' or 'G' or 'P' or 'B':
                schema.Type = JsonSchemaType.String;
                schema.Format = "byte"; // base64
                break;
            default:
                schema.Description = shape + ". The API can't convert this type yet; its value is passed through as read.";
                break;
        }

        // A blank number, date, logical or memo reads as null, and Visual FoxPro nullable fields can
        // hold .NULL. of any type. Character fields read blank as "", so they're null only when nullable.
        if (schema.Type is { } type && (field.IsNullable || CanBeBlank(field)))
            schema.Type = type | JsonSchemaType.Null;
        if (field.IsAutoIncrement)
            schema.ReadOnly = true;
        return schema;
    }

    private static bool CanBeBlank(DbfField field) => field.Type switch
    {
        'N' or 'F' or 'D' or 'T' or 'L' or 'M' or 'G' or 'P' => true,
        'B' => field.Length != 8,
        _ => false,
    };

    private static decimal Pow10(int power)
    {
        var result = 1m;
        for (var i = 0; i < power; i++)
            result *= 10;
        return result;
    }

    // Component names may only hold letters, digits, '.', '-' and '_'. A table name with anything
    // else is tidied, and the rare clash after tidying gets a number; the schema's title keeps the real name.
    internal static string ComponentName(string table, HashSet<string> used)
    {
        var name = SchemaPrefix + NotAllowedInComponentName().Replace(table, "_");
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
            candidate = $"{name}_{n}";
        return candidate;
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex NotAllowedInComponentName();
}
