#!/usr/bin/env bash
# Usage: scripts/gh/issue-context.sh <issue-number>
# Everything an agent needs from an issue in one call: title, state, labels, parent, sub-issues,
# linked PRs, the full body and every comment, in order. Nothing is truncated; only HTML comments
# are dropped. Replaces `gh issue view <n> --comments`, which outside a terminal prints the
# comments only (no title, no body) and prints nothing at all for an issue without comments.
set -euo pipefail
source "$(dirname "$0")/_lib.sh"

issue="${1:?issue number required}"
require_number "$issue"

json=$(gql -F num="$issue" -f owner="$OWNER" -f repo="$REPO" -f query='
  query($owner: String!, $repo: String!, $num: Int!) {
    repository(owner: $owner, name: $repo) {
      issue(number: $num) {
        number title state url body
        labels(first: 30) { nodes { name } }
        parent { number title state }
        subIssues(first: 50) { nodes { number title state } }
        closedByPullRequestsReferences(first: 10, includeClosedPrs: true) {
          nodes { number title state headRefName }
        }
        comments(last: 100) {
          totalCount
          pageInfo { hasPreviousPage }
          nodes { author { login } createdAt body }
        }
      }
    }
  }')

jq -e '.data.repository.issue' >/dev/null <<<"$json" || { echo "Issue #$issue not found" >&2; exit 1; }

jq -r '
  .data.repository.issue as $i
  | "#\($i.number) [\($i.state)] \($i.title)",
    $i.url,
    "labels: \([$i.labels.nodes[].name] | join(", ") | if . == "" then "-" else . end)",
    (if $i.parent then "parent: #\($i.parent.number) [\($i.parent.state)] \($i.parent.title)" else empty end),
    (if ($i.subIssues.nodes | length) > 0
      then "sub-issues:", ($i.subIssues.nodes[] | "  #\(.number) [\(.state)] \(.title)")
      else empty end),
    (if ($i.closedByPullRequestsReferences.nodes | length) > 0
      then "linked PRs:", ($i.closedByPullRequestsReferences.nodes[] | "  #\(.number) [\(.state)] \(.headRefName) \(.title)")
      else empty end),
    "",
    "=== body ===",
    ($i.body | if . == "" then "(empty)" else . end),
    ($i.comments as $c
      | $c.nodes | to_entries[]
      | "", "=== comment \(.key + 1 + $c.totalCount - ($c.nodes | length))/\($c.totalCount) · \(.value.author.login // "ghost") · \(.value.createdAt[0:16] | sub("T"; " ")) ===",
        .value.body),
    (if $i.comments.pageInfo.hasPreviousPage
      then "", "!!! only the newest 100 of \($i.comments.totalCount) comments shown; read the rest with: gh api repos/'"$OWNER/$REPO"'/issues/\($i.number)/comments --paginate"
      else empty end)
' <<<"$json" | clean_md
