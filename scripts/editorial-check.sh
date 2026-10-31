#!/usr/bin/env bash
#    ____________
#   |  REVIEWED  |     ProjectThunder :: Thunder.Dbf
#   |  [x] safe  |     The machine-checkable half of docs/maintainers/editorial-guidelines.md.
#   |  [x] clear |     Runs in CI on every push and pull request; run it yourself before
#   |____________|     committing:   scripts/editorial-check.sh
#
# Checks the files git would publish (tracked, plus new files that aren't ignored).
# Exits non-zero, listing every problem, if anything fails.

set -o pipefail  # (not -u: macOS still ships bash 3.2, where empty arrays trip it)
cd "$(git rev-parse --show-toplevel)"

failures=0
fail() {
    echo "  ✗ $1"
    failures=$((failures + 1))
}
section() { echo "• $1"; }

# Everything that would be published, minus binary fixtures.
files=()
while IFS= read -r line; do files+=("$line"); done < <(git ls-files --cached --others --exclude-standard | sort -u)
text_files=()
for f in "${files[@]}"; do
    [[ -f "$f" ]] || continue
    case "$f" in
        tests/fixtures/*.[dD][bB][fF]|tests/fixtures/*.[dD][bB][tT]|tests/fixtures/*.[fF][pP][tT]) continue ;;
        tests/fixtures/*.[mM][dD][xX]|tests/fixtures/*.[cC][dD][xX]|tests/fixtures/*.[nN][dD][xX]|tests/fixtures/*.[nN][tT][xX]|tests/fixtures/*.[iI][dD][xX]) continue ;;
        *.png|*.jpg|*.ico|*.zip|*.nupkg) continue ;;
    esac
    text_files+=("$f")
done

# Lines in text files matching a regex, excluding the scripts and guidelines that quote the patterns.
search() {
    grep -nIE -- "$1" "${text_files[@]}" 2>/dev/null \
        | grep -v -e '^scripts/editorial-check.sh:' -e '^scripts/history-check.sh:' -e '^docs/maintainers/editorial-guidelines.md:'
}

section "Secrets"
secret_patterns=(
    'AKIA[0-9A-Z]{16}'                                  # AWS access key
    'gh[pousr]_[A-Za-z0-9]{36,}'                        # GitHub tokens
    'sk-[A-Za-z0-9_-]{20,}'                             # API keys in the sk- style
    'xox[baprs]-[A-Za-z0-9-]{10,}'                      # Slack tokens
    '-----BEGIN [A-Z ]*PRIVATE KEY-----'
    '(Password|Pwd)=[^;"'"'"' ]+'                       # credentials in connection strings
    '"Sha256": *"[0-9a-fA-F]{64}"'                      # real key hashes in config (examples use placeholders)
)
for pattern in "${secret_patterns[@]}"; do
    while IFS= read -r hit; do fail "possible secret: $hit"; done < <(search "$pattern")
done

section "Personal paths"
while IFS= read -r hit; do fail "personal or machine path: $hit"; done < <(search '(/Users/[A-Za-z0-9._-]+/|[Cc]:\\\\Users\\\\[A-Za-z]|/home/[a-z][a-z0-9_-]*/|/private/tmp/|/var/folders/|scratch-workspaces)')

section "Brand"
while IFS= read -r hit; do fail "write ProjectThunder as one word: $hit"; done < <(search 'Project Thunder')

section "Banners"
for f in "${text_files[@]}"; do
    [[ "$f" == *.cs ]] || continue
    case "$f" in */obj/*|*/bin/*) continue ;; esac
    head -n 12 "$f" | grep -q 'ProjectThunder ::' || fail "no ProjectThunder banner: $f"
done

section "Fixtures"
if [[ -d tests/fixtures ]]; then
    for dir in tests/fixtures/*/; do
        name=$(basename "$dir")
        grep -q "\`$name/\`" tests/fixtures/SOURCES.md || fail "fixture folder without a SOURCES.md row: $name"
        if ! ls "$dir" | grep -qiE '^(LICENSE|COPYING|NOTICE)'; then
            fail "fixture folder without a license file: $name"
        fi
    done
fi

section "Stray data files"
for f in "${files[@]}"; do
    case "$f" in tests/fixtures/*) continue ;; esac
    if [[ "$f" =~ \.([dD][bB][fF]|[dD][bB][tT]|[fF][pP][tT]|[mM][dD][xX]|[cC][dD][xX]|[nN][dD][xX]|[nN][tT][xX]|[cC][sS][vV]|[dD][bB]|[sS][qQ][lL][iI][tT][eE])$ ]]; then
        fail "data file outside tests/fixtures (real data stays out of the repo): $f"
    fi
done

section "Leftovers"
while IFS= read -r hit; do fail "leftover marker (link an issue or remove it): $hit"; done < <(search '\b(TODO|FIXME|HACK|XXX)\b' | grep -v 'github.com/.*/issues/')

section "Workflows"
for f in "${files[@]}"; do
    case "$f" in .github/workflows/*.yml|.github/workflows/*.yaml) ;; *) continue ;; esac
    while IFS= read -r hit; do
        fail "action not pinned to a full commit SHA (tags can be moved): $f: $hit"
    done < <(grep -nE '^\s*-?\s*uses:' "$f" | grep -vE 'uses: *[^@ ]+@[0-9a-f]{40}( |$)')
done

section "Large files"
for f in "${files[@]}"; do
    [[ -f "$f" ]] || continue
    size=$(wc -c < "$f")
    (( size > 5 * 1024 * 1024 )) && fail "over 5 MB: $f ($((size / 1024 / 1024)) MB)"
done

echo
if (( failures > 0 )); then
    echo "Editorial check failed: $failures problem(s). See docs/maintainers/editorial-guidelines.md."
    exit 1
fi
echo "Editorial check passed."
