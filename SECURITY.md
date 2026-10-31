# Security policy

```
   _______
  |  ___  |     ProjectThunder :: Thunder.Dbf
  | |   | |     Thunder.Dbf sits between the web and legacy data full of real
  |_|___|_|     customers' details. Security reports get our fastest attention.
  |_______|
```

## Reporting a vulnerability

**Please don't open a public issue.** Report it privately through GitHub: on the repository's **Security** tab, choose **Report a vulnerability**. Include what you found, how to reproduce it, and what an attacker could gain.

| What happens | How fast |
|---|---|
| We acknowledge your report | within 2 business days |
| We confirm or rule it out, and agree a disclosure date with you | within 7 days |
| Fix released for critical or high severity | within 14 days of confirmation |
| Fix released for medium or low severity | in the next release, within 90 days |

You'll be credited in the advisory and the changelog unless you'd rather not be.

## Supported versions

Until 1.0, only the latest release gets security fixes.

## What Thunder.Dbf protects against

See [docs/security.md](docs/security.md) for the full threat model. In short:

- **Covered:** untrusted table names, CSV files and HTTP requests trying to reach files outside the data folder (including through links), read data they shouldn't, inject SQL, or crash or hang the process with malformed files. Error messages are safe to show clients.
- **Not covered:** someone who can already write to the data folder (they can change the data directly), hard links planted by such a person, and the legacy application's own security.
