# Web service

```
   browser / app  --HTTPS-->  [ thunder-dbf-server ]  -->  .dbf .dbt .mdx      ProjectThunder :: Thunder.Dbf
                              keys, permissions,                               A safe JSON API in front of
                              journal, backups                                 the legacy folder.
```

`Thunder.Dbf.Server` is a ready-to-run ASP.NET Core service. It's the safe way to put a web app on top of a legacy system:

- the service is the only process (besides the legacy app) that writes the files
- every request is authenticated, authorized per table and field, validated, journaled and rate-limited

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
| `GET /tables/{name}/records?skip&take` | records, paged |
| `GET /tables/{name}/records/{recno}` | one record |
| `GET /tables/{name}/seek?tag&key` | lookup through the `.mdx` index |
| `POST`, `PATCH`, `DELETE` on records | writes, when enabled for the table and the key |
| `GET /openapi/v1.json` | the OpenAPI description |

Writes from the service take the same locks as the legacy app and keep the production index exact, so both can work on the same files. Run it on Windows (ideally on the file server) or Linux, where byte-range locks are enforced (see [Locking](locking.md)).
