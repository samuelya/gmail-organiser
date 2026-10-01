#!/usr/bin/env bash
# Usage: scripts/gh/round.sh <issue> failed|passed|review-fix|polish|escalation <n>/<m> <model> [details|-] [--dry-run]
#   failed      posts "Round n/m failed on <model>" + details (what failed, planned fix)
#   passed      posts "Round n/m passed on <model>" + details (verified SHA, evidence)
#   review-fix  posts "Round n/m review-fix on <model>" + details (findings fixed, verified SHA)
#   polish      posts "Round n/m polish on <model>" + details (what the owner asked for, verified SHA)
#   escalation  posts "Escalation: n/m rounds failed on <model>" + details (each round, root cause)
# Every coder run ends with one of these, not just the failures: #212 spent 4 runs and 107M input
# tokens against 0 round comments, so the metrics read it as a clean one-round issue (#207).
# Details come from the 5th argument, or from stdin when it is "-". --dry-run prints the comment.
# One canonical first line, so scripts/gh/team-metrics.sh counts rounds instead of guessing at
# hand-written variants (four formats across six issues broke the failed-round metric, #207).
set -euo pipefail
source "$(dirname "$0")/_lib.sh"

dry=0
args=()
for a in "$@"; do [[ "$a" == "--dry-run" ]] && dry=1 || args+=("$a"); done
set -- "${args[@]}"

issue="${1:?issue number required}"; require_number "$issue"
kind="${2:?failed|passed|escalation required}"
round="${3:?round required, e.g. 1/2}"
model="${4:?model required, e.g. claude-sonnet-5}"
details="${5:-}"
[[ "$round" =~ ^[0-9]+/[0-9]+$ ]] || { echo "Round must look like n/m, got: '$round'" >&2; exit 1; }
[[ "$model" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "Model must be a plain id, got: '$model'" >&2; exit 1; }
[[ "$details" == "-" ]] && details=$(cat)

case "$kind" in
  failed)     head="Round $round failed on $model" ;;
  passed)     head="Round $round passed on $model" ;;
  review-fix) head="Round $round review-fix on $model" ;;
  polish)     head="Round $round polish on $model" ;;
  escalation) head="Escalation: $round rounds failed on $model" ;;
  *) echo "Kind must be failed, passed, review-fix, polish or escalation, got: '$kind'" >&2; exit 1 ;;
esac

body="$head"
[[ -n "$details" ]] && body+=$'\n\n'"$details"
if (( dry )); then printf '%s\n' "$body"; exit 0; fi
gh issue comment "$issue" -R "$OWNER/$REPO" --body "$body"
