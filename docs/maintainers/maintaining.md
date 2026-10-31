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
| Every push and pull request | Pack the NuGet packages and check them: README, icon, symbols, XML docs, and the CLI installs and runs | `ci.yml`, pack job; `scripts/check-packages.sh` |
| Every `vX.Y.Z` tag | Build, test, pack, publish to nuget.org, and create the GitHub release (see [Releasing](#releasing)) | `.github/workflows/release.yml` |
| Every pull request, plus weekly | CodeQL security analysis | `.github/workflows/codeql.yml` |
| Weekly (Monday) | Dependabot update pull requests for NuGet packages and Actions | `.github/dependabot.yml` |
| Weekly (Monday) | Tests on the latest SDK, vulnerability rescan and editorial check; opens or updates a `maintenance` issue if anything fails | `.github/workflows/weekly-maintenance.yml` |
| Continuously | Secret scanning with push protection, Dependabot alerts | GitHub settings (below) |
| Every new issue | Claude labels it, answers from the docs or asks for what's missing to reproduce it, links duplicates, and points security reports to SECURITY.md. It can only read the repo and comment or label. | `.github/workflows/claude-triage.yml` |
| Every pull request from this repo | Claude reviews it against the [editorial guidelines](editorial-guidelines.md), with comments on the lines | `.github/workflows/claude-review.yml` |
| A comment mentioning `@claude`, from someone with write access | Claude answers, reviews a fork's pull request, or drafts a fix on a branch. It never merges, and it can't build or run code (a fork's code would run with a write token). | `.github/workflows/claude.yml` |

The three Claude workflows do nothing until the repository has an `ANTHROPIC_API_KEY` secret (an Anthropic API key) or a `CLAUDE_CODE_OAUTH_TOKEN` secret (from `claude setup-token`, for a Claude subscription). Add one under Settings → Secrets and variables → Actions. A person still decides every merge: branch protection requires a maintainer's approval.

## What people do

### Every pull request

1. **Wait for green:** all three test jobs, the pack check, the editorial gate and CodeQL.
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
4. Follow [Releasing](#releasing): the tag does the rest.

## Releasing

```
   v0.1.0 ──> build, test, pack ──> server zips ──> nuget.org ──> GitHub release
              (ubuntu)              (win, linux,    (trusted      (packages, zips,
                                     macOS)          publishing)   checksums, notes)
```

What ships:

| Artifact | Where |
|---|---|
| `Thunder.Dbf`, `Thunder.Dbf.Sql` (libraries, with XML docs and `.snupkg` symbols) | nuget.org and the GitHub release |
| `thunder-dbf` (the CLI, as a .NET tool) | nuget.org and the GitHub release |
| `thunder-dbf-server-<version>-<rid>.zip` for `win-x64`, `linux-x64`, `osx-arm64` | GitHub release only |

The web service is an app, not a library, so it isn't a NuGet package. Each zip is self-contained (no .NET install needed): one executable, `appsettings.json`, the README and the license. Each one is built on its own operating system, because a macOS executable built on Linux isn't code-signed and Apple silicon won't run it.

### Steps

1. On a branch, bump `<Version>` in `Directory.Build.props` (the one place the version lives).
2. In `CHANGELOG.md`, turn `## [X.Y.Z] - Unreleased` into `## [X.Y.Z] - YYYY-MM-DD`. That section becomes the release notes.
3. Merge it through a pull request as usual, with CI green.
4. Tag the merge commit on `main` and push the tag:

   ```bash
   git tag -a vX.Y.Z -m "Thunder.Dbf X.Y.Z"
   ```

   ```bash
   git push origin vX.Y.Z
   ```

5. Watch the **Release** workflow. If the `nuget` environment has required reviewers, approve the publish job when it asks.

For a prerelease, use a version like `0.2.0-beta.1` in both places; the GitHub release is then marked as a prerelease.

### What the workflow does

1. **Checks before building anything:** the tag must be `v` + `<Version>`, and `CHANGELOG.md` must have a dated section for that version. Either mismatch stops the release.
2. **Builds, tests and packs** on Ubuntu, then runs `scripts/check-packages.sh`, the same check CI runs on every pull request.
3. **Publishes the web service** as a self-contained zip on Windows, Linux and macOS.
4. **Pushes to nuget.org** with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the job trades a GitHub OIDC token for an API key that lasts about an hour, so no key is stored in the repository. `--skip-duplicate` lets a re-run finish a release that failed halfway.
5. **Creates the GitHub release** with the `.nupkg` files, the zips, a `SHA256SUMS.txt` and the changelog section.

Every job gets only the permissions it needs: `contents: read` to build, `id-token: write` (and nothing else) to publish to nuget.org, and `contents: write` only for the job that creates the release.

If a release fails before the nuget.org step, fix the problem, delete the tag (`git push origin :vX.Y.Z`, then `git tag -d vX.Y.Z`) and tag again. Once a version is on nuget.org it can be unlisted but never replaced, so fix forward with a new patch version.

### One-time nuget.org setup (repository owner)

1. **Sign in to nuget.org** with the account (or organization) that will own the packages, with two-factor authentication on.
2. **Reserve the ID prefixes,** so no one else can publish packages that look like ours: email `account@nuget.org` asking to reserve `Thunder.Dbf` (it covers `Thunder.Dbf.*`) and `thunder-dbf` for that account. See [ID prefix reservation](https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation). Reserved packages get the verified check mark.
3. **Add a trusted publishing policy:** on nuget.org, your user name → **Trusted Publishing** → **Add policy**:
   - Owner: the account or organization from step 1
   - Repository owner: `pthunder`
   - Repository: `Thunder.Dbf`
   - Workflow file: `release.yml`
   - Environment: `nuget`
   - Scopes: allow pushing new packages (needed for the first release) and new versions of existing ones.

   Some policies (usually for private repositories) start out temporarily active for 7 days and become permanent after the first successful publish. If yours shows that status, create it shortly before the first release, or restart the window then.
4. **Tell the workflow the nuget.org user name:** on GitHub, **Settings → Secrets and variables → Actions → Variables**, add `NUGET_USER` with the nuget.org user name (a profile name, not an email address, and not a secret).
5. **Create the `nuget` environment:** **Settings → Environments → New environment** → `nuget`. Under **Deployment branches and tags**, allow only tags matching `v*.*.*`, and add yourself as a required reviewer if you want to approve each publish.

No `NUGET_API_KEY` secret is needed. If trusted publishing is ever unavailable, the fallback is a scoped API key (push only, `Thunder.Dbf*` and `thunder-dbf` globs, short expiry) stored as an environment secret on `nuget` and passed to `dotnet nuget push` in place of the login step's output.

## One-time GitHub settings (repository owner)

These live in **Settings**, not in the repository, so they're listed here to switch on when the repository goes public:

- **Security → Private vulnerability reporting:** on.
- **Security → Secret scanning** and **Push protection:** on.
- **Security → Dependabot alerts** and **Dependabot security updates:** on.
- **Branches → protect `main`:**
  - require a pull request with one approving review from code owners
  - require these status checks: `Test (windows-latest)`, `Test (ubuntu-latest)`, `Test (macos-latest)`, `Pack`, `Editorial and security gates`, `analyze`
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
