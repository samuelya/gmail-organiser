#!/usr/bin/env bash
# Lists, and with --apply removes, the Docker volumes agents' compose projects left behind:
# gmo-test-<pr> (tester) once the PR is closed or merged, gmo-<issue> (coder) once the issue is
# closed. Only volumes whose compose project label matches those names are touched; the owner's
# stack and anything else are never listed. Lead-run (the guard blocks agents' volume deletes).
# Usage: scripts/gh/clean-test-volumes.sh [--apply]
set -uo pipefail
source "$(dirname "$0")/_lib.sh"
apply=0; [[ "${1:-}" == "--apply" ]] && apply=1

# macOS ships bash 3.2 (no associative arrays): volumes come sorted, so cache the last project.
last=""; s=""
removed=0; kept=0
while read -r vol project; do
  [[ "$project" =~ ^gmo-(test-)?([0-9]+)$ ]] || continue
  n="${BASH_REMATCH[2]}"
  if [[ "$project" != "$last" ]]; then
    last="$project"
    if [[ -n "${BASH_REMATCH[1]}" ]]; then
      s=$(gh pr view "$n" -R "$OWNER/$REPO" --json state --jq .state 2>/dev/null || echo UNKNOWN)
    else
      s=$(gh issue view "$n" -R "$OWNER/$REPO" --json state --jq .state 2>/dev/null || echo UNKNOWN)
    fi
  fi
  if [[ "$s" == OPEN || "$s" == UNKNOWN ]]; then
    echo "keep    $vol ($project: $s)"; kept=$((kept + 1)); continue
  fi
  if (( apply )); then
    if docker volume rm "$vol" >/dev/null 2>&1; then echo "removed $vol ($project: $s)"; removed=$((removed + 1))
    else echo "in use  $vol ($project: $s; stop its containers with docker compose -p $project down)"; kept=$((kept + 1)); fi
  else
    echo "remove  $vol ($project: $s)"; removed=$((removed + 1))
  fi
done < <(docker volume ls --format '{{.Label "com.docker.compose.project"}} {{.Name}}' | sort | awk '{print $2, $1}')

if (( apply )); then echo "$removed removed, $kept kept."
else echo "$removed to remove, $kept kept. Run with --apply to remove them."; fi
