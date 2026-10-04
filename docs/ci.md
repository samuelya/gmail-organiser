# CI

GitHub Actions, `.github/workflows/`. Every workflow has `permissions: contents: read` and every job has `timeout-minutes`.

## Workflows and jobs

| Workflow | File | Trigger | Job | What it does |
|---|---|---|---|---|
| Secret scan | `secret-scan.yml` | every PR, push to `main` | `Secret scan` | gitleaks over the full history |
| CI | `ci.yml` | every PR, push to `main` | `api` | setup-dotnet from `global.json`, restore (NuGet cache), `dotnet format --verify-no-changes`, `dotnet build -warnaserror`, apt-installs Tesseract (OCR tests), `dotnet test` (Testcontainers uses the runner's Docker) |
| CI | `ci.yml` | every PR, push to `main` | `web` | in `src/web`: setup-node from `.nvmrc` (npm cache), `npm ci`, `npm run lint`, `npm test`, `npm run build` |
| CI | `ci.yml` | every PR, push to `main` | `apps-script` | setup-node from `.nvmrc`, `node --test 'scripts/apps-script/*.test.mjs'`: loads `auto-archive.gs` through `node:vm` with a fake `GmailApp` (no dependencies, no Google login) |
| Images | `images.yml` | PR / push to `main` that changes `src/*/Dockerfile*`, `src/web/.dockerignore`, `src/web/nginx.conf`, `global.json`, `Directory.Build.props`, `src/api/**/*.csproj`, `src/web/package*.json`, `src/web/angular.json`, `docker-compose*.yml` or `images.yml` | `images` | `docker build` of the api and web images, plus the api image with `WITH_CLAUDE=true` (checks `claude --version`), no push |
| Publish | `publish.yml` | push of a `v*.*.*` tag, or manual run (`workflow_dispatch`) | `publish api`, `publish web` (not required) | Builds linux/amd64 + linux/arm64 and pushes `ghcr.io/<owner>/gmail-organiser-{api,web}`. Job has `packages: write` (GITHUB_TOKEN). Tags: `vX.Y.Z` gives `X.Y.Z`, `X.Y`, `latest`; a pre-release `vX.Y.Z-rc.N` gives only `X.Y.Z-rc.N`; a manual run gives `sha-<short>` and `<branch>`, never `latest` |

A new push to a PR cancels that PR's previous run.

Release: `git tag vX.Y.Z && git push origin vX.Y.Z` on `main`; Publish pushes both images and passes `VERSION=X.Y.Z` to the api image, which `/healthz` and the Settings footer show (a manual run and a local build show the `Directory.Build.props` version). GHCR packages start private. Steps: "Release checklist" below.

## Required checks

Branch protection on `main` requires **`Secret scan`**, **`api`** and **`web`**. `images` is not required: it only runs when its paths change. `apps-script` runs on every PR but is not required until the owner adds it to the branch protection rule.

Branch protection matches checks by job name. **Keep these names stable.** Renaming a job (or its `name:`) makes the required check wait forever; change the branch protection rule in the same step.

## Run the same commands locally

API (repository root; .NET SDK per `global.json`):

```sh
dotnet restore GmailOrganiser.slnx
dotnet format GmailOrganiser.slnx --verify-no-changes --no-restore   # lint; drop --verify-no-changes to fix
dotnet build GmailOrganiser.slnx -warnaserror --no-restore
dotnet test --solution GmailOrganiser.slnx                           # integration tests need Docker running; OCR tests skip without tesseract
dotnet test --filter "FullyQualifiedName~<Name>"                     # single test or class
```

Web (`src/web`; Node per `.nvmrc`):

```sh
npm ci
npm run lint
npm test
npm test -- --include src/app/app.spec.ts                            # single spec file
npm run build
```

Apps Script (repository root; Node per `.nvmrc`; Node 22+ takes a glob, not a directory):

```sh
node --test 'scripts/apps-script/*.test.mjs'
```

Images (repository root; same as the `images` job):

```sh
docker build -f src/api/Dockerfile -t gmail-organiser-api:ci .
docker build -f src/web/Dockerfile -t gmail-organiser-web:ci src/web
docker build -f src/api/Dockerfile --build-arg WITH_CLAUDE=true -t gmail-organiser-api:ci-claude .   # with the Claude Code CLI
```

`docker compose -p <project> up --build` tags its images `<project>-api:local` and `<project>-web:local` (the default project keeps `gmail-organiser-*:local`), so agent and tester stacks need nothing extra; `API_IMAGE`/`WEB_IMAGE` override the tag.

## End-to-end (Playwright, local only)

Not part of CI, by decision: it moves into a workflow only if an escaped defect shows review plus unit tests are not enough.

Precondition: the API runs with `GMAIL_FAKE=true` and `LLM_FAKE=true` (deterministic fake models, no Ollama) on 5181 (the `GmailOrganiser.Api` launch profile) against the dev db. The spec checks `/healthz` first and stops with a clear message if the API is not up. It starts `ng serve` on 4200 itself (or reuses one already running) and is safe to rerun on the same database.

```sh
npx playwright install chromium   # once, into the user cache
npm run e2e                       # headless, Chromium
npm run e2e:ui                    # Playwright UI mode
```

Other ports: `E2E_BASE_URL=http://localhost:<web>` and `E2E_API_URL=http://localhost:<api>` (the dev server then proxies to that API, which must list the web origin in `Security__AllowedOrigins__0`). With `E2E_API_URL` set, the spec always starts its own dev server and fails if the web port is already in use, because a running `npm start` would proxy to 5181 instead.

`npm run e2e` runs only the `chromium` project, so it never runs the screenshot spec.

### Screenshots

The README images in `docs/images/<page>.png` come from the same fake stack (`GMAIL_FAKE=true`, `LLM_FAKE=true`), so they show only synthetic `example.com` data. With the API up as above, `npm run screenshots` (in `src/web`) picks the fake models, fetches, runs an analysis, approves and applies the first review group in the UI, applies a second run so Clean-up has content, syncs the filters, and overwrites the eight images at 1440×900 in the light theme. It reruns on a reused database, but a fresh one gives the fullest pictures. On other ports, leave `App__BaseUrl` at its development default so the Settings image shows the default redirect URI (`localhost:4200`). Not part of CI.

## What CI does not have

- **No Ollama.** LLM code is tested with the fake `IChatClient` / `IEmbeddingGenerator`.
- **No Google login.** Gmail code is tested with `FakeGmailClient`; nothing in CI talks to a real mailbox.
- No secrets beyond `GITHUB_TOKEN` (used by the secret scan).

What only the real mailbox, real Ollama models or Google's cloud can show is listed as an "Owner check" on the PR.

## Release checklist

The owner follows it top to bottom for every release; agents prepare the version bump PR but never tag, publish or create the release.

1. **Scope done.** Every sub-issue of the release's milestone epics is Done, and no open `found-after-merge` bug has priority P1: `gh issue list --label found-after-merge --state open`.
2. **`main` green.** The latest `main` run has `Secret scan`, `api` and `web` green.
3. **Owner checks verified** on the real mailbox, real Ollama models, Claude Desktop and Google's cloud. The lists by epic:
   - **M1 #4, M2 #21, M3 #22, M4 #23, M5 #24, attachments #68:** the owner-check lists on the epic issues (collected from their merge notes).
   - **M2 Fetch (#21), later PRs:** #251 Inbox and All mail totals match Gmail; #252 a second mailbox fetch is faster and picks up Gmail label/read changes; #253 "Resync labels" picks up label and read changes (note its duration); #255 the Dashboard loads (`GET /api/fetch/status` 200).
   - **M6 Rules & Apps Script (#25):** #261 filter sync count and summaries; #262 `auto-archive.gs` dry run, real run and `installDailyTrigger`, nested label with a space via the `-` form; #268 label plan over the real labels (report absurd nest/merge proposals); #269 create a filter from a proposal (incl. `-has:attachment`), delete and restore it, preview estimate vs local count; #272 Apps Script guide against Google's current UI; #285 filter review spot-check, apply one merge or drop_label; #286 nested rename, `labels.patch` clash answer, merge and undo; #303 filter review summary with the real model within 120 s; #316 Claude Desktop `review-label-plan` prompt and one headless run on a filter finding; #327 Send to Claude on a finding and a plan, Accept / Keep local.
   - **M7 All Mail, labelled phase & release (#26):** #257 purge local data with a real connection; #259 labelled-scope analysis mostly proposes "keep"; #267 move and relabel apply removes the old label, undo restores it; #320 / #322 Settings → Reconnect → consent → back on Settings, and cancel shows the error.
   - **Document-type labels (#236; merged parts ship in the release):** #278 the real model's format grammar accepts `documentTypeLabel`; #287 analysis-v4 with a document-type parent (reset a custom prompt override first); #291 approve/apply with a new and an existing document-type label, nested under the parent, undo; #296 set the parent in Settings; #301 Claude proposes a different document type; #313 / #328 re-analyse a run and single emails, Use new, apply, undo.
   - **Release hygiene (#206), still open:** complete `.env.example`; on the main checkout `git log -p --all | grep -icF -f .claude/private-terms.txt` prints `0` (else decide on a history rewrite before going public); read the README as the public will.
4. **Screenshots.** If the UI changed since the last release, re-run `npm run screenshots` (see "Screenshots") and commit the images.
5. **Version bump.** One PR "chore: release vX.Y.Z" sets `<Version>` in `Directory.Build.props` and `version` in `src/web/package.json` (and `package-lock.json`).
6. **Tag** on the merged `main`: `git tag vX.Y.Z && git push origin vX.Y.Z`.
7. **Publish green**, then on a clean machine with only `docker-compose.yml` and a `.env` made from `.env.example` with `API_IMAGE` / `WEB_IMAGE` set to the `ghcr.io/<owner>/gmail-organiser-{api,web}:X.Y.Z` images (README): `docker compose pull && docker compose up -d`, run the setup wizard with the real Google client, and check Settings shows "Gmail Organiser vX.Y.Z".
8. **GitHub release:** `gh release create vX.Y.Z --generate-notes`.
9. **GHCR visibility:** if the repository is public, set both `gmail-organiser-api` and `gmail-organiser-web` packages to public (Package settings → Change visibility).
10. **Docs:** update the pinned issue #3 and the `docs/DESIGN.md` header for the released version.
