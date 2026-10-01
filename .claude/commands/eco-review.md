---
name: eco-review
description: Periodic review of the agent development ecosystem. Measures token use and quality over the last window, compares with the previous review, and proposes at most three evidence-backed changes.
---

# Ecosystem review

Run every two weeks or ten merged PRs, on Opus. Budget: about 15 turns. Numbers come from one script; never read transcripts.

1. **Measure:** `scripts/gh/team-metrics.sh --since <previous review's comment timestamp>` (first review: `--weeks 2`).
2. **Compare:** find the "Ecosystem review log" issue (`gh issue list --search "Ecosystem review log in:title" --state all --json number --jq '.[0].number'`; create it on the first review), read its newest comment with `scripts/gh/issue-context.sh`, and build a scorecard: merged PRs/week; input tokens per merged PR; avg context per turn (coder / tester / lead); median coder turns and runs at `maxTurns`; failed rounds, escalations and tester bugs per PR; escaped defects; share of tokens on Opus + Fable; CI minutes per run; context budget (`wc -c CLAUDE.md .claude/agents/custom/*.md`).
3. **Diagnose the three most expensive runs:** classify each as (a) large tool output kept in context, (b) retries or rediscovery, (c) spec ambiguity or wrong premise, (d) issue too big, (e) legitimate size. One line each.
4. **Levers (yes/no with the number):** a rule with no incident behind it → delete; a hook rule that never fired in two windows → delete; a prose rule agents keep breaking → make it a hook or script; `maxTurns` hit often → issues too big (BA); never hit → lower it; autocompact 25 % still right; a failed round on a platform fact → add it to the design-check list.
5. **Propose at most three changes:** symptom (number) → change → expected effect (number) → how the next review measures it. Prefer hook or script over doc over prose.
6. **Record:** one comment on the log issue (scorecard, diagnosis, proposals); tell the owner the proposals in at most ten lines; implement the picked ones through a PR.
