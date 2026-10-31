<!-- ProjectThunder :: Thunder.Dbf. Thanks for helping! Please tick what applies. -->

## What and why

<!-- One or two sentences: what changes for users, and why. Link the issue if there is one. -->

## Checklist

Full rules: [editorial guidelines](../docs/maintainers/editorial-guidelines.md).

**Security and privacy**
- [ ] No secrets, personal paths, or real people's data, including in tests, fixtures and docs
- [ ] Error messages and hints stay safe to show users (no paths, account names or table lists)
- [ ] Untrusted input (table names, CSV, HTTP bodies) is never concatenated into paths, SQL or expressions
- [ ] New options default to the safe behavior
- [ ] Values read from files are bounded before use

**Legacy compatibility**
- [ ] Writes keep locks, indexes and untouched bytes intact (or this PR doesn't write files)
- [ ] New file-format code is tested against real files where they exist, plus hand-built edge cases

**Quality**
- [ ] Tests added or updated, and I checked they fail without the change
- [ ] `scripts/editorial-check.sh` passes
- [ ] Docs, samples and CHANGELOG.md updated for user-visible changes
