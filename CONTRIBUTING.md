# Contributing

```
       .-----.
      /     /      ProjectThunder :: Thunder.Dbf
     /     /__     Thanks for helping legacy data reach the modern world,
    /__      /     safely.
      /    /
     /   /
    /  /
   //
  '
```

## Before you start

- **Found a security problem?** Report it privately (see [SECURITY.md](SECURITY.md)), not in an issue.
- **Bigger change?** Open an issue first, so we can agree on the approach before you write it.
- **Real data stays out.** Never attach or commit a real customer database. To test against one, point `THUNDER_TEST_DATA` at a private copy (see [docs/testing.md](docs/testing.md)).

## Building and testing

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet test Thunder.Dbf.slnx
```

Before you commit:

```bash
scripts/editorial-check.sh
```

Or install it as a pre-commit hook:

```bash
scripts/install-hooks.sh
```

## What a good pull request looks like

Every change, from anyone, meets the [editorial guidelines](docs/maintainers/editorial-guidelines.md); the pull-request template walks you through them. The short version:

1. **Safe:** no secrets or personal data, safe error messages, untrusted input treated as data, safe defaults.
2. **Compatible:** the legacy app must still be able to read anything Thunder.Dbf writes.
3. **Proven:** tests that fail without your change, against real files where possible.
4. **Clear:** comments that explain why, and a ProjectThunder banner on new files.

CI runs the tests on Windows, Linux and macOS, plus the editorial check, a vulnerable-package scan and CodeQL. A maintainer reviews every pull request.

## Code of conduct

Be kind and assume good intent. See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
