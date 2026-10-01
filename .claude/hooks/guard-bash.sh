#!/usr/bin/env bash
# PreToolUse hook for Bash (see .claude/settings.json). Blocks the commands the team rules forbid,
# deterministically and at zero token cost, so the agent definitions don't have to repeat them.
# Exit 2 blocks the call; the message on stderr is what the agent sees. Exit 0 lets it through.
# Test: echo '{"tool_input":{"command":"npm install -g x"}}' | .claude/hooks/guard-bash.sh
set -uo pipefail
input=$(cat)
cmd=$(jq -r '.tool_input.command // empty' <<<"$input" 2>/dev/null)
[[ -n "$cmd" ]] || exit 0
# agent_type is present only when a subagent makes the call; the lead session has none.
agent=$(jq -r '.agent_type // empty' <<<"$input" 2>/dev/null)

# Match only what the shell would run: drop heredoc bodies (file content written with cat <<'EOF')
# and quoted message payloads (--body/--title/-m "..."), so a doc or comment that merely mentions a
# forbidden command is not blocked.
cmd=$(perl -0pe '
  s/<<-?\s*(["'"'"']?)(\w+)\1[^\n]*\n.*?\n\s*\2(\n|\z)/ <<HEREDOC_STRIPPED\n/sg;
  s/(--body|--body-file|--title|-m|-F body=)\s*("(?:[^"\\]|\\.)*"|'"'"'[^'"'"']*'"'"')/$1 ""/g;
' <<<"$cmd")

log="${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || echo .)}/.claude/guard.log"
block() {
  printf '%s\t%s\t%s\n' "$(date -u +%FT%TZ)" "${agent:-lead}" "${1%%[;:(]*}" >> "$log" 2>/dev/null || true
  echo "Blocked by .claude/hooks/guard-bash.sh: $1" >&2; exit 2
}
has() { grep -qE -- "$1" <<<"$cmd"; }
is_coder() { [[ "$agent" == "backend-coder" || "$agent" == "frontend-coder" ]]; }

# Global toolchain changes: use npx, project-local deps or the scratchpad; ask the lead otherwise.
has '\bnpm (install|i|add)\b.*(-g\b|--global\b)|\bcorepack (enable|prepare)\b|\bbrew (install|upgrade|uninstall)\b|\bdotnet (workload install|tool install (-g|--global))|\bpip3? install\b.*(--user|-g\b)|\bsudo\b' \
  && block "global toolchain or sudo change; use npx/project-local deps (agent rule: never change the machine's global toolchain)."

# Secrets and the owner's live data. .env holds the owner's Google and Claude tokens; the Docker
# volumes hold their mailbox index and decisions.
has '\b(cat|less|more|head|tail|grep|sed|awk|source)\b[^|;&]*(^|[ /])\.env(\.[a-z]+)?\b' && ! has '\.env\.example\b' \
  && block "reading .env; use .env.example for the variable names."
has '\bdocker (compose|volume)\b.*(\bdown\b.*(-v\b|--volumes\b)|\bvolume (rm|prune)\b)|\bdocker volume (rm|prune)\b|\bdocker system prune\b' \
  && block "deleting Docker volumes wipes the owner's local data; ask the owner."
if [[ -n "$agent" ]] && has '\bdocker compose\b.*\b(up|down|restart|stop)\b' && ! has 'COMPOSE_PROJECT_NAME=|\bdocker compose\b[^|;&]*(-p|--project-name)\b'; then
  block "docker compose without a project name targets the owner's running stack; use Testcontainers, or COMPOSE_PROJECT_NAME=gmo-<issue> with free ports."
fi

# Git safety: shared stash stack, protected main, hooks.
has '\bgit stash\b' && block "git stash is shared across worktrees; make a temporary WIP commit instead."
has '\bgit push\b.*(--force\b|-f\b|--force-with-lease\b).*\bmain\b|\bgit push\b.*\bmain\b.*(--force\b|-f\b)' && block "force-push to main."
has '\bgit push\b.*\borigin\b +(HEAD:)?main\b|\bgit push\b +origin +main\b' && block "direct push to main; open a PR."
has '\bgit (commit|push|merge)\b.*--no-verify\b' && block "--no-verify skips hooks (including the private-terms check)."
has '\bgit config\b.*\bcore\.hooksPath\b' && [[ -n "$agent" ]] && block "core.hooksPath is shared config; ask the lead."
has '\bgit checkout\b +main\b|\bgit switch\b +main\b' && has '\.claude/worktrees/' && block "switching a worktree to main."

# Merging and closing are the owner's. Subagents never do them; the lead prefixes OWNER_MERGE=1
# when the owner asked for the merge (the prefix is ignored for a subagent, so it can't self-grant).
if has '\bgh pr merge\b|\bgh (issue|pr) close\b'; then
  [[ -n "$agent" ]] && block "merging and closing are the owner's steps; report to team-lead instead."
  has '^OWNER_MERGE=1 ' || block "gh pr merge / close is the owner's step (lead: prefix OWNER_MERGE=1 when the owner asked for it)."
fi

# GitHub reads go through the shared scripts (one call, nothing truncated, no wrong-flag surprises).
has '\bgh issue view\b' && ! has '\bgh issue view\b[^|;&]*--json\b' && block "use scripts/gh/issue-context.sh <n> (or gh issue view --json <fields> --jq for one field)."
has '\bgh pr view\b' && ! has '\bgh pr view\b[^|;&]*--json\b' && block "use scripts/gh/pr-context.sh <pr> (or gh pr view --json <fields> --jq for one field)."
has '\bgh pr checks\b.*--watch\b' && block "use scripts/gh/wait-ci.sh <pr> (bounded, prints progress)."
has '\bgh run watch\b' && block "use scripts/gh/wait-ci.sh <pr>."

# Coders push and hand off; the lead reads CI. A coder fixing red CI inside its own round ran 173
# turns in the previous project (eco-review 6, proposal 1).
is_coder && has 'wait-ci\.sh|\bgh pr checks\b|\bgh run (view|list)\b' \
  && block "coders push and hand off without waiting on CI; the lead runs wait-ci.sh."

# wait-ci.sh blocks on purpose: its 540 s deadline sits under the Bash tool's 600 s cap, so the call
# always returns. Backgrounding it ends the agent's turn and the round sits idle on a green PR.
[[ "$(jq -r '.tool_input.run_in_background // empty' <<<"$input" 2>/dev/null)" == "true" ]] \
  && has 'wait-ci\.sh' \
  && block "run wait-ci.sh in the foreground (Bash timeout 600000); backgrounding it ends your turn and stalls the round."

# Small outputs, no poll loops.
has '\btail -n? ?([2-9][0-9]{2,}|[1-9][0-9]{3,})\b' && block "tail of 200+ lines; use tail -40 or grep for the message you need."
has '\bsleep +([3-9][0-9]|[1-9][0-9]{2,})\b' && block "long sleep; use scripts/gh/wait-ci.sh or a bounded wait instead of a poll loop."
has '\bwhile\b.*\bsleep\b' && has '\bgh (pr|run)\b' && block "poll loop over gh; use scripts/gh/wait-ci.sh <pr>."

exit 0
