#!/usr/bin/env bash
# Usage: scripts/gh/set-status.sh <issue-number> "<Status option name>"
# One GraphQL read (issue item + project + Status options) and one write, so parallel agents
# stay clear of GitHub's secondary rate limit (the old version listed every project item).
set -euo pipefail
source "$(dirname "$0")/_lib.sh"

issue="${1:?issue number required}"
status="${2:?status name required (Backlog|Ready|In progress|In review|Done)}"
[[ "$issue" =~ ^[0-9]+$ ]] || { echo "Issue number must be numeric: $issue" >&2; exit 1; }


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
          field(name: "Status") {
            ... on ProjectV2SingleSelectField { id options { id name } }
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
field_id=$(jq -r '.field.id' <<<"$project")
option_id=$(jq -r --arg s "$status" '.field.options[] | select(.name == $s) | .id' <<<"$project")
[[ -n "$option_id" ]] || { echo "Status option '$status' not found" >&2; exit 1; }

item_id=$(jq -r --arg p "$project_id" \
  '.data.repository.issue.projectItems.nodes[] | select(.project.id == $p) | .id' <<<"$lookup")

if [[ -z "$item_id" ]]; then
  item_id=$(gql -f project="$project_id" -f content="$issue_id" -f query='
    mutation($project: ID!, $content: ID!) {
      addProjectV2ItemById(input: { projectId: $project, contentId: $content }) { item { id } }
    }' | jq -r '.data.addProjectV2ItemById.item.id')
fi

gql -f project="$project_id" -f item="$item_id" -f field="$field_id" -f option="$option_id" -f query='
  mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
    updateProjectV2ItemFieldValue(input: {
      projectId: $project, itemId: $item, fieldId: $field,
      value: { singleSelectOptionId: $option }
    }) { projectV2Item { id } }
  }' >/dev/null

echo "#$issue -> $status"
