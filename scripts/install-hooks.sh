#!/usr/bin/env bash
#   ProjectThunder :: Thunder.Dbf
#   Installs git hooks: pre-commit runs the editorial check, and commit-msg checks the message,
#   author and committer, so problems are caught before they're committed rather than in CI.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
hooks=$(git rev-parse --git-path hooks) # .git/hooks, or the shared one from a worktree
mkdir -p "$hooks"
marker='# Installed by scripts/install-hooks.sh'

# A hook someone wrote themselves is left alone: say so rather than overwrite it.
install() {
    local path="$hooks/$1"
    if [[ -e "$path" ]] && ! grep -qF "$marker" "$path"; then
        echo "Left $path as it is: it isn't one of ours. Add the check to it by hand, or remove it and run this again."
        return
    fi
    cat > "$path"
    chmod +x "$path"
    echo "Installed $path."
}

install pre-commit <<HOOK
#!/usr/bin/env bash
$marker
exec scripts/editorial-check.sh
HOOK

# The message, the author and the committer are published too: no internal tool, host or
# machine names, and no address made up from the machine's name.
install commit-msg <<HOOK
#!/usr/bin/env bash
$marker
source "\$(git rev-parse --show-toplevel)/scripts/internal-names.sh"
if hits=\$(grep -v '^#' "\$1" | grep -nE "\$internal"); then
    echo "The commit message names an internal tool, host or machine (say \"an automated review\" or \"the Windows test run\"):"
    echo "\$hits"
    exit 1
fi
for who in GIT_AUTHOR_IDENT GIT_COMMITTER_IDENT; do
    if git var "\$who" | grep -qE '@[A-Za-z0-9-]+\.local>'; then
        echo "Your git email is made up from this machine's name. Set one: git config user.email <your GitHub no-reply address>"
        exit 1
    fi
done
HOOK

echo "Skip them once with: git commit --no-verify"
