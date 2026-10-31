# Maintaining Thunder.Dbf

```
   _______________
  | MON TUE WED.. |     ProjectThunder :: Thunder.Dbf
  |  [x] [ ] [ ]  |     A routine small enough to actually keep: machines watch
  |_______________|     everything continuously, people spend about an hour a week.
```

## What runs on its own

| When | What | Where |
|---|---|---|
| Every push and pull request | Build and tests on Windows, Linux and macOS | `.github/workflows/ci.yml` |
| Every push and pull request | Editorial gate: secrets, personal paths, banners, fixture licenses, stray data, leftovers | `scripts/editorial-check.sh` |
| Every push and pull request | Known-vulnerable package scan | `ci.yml`, editorial job |
| Every pull request, plus weekly | CodeQL security analysis | `.github/workflows/codeql.yml` |
| Weekly (Monday) | Dependabot update pull requests for NuGet packages and Actions | `.github/dependabot.yml` |
| Weekly (Monday) | Tests on the latest SDK, vulnerability rescan and editorial check; opens or updates a `maintenance` issue if anything fails | `.github/workflows/weekly-maintenance.yml` |
| Continuously | Secret scanning with push protection, Dependabot alerts | GitHub settings (below) |

## What people do

### Every pull request

1. **Wait for green:** all three test jobs, the editorial gate and CodeQL.
2. **Review against the [editorial guidelines](editorial-guidelines.md)**, using the pull-request checklist. Look hardest at the parts machines can't check:
   - error messages that might leak paths or names
   - untrusted input that might reach a path or a query
   - writes that could leave a file the legacy app can't read
3. **Squash-merge** with a message that says what changed for users.

### Weekdays: five minutes

- Any private security report? Acknowledge it within 2 business days ([SECURITY.md](../../SECURITY.md)).
- Is `main` red? Fixing it comes before anything else.

### Mondays: about thirty minutes

1. Read the `maintenance` issue, if the weekly job opened one, and fix or schedule each item.
2. Merge Dependabot pull requests that are green; read the release notes for major versions first.
3. Check new CodeQL and Dependabot alerts on the Security tab.
4. Triage new issues:
   - label them
   - ask for `thunder-dbf info` output (never the data itself)
   - reply within 3 business days, even if only "seen, looking"

### Monthly

- Close or nudge issues and pull requests idle for 30 days or more.
- Re-run `scripts/history-check.sh` and the full test suite against your private legacy folders (`THUNDER_TEST_DATA`).
- Check that the docs and samples still match the API.
- Check for .NET servicing releases and update the SDK in CI if needed.

### Every release

1. `scripts/editorial-check.sh` and `scripts/history-check.sh` pass.
2. CI is green on all three operating systems.
3. **Independent full review:** a second reviewer (person or AI) reviews the whole repository against the editorial guidelines, with security first. Every finding is fixed or explicitly deferred with an issue.
4. Update `CHANGELOG.md`, bump `<Version>` in `Directory.Build.props`, tag `vX.Y.Z`, and publish a GitHub release with the changelog section.
5. After the first public release, publish NuGet packages from the tag.

## One-time GitHub settings (repository owner)

These live in **Settings**, not in the repository, so they're listed here to switch on when the repository goes public:

- **Security → Private vulnerability reporting:** on.
- **Security → Secret scanning** and **Push protection:** on.
- **Security → Dependabot alerts** and **Dependabot security updates:** on.
- **Branches → protect `main`:**
  - require a pull request with one approving review from code owners
  - require these status checks: `Test (windows-latest)`, `Test (ubuntu-latest)`, `Test (macos-latest)`, `Editorial and security gates`, `analyze`
  - require branches to be up to date
  - block force pushes and deletion
- **General:** allow squash merging only; automatically delete head branches.
- **About (the gear next to the description):**
  - Description: "Safe, modern apps on top of legacy dBASE, FoxPro and Clipper data. Pure .NET: tables, memos, MDX/CDX/NTX indexes, locking, SQL sync, JSON API."
  - Topics: `dbase`, `foxpro`, `visual-foxpro`, `clipper`, `xbase`, `dbf`, `mdx`, `cdx`, `ntx`, `goldmine`, `legacy-modernization`, `data-migration`, `dotnet`, `csharp`.

  The topics and `llms.txt` are how people and AI assistants find the project.
- **Account:** two-factor authentication for everyone with write access.

## Publishing for the first time

Making the repository public publishes its whole history, so before you press the button:

1. `scripts/editorial-check.sh`: today's files are clean.
2. `scripts/history-check.sh`: every past commit is clean too. If it isn't, rewrite history (`git filter-repo`) before going public.
3. The independent full review (release step 3) is done.
4. The one-time settings above are on.
