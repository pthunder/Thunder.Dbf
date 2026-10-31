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
| GET | `/tables` | The tables this key may read, and whether it may write each one. Needs `CanListTables`. |
| GET | `/tables/{name}` | Fields (name, type, width, decimals), record count, code page and index tags. |
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
| `Paging:DefaultTake` / `MaxTake` | `50` / `500` | Page sizes. |
| `Paging:MaxResponseBytes` | `8388608` | A rough cap on one response's size. A page of big memos ends early, with a `next` link. |
| `Paging:MaxSeekScan` | `10000` | The most index entries one seek looks at. |
| `Journal:Path` | `journal/writes.jsonl` | The write journal. Relative to the service's folder. |
| `Backups:Folder` | `backups` | Where tables are copied before their first write. Relative to the service's folder. |
| `RateLimit:PerKeyPerMinute` | `600` | Requests per key per minute. |
| `RateLimit:AnonymousPerMinute` | `60` | Requests per address per minute without a valid key. |
| `RateLimit:PerAddressPerMinute` | `1200` | Requests per address per minute, checked before the key is. Raise it if many users share one address. |
| `LockTimeoutSeconds` | `30` | How long a request waits for the table before a 503. |

Mistakes such as a missing folder, a malformed hash or a duplicate key id stop the service at startup with a sentence saying what to fix.

## Security model

- **Coexists with the legacy app.** Every write takes the same byte-range record and header locks dBASE IV takes, and keeps the production `.mdx` up to date, so the legacy app keeps working on the same files. Run the service on Windows (next to the data or on the file server) or Linux: .NET can't take byte-range locks on macOS. A record the legacy app has locked answers 423 after a short wait.
- **Read-only by default, three switches for writes.** A write needs `Data:ReadOnly: false`, `AllowWrites` on the table, and the table in the key's `Write` list. With the master switch off, the write routes aren't even mapped.
- **Keys are hashed.** Config holds only SHA-256 hashes, compared in constant time (`CryptographicOperations.FixedTimeEquals`) against every configured key. Someone who reads `appsettings.json` still can't call the API. Keys are only accepted in the `X-Api-Key` header, never the query string, so they don't end up in access logs. Generated keys are 256 random bits, so a fast unsalted hash is enough.
- **Tables you can't read don't exist.** A table missing from the key's `Read` list gets the same 404, word for word, as a table that isn't there, so keys can't probe for names. Only keys with `CanListTables` see names in hints.
- **Names can't become paths.** `{name}` is matched against the folder's own list of `.dbf` files, so `..%2F`, encoded backslashes, drive letters and companion files (`.dbt`, `.mdx`) never match anything. The library then refuses separators, `..`, and links pointing outside the folder as a second check.
- **Field allow-lists hide fields everywhere:** in records, in the schema, in index tags whose expressions mention them (seeking by such a tag would reveal values; a tag whose expression can't be read is hidden too), and on write, where a hidden field gets the same error as one that doesn't exist.
- **One writer, one request at a time per table.** `DbfTable` isn't thread-safe, so each table has its own lock. Requests on the same table take turns; different tables run in parallel. Each request opens the table and closes it again, so the legacy app is never locked out for longer than one request.
- **Backups before the first write.** The first write to each table after startup first copies its `.dbf`, `.dbt`, `.fpt`, `.mdx` and `.cdx` into `backups/<yyyyMMdd-HHmmssZ>/`, holding the table lock so the legacy app can't change them mid-copy. Files are copied under a `.partial` name and renamed only when all of them are complete.
- **A journal of every write.** Two JSON lines per insert, update or delete: an `intent` line before the file changes (with the values asked for), then a `done` line with the full record before and after. Each has the time, key id (never the key), table, record number and the caller's address. An `intent` with no `done` after it is a write that failed or was cut off. The file is only ever appended to and is flushed to disk before the response is sent.
- **PATCH changes only the fields you send.** They're written under the record lock onto the record as it is on disk at that moment, so a change the legacy app just made to another field survives.
- **Rate limits** apply per address before any key is checked, then per key. Failed key attempts are logged at most once a minute per address, with a count, so a guessing attack can't flood the log.
- **HTTPS** redirection and HSTS are on outside the Development environment.
- **No leaks in errors.** Library messages name files and the service account so that whoever runs the server can fix things. Before a message reaches a client, the server strips paths down to file names and replaces the account name. Unexpected errors return only a `traceId`.

OpenID Connect can be added later as another authentication scheme. Permissions hang off a key id claim rather than the scheme, so endpoints won't change.

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
- An OpenAPI schema per table, generated from the DBF fields. Today records are described generically, and `GET /tables/{name}` gives the real fields.
- OpenID Connect.
