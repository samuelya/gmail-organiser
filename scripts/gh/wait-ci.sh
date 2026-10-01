#!/usr/bin/env bash
# Usage: scripts/gh/wait-ci.sh <pr-number> [--timeout <seconds>]
# Waits for a PR's CI in one blocking call and never stays silent: it prints a status line whenever
# a check changes state, and a heartbeat at least every 60 s, so a long run shows progress rather
# than looking hung.
#
# Exit 0: all checks green (skipped counts as green).
# Exit 1: a check failed or was cancelled (fails fast; prints the failed job's log tail).
# Exit 2: still running at the deadline. Prints the pending checks. Run the same command again.
#
# The default deadline is 540 s, below the Bash tool's 600 000 ms cap, so the call always returns
# with output instead of being killed silently. PR runs here take 5-14 min, so a second call is
# normal. Pass --timeout to change it. Don't wrap this in a sleep/poll loop.
#
# When run from the PR's own branch, it first waits (up to 2 min) until the PR head is this
# checkout's HEAD, so a fresh push is never judged by the previous commit's checks.
set -euo pipefail
source "$(dirname "$0")/_lib.sh"

pr="${1:?PR number required}"
require_number "$pr"
shift
timeout=540
while (( $# )); do
  case "$1" in
    --timeout) timeout="${2:?--timeout needs seconds}"; require_number "$timeout"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 64 ;;
  esac
done
repo="$OWNER/$REPO"
start=$(date +%s)
elapsed() { echo "$(( $(date +%s) - start ))s"; }

# checks: prints "bucket<TAB>name<TAB>link" per check on the PR head; empty when none registered.
# gh exits 8 while checks are pending (still printing the list); any other failure is shown once.
checks() {
  local out rc=0
  out=$(gh pr checks "$pr" -R "$repo" --json name,bucket,link \
    --jq '.[] | "\(.bucket)\t\(.name)\t\(.link)"' 2>&1) || rc=$?
  if (( rc != 0 && rc != 8 )) && ! grep -q $'\t' <<<"$out"; then
    echo "gh pr checks failed (exit $rc): $(head -1 <<<"$out")" >&2
    return 0
  fi
  grep $'\t' <<<"$out" || true
}

pr_json=$(gh pr view "$pr" -R "$repo" --json headRefName,headRefOid,state)
[[ "$(jq -r .state <<<"$pr_json")" == "OPEN" ]] || { echo "PR #$pr is $(jq -r .state <<<"$pr_json"); nothing to wait for."; exit 1; }
head_branch=$(jq -r .headRefName <<<"$pr_json")
if [[ "$(git branch --show-current 2>/dev/null)" == "$head_branch" ]]; then
  local_sha=$(git rev-parse HEAD)
  remote_sha=$(jq -r .headRefOid <<<"$pr_json")
  if [[ "$remote_sha" != "$local_sha" ]]; then
    echo "Waiting for PR head to reach local HEAD ${local_sha:0:7} (PR is at ${remote_sha:0:7})..."
    for _ in $(seq 1 24); do
      sleep 5
      remote_sha=$(gh pr view "$pr" -R "$repo" --json headRefOid --jq .headRefOid)
      [[ "$remote_sha" == "$local_sha" ]] && break
    done
    [[ "$remote_sha" == "$local_sha" ]] || { echo "PR head is still ${remote_sha:0:7}, not local HEAD ${local_sha:0:7}. Did the push succeed?"; exit 1; }
  fi
fi
echo "PR #$pr head ${remote_sha:-$(jq -r .headRefOid <<<"$pr_json")} - waiting for CI (deadline ${timeout}s)..."

last_summary=""
last_print=$start
while :; do
  now=$(date +%s)
  all=$(checks)
  pending=$(grep -c $'^pending\t' <<<"$all" || true)
  bad=$(grep -vE $'^(pass|skipping|pending)\t' <<<"$all" || true)
  total=$(grep -c . <<<"$all" || true)
  summary="$(grep -c $'^pass\t' <<<"$all" || true) passed, $(grep -c $'^skipping\t' <<<"$all" || true) skipped, $pending pending of $total"

  if [[ -n "$bad" ]]; then
    echo "[$(elapsed)] CI not green on PR #$pr:"
    echo "$bad"
    run_id=$(grep -m1 -oE 'actions/runs/[0-9]+' <<<"$bad" | cut -d/ -f3 || true)
    if [[ -n "$run_id" ]] && grep -q $'^fail\t' <<<"$bad"; then
      echo "--- failures (scripts/gh/ci-failures.sh $pr --run $run_id; add --post to hand them to the next round) ---"
      "$(dirname "$0")/ci-failures.sh" "$pr" --run "$run_id" 2>/dev/null \
        || gh run view "$run_id" -R "$repo" --log-failed 2>/dev/null | tail -40 || true
    fi
    exit 1
  fi

  # Checks take a few seconds to register after a push; treat "none yet" as pending for 90 s.
  if (( total > 0 && pending == 0 )); then
    echo "[$(elapsed)] CI green on PR #$pr ($summary)."
    exit 0
  fi
  if (( total == 0 && now - start > 90 )); then
    echo "[$(elapsed)] No checks registered on PR #$pr after 90 s. Is the workflow triggered for this PR?"
    exit 1
  fi

  if [[ "$summary" != "$last_summary" || $(( now - last_print )) -ge 60 ]]; then
    echo "[$(elapsed)] $summary"
    last_summary="$summary"; last_print=$now
  fi

  if (( now - start >= timeout )); then
    echo "[$(elapsed)] Deadline reached; still pending on PR #$pr:"
    grep $'^pending\t' <<<"$all" | cut -f2 | sed 's/^/  /'
    echo "Run again: scripts/gh/wait-ci.sh $pr"
    exit 2
  fi
  sleep 20
done
