#!/usr/bin/env bash
# Usage: scripts/gh/set-status.sh <issue-number> "<Status option name>"
# Shorthand for scripts/gh/set-fields.sh <issue> "Status=<name>".
set -euo pipefail

issue="${1:?issue number required}"
status="${2:?status name required (Backlog|Ready|In progress|In review|Done)}"
exec "$(dirname "$0")/set-fields.sh" "$issue" "Status=$status"
