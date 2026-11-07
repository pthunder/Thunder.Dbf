#!/usr/bin/env bash
#   ProjectThunder :: Thunder.Dbf
#   The internal-names pattern, shared by editorial-check.sh, history-check.sh and the commit-msg
#   hook install-hooks.sh installs. Sourced, not run: it sets $internal.
#
#   Built in: the review tools' names (Codex, ChatGPT and gpt-* model names), and addresses made
#   up from a machine's name (user@host.local). Our own hosts and machines are private, so they
#   live in .git/info/internal-names (one extended regex per line, # for comments), which is
#   never published; the list would otherwise name what it hides.
internal='\b(Codex|ChatGPT|gpt-[0-9][a-z0-9.-]*)\b|@[A-Za-z0-9-]+\.local\b'
internal_private="$(git rev-parse --git-common-dir)/info/internal-names"
if [[ -s "$internal_private" ]]; then
    # Comments and blank lines left out; an empty alternative would match every line.
    internal_extra=$(grep -v -E '^[[:space:]]*(#|$)' "$internal_private" | paste -sd'|' -)
    [[ -n "$internal_extra" ]] && internal+="|$internal_extra"
fi
