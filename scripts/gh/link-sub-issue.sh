#!/usr/bin/env bash
# Usage: scripts/gh/link-sub-issue.sh <parent-issue> <child-issue>
# Links <child> as a sub-issue of <parent>. Safe to re-run: an existing link is reported, not an error.
set -euo pipefail
source "$(dirname "$0")/_lib.sh"

parent="${1:?parent issue number required}"
child="${2:?child issue number required}"
require_number "$parent"
require_number "$child"

info=$(gql -F num="$child" -f owner="$OWNER" -f repo="$REPO" -f query='
  query($owner: String!, $repo: String!, $num: Int!) {
    repository(owner: $owner, name: $repo) { issue(number: $num) { databaseId parent { number } } }
  }')
child_id=$(jq -r '.data.repository.issue.databaseId // empty' <<<"$info")
[[ -n "$child_id" ]] || { echo "Issue #$child not found" >&2; exit 1; }

current=$(jq -r '.data.repository.issue.parent.number // empty' <<<"$info")
if [[ "$current" == "$parent" ]]; then
  echo "#$child is already a sub-issue of #$parent"
  exit 0
elif [[ -n "$current" ]]; then
  echo "#$child already has parent #$current; an issue can have only one parent" >&2
  exit 1
fi

gh api -X POST "repos/$OWNER/$REPO/issues/$parent/sub_issues" -F sub_issue_id="$child_id" >/dev/null
echo "#$child -> sub-issue of #$parent"
