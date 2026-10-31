# Thunder.Dbf.Server

```
         ____
        /   /       T H U N D E R . D B F   S E R V E R
       /   /__      a safe JSON API in front of a legacy dBASE / FoxPro folder
      /__    /
        /  /        the legacy app keeps running on the same files;
       / /          this service is the only other thing that writes them
      //
      '
```

A small ASP.NET Core service that lets a web app read, and carefully write, the tables of a legacy dBASE or FoxPro system. It's built on the Thunder.Dbf library. It answers JSON, explains every refusal as an RFC 7807 problem, and never writes anything unless the configuration explicitly allows it.

## Quick start

```bash
# 1. Make an API key. The key goes to the client; only its hash goes in config.
dotnet run --project src/Thunder.Dbf.Server -- hash-key
#   key:    <a random 43-character key>
#   sha256: <64 hex digits>

# 2. Point it at the data folder and add the key's hash (appsettings.json, environment variables, or the command line).
dotnet run --project src/Thunder.Dbf.Server -- \
  --Thunder:Data:Folder=/srv/legacy/data \
  --Thunder:ApiKeys:0:Id=website \
  --Thunder:ApiKeys:0:Sha256=<the sha256 line> \
  --Thunder:ApiKeys:0:Read:0=* \
  --urls http://localhost:5080

# 3. Ask it something.
curl -H "X-Api-Key: <the key line>" "http://localhost:5080/tables/CONTACT1/records?take=10"
```

To hash a key you already have without leaving it in your shell history, pipe it in: `thunder-dbf-server hash-key -`. Standard tools give the same hash: `printf %s "$KEY" | shasum -a 256` on macOS/Linux, or in PowerShell 7 `[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key))).ToLower()`.

## Endpoints

| Method | Route | What it does |
|---|---|---|
| GET | `/health` | `{"status":"ok"}`, or 503 if the data folder can't be seen. No key needed. |
| GET | `/openapi/v1.json` | The OpenAPI document. No key needed; it lists routes, not tables. |
| GET | `/openapi/tables.json` | The same document plus a typed schema for every table this caller may read. Needs `CanListTables`. |
| GET | `/tables` | The tables this key may read, and whether it may write each one. Needs `CanListTables`. |
| GET | `/tables/{name}` | Fields (name, type, width, decimals), record count, code page and index tags. |
| GET | `/tables/{name}/openapi` | The OpenAPI document plus the typed schema of this one table. |
| GET | `/tables/{name}/records?skip=&take=&includeDeleted=` | A page of records. `take` is capped at `Paging:MaxTake`. |
| GET | `/tables/{name}/records/{recno}` | One record by its 1-based record number. |
| GET | `/tables/{name}/seek?tag=&key=&take=` | Records found through the production `.mdx` index, like dBASE's `SEEK`. |
| POST | `/tables/{name}/records` | Adds a record. Returns 201 with the record and its `Location`. |
| PATCH | `/tables/{name}/records/{recno}` | Changes only the fields in the body. |
| DELETE | `/tables/{name}/records/{recno}` | Marks the record deleted, like dBASE's `DELETE`. It stays in the file until the legacy app packs the table. |

The write routes only exist when `Data:ReadOnly` is `false`. A read-only server answers them with 405.

`{name}` is the table's file name, with or without `.dbf`, in any case. Paging counts record numbers: `skip=100&take=50` covers records 101 to 150, and drops deleted ones unless `includeDeleted=true`, so a page can be short. Follow `next` for the following page.

### Records

```json
{
  "_recno": 7,
  "_deleted": false,
  "NAME": "Smith",
  "BALANCE": 1234.50,
  "HIRED": "1997-04-12",
  "ACTIVE": true,
  "NOTES": "Prefers phone calls.",
  "PHOTO": "iVBORw0KGgo..."
}
```

| DBF type | JSON |
|---|---|
| C character | string (trailing spaces trimmed) |
| N numeric, F float, Y currency, I integer, B double | number |
| D date | `"yyyy-MM-dd"` |
| T datetime | `"yyyy-MM-ddTHH:mm:ss.fff"`, no time zone (DBF doesn't store one) |
| L logical | `true`, `false` or `null` |
| M memo | string |
| binary, picture and OLE memos | base64 string |
| blank values | `null` |

When writing, every value is checked against the table before a byte changes: the JSON type, the width in bytes in the table's code page (so `"Müller"` is measured the way dBASE measures it), characters the code page can't store, decimal places, and numeric width. Failures come back as one 400 listing every bad field:

```json
{
  "type": "https://httpstatuses.io/400",
  "title": "Some fields aren't valid.",
  "status": 400,
  "detail": "See errors for what each field needs.",
  "code": "ValidationFailed",
  "errors": {
    "NAME": ["The field holds 10 bytes; this text needs 21."],
    "SALARY": ["The field keeps 2 decimal places; this number has more."]
  },
  "traceId": "00-4bf92f..."
}
```

A PATCH starts from the stored record and changes only the fields you send. Every other field, memo included, keeps its exact bytes, so formatting the legacy app wrote survives.

### A schema per table

`/openapi/v1.json` is served to anyone, so it describes records generically and never names a table. A caller who signs in can ask for a copy of the document that adds one component schema per table, `Table_<NAME>`, read from the DBF header at that moment, so code generators get real types:

- `GET /tables/{name}/openapi` for one table the caller may read. A table it can't read answers 404, the same as one that doesn't exist.
- `GET /openapi/tables.json` for every table the caller may read. It names them all, so like `GET /tables` it needs `CanListTables`.

Each schema lists `_recno`, `_deleted` and every field the caller may see. Fields hidden by an allow-list aren't mentioned. A table name with characters a component name can't hold becomes `_`, and the schema's `title` keeps the real name.

| DBF field | Schema |
|---|---|
| C character | `string`, `maxLength` = the width (bytes in the table's code page) |
| N, F with no decimals, up to 18 digits; I integer | `integer` (`int64`; `int32` for I) |
| N, F with decimals; Y currency | `number` with `multipleOf` (0.01 for two decimals; 0.0001 for currency) |
| B double | `number`, `double` |
| D date / T datetime | `string`, `date` / `date-time` (no time zone: DBF doesn't store one) |
| L logical | `boolean` |
| M memo | `string` |
| binary, picture and OLE memos | `string`, `byte` (base64) |

A type is nullable (`["number", "null"]`) when a blank value reads as `null`, which covers numbers, dates, logicals and memos, and for every Visual FoxPro nullable field. Visual FoxPro autoincrement fields are `readOnly`.

### Errors

Every error is a ProblemDetails object with `status`, `title`, `detail`, a machine-readable `code` and, when there's something to do about it, a `hint`. Library errors keep their codes (`FileInUse`, `IndexWouldGoStale`, `EncryptedTable`, ...) and status codes. Responses never include file paths, user names or stack traces; the server log has the full story under the same `traceId`.

## Configuration

Everything lives under `Thunder` in `appsettings.json`, which has a commented example. Environment variables work too (`Thunder__Data__Folder=...`).

| Setting | Default | Meaning |
|---|---|---|
| `Data:Folder` | (required) | The data folder, or a Thunder.Dbf connection string. |
| `Data:ReadOnly` | `true` | The master switch. While true, nothing is written whatever else says. |
| `Data:CodePage` | (each table's own) | Force a code page, like 1252, 437 or 850. |
| `Data:IgnoreMissingMemo` | `false` | Read memos as null when a `.dbt`/`.fpt` is missing. |
| `Tables:{NAME}:AllowWrites` | `false` | Lets keys with write permission change this table. |
| `Tables:{NAME}:AllowUnmaintainedIndexes` | `false` | Allows writes to a table whose index Thunder.Dbf can't keep up to date: a FoxPro `.cdx`, or a production `.mdx` that's missing. A present `.mdx` is always kept up to date. Only if the legacy app reindexes before relying on it. |
| `ApiKeys:n:Id` | (required) | The key's name in logs and the journal. |
| `ApiKeys:n:Sha256` | (required) | SHA-256 of the key, 64 hex digits. |
| `ApiKeys:n:CanListTables` | `false` | May call `GET /tables`, and sees table names in 404 hints. |
| `ApiKeys:n:Read` | `[]` | Tables it may read, or `["*"]`. |
| `ApiKeys:n:Write` | `[]` | Tables it may write, or `["*"]`. The table needs `AllowWrites` too. |
| `ApiKeys:n:Fields:{NAME}` | (all fields) | An allow-list. Other fields don't exist for this key. |
| `OpenIdConnect:Authority` | (empty: off) | The provider's issuer URL. Setting it turns on bearer sign-in. |
| `OpenIdConnect:Audience` | (required with Authority) | The audience tokens must be issued for: this API's client id or app ID URI. |
| `OpenIdConnect:RequireHttpsMetadata` | `true` | Fetch the provider's metadata and keys over HTTPS only. Turn off only for a local test provider. |
| `OpenIdConnect:SubjectClaim` | `sub` | The claim that names the user, as `oidc:<value>`, in logs, the journal and rate limits. |
| `OpenIdConnect:Profiles:{name}` | (none) | A named set of permissions: `CanListTables`, `Read`, `Write` and `Fields`, exactly as on an API key. |
| `OpenIdConnect:Map:n` | (none) | One rule: `Role`, `Group`, or `Claim` with `Value`, and the `Profile` it grants. The first rule that matches wins. |
| `Paging:DefaultTake` / `MaxTake` | `50` / `500` | Page sizes. |
| `Paging:MaxResponseBytes` | `8388608` | A rough cap on one response's size. A page of big memos ends early, with a `next` link. |
| `Paging:MaxSeekScan` | `10000` | The most index entries one seek looks at. |
| `Journal:Path` | `journal/writes.jsonl` | The write journal. Relative to the service's folder. |
| `Backups:Folder` | `backups` | Where tables are copied before their first write. Relative to the service's folder. |
| `RateLimit:PerKeyPerMinute` | `600` | Requests per key, or per signed-in user, per minute. |
| `RateLimit:AnonymousPerMinute` | `60` | Requests per address per minute without a valid key. |
| `RateLimit:PerAddressPerMinute` | `1200` | Requests per address per minute, checked before the key is. Raise it if many users share one address. |
| `LockTimeoutSeconds` | `30` | How long a request waits for the table before a 503. |

Mistakes such as a missing folder, a malformed hash, a duplicate key id or a mapping rule naming a profile that doesn't exist stop the service at startup with a sentence saying what to fix.

## Signing in with OpenID Connect

Besides API keys, the server can accept access tokens from an OpenID Connect provider such as Microsoft Entra ID, Auth0 or Keycloak. Clients send `Authorization: Bearer <token>`. A request carrying both a bearer token and `X-Api-Key` is refused with 400 (`TwoCredentials`), because guessing which one was meant could run it with permissions nobody expected.

Users get permissions through **profiles**, which have the same shape as an API key's permissions. **Map** rules, checked in order, give a token a profile from one of its roles (the `roles` or `role` claim), groups (`groups`) or any other claim and value. The first rule that matches wins, so a user in two groups gets one profile's permissions, never a mix. A valid token that matches no rule gets 403 with a ProblemDetails saying so.

```jsonc
"Thunder": {
  "OpenIdConnect": {
    "Authority": "https://login.microsoftonline.com/<tenant id>/v2.0",
    "Audience": "<the API's application id>",
    "Profiles": {
      "clerks":    { "Read": [ "CONTACT1" ], "Write": [ "CONTACT1" ],
                     "Fields": { "CONTACT1": [ "ACCOUNTNO", "COMPANY", "CONTACT", "PHONE1" ] } },
      "reporting": { "Read": [ "*" ], "CanListTables": true }
    },
    "Map": [
      { "Role": "Thunder.Clerk", "Profile": "clerks" },
      { "Group": "<group object id>", "Profile": "reporting" },
      { "Claim": "scope", "Value": "dbf.read", "Profile": "reporting" }
    ]
  }
}
```

| Provider | Authority | Audience | Roles and groups |
|---|---|---|---|
| Microsoft Entra ID | `https://login.microsoftonline.com/<tenant id>/v2.0` | the API's application (client) id; set `accessTokenAcceptedVersion` to 2 in its manifest, so tokens come from the v2.0 issuer | App roles arrive in `roles`; security groups in `groups` (as object ids) once the app registration emits them. Consider `"SubjectClaim": "oid"` for a stable user id. |
| Auth0 | `https://<tenant>.auth0.com/` (with the trailing slash) | the API's identifier | Add roles with an Action under a namespaced claim, then map it with `"Claim": "https://example.com/roles"`. API permissions arrive in `permissions`. |
| Keycloak | `https://<host>/realms/<realm>` | the client id, added to `aud` with an audience mapper | Add a "User Realm Role" or "Group Membership" mapper that writes a flat `roles` or `groups` claim (Keycloak's own `realm_access` is nested and isn't read). |

Scopes come as one space-separated claim (`scope` or `scp`), so a `Claim` rule on either matches one scope within it. Every other comparison is exact, case included.

Tokens are checked against the provider's published keys, fetched over HTTPS from `<Authority>/.well-known/openid-configuration`. They must be signed with an RSA or ECDSA key, be issued by the Authority for the Audience, carry an expiry (with one minute of clock skew) and a subject. A 401 says only whether the token has expired; the server log has the rest. The token itself is never logged or kept.

## Security model

- **Coexists with the legacy app.** Every write takes the same byte-range record and header locks dBASE IV takes, and keeps the production `.mdx` up to date, so the legacy app keeps working on the same files. Run the service on Windows (next to the data or on the file server) or Linux: .NET can't take byte-range locks on macOS. A record the legacy app has locked answers 423 after a short wait.
- **Read-only by default, three switches for writes.** A write needs `Data:ReadOnly: false`, `AllowWrites` on the table, and the table in the key's `Write` list. With the master switch off, the write routes aren't even mapped.
- **Keys are hashed.** Config holds only SHA-256 hashes, compared in constant time (`CryptographicOperations.FixedTimeEquals`) against every configured key. Someone who reads `appsettings.json` still can't call the API. Keys are only accepted in the `X-Api-Key` header, never the query string, so they don't end up in access logs. Generated keys are 256 random bits, so a fast unsalted hash is enough.
- **Signed-in users go through the same checks as keys.** A token's roles and groups pick a profile shaped exactly like a key's permissions, and every endpoint checks it the same way. Which handler vouched for the caller decides where permissions come from, and claims named `thunder:*` in a token are dropped, so a token can't pass itself off as an API key. Users appear as `oidc:<sub>` in logs, the journal and rate limits. API key ids can't start with `oidc:`.
- **Tables you can't read don't exist.** A table missing from the key's `Read` list gets the same 404, word for word, as a table that isn't there, so keys can't probe for names. Only keys with `CanListTables` see names in hints.
- **Names can't become paths.** `{name}` is matched against the folder's own list of `.dbf` files, so `..%2F`, encoded backslashes, drive letters and companion files (`.dbt`, `.mdx`) never match anything. The library then refuses separators, `..`, and links pointing outside the folder as a second check.
- **Field allow-lists hide fields everywhere:** in records, in the schema and the per-table OpenAPI schemas, in index tags whose expressions mention them (seeking by such a tag would reveal values; a tag whose expression can't be read is hidden too), and on write, where a hidden field gets the same error as one that doesn't exist.
- **One writer, one request at a time per table.** `DbfTable` isn't thread-safe, so each table has its own lock. Requests on the same table take turns; different tables run in parallel. Each request opens the table and closes it again, so the legacy app is never locked out for longer than one request.
- **Backups before the first write.** The first write to each table after startup first copies its `.dbf`, `.dbt`, `.fpt`, `.mdx` and `.cdx` into `backups/<yyyyMMdd-HHmmssZ>/`, holding the table lock so the legacy app can't change them mid-copy. Files are copied under a `.partial` name and renamed only when all of them are complete.
- **A journal of every write.** Two JSON lines per insert, update or delete: an `intent` line before the file changes (with the values asked for), then a `done` line with the full record before and after. Each has the time, key id or `oidc:<sub>` (never the key or token), table, record number and the caller's address. An `intent` with no `done` after it is a write that failed or was cut off. The file is only ever appended to and is flushed to disk before the response is sent.
- **PATCH changes only the fields you send.** They're written under the record lock onto the record as it is on disk at that moment, so a change the legacy app just made to another field survives.
- **Rate limits** apply per address before any key or token is checked, then per key or signed-in user. Failed key attempts are logged at most once a minute per address, with a count, so a guessing attack can't flood the log.
- **HTTPS** redirection and HSTS are on outside the Development environment.
- **No leaks in errors.** Library messages name files and the service account so that whoever runs the server can fix things. Before a message reaches a client, the server strips paths down to file names and replaces the account name. Unexpected errors return only a `traceId`.

## Deploying as a Windows service next to the data folder

Run the service on the machine that holds the data, or on one with a fast, reliable link to the share. Give it its own folder, for example `C:\Services\Thunder.Dbf`, which is not inside the data folder. The journal and backups go there by default.

```powershell
dotnet publish src/Thunder.Dbf.Server -c Release -r win-x64 --self-contained -o C:\Services\Thunder.Dbf

# Edit C:\Services\Thunder.Dbf\appsettings.json: Data:Folder, ApiKeys, and Kestrel's HTTPS endpoint and certificate.

sc.exe create Thunder.Dbf binPath= "C:\Services\Thunder.Dbf\thunder-dbf-server.exe" start= delayed-auto obj= "NT SERVICE\Thunder.Dbf"
sc.exe description Thunder.Dbf "Thunder.Dbf JSON API over the legacy data folder"
sc.exe start Thunder.Dbf
```

- When running as a service, it reads `appsettings.json` from its own folder and logs to the Windows Event Log.
- Grant the service account read access to the data folder, plus modify access if writes are enabled. It needs modify access on its own folder for `journal\` and `backups\`. For a share on another server, use a domain account rather than a virtual one.
- Configure HTTPS in `appsettings.json` with a `Kestrel:Endpoints:Https` entry and its certificate, or put the service behind IIS or a reverse proxy that terminates TLS.
- Run `thunder-dbf check <folder>` from the CLI first. It reports locked, encrypted or damaged tables before the web app finds them.

## Not done yet

- Writes to FoxPro tables with a `.cdx` index. dBASE IV `.mdx` indexes are kept up to date on every write; `.cdx` is read-only for now, so those writes are refused unless `AllowUnmaintainedIndexes` is set.
- Recall (undelete), packing, and creating tables over HTTP.
