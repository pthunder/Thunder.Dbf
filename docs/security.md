# Security

```
   _______
  |  ___  |     ProjectThunder :: Thunder.Dbf
  | |   | |     The threat model: what's protected, what isn't, and why.
  |_|___|_|
  |_______|
```

Legacy dBASE and FoxPro folders often hold customer records, and the point of Thunder.Dbf is to put a web app in front of them. This page is for whoever deploys it. To report a vulnerability, see [SECURITY.md](../SECURITY.md).

## Who we defend against

**An untrusted client of your app or the web service:** someone who controls table names, field values, CSV uploads, HTTP requests and query parameters.

| Threat | Defense |
|---|---|
| Reaching files outside the data folder (`../`, drive letters, encoded slashes, device names, links) | Names are matched against the folder's actual files, never turned into paths. Tables and companion files that are links (symbolic, or through a parent folder) are refused, not followed. |
| Learning about the server from errors | `Message` and `Hint` never hold paths, account names or table lists; those live in `FilePath`/`Diagnostic` for logs. The web service also scrubs and logs server-side only. |
| SQL injection (bridge, migrator, ADO.NET) | Values always travel as parameters; identifiers are quoted by the dialect. |
| Expression injection | Expressions come from index files, not from requests; the evaluator has no side effects, file access or dynamic code. |
| Malformed or hostile files: huge lengths, cycles, bad pointers | Every length, count and pointer read from a file is bounded; index walks visit each page once; memos are capped (`MaxMemoBytes`). Bad files produce explained errors, not hangs or huge allocations. |
| Formula injection through exported CSV | `DbfExport` neutralizes text starting with `= + - @` by default. |
| Unauthorized reads and writes over HTTP | API keys stored only as SHA-256 hashes and compared in constant time; per-key table and field permissions; read-only by default; three separate switches for writes; per-key rate limits. |
| Corrupting data the legacy app depends on | Locks, index maintenance, untouched-byte preservation, validation before writing, and safe commit order. |

## What's out of scope

- **Anyone who can already write to the data folder.** They can change the data directly.
  - They could also swap a file for a link between the moment Thunder.Dbf checks it and the moment it opens it. .NET has no portable way to open a file *relative to an already-checked directory*, so this race can't be fully closed. Treat write access to the data folder as trusted.
  - They could plant a **hard link** to a file elsewhere on the same volume, which looks like an ordinary file.
- **The legacy application's own security,** including dBASE IV PROTECT. Encrypted tables are refused, not decrypted.
- **Transport and hosting.** Use HTTPS, a firewall, and a service account with only the rights it needs. See the [web service](web-service.md) guide.

## Deployment checklist

- Run the service under its own account, with **read** rights on the data folder, **modify** rights only if writes are enabled, and nothing else.
- Keep the data folder writable only by that account and the legacy app's users.
- Keep `ReadOnly: true` until you need writes. Then enable them per table, and per API key.
- Turn off opportunistic locking for the data files (see [Locking](locking.md)).
- Back up before enabling writes, and keep the write journal.
- Run `thunder-dbf check` after every change to the folder or its permissions.
