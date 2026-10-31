# Web service

```
   browser / app  --HTTPS-->  [ thunder-dbf-server ]  -->  .dbf .dbt .mdx      ProjectThunder :: Thunder.Dbf
                              keys, permissions,                               A safe JSON API in front of
                              journal, backups                                 the legacy folder.
```

`Thunder.Dbf.Server` is a ready-to-run ASP.NET Core service. It's the safe way to put a web app on top of a legacy system:

- the service is the only process (besides the legacy app) that writes the files
- every request is authenticated (an API key, or a token from your OpenID Connect provider), authorized per table and field, validated, journaled and rate-limited

The full guide, covering configuration, endpoints, the security model and deployment as a Windows service, is in [src/Thunder.Dbf.Server/README.md](../src/Thunder.Dbf.Server/README.md).

## At a glance

```bash
dotnet run --project src/Thunder.Dbf.Server -- hash-key        # make an API key and its hash
dotnet run --project src/Thunder.Dbf.Server                    # serve the folder in appsettings.json
```

```bash
curl -H "X-Api-Key: $KEY" https://localhost:5001/tables/CONTACT1/records?take=10
```

| Endpoint | Does |
|---|---|
| `GET /tables` | tables the key may see |
| `GET /tables/{name}` | fields, types and index tags |
| `GET /tables/{name}/openapi` | the OpenAPI document with a typed schema for this table |
| `GET /tables/{name}/records?skip&take` | records, paged |
| `GET /tables/{name}/records/{recno}` | one record |
| `GET /tables/{name}/seek?tag&key` | lookup through the `.mdx` index |
| `POST`, `PATCH`, `DELETE` on records | writes, when enabled for the table and the key |
| `GET /openapi/v1.json` | the OpenAPI description, generic and public |
| `GET /openapi/tables.json` | the same, with a `Table_<NAME>` schema for every table the caller may read |

Writes from the service take the same locks as the legacy app and keep the production index exact, so both can work on the same files. Run it on Windows (ideally on the file server) or Linux, where byte-range locks are enforced (see [Locking](locking.md)).

## Signing in

Callers use one of two credentials, never both (a request with both is refused with 400):

- **An API key** in `X-Api-Key`. The server stores only its SHA-256 hash; `thunder-dbf-server hash-key` makes one.
- **A bearer token** from an OpenID Connect provider, in `Authorization: Bearer`, once `Thunder:OpenIdConnect:Authority` is set.

A token's roles, groups or claims pick a named **profile**, which has the same shape as an API key's permissions. Rules are checked in order and the first match wins; a valid token that matches none gets 403.

| Setting | Default | Meaning |
|---|---|---|
| `OpenIdConnect:Authority` | (empty: off) | The provider's issuer URL. |
| `OpenIdConnect:Audience` | (required) | The audience tokens must carry: this API's client id or identifier. |
| `OpenIdConnect:RequireHttpsMetadata` | `true` | Keys are fetched over HTTPS only. |
| `OpenIdConnect:SubjectClaim` | `sub` | Names the user as `oidc:<value>` in logs, the journal and rate limits. |
| `OpenIdConnect:Profiles:{name}` | (none) | `CanListTables`, `Read`, `Write`, `Fields`, as on an API key. |
| `OpenIdConnect:Map:n` | (none) | `Role`, `Group`, or `Claim` + `Value`, and the `Profile` it grants. |

```jsonc
"OpenIdConnect": {
  "Authority": "https://login.microsoftonline.com/<tenant id>/v2.0",   // Entra ID
  // "Authority": "https://<tenant>.auth0.com/",                        // Auth0
  // "Authority": "https://sso.example.com/realms/<realm>",            // Keycloak
  "Audience": "<the API's application id or identifier>",
  "Profiles": {
    "clerks":    { "Read": [ "CONTACT1" ], "Write": [ "CONTACT1" ] },
    "reporting": { "Read": [ "*" ], "CanListTables": true }
  },
  "Map": [
    { "Role": "Thunder.Clerk", "Profile": "clerks" },
    { "Group": "<group id>", "Profile": "reporting" },
    { "Claim": "https://example.com/roles", "Value": "reporting", "Profile": "reporting" }
  ]
}
```

```bash
curl -H "Authorization: Bearer $TOKEN" https://localhost:5001/tables/CONTACT1/records?take=10
```

Provider notes (Entra app roles and groups, Auth0 namespaced claims, Keycloak mappers) are in the [server guide](../src/Thunder.Dbf.Server/README.md#signing-in-with-openid-connect).
