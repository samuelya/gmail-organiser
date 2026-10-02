#!/usr/bin/env bash
# UserPromptSubmit hook (see .claude/settings.json): fires only in the lead session. Counts the PRs
# merged since this session's transcript began and, when there are any, tells the owner and the
# lead to /clear: state is on GitHub, and a lead session carried across merges was 52 % of all
# input tokens at 108k context per turn (eco-review 1, #52). Silent on any error.
# Test: echo '{"transcript_path":"/path/to/session.jsonl"}' | .claude/hooks/lead-fresh.sh
set -uo pipefail
input=$(cat)
t=$(jq -r '.transcript_path // empty' <<<"$input" 2>/dev/null)
[[ -n "$t" && -f "$t" ]] || exit 0
start=$(grep -m1 -o '"timestamp":"[^"]*"' "$t" 2>/dev/null | cut -d'"' -f4)
[[ -n "$start" ]] || exit 0
n=$(gh pr list --state merged --search "merged:>=${start%%.*}Z" --json number --jq length 2>/dev/null) || exit 0
[[ "$n" =~ ^[1-9][0-9]*$ ]] || exit 0
msg="$n PR(s) merged since this session started: run /clear before the next issue (CLAUDE.md, Cost discipline)."
jq -n --arg m "$msg" '{systemMessage: $m, hookSpecificOutput: {hookEventName: "UserPromptSubmit", additionalContext: ("lead-fresh hook: " + $m + " Remind the owner if they ask for new work.")}}'
