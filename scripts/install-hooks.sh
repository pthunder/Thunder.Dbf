#!/usr/bin/env bash
#   ProjectThunder :: Thunder.Dbf
#   Installs git hooks: pre-commit runs the editorial check, and commit-msg checks the message
#   and author, so problems are caught before they're committed rather than in CI.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
hook=.git/hooks/pre-commit
cat > "$hook" <<'HOOK'
#!/usr/bin/env bash
exec scripts/editorial-check.sh
HOOK
chmod +x "$hook"

# The commit message and the author are published too: no internal tool, host or machine names,
# and no address made up from the machine's name (set user.email, e.g. your GitHub no-reply one).
msg_hook=.git/hooks/commit-msg
cat > "$msg_hook" <<'HOOK'
#!/usr/bin/env bash
internal='\b(Codex|gpt-[0-9][a-z0-9.-]*)\b|@[A-Za-z0-9-]+\.local\b'
# Private names (our own hosts and machines) live in an untracked file, so this list doesn't publish them.
private="$(git rev-parse --git-common-dir)/info/internal-names"
[[ -s "$private" ]] && internal+="|$(grep -v '^#' "$private" | grep . | paste -sd'|' -)"
if hits=$(grep -nE "$internal" "$1"); then
    echo "Commit message names an internal tool, host or machine (say \"automated review\" or \"the Windows test run\"):"
    echo "$hits"
    exit 1
fi
if git var GIT_AUTHOR_IDENT | grep -qE '@[A-Za-z0-9-]+\.local>'; then
    echo "Your git email is made up from this machine's name. Set one: git config user.email <your GitHub no-reply address>"
    exit 1
fi
HOOK
chmod +x "$msg_hook"
echo "Installed $hook and $msg_hook. Skip them once with: git commit --no-verify"
