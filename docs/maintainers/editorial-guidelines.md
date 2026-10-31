# Code editorial guidelines

```
   ____________
  |  REVIEWED  |     ProjectThunder :: Thunder.Dbf
  |  [x] safe  |     What every change must meet before it's published.
  |  [x] clear |     scripts/editorial-check.sh checks what a machine can;
  |  [x] kind  |     reviewers check the rest with this page.
  |____________|
```

Thunder.Dbf exists so people can put web apps on top of legacy data that often holds customers' names, addresses and history. It also runs next to applications that are still in daily use. A careless change can leak that data or corrupt those files, so every change, from anyone, is held to these rules.

## 1. Security

1. **No secrets, ever.** No API keys, passwords, tokens, connection strings with credentials, or private keys in code, tests, docs, fixtures or commit messages. Tests generate throwaway keys at run time.
2. **Errors are safe to show.** `DbfException.Message` and `Hint` must never contain file paths, account names, server names or lists of tables. Put those in `FilePath` or the `diagnostic` argument, which are for logs.
3. **Untrusted input stays data.** Table names, field names, CSV contents and HTTP bodies are never concatenated into paths, SQL or expressions. Use the resolver in `DbfDatabase`, parameters in SQL, and the expression parser.
4. **Safe by default.** New options default to the safer behavior: read-only, refusing, validating. A riskier mode is opt-in, named plainly and documented with its risk.
5. **Never trust file contents.** Lengths, counts, pointers and offsets read from a file are bounded before use (long arithmetic, explicit limits, visited sets for anything that links). A damaged file must produce an explained error, not a crash, a hang or a huge allocation.
6. **Don't break the legacy app.** Writes take dBASE-compatible locks, keep indexes in step, and leave unchanged bytes alone. Anything that could leave a file in a state the legacy app can't read needs a test proving it can't.
7. **Pin what runs in CI.** GitHub Actions are referenced by full commit SHA (with the version in a comment), never by a tag that can be moved. Dependabot proposes updates weekly.
8. **Dependencies are a cost.** The core library has none. Adding a package anywhere needs a reason in the PR, a maintained upstream and a license compatible with MIT.

## 2. Privacy and data

1. **No real people's data in the repo.** Fixtures must be openly licensed test data or generated. Customer databases, CRM exports (including "sample" data from commercial products) and anything with real names, phone numbers or addresses stay out. Use `THUNDER_TEST_DATA` to test against private data locally.
2. **Every fixture has a source.** Each folder in `tests/fixtures` has its license file and a row in `tests/fixtures/SOURCES.md`: where it came from, the pinned commit and the license.
3. **No personal paths.** Nothing like `/Users/<name>`, `C:\Users\<name>` or `/home/<name>`, and no machine-specific temp paths, in committed files.

## 3. Correctness

1. **Tests prove it.** New behavior comes with tests. File-format code is tested against real files where they exist, plus hand-built files for the cases real files don't cover (interior index nodes, corruption).
2. **Bugs get a regression test.** It reproduces the reported input first and passes after the fix.
3. **Tests must be able to fail.** For format and index code, briefly break the implementation and confirm the tests catch it before trusting a green run.

## 4. Writing

1. **Plain, specific, kind.** Comments say *why* (a format quirk, a legacy app's behavior, a trade-off) rather than restating the code. Error messages say what happened, which thing it happened to, and what to do next.
2. **Match the surroundings.** File-scoped namespaces, XML doc comments on public members, and the existing comment density and naming.
3. **The brand is one word:** ProjectThunder. Every source file starts with an ASCII-art banner containing `ProjectThunder :: <project>`. Banners can be fun; diagrams of byte layouts are better still.
4. **No leftovers.** No `TODO`, `FIXME` or `HACK` without an issue link, no commented-out code, and no debugging output.

## 5. Docs

1. **User-visible changes update the docs** in the same PR: README, the relevant page in `docs/`, `CHANGELOG.md`, and the samples if their API changed.
2. **Format notes cite sources.** Anything learned from another implementation (Xbase64, Harbour) is credited, and its code is never copied.

## What's automated

`scripts/editorial-check.sh` runs on every push and pull request, and before every commit made by maintainers. It checks:

| Check | What it looks for |
|---|---|
| Secrets | key and token patterns, private keys, `Password=` in connection strings |
| Personal paths | `/Users/x`, `C:\Users\x`, `/home/x`, temp-folder paths |
| Brand | "Project Thunder" written as two words |
| Banners | every `.cs` file starts with a ProjectThunder banner |
| Fixtures | every fixture folder has a license and a SOURCES.md row |
| Stray data | table, memo, index, CSV or database files outside `tests/fixtures` |
| Leftovers | `TODO`, `FIXME`, `HACK` and `XXX` without an issue link |
| Workflows | every GitHub Action pinned to a full commit SHA |
| Size | files over 5 MB |

CI also runs the full test suite on Windows, Linux and macOS, a vulnerable-package scan, CodeQL, and `scripts/check-packages.sh` on freshly packed NuGet packages. The libraries fail to build if a public member has no XML doc comment.

Everything else on this page is checked by the reviewer, using the pull-request checklist.
