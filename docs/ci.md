# CI

GitHub Actions, `.github/workflows/`. Every workflow has `permissions: contents: read` and every job has `timeout-minutes`.

## Workflows and jobs

| Workflow | File | Trigger | Job | What it does |
|---|---|---|---|---|
| Secret scan | `secret-scan.yml` | every PR, push to `main` | `Secret scan` | gitleaks over the full history |
| CI | `ci.yml` | every PR, push to `main` | `api` | setup-dotnet from `global.json`, restore (NuGet cache), `dotnet format --verify-no-changes`, `dotnet build -warnaserror`, `dotnet test` (Testcontainers uses the runner's Docker) |
| CI | `ci.yml` | every PR, push to `main` | `web` | in `src/web`: setup-node from `.nvmrc` (npm cache), `npm ci`, `npm run lint`, `npm test`, `npm run build` |
| Images | `images.yml` | PR / push to `main` that changes `src/*/Dockerfile*`, `src/web/.dockerignore`, `src/web/nginx.conf`, `docker-compose*.yml` or `images.yml` | `images` | `docker build` of the api and web images, no push |

A new push to a PR cancels that PR's previous run.

## Required checks

Branch protection on `main` requires **`Secret scan`**, **`api`** and **`web`**. `images` is not required: it only runs when its paths change.

Branch protection matches checks by job name. **Keep these names stable.** Renaming a job (or its `name:`) makes the required check wait forever; change the branch protection rule in the same step.

## Run the same commands locally

API (repository root; .NET SDK per `global.json`):

```sh
dotnet restore GmailOrganiser.slnx
dotnet format GmailOrganiser.slnx --verify-no-changes --no-restore   # lint; drop --verify-no-changes to fix
dotnet build GmailOrganiser.slnx -warnaserror --no-restore
dotnet test --solution GmailOrganiser.slnx                           # integration tests need Docker running
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

Images (repository root; same as the `images` job):

```sh
docker build -f src/api/Dockerfile -t gmail-organiser-api:ci .
docker build -f src/web/Dockerfile -t gmail-organiser-web:ci src/web
```

## What CI does not have

- **No Ollama.** LLM code is tested with the fake `IChatClient` / `IEmbeddingGenerator`.
- **No Google login.** Gmail code is tested with `FakeGmailClient`; nothing in CI talks to a real mailbox.
- No secrets beyond `GITHUB_TOKEN` (used by the secret scan).

What only the real mailbox, real Ollama models or Google's cloud can show is listed as an "Owner check" on the PR.
