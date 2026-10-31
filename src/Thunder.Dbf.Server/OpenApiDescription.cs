//      openapi: 3.1            ProjectThunder :: Thunder.Dbf
//      paths:                  The document at /openapi/v1.json: every route, its
//        /tables/{name}: ...   ProblemDetails answers, and the X-Api-Key header.
//      components:
//        securitySchemes:      Records are described generically (an object keyed by
//          ApiKey: header      field name); GET /tables/{name} gives the real fields.

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Thunder.Dbf.Server;

/// <summary>Adds the title, description and API key scheme to the generated OpenAPI document.</summary>
internal sealed class OpenApiDescription : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Thunder.Dbf",
            Version = "v1",
            Description = "A safe JSON API over a legacy dBASE/FoxPro data folder. Records are JSON objects keyed by field name, " +
                "plus _recno (1-based record number) and _deleted. Dates are yyyy-MM-dd, numbers are JSON numbers, memos are strings " +
                "and binary memos are base64. Errors are RFC 7807 ProblemDetails with a code and, usually, a hint.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeyAuthentication.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthentication.HeaderName,
            Description = "An API key issued by the server's administrator. The server stores only its SHA-256 hash.",
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(ApiKeyAuthentication.SchemeName, document)] = [],
        });
        return Task.CompletedTask;
    }
}
