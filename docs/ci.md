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

Release: `git tag vX.Y.Z && git push origin vX.Y.Z` on `main`; Publish pushes both images. GHCR packages start private.

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

## End-to-end (Playwright, local only)

Not part of CI, by decision: it moves into a workflow only if an escaped defect shows review plus unit tests are not enough.

Precondition: the API runs with `GMAIL_FAKE=true` on 5181 (the `GmailOrganiser.Api` launch profile) against the dev db. The spec checks `/healthz` first and stops with a clear message if the API is not up. It starts `ng serve` on 4200 itself (or reuses one already running) and is safe to rerun on the same database.

```sh
npx playwright install chromium   # once, into the user cache
npm run e2e                       # headless, Chromium
npm run e2e:ui                    # Playwright UI mode
```

Other ports: `E2E_BASE_URL=http://localhost:<web>` and `E2E_API_URL=http://localhost:<api>` (the dev server then proxies to that API, which must list the web origin in `Security__AllowedOrigins__0`). With `E2E_API_URL` set, the spec always starts its own dev server and fails if the web port is already in use, because a running `npm start` would proxy to 5181 instead.

## What CI does not have

- **No Ollama.** LLM code is tested with the fake `IChatClient` / `IEmbeddingGenerator`.
- **No Google login.** Gmail code is tested with `FakeGmailClient`; nothing in CI talks to a real mailbox.
- No secrets beyond `GITHUB_TOKEN` (used by the secret scan).

What only the real mailbox, real Ollama models or Google's cloud can show is listed as an "Owner check" on the PR.
