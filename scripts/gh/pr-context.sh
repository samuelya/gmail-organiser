#!/usr/bin/env bash
# Usage: scripts/gh/pr-context.sh <pr-number>
# Everything an agent needs from a PR in one call: head branch and SHA, merge state, linked issues,
# a CI summary (every check that is not green is listed with its link), changed files (not the
# diff), the body, every comment, reviews with text, and unresolved review threads. Nothing is
# truncated; HTML comments and resolved threads are dropped (the resolved count is shown).
set -euo pipefail
source "$(dirname "$0")/_lib.sh"

pr="${1:?PR number required}"
require_number "$pr"

json=$(gql -F num="$pr" -f owner="$OWNER" -f repo="$REPO" -f query='
  query($owner: String!, $repo: String!, $num: Int!) {
    repository(owner: $owner, name: $repo) {
      pullRequest(number: $num) {
        number title state isDraft url body
        headRefName headRefOid baseRefName mergeable mergeStateStatus
        closingIssuesReferences(first: 10) { nodes { number title state } }
        files(first: 100) { totalCount nodes { path additions deletions } }
        commits(last: 1) {
          nodes { commit { statusCheckRollup {
            state
            contexts(first: 50) { nodes {
              __typename
              ... on CheckRun { name conclusion status detailsUrl }
              ... on StatusContext { context state targetUrl }
            } }
          } } }
        }
        comments(last: 100) {
          totalCount
          pageInfo { hasPreviousPage }
          nodes { author { login } createdAt body }
        }
        reviews(last: 50) { nodes { author { login } state submittedAt body } }
        reviewThreads(last: 100) { nodes {
          isResolved path line
          comments(first: 20) { nodes { author { login } body } }
        } }
      }
    }
  }')

jq -e '.data.repository.pullRequest' >/dev/null <<<"$json" || { echo "PR #$pr not found" >&2; exit 1; }

jq -r '
  def when: .createdAt // .submittedAt // "" | .[0:16] | sub("T"; " ");
  .data.repository.pullRequest as $p
  | ($p.commits.nodes[0].commit.statusCheckRollup // {state: "NONE", contexts: {nodes: []}}) as $ci
  | [$ci.contexts.nodes[]
      | if .__typename == "CheckRun"
        then {name, result: (.conclusion // .status), url: .detailsUrl}
        else {name: .context, result: .state, url: .targetUrl} end] as $checks
  | [$checks[] | select(.result | IN("SUCCESS", "SKIPPED", "NEUTRAL") | not)] as $notGreen
  | [$p.reviewThreads.nodes[] | select(.isResolved | not)] as $open
  | "PR #\($p.number) [\($p.state)\(if $p.isDraft then ", draft" else "" end)] \($p.title)",
    $p.url,
    "head: \($p.headRefName) @ \($p.headRefOid)  base: \($p.baseRefName)",
    "merge: \($p.mergeable) / \($p.mergeStateStatus)",
    "closes: \([$p.closingIssuesReferences.nodes[] | "#\(.number) [\(.state)]"] | join(", ") | if . == "" then "-" else . end)",
    "CI: \($ci.state) (\($checks | length) checks, \([$checks[] | select(.result == "SUCCESS")] | length) passed, \([$checks[] | select(.result == "SKIPPED")] | length) skipped)",
    ($notGreen[] | "  \(.result): \(.name) \(.url // "")"),
    "",
    "=== files (\($p.files.totalCount)) ===",
    ($p.files.nodes[] | "\(.path) +\(.additions) -\(.deletions)"),
    (if $p.files.totalCount > 100 then "!!! only the first 100 files listed; see: gh pr diff \($p.number) --name-only" else empty end),
    "",
    "=== body ===",
    ($p.body | if . == "" then "(empty)" else . end),
    ($p.comments as $c
      | $c.nodes | to_entries[]
      | "", "=== comment \(.key + 1 + $c.totalCount - ($c.nodes | length))/\($c.totalCount) · \(.value.author.login // "ghost") · \(.value | when) ===",
        .value.body),
    (if $p.comments.pageInfo.hasPreviousPage
      then "", "!!! only the newest 100 of \($p.comments.totalCount) comments shown; read the rest with: gh api repos/'"$OWNER/$REPO"'/issues/\($p.number)/comments --paginate"
      else empty end),
    ($p.reviews.nodes[] | select(.body != "")
      | "", "=== review \(.state) · \(.author.login // "ghost") · \(when) ===", .body),
    (if ($open | length) > 0 or ($p.reviewThreads.nodes | length) > 0
      then "", "=== unresolved review threads: \($open | length) (resolved: \(($p.reviewThreads.nodes | length) - ($open | length))) ==="
      else empty end),
    ($open[] | "", "--- \(.path):\(.line // "?") ---",
      (.comments.nodes[] | "[\(.author.login // "ghost")] \(.body)"))
' <<<"$json" | clean_md
