# Gmail Organiser — Design Plan (Draft v0.1)

> Status: **for review**. Nothing is built yet. Comments/changes welcome on any section.

## 1. Goal

A self-hosted, single-user tool that uses a **local LLM (via Ollama)** to organise a large Gmail mailbox, with a human approving every change:

1. Suggest labels for emails in batches → user approves → labels applied → next batch.
2. Turn approved decisions into **Gmail filters** so new mail is organised automatically.
3. Review and improve the user's **existing filters and labels**.
4. Find ads / low-value mail that can be **safely trashed** (or unsubscribed from).
5. Keep the inbox focused on important mail.
6. Optionally get a **second opinion from Claude** (via the user's Claude Desktop subscription) on hard cases and label-structure plans.

Will be published as a **public GitHub project**: no personal data, no hardcoded accounts, models, URLs or label names.

### Non-goals (v1)
- Multiple Gmail accounts per install (run a second instance instead).
- Hosting on the internet / multi-user auth.
- Reading/sending replies, composing email.
- Permanent deletion (only Trash, which Gmail keeps for 30 days).

## 2. Decisions taken

| Topic | Decision |
|---|---|
| Backend | .NET 10 (LTS) ASP.NET Core Web API |
| Frontend | Angular 22 + Angular Material + Tailwind CSS, original code (admin-style layout inspired by common dashboard templates; no third-party template code) |
| Database | PostgreSQL 17 + `pgvector` (relational data + similarity memory) |
| LLM | Ollama, **running on the host** (not in Docker) so Apple Silicon GPU/MLX models work. URL + chat model + embedding model selectable in Settings |
| Hosting | `docker compose`: `web`, `api`, `db`. Only `web` is published, on `127.0.0.1:${WEB_PORT}` (default 5180); `api` and `db` stay on the compose network. A dev override publishes `db` on `127.0.0.1` for the IDE-run API |
| App login | None (localhost only). "Sign in with Google" only connects Gmail |
| Accounts | One Gmail account per install |
| Label style | Nested hierarchy, e.g. `Bills/Water`, `Property/Example-Street/Investment` |
| Time-based archiving | Stays in **Google Apps Script** (runs in Google's cloud even when the PC is off). Tool ships a generalised, configurable version of the script and can create filters that feed it |
| Scope | Inbox first, then All Mail including already-labelled mail |
| Volume | Designed for 100k+ messages: resumable background jobs, progress tracking |
| Claude integration | Optional second opinion. Local LLM does everything; user can send any item to Claude for review. Runs Claude Code headlessly in the `api` container with the user's own `setup-token` (subscription, one-click); Claude Desktop via MCP as manual fallback. No API key |

## 3. Requirement review — changes / additions I recommend

1. **Fetch and analyse are separate.** Fetching (no LLM) runs in the background in chunks; the LLM only analyses the number of emails the user asks for, tracked per email so nothing is analysed twice (§6.1–6.2). 100k+ emails × a 27B model would otherwise take days.
2. **Sender is the main unit.** Emails can be fetched and analysed per sender; once a sender's decision is approved it can be applied to the rest of that sender's emails without the LLM, and becomes a Gmail filter candidate (`from:` / `list:` criteria).
3. **Mark, don't delete.** The portal labels deletable mail `To-Be-Deleted`; the user reviews it and deletes on command, which moves mail to Trash (recoverable for 30 days). This also keeps the narrower `gmail.modify` scope.
4. **Protected mail.** Never marked `To-Be-Deleted`: any email with an attachment, starred, marked important, threads the user replied to, senders on a user-maintained allowlist.
5. **Unsubscribe option** for ad senders (uses the `List-Unsubscribe` header; one-click where supported, otherwise opens the link for the user). Trashing ads without unsubscribing just refills the inbox.
6. **Undo log.** Every applied action is recorded (message IDs + before/after labels) so a batch can be reverted.
7. **Dry-run by default** for filter creation: preview "this filter would match N existing emails" before creating it.
8. **Embedding model needed.** Memory/similarity search needs an Ollama embedding model (e.g. `nomic-embed-text` or a Qwen embedding model). Currently none installed locally — setup guide will cover `ollama pull`. Feature degrades gracefully without it (exact-sender memory still works).
9. **Google OAuth "Testing" mode expires refresh tokens after 7 days.** Setup guide will explain publishing the user's own OAuth app to "In production" (unverified is fine for personal use) to avoid weekly re-login.

## 4. Architecture

```
 Browser (localhost)
     │
 ┌───▼────────────┐   /api    ┌──────────────────────────────┐        ┌───────────────┐
 │ web (nginx +   │──────────▶│ api (.NET 10)                │──────▶ │ Gmail API     │
 │ Angular build) │◀─SignalR──│  • REST controllers          │        └───────────────┘
 └────────────────┘           │  • MCP endpoint (/mcp)       │◀────── Claude Desktop (optional,
                              │  • Background job runner     │        via local stdio bridge)
                              │  • Gmail / Ollama clients    │──────▶ Ollama on host
                              └──────────────┬───────────────┘   (host.docker.internal:11434)
                                             │
                                     ┌───────▼────────┐
                                     │ db: Postgres + │
                                     │ pgvector       │  (named volume)
                                     └────────────────┘
```

### Backend modules (one ASP.NET Core project, clean folders; split later if needed)
- **Gmail** – OAuth, sync (full + incremental via `history.list`), label/filter CRUD, batch modify, trash, unsubscribe. Rate-limit aware (Gmail quota ≈ 250 units/user/s; `messages.get` = 5 units → use batch requests + backoff).
- **Fetch** – background metadata fetch in chunks (mailbox, per sender, incremental); bodies fetched **only when an email is analysed**.
- **Senders** – per-sender stats and analysis progress; signals for clean-up (volume, read rate, has-unsubscribe, category).
- **Llm** – Ollama client (via `Microsoft.Extensions.AI` / OllamaSharp), structured JSON output with schema validation, prompt templates stored as editable files, model list from Ollama `/api/tags`.
- **Memory** – stores approved/rejected decisions + embeddings; retrieves similar past decisions to include in prompts (few-shot from the user's own choices).
- **Analysis** – user-started runs (count + scope), run queue, per-email status tracking.
- **Review** – approval workflow, apply to rest of sender, apply + undo.
- **Rules** – existing filter analysis, new filter generation, Apps Script export.
- **Mcp** – MCP server (official C# SDK, `ModelContextProtocol.AspNetCore`) exposing review tools to Claude Code (headless) and Claude Desktop (§6.7).
- **Jobs** – Postgres-backed job table + `BackgroundService`; resumable after restart; progress pushed via SignalR.

## 5. Gmail access

- User creates their **own** Google Cloud project + OAuth client (type *Web application*, redirect URI `{APP_BASE_URL}/api/auth/google/callback`; register both `http://localhost:4200/api/auth/google/callback` for the IDE run and `http://localhost:${WEB_PORT}/api/auth/google/callback` for compose). Client ID/secret entered in the Setup page (or `.env`) — never committed.
- Scopes: `gmail.modify` (read, label, archive, trash), `gmail.settings.basic` (filters), `gmail.labels`.
- Refresh token stored **encrypted** in DB (ASP.NET Data Protection; keys in a Docker volume).

## 6. Core workflows

### 6.1 Fetch (background, no LLM)
Fetching and analysis are **separate processes**. Fetch only stores email metadata; it never calls the LLM.

- **Mailbox fetch:** background job pulls metadata (headers, labels, size, snippet, has-attachment, `List-Unsubscribe`) in chunks of **100** (configurable), **Inbox first, then All Mail**. Already-stored mail gets a labels-only refresh (`format=minimal`) instead of a full re-read. Saves its page cursor after every chunk → pause/resume/restart-safe. Progress on the dashboard.
- **Sender fetch:** user enters a sender address or domain (or picks one from the Senders list) → job fetches **all** emails from that sender via Gmail search (`from:`). It runs on the shared Fetch queue, so it is **queued behind a running mailbox fetch** and starts when that finishes (one Gmail reader at a time; owner decision #92).
- **Resync labels:** background job on the Fetch queue that re-reads only the labels (and so read state) of **every stored email** in id order, in chunks, without downloading content; checkpoints the last id after every chunk, so it is resumable. Never writes Gmail and does not move the incremental fetch's history ID.
- **Incremental fetch:** after the first full pass, new/changed mail is picked up via Gmail `history.list` (also detects labels the user removed in Gmail).
- Fetching updates a **Senders** table (address, domain, display name, total emails, analysed count, applied count, last seen) so the UI can show per-sender progress.
- Already-fetched emails are upserted, never duplicated.

### 6.2 Analyse (LLM, user-started, in small batches)
Every fetched email has an **analysis status**:
`not analysed → analysed (suggestion stored) → approved / rejected → applied`

- The LLM **only runs when the user starts a run**, choosing:
  - **how many** emails (e.g. 10, 20, any number), and
  - **which**: next unanalysed from the Inbox, next unanalysed overall, or **a specific sender** (e.g. "analyse 50 from `billing@…`").
- Runs are queued and processed one at a time so the local LLM is never overloaded. A run can be cancelled; finished emails keep their results.
- An email that has been analysed is **never re-analysed automatically**. A **Re-analyse** action (on selected emails or a sender) resets them, e.g. after switching model or rejecting a suggestion.
- For efficiency, emails from the same sender in one run are sent to the LLM together (several per prompt), but each email gets **its own** suggestion and status.

**LLM input:** sender info, subject/snippet/cleaned body (truncated), current label tree, and similar past decisions from memory.
**LLM output (JSON per email):** `topicLabel` (e.g. `Bills/Water`), `isNewLabel`, `needsAction` (bool), `toBeDeleted` (bool), `unsubscribeSuggested`, `confidence`, `reason`, plus a sender-level `filterCriteria` suggestion.

### 6.3 Review & apply
- Review screen lists analysed emails grouped by sender; per item **Approve / Edit / Reject / Send to Claude**. Bulk-approve above a confidence threshold.
- **Apply to rest of sender:** after approving a sender's pattern, one button applies the same decision to **all remaining emails from that sender without the LLM**, and offers the matching Gmail filter (6.5). The user can still choose to analyse them individually instead (useful for mixed senders such as a bank sending statements and ads).
- Apply: create missing labels, `batchModify` (≤1000 IDs/call), write undo log, save decision to memory.

**Outcome labels**
| Suggestion | What the portal does |
|---|---|
| Normal mail | Topic label (e.g. `Bills/Water`) + archive (leaves inbox) |
| Needs action (e.g. invoice to pay) | Topic label **+ `Action/ToDo`**, stays in inbox. When the user removes `Action/ToDo`, it is auto-archived (6.6) |
| Deletable (ads, low value) | **`To-Be-Deleted`** label + archive. The portal never deletes on its own |

Label names `Action/ToDo` and `To-Be-Deleted` are configurable.

For the already-labelled phase, suggestions are different: *merge labels*, *move to hierarchy*, *relabel misfiled mail* — the existing label is treated as a strong signal, not overwritten.

### 6.4 Clean-up (ads / low value)
- Signals: `CATEGORY_PROMOTIONS`/`SOCIAL`, `List-Unsubscribe` present, never opened, high-volume sender, old.
- **Protected — never marked `To-Be-Deleted`:** any email **with an attachment**, starred, marked important, threads the user replied to, senders on the allowlist.
- **Clean-up page** shows everything labelled `To-Be-Deleted` (grouped by sender) for review. User can remove items from it, or run **Delete** on all/selected → **moved to Trash** (Gmail keeps Trash 30 days). Permanent deletion is not offered.
- **Unsubscribe** option per sender (one-click where the sender supports it, otherwise opens the link).

### 6.5 Rules
- **New filters** from approved senders/patterns: criteria preview + match count → create via Gmail API. Option: apply label, skip inbox, mark read, or apply the `Auto Archive` label (feeds the Apps Script).
- **Existing filter review** (deterministic checks + LLM summary): duplicates, overlapping criteria, filters targeting deleted labels, filters matching 0 recent emails, mergeable filters (`from:(a OR b)`), filters inconsistent with the new label tree. Each finding has a proposed fix to approve.
- **Label review**: empty labels, near-duplicate names, flat → nested migration plan.
- Gmail limit: ~1000 filters per account — the tool consolidates criteria where possible.

### 6.6 Apps Script (time-based archiving)
`scripts/apps-script/auto-archive.gs` — generalised version of the user's existing script, run by a time-driven trigger in Google's cloud (works when the PC is off):
- **Label rules:** config map of `label → days → archive` (the user's current `Auto Archive` behaviour), paginated (handles >500 threads).
- **Action done → archive:** threads in the inbox that have a user label but **no** `Action/ToDo` label are archived (search `in:inbox has:userlabels -label:action-todo`). So when the user removes `Action/ToDo` after handling an invoice, it leaves the inbox on the next run. Unprocessed mail (no labels yet) is untouched.
- Never trashes or deletes.
- The portal's Settings page generates the config block for copy-paste; install steps in the docs. When the portal is running, its incremental fetch also detects the removed `Action/ToDo` label and archives immediately.

### 6.7 Claude review (optional)
The local LLM handles everything by default. For any item the user can either **accept the local suggestion** or **Send to Claude** for a second opinion, using their own Claude subscription. Everything runs locally; nothing is hosted.

**Reviewer modes** (chosen in Settings; feature is off until one is configured)
| Mode | How | One-click? |
|---|---|---|
| **Claude Code (headless)** — primary | The `api` container includes the Claude Code CLI. The user runs `claude setup-token` once on their machine and puts the token in `.env` as `CLAUDE_CODE_OAUTH_TOKEN` (never committed, never shown in the UI). On "Send to Claude", the API runs `claude -p` with an MCP config pointing at its own `/mcp` endpoint, only the review MCP tools allowed (`--allowedTools`), JSON output, and a timeout. | Yes |
| **Claude Desktop** — manual fallback | Claude Desktop connects to the same `/mcp` endpoint; portal copies a ready prompt (*"Review the pending items in Gmail Organiser"*) for the user to paste. | No |

The token is only used by the Claude Code CLI, never by the portal's own code to call Anthropic directly. Each installation uses its owner's own token; the repo ships no credentials. Reviews are queued and run one at a time to stay within subscription rate limits; a failure (expired token, limit reached) marks the item "Claude unavailable" and the local suggestion is unaffected.

**What can be sent to Claude**
- A single email suggestion, a sender's suggestions, a whole run, a new-label / hierarchy plan, or a filter proposal / rule finding.
- Optional setting: auto-mark low-confidence items and new-label proposals as "suggested for Claude review" (still needs the user's click to send).

**Flow**
1. User clicks **Send to Claude** in the portal → item goes into the Claude queue.
2. Headless mode: the API starts a Claude Code run for the queue immediately. Desktop mode: user pastes the copied prompt into Claude Desktop.
3. Claude uses the MCP tools to read the item (samples, label tree, past decisions) and submits its verdict.
4. The portal shows Claude's verdict beside the local suggestion (live via SignalR). User approves either one, or edits.
5. Accepted Claude corrections are saved to memory, so the local LLM improves over time.

**How it connects**
- The API serves an MCP endpoint at `http://localhost:<port>/mcp` (official C# SDK, `ModelContextProtocol.AspNetCore`), protected by a locally generated token.
- Headless Claude Code reaches it inside the container network (`http://localhost:<port>/mcp` from within `api`).
- Claude Desktop connects via a local stdio bridge entry in `claude_desktop_config.json` (e.g. `npx mcp-remote ...`; exact command verified during M6). Settings page generates the snippet to copy-paste. Later option: one-click Claude Desktop extension (`.mcpb`).

**MCP tools**
| Tool | Purpose |
|---|---|
| `list_pending_reviews` | Queue items with the local LLM's suggestion + reason |
| `get_review_item` | Sender stats, sample emails, current label tree, similar past decisions |
| `get_label_tree` / `get_filters` | Current taxonomy and rules, with analysis findings |
| `get_label_plan` | Proposed new labels / merges / hierarchy migration with rationale |
| `submit_review` | Verdict: `agree` / `alternative` (label path, action, filter criteria) / `needs-human`, plus reasoning |
| `submit_taxonomy_feedback` | Comments or an alternative structure for a label plan |

MCP **prompts** ship with the server (`review-pending`, `review-label-plan`).

Claude's tools are review-only: applying labels, trashing and creating filters stay in the portal behind the user's approval. This also guards against instructions hidden inside email content.

## 7. Data model (main tables)

| Table | Purpose |
|---|---|
| `settings` | Ollama URL/models, batch sizes, protection rules, thresholds |
| `oauth_tokens` | Encrypted Google tokens |
| `messages` | Gmail ID, thread ID, sender, to, list-id, subject, date, labels, category, flags, has-attachment, size, snippet, **analysis status** |
| `senders` | Address, domain, name, total / analysed / applied counts, last seen, allowlisted |
| `analysis_runs` | User-started runs: scope (inbox / all / sender), requested count, status, progress |
| `suggestions` | LLM output per message (+ sender-level filter suggestion), model used, prompt version |
| `fetch_state` | Mailbox fetch cursor, last `historyId`, per-sender fetch jobs |
| `decisions` | Approved/rejected outcomes = LLM memory, with `embedding vector` |
| `action_log` | Applied changes for undo |
| `filters` | Snapshot of Gmail filters + analysis findings |
| `jobs` | Background job state, cursor, progress |
| `external_reviews` | Claude review queue + submitted verdicts, linked to suggestions / label plans / filter findings |

Email bodies are **not** stored; they are fetched when an email is analysed and discarded afterwards. A "Purge local data" button wipes the DB.

## 8. Frontend pages
1. **Setup wizard** – Google OAuth client, connect Gmail, Ollama URL, pick chat + embedding model (dropdown from Ollama), test connection.
2. **Dashboard** – fetch progress (fetched / total, pause/resume), analysis progress (not analysed / analysed / applied), `Action/ToDo` and `To-Be-Deleted` counts, running jobs.
3. **Senders** – searchable list with per-sender counts and progress; "Fetch all from sender", "Analyse N from sender".
4. **Analyse** – start a run: count (10/20/custom) + scope (Inbox / All / sender); run queue with cancel.
5. **Review** – analysed emails grouped by sender: suggestion, confidence, reason, email preview; Approve / Edit / Reject / Send to Claude / Re-analyse; "Apply to rest of sender".
6. **Clean-up** – everything labelled `To-Be-Deleted`, grouped by sender; remove from list, Delete (→ Trash), Unsubscribe.
7. **Rules** – existing filters with findings, proposed filters, label tree editor.
8. **History** – action log with undo.
9. **Settings** – models, fetch chunk size, default analysis count, label names (`Action/ToDo`, `To-Be-Deleted`), protection rules, prompt templates, purge data.
10. **Claude review** (in Settings + review UI) – choose mode (headless Claude Code / Claude Desktop), test connection, Claude Desktop config snippet; "Send to Claude" buttons in review screens; Claude's verdicts shown beside local suggestions.

Light/dark theme, collapsible side nav, responsive.

## 9. Configuration & open-source hygiene
- All config via `.env` (`.env.example` committed) + Settings page. No personal values in code, prompts, tests or seed data.
- Default Ollama URL `http://host.docker.internal:11434`; models chosen by the user — nothing defaulted to a specific model name beyond a suggestion in docs.
- Test fixtures use synthetic emails only.
- `.gitignore` covers `.env`, data volumes, keys.
- Licence: MIT (suggested).
- Docs: README quick start, Google OAuth setup guide, Ollama setup, Apps Script install.

## 10. Repository layout
```
/src/api            .NET 10 Web API
/src/api.Tests      xUnit tests (unit + Testcontainers integration)
/src/web            Angular 22 app
/scripts/apps-script
/docs               DESIGN.md, setup guides
docker-compose.yml, .env.example, README.md, LICENSE
```

Names: compose project `gmail-organiser`, C# root namespace `GmailOrganiser`, Angular project `gmail-organiser`. Default `WEB_PORT=5180`.

## 11. Delivery milestones
1. **M1 Foundation** – compose stack, DB migrations, Setup wizard, Gmail OAuth, Ollama model picker.
2. **M2 Fetch** – chunked mailbox fetch (Inbox first) with resume, sender fetch, incremental fetch, Senders page, dashboard progress.
3. **M3 Analyse & review (Inbox)** – analysis runs (count + scope), per-email status, review UI, apply to rest of sender, `Action/ToDo`, undo log, memory.
4. **M4 Claude review** – MCP server, Claude Code CLI in `api` image + headless runner, "Send to Claude" queue, verdicts in UI, setup guide (`claude setup-token`, Claude Desktop).
5. **M5 Clean-up** – `To-Be-Deleted` flow, protection rules, Delete → Trash, unsubscribe.
6. **M6 Rules & Apps Script** – filter generation, existing filter + label review, Apps Script (label rules + action-done archiving).
7. **M7 All Mail + labelled phase**, docs polish, public release.

## 12. Open questions for review
1. ~~Is the project name final for the repo?~~ **Resolved:** "Gmail Organiser", repo `gmail-organiser`.
