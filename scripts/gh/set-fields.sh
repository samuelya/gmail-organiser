#!/usr/bin/env bash
# Usage: scripts/gh/set-fields.sh <issue-number> "<Field>=<Option>" ["<Field>=<Option>" ...]
# Sets single-select Project fields (Status, Size, Priority, Phase) on an issue, adding it to the
# Project first if needed. One GraphQL read for the issue, fields and options, then one write per
# field, so callers never look up field or option ids themselves.
# Example: scripts/gh/set-fields.sh 57 Size=M Priority=P1 "Phase=M2 Fetch" Status=Ready
set -euo pipefail
source "$(dirname "$0")/_lib.sh"

issue="${1:?issue number required}"
shift
(( $# > 0 )) || { echo "At least one Field=Option pair required" >&2; exit 1; }
require_number "$issue"
for pair in "$@"; do
  [[ "$pair" == ?*=?* ]] || { echo "Expected Field=Option, got: '$pair'" >&2; exit 1; }
  [[ "$pair" == "Status=Ready" ]] && check_ready_size "$issue"
done

lookup=$(gql -F num="$issue" -f owner="$OWNER" -f repo="$REPO" -f title="$PROJECT_TITLE" -f query='
  query($owner: String!, $repo: String!, $num: Int!, $title: String!) {
    repository(owner: $owner, name: $repo) {
      issue(number: $num) {
        id
        projectItems(first: 20) { nodes { id project { id } } }
      }
    }
    user(login: $owner) {
      projectsV2(first: 20, query: $title) {
        nodes {
          id
          title
          fields(first: 50) {
            nodes { ... on ProjectV2SingleSelectField { id name options { id name } } }
          }
        }
      }
    }
  }')

issue_id=$(jq -r '.data.repository.issue.id // empty' <<<"$lookup")
[[ -n "$issue_id" ]] || { echo "Issue #$issue not found" >&2; exit 1; }

project=$(jq -c --arg t "$PROJECT_TITLE" '.data.user.projectsV2.nodes[] | select(.title == $t)' <<<"$lookup")
[[ -n "$project" ]] || { echo "Project '$PROJECT_TITLE' not found" >&2; exit 1; }
project_id=$(jq -r .id <<<"$project")

# Resolve every pair before writing anything, so a typo leaves the item untouched.
writes=()
for pair in "$@"; do
  name="${pair%%=*}"
  value="${pair#*=}"
  field=$(jq -c --arg n "$name" '.fields.nodes[] | select(.name == $n)' <<<"$project")
  [[ -n "$field" ]] || { echo "Single-select field '$name' not found" >&2; exit 1; }
  option_id=$(jq -r --arg v "$value" '.options[] | select(.name == $v) | .id' <<<"$field")
  [[ -n "$option_id" ]] || {
    echo "Option '$value' not found for '$name'; options: $(jq -r '[.options[].name] | join(", ")' <<<"$field")" >&2
    exit 1
  }
  writes+=("$(jq -r .id <<<"$field") $option_id")
done

item_id=$(jq -r --arg p "$project_id" \
  '.data.repository.issue.projectItems.nodes[] | select(.project.id == $p) | .id' <<<"$lookup")

if [[ -z "$item_id" ]]; then
  item_id=$(gql -f project="$project_id" -f content="$issue_id" -f query='
    mutation($project: ID!, $content: ID!) {
      addProjectV2ItemById(input: { projectId: $project, contentId: $content }) { item { id } }
    }' | jq -r '.data.addProjectV2ItemById.item.id')
fi

for w in "${writes[@]}"; do
  gql -f project="$project_id" -f item="$item_id" -f field="${w%% *}" -f option="${w#* }" -f query='
    mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
      updateProjectV2ItemFieldValue(input: {
        projectId: $project, itemId: $item, fieldId: $field,
        value: { singleSelectOptionId: $option }
      }) { projectV2Item { id } }
    }' >/dev/null
done

echo "#$issue -> $*"
