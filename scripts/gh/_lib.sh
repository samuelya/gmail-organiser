# Shared helpers for scripts/gh/*.sh. Source it; don't run it.

# Owner/repo come from the checkout's GitHub remote, so the scripts work unchanged in any fork.
# Override with GH_REPO=owner/name when needed.
_nwo="${GH_REPO:-$(gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null)}"
[[ "$_nwo" == */* ]] || { echo "Cannot resolve the GitHub repo; run inside the checkout or set GH_REPO=owner/name" >&2; exit 1; }
OWNER="${_nwo%%/*}"
REPO="${_nwo#*/}"
# GitHub Project (v2) whose Status field scripts/gh/set-status.sh updates.
PROJECT_TITLE="${GH_PROJECT_TITLE:-Gmail Organiser}"

# gh api graphql with retries on rate-limit responses only, with a growing wait.
gql() {
  local attempt out
  for attempt in 1 2 3 4; do
    if out=$(gh api graphql "$@" 2>&1); then
      printf '%s' "$out"
      return 0
    fi
    if grep -qi 'rate limit' <<<"$out" && (( attempt < 4 )); then
      echo "GitHub rate limit hit; retrying in $(( attempt * 20 ))s..." >&2
      sleep $(( attempt * 20 ))
      continue
    fi
    grep -o 'gh: .*' <<<"$out" >&2 || echo "$out" >&2
    return 1
  done
}

require_number() {
  [[ "$1" =~ ^[0-9]+$ ]] || { echo "Expected a number, got: '$1'" >&2; exit 1; }
}

# Drops HTML comments (invisible on GitHub, so nobody reading the issue sees them either)
# and collapses runs of blank lines.
clean_md() {
  perl -0pe 's/<!--.*?-->//gs; s/\r//g; s/\n{3,}/\n\n/g'
}
