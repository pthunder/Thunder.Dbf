#!/usr/bin/env bash
#   ProjectThunder :: Thunder.Dbf
#   Installs a git pre-commit hook that runs the editorial check, so problems are caught
#   before they're committed rather than in CI.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
hook=.git/hooks/pre-commit
cat > "$hook" <<'HOOK'
#!/usr/bin/env bash
exec scripts/editorial-check.sh
HOOK
chmod +x "$hook"
echo "Installed $hook. Skip it once with: git commit --no-verify"
