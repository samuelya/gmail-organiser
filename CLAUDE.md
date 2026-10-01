# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository. It is in every agent's context: shared rules live here once, agent definitions stay short.

## Project

Self-hosted, single-user Gmail organiser: a local LLM (Ollama) suggests labels, clean-up and Gmail filters in user-started batches; the user approves every change. Optional second opinion from Claude via MCP. **Public repo.** Design: `docs/DESIGN.md` (read the section your issue names, not the whole file).

- `src/api` — .NET 10 ASP.NET Core Web API: Gmail, Fetch, Senders, Analysis, Llm, Memory, Review, Rules, Mcp, Jobs (background jobs + SignalR).
- `src/web` — Angular 22 + Angular Material + Tailwind (original code; no third-party template code).
- `db` — PostgreSQL 17 + pgvector. Ollama runs on the **host**, reached at `host.docker.internal:11434`.
- `scripts/apps-script` — Google Apps Script for time-based archiving.
- `docker compose`: `web`, `api`, `db`, all bound to `127.0.0.1`.

## Rules

- Do what the issue asks; nothing more. Prefer editing existing files; no new docs unless asked. Keep files under 500 lines.
- **Never use the owner's real mailbox, tokens or data.** Development and tests use the fake Gmail client and a fake `IChatClient`; integration tests use Testcontainers Postgres. CI has no Ollama and no Google login. What only a real mailbox can show goes to the owner (see "Owner check").
- **Nothing personal or hardcoded.** No email addresses, label names, sender names, model names, URLs or IDs in code, prompts, tests or fixtures; use `example.com` and synthetic data. Config comes from `.env` (`.env.example` lists the names) and the Settings page. The `.githooks/pre-commit` hook blocks the owner's private terms.
- **Gmail safety.** The portal never deletes: deletable mail gets the `To-Be-Deleted` label; "Delete" moves it to Trash. Never request the full `https://mail.google.com/` scope. Every Gmail mutation writes the undo log.
- Validate input at system boundaries. Never commit secrets or `.env`.
- NEVER add a `Co-Authored-By` trailer to commits.

## Team

Run `/team-up` at the start of an implementation session; `/eco-review` every two weeks or ten merged PRs. Read the pinned issue "Architecture & conventions (read first)" before round 1 of any issue.

| Agent | Model | Owns | Never |
|---|---|---|---|
| `business-analyst` | `claude-opus-5-5` (Fable for an epic's design) | issues, epics, sub-issues, Project fields, "BA log" | edits code, closes issues |
| `backend-coder` | `claude-opus-5-5` | `src/api/**`, `src/api.Tests/**`, `docker-compose*.yml`, `.github/workflows/**`, `scripts/apps-script/**` | touches `src/web`, merges |
| `frontend-coder` | `claude-opus-5-5` | `src/web/**` | touches api, merges |
| `tester` | `claude-sonnet-5-5` (Opus for escalated, security or data PRs) | PR verification, `type:bug` issues | fixes code, merges |
| lead (this session) | `claude-opus-5-5`; `/model` Fable only for an escalation or design decision | orchestration, `/code-review`, merges on the owner's say-so | codes in an agent's worktree |

Definitions: `.claude/agents/custom/`. The guard hook `.claude/hooks/guard-bash.sh` blocks the mechanical rules (global installs, `sudo`, reading `.env`, deleting Docker volumes, agents' `docker compose` without a project name, `git stash`, pushes to `main`, `--no-verify`, merges/closes, `gh issue|pr view` without `--json`, coders waiting on CI, CI watch/poll loops, long `tail`/`sleep`). A blocked call is not a failure: do what the message says.

**Pipeline:** owner picks an issue → coder in its own worktree (`.claude/worktrees/gmo-<issue>`, branch `feat/<issue>-<slug>`) → PR `Closes #n` → `/code-review` (effort scaled to risk) → fresh coder fixes findings → lead `scripts/gh/wait-ci.sh <pr>` (red: `ci-failures.sh <pr> --post`, fresh coder) → tester on green CI → fresh coder fixes → owner merges.

- **Agent names** (`team-metrics.sh` counts runs by them): `abe-<issue>`, `afe-<issue>`, `atest-<pr>`, `aba-<topic>`; add `-r<n>` for later rounds.
- **Tester scope:** runs for PRs that change Gmail mutations, data (entities, migrations, memory), LLM output handling, MCP tools or security. Skipped for small fixes and docs, where review plus green CI is enough; the lead says which in the merge note.
- **Start gates:** check `ListAgents` before starting an agent on a worktree; never two writers in one worktree. Start the tester only on the final PR head SHA; if the head moves, stop it and restart.
- **Rounds:** every coder run ends with `scripts/gh/round.sh <issue> passed|failed|review-fix|polish|escalation <n>/2 <model> "<details>"`. Two attempts per tier: Opus 5.5 ×2 → Fable 5.1 ×2 (label `escalated:fable`) → owner (`needs-owner`). Skip the second attempt when the first failed on a wrong approach. Red CI is a `review-fix` round and doesn't count against the tier.
- **Owner escalation (`needs-owner`):** permission-blocked actions (secrets, Google Cloud setup, global toolchain), changes to approved decisions, scope or cost changes.
- **Escaped defects:** filed `type:bug` + `found-after-merge` with `Escaped from #<pr>`.
- **Owner check:** anything agents can't verify (the real Gmail account and Google OAuth consent, real Ollama models, Claude Desktop, Apps Script in Google's cloud) is stated plainly and listed in the merge note for the owner. Never build a proxy and call it verified.

GitHub helpers: `scripts/gh/issue-context.sh <n>`, `pr-context.sh <pr>`, `set-status.sh <issue> "<Backlog|Ready|In progress|In review|Done>"`, `link-sub-issue.sh <parent> <child>`, `wait-ci.sh <pr>` (foreground, Bash timeout 600000; exit 0 green, 1 failed, 2 still running: run again), `ci-failures.sh <pr> [--post]`, `team-metrics.sh`. Shared config (`CLAUDE.md`, `.claude/`, `.githooks/`, root files) is lead-only and changes via PR.

## Cost discipline

Cost = context × turns. Keep both small.

- **Caps:** `maxTurns` per agent (coders 120, tester 60, BA 100); autocompact at 25 %. Commit WIP checkpoints so a capped run loses nothing.
- **Small issues:** at most ~15 changed files per issue; the BA splits anything bigger than M.
- **One round per agent.** A fresh agent per round, pointed at the issue and PR comments; GitHub is the shared memory. Fix rounds read only the issue, `pr-context.sh` and the files the findings name.
- **Coders don't run the full test suite or wait on CI.** They write tests, run the build and lint plus the tests for the code they touched, push and hand off. The tester never duplicates CI.
- **Read narrowly:** grep before reading, read line ranges, batch independent calls, keep tool output short.
- **Design check before coding** for platform semantics: Gmail `historyId`/sync, batch limits and quota, filter criteria, OAuth refresh-token expiry, EF Core migrations, pgvector, job resumability, SignalR, MCP transport, Claude Code headless. Post three sentences on the issue: the approach, and why it works in the failure case. Measure rather than argue.
- **Short messages:** agent → lead at most 5 lines; detail goes in the issue or PR. The owner sees the feature working before polish rounds. Bundle disjoint small bugs into one PR.
- **No rediscovery:** anything two agents had to work out goes into the docs they read, not lead memory.
- **Lead starts fresh** (`/clear`) after each merge; state is on GitHub.

## Build & Test

To be filled in by the M1 foundation issue with the real commands (build, lint, run a single test, compose up). CI and required checks: `docs/ci.md` (created with the first workflow).
