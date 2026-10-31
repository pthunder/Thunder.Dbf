#!/usr/bin/env bash
#   ProjectThunder :: Thunder.Dbf
#   Making a repository public publishes every commit ever made, not just today's files.
#   This scans the whole history (every file version, every commit message and every path)
#   for the same secrets, personal paths and private data files the editorial check looks for.
#
#   It fails closed: if git can't answer a question (a shallow clone, a broken object, a
#   command that errors), the check fails instead of passing on an empty answer.
set -o pipefail
cd "$(git rev-parse --show-toplevel)" || { echo "Not inside a git repository."; exit 2; }

die() { echo "History check couldn't run: $*"; exit 2; }

[[ "$(git rev-parse --is-shallow-repository)" == "false" ]] \
    || die "this is a shallow clone, so older commits can't be scanned. Run 'git fetch --unshallow' first."

patterns='AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{36,}|sk-[A-Za-z0-9_-]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|(Password|Pwd)=[^;"'"'"' ]+|/Users/[A-Za-z0-9._-]+/|[Cc]:\\\\Users\\\\[A-Za-z]|/home/[a-z][a-z0-9_-]*/|/private/tmp/|/var/folders/|scratch-workspaces'

# The files that define the patterns would match themselves.
excluded='^(scripts/editorial-check\.sh|scripts/history-check\.sh|docs/maintainers/.*)$'

objects=$(git rev-list --all --objects) || die "git rev-list failed."
commits=$(git rev-list --all --count) || die "git rev-list --count failed."

# Every blob that ever existed, by every path it was stored under, except the excluded files.
# Data files aren't text, so they're judged by name below rather than grepped.
hits=""
while read -r sha path; do
    [[ -z "$path" ]] && continue
    [[ "$path" =~ $excluded ]] && continue
    [[ "$(git cat-file -t "$sha")" == "blob" ]] || continue
    content=$(git cat-file blob "$sha") || die "can't read $path ($sha)."
    found=$(grep -nE -a "$patterns" <<<"$content" | head -3)
    [[ -n "$found" ]] && hits+="$path ($sha):"$'\n'"$found"$'\n'
done <<<"$objects"

log=$(git log --all --format='%H %s%n%b') || die "git log failed."
messages=$(grep -nE "$patterns" <<<"$log" | head -20)

names=$(git log --all --name-only --format=) || die "git log --name-only failed."
data=$(sort -u <<<"$names" | grep -viE '^tests/fixtures/' | grep -iE '\.(dbf|dbt|fpt|mdx|cdx|ndx|ntx|idx|csv|db|sqlite)$')

failed=0
[[ -n "$hits" ]] && { echo "Secrets or personal paths in history:"; echo "$hits"; failed=1; }
[[ -n "$messages" ]] && { echo "Secrets or personal paths in commit messages:"; echo "$messages"; failed=1; }
[[ -n "$data" ]] && { echo "Data files outside tests/fixtures somewhere in history:"; echo "$data"; failed=1; }

if (( failed )); then
    echo "History check failed. Rewrite history (git filter-repo) before making the repository public."
    exit 1
fi
echo "History check passed: $commits commits and every file version in them scanned."
