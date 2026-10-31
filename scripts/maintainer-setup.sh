#!/usr/bin/env bash
#   ProjectThunder :: Thunder.Dbf
#   One-time setup a maintainer does by hand, because it involves a secret or speaks for the
#   project. Run it from a terminal:  bash scripts/maintainer-setup.sh
#
#     1. Claude on GitHub: stores ANTHROPIC_API_KEY (or CLAUDE_CODE_OAUTH_TOKEN) as a repository
#        secret, typed at a hidden prompt, so the key never lands in shell history or a file.
#     2. nuget.org: opens a draft email asking to reserve the Thunder.Dbf package prefix, which
#        gives the packages nuget.org's verified checkmark. You read it and press send.
#
#   Each step asks first and can be skipped. Needs the GitHub CLI (gh), signed in as a repo admin.
set -euo pipefail

repo="pthunder/Thunder.Dbf"
nuget_account="ProjectThunder"

ask() {
    local answer
    read -r -p "$1 [y/N] " answer
    [[ "$answer" =~ ^[Yy] ]]
}

command -v gh >/dev/null || { echo "Install the GitHub CLI first: https://cli.github.com"; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "Sign in to the GitHub CLI first: gh auth login"; exit 1; }

# ---- 1. Claude on GitHub -------------------------------------------------------------------

echo
echo "== Claude on GitHub =="
echo "Turns on issue triage, pull request reviews and @claude (.github/workflows/claude*.yml)."
existing=$(gh secret list --repo "$repo" --json name -q '.[].name' | grep -E '^(ANTHROPIC_API_KEY|CLAUDE_CODE_OAUTH_TOKEN)$' || true)
if [[ -n "$existing" ]]; then
    echo "Already set: $existing"
fi
if ask "Store a key now?"; then
    echo "  1) An Anthropic API key (console.anthropic.com > API keys), billed per use"
    echo "  2) A Claude subscription token (run 'claude setup-token' in another terminal first)"
    read -r -p "Which? [1/2] " kind
    name=$([[ "$kind" == 2 ]] && echo CLAUDE_CODE_OAUTH_TOKEN || echo ANTHROPIC_API_KEY)
    read -r -s -p "Paste the value for $name (hidden): " value
    echo
    if [[ -z "$value" ]]; then
        echo "Nothing pasted; skipped."
    else
        printf '%s' "$value" | gh secret set "$name" --repo "$repo"
        unset value
        echo "Stored $name. New issues and pull requests get Claude from now on."
    fi
fi

# ---- 2. nuget.org prefix reservation -------------------------------------------------------

echo
echo "== nuget.org prefix reservation (optional) =="
echo "Reserving 'Thunder.Dbf' gives the packages a verified checkmark and stops anyone else"
echo "publishing under Thunder.Dbf.*. nuget.org reviews requests by email; it takes a few days."
if ask "Open a draft email to account@nuget.org?"; then
    subject="Package ID prefix reservation: Thunder.Dbf"
    body="Hello,

I'd like to reserve the package ID prefix Thunder.Dbf for the nuget.org account ${nuget_account}.

Packages: Thunder.Dbf, Thunder.Dbf.Sql (and the tool thunder-dbf, which isn't under the prefix)
Project: https://github.com/${repo}
Owner: ProjectThunder, https://projectthunder.com

Thunder.Dbf is an MIT-licensed .NET library for dBASE, FoxPro and Clipper data files.

Thank you,
ProjectThunder"
    encode() { python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1]))' "$1"; }
    url="mailto:account@nuget.org?subject=$(encode "$subject")&body=$(encode "$body")"
    if command -v open >/dev/null; then
        open "$url"
    elif command -v xdg-open >/dev/null; then
        xdg-open "$url"
    else
        echo "Send this to account@nuget.org:"; echo; echo "Subject: $subject"; echo; echo "$body"
    fi
    echo "The draft is open in your mail app. Read it over, then send it yourself."
fi

echo
echo "Done."
