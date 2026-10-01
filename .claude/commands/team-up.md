---
name: team-up
description: Readiness check for the Gmail organiser agent team (business-analyst, backend-coder, frontend-coder, tester) before an implementation session
---

# Team up

Run at the start of an implementation session. One Bash call for the reads, then one table.

1. `gh auth status`: scopes include `repo` and `project`.
2. `.claude/agents/custom/`: the four definitions exist; read each one's `model:` line only (`grep -m1 '^model:'`).
3. Guard hook: `.claude/settings.json` has the `PreToolUse` `Bash` entry for `.claude/hooks/guard-bash.sh`, and the script is executable. Private-terms hook: `git config core.hooksPath` is `.githooks` and `.claude/private-terms.txt` exists. Say first if either is missing.
4. Open escalations, owner first: `gh issue list --label needs-owner`, then `--label escalated:fable`.
5. Open PRs (`gh pr list`) and leftover worktrees (`git worktree list`): flag any worktree whose PR is merged and offer to remove it.
6. Print a table: agent | default model | escalation tier.

Models: lead, coders and BA on `claude-opus-5-5`; tester on `claude-sonnet-5-5`; escalation tier `claude-fable-5-1`, then the owner.
