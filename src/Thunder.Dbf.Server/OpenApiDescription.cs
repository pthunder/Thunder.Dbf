//      openapi: 3.1            ProjectThunder :: Thunder.Dbf
//      paths:                  The document at /openapi/v1.json: every route, its
//        /tables/{name}: ...   ProblemDetails answers, the X-Api-Key header and, when
//      components:             OpenID Connect is on, bearer tokens.
//        securitySchemes:
//          ApiKey: header      Records are described generically here, because anyone may read
//          Bearer: http        this document; TableOpenApi adds a schema per table for callers.

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

        // Two requirements means either one will do. The authority isn't named: this document is
        // served to anyone, and a tenant id is nobody else's business.
        var thunder = context.ApplicationServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ThunderOptions>>().Value;
        if (thunder.OpenIdConnect.Enabled)
        {
            document.Components.SecuritySchemes[OidcAuthentication.SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "An access token from the server's OpenID Connect provider, issued for this API. Don't send an API key as well.",
            };
            document.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(OidcAuthentication.SchemeName, document)] = [],
            });
        }
        return Task.CompletedTask;
    }
}
