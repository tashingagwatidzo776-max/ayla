# Secrets in Infisical

This runbook moves this checkout's real credentials out of machine-local files
and into [Infisical](https://infisical.com), so they are stored once instead of
on every operator's disk. It is written against this repo's actual layout — a
Windows .NET 8 WPF desktop app, a Python MT5 sidecar, Python CI scripts, and
GitHub Actions — rather than the generic quickstart.

Upstream references: [quickstart](https://infisical.com/docs/documentation/platform/secrets-mgmt/quick-starts/deliver-first-secret),
[CLI install](https://infisical.com/docs/cli/overview),
[`infisical run`](https://infisical.com/docs/cli/commands/run),
[machine identities](https://infisical.com/docs/documentation/platform/identities/machine-identities),
[Universal Auth](https://infisical.com/docs/documentation/platform/identities/universal-auth),
[secret scanning](https://infisical.com/docs/cli/scanning-overview).

> Status: the CLI is installed on this machine (v0.43.137 via npm, shims on
> PATH) and `docs/secrets-infisical.md` is linked in the README. The remaining
> steps need only an Infisical account: login, project creation, secret import,
> and the wrapped start command.
> 
> (Original pre-install note: none of the one-time steps had been run in this
> checkout — they need an Infisical account and the CLI, neither of which
> existed on this machine.)

## 0. What this project reads (and where secrets live today)

**There is no `.env` file in this repository, and there never was.** The
quickstart's "drag your `.env` in" step has nothing to drag here, so the table
below is the inventory to import instead.

The app reads its LLM endpoint plus a few operator/test variables from the
environment, and one credential from `settings.json`:

| Variable | Secret? | Read by | Stored today |
|---|---|---|---|
| `TF_LLM_BASE_URL` | no — endpoint URL | `JournalAnalystService`, `RiskNarratorService`, `scripts/ai_alpha/propose.py` | env only; default `http://127.0.0.1:11434/v1` (local Ollama) |
| `TF_LLM_MODEL` | no — model name | same three call sites | env only; default `qwen3:0.6b` |
| `TF_LLM_API_KEY` | **only if** you point `TF_LLM_BASE_URL` at a hosted provider | same three call sites | not set anywhere (local Ollama ignores it) |
| `FLAKE_WEBHOOK_URL` | **yes** — anyone holding it can post | `scripts/flake_tracker.py` | GitHub repo secret, forwarded by `ci.yml` / `flake-tracker.yml` |
| `METRICS_WEBHOOK_URL` | **yes** — same | `metrics-digest.yml` | GitHub repo secret |
| `GH_TOKEN` | credential, but minted per run | `scripts/generate_health.py`, `scripts/check_mergeability.py`, `scripts/restore_protection.py` | GitHub Actions `secrets.GITHUB_TOKEN` (nothing to migrate) |
| `GITHUB_REPOSITORY`, `PAGES_RUN_ID`, `PAGES_RUN_CONCLUSION` | no | `scripts/generate_health.py` | Actions-provided |
| `GATE` | no — coverage gate number | `scripts/generate_trend.py` | env only; default `60` |
| `TF_DATA_DIR` | no — data-dir redirect | `SettingsService.DataDir`, `scripts/ci-local.ps1` | set by the local gate only |
| `TF_TESTS_ALLOW_LIVE_DATADIR`, `TF_UIA_PID` | no — test/CI switches | test suites | set by tests/CI |

`%APPDATA%\tf\data\settings.json` is **not** an env file and is not migrated
wholesale: it holds operator settings (FX brain caps, terminal path, toggles)
that the WPF app reads through `SettingsService`, and only one field there is a
credential — `WebhookUrl` (the Discord/Slack webhook). The migration below
covers the environment-variable surface; moving `WebhookUrl` out of
`settings.json` would be a deliberate change to `SettingsService`/`App.xaml.cs`
and is a separate decision, not a CLI step.

So the real scope is small: the LLM endpoint pair (configuration, not a
credential) and, once a hosted LLM or a shared webhook is involved,
`TF_LLM_API_KEY` and the two webhook URLs.

## 1. Pick the target

The delivery method differs per target, so decide first:

| Target | Delivery |
|---|---|
| **Local development** (this machine, the soak session) | interactive `infisical login` + `infisical run` (steps 3–5) |
| **CI/CD** (GitHub Actions) | machine identity + Universal Auth, client ID/secret in GitHub repo secrets (step 6) |
| **Kubernetes / production** | machine identity + Universal Auth (or the operator); never an interactive login |

The rest of this document defaults to **local development with `--env=dev`**,
with the CI/CD path called out in step 6.

## 2. Create the account and project

1. Sign up at <https://app.infisical.com> (or your self-hosted URL).
2. **Secrets Management → + Add New Project**, name it after the service —
   `dongfx` matches this repo. Every project starts with **Development**,
   **Staging**, and **Production** environments.
3. Add the secrets. You can drag and drop a `.env` file onto the Secrets
   Overview page to import everything at once; in this repo that file is the
   `.env.example` template in the repo root (placeholder values only — replace
   them in the Infisical UI, never in the file). Otherwise press
   **+ Add a New Secret** and type each key from the table in section 0.
4. Keep the **Development** values non-production: `TF_LLM_BASE_URL` should
   stay `http://127.0.0.1:11434/v1` unless you deliberately point dev at a
   hosted provider.

## 3. Install the CLI and authenticate

Install per <https://infisical.com/docs/cli/overview>. On this machine
(Windows) the options are:

```powershell
winget install infisical
# or
scoop bucket add org https://github.com/Infisical/scoop-infisical.git
scoop install infisical
# or, per-user with Node.js (no admin rights; used on this machine —
# the npm package ships the native infisical.exe and npm writes the shims)
npm install -g @infisical/cli
```

Then authenticate:

```powershell
infisical login
```

`infisical login` opens a browser. In WSL 2, a Codespace, or a remote SSH
session with no browser, use the interactive-shell flow instead:

```powershell
infisical login -i
```

Tip: if CLI invocations hang at startup, it is usually the update check
phoning home — `export INFISICAL_DISABLE_UPDATE_CHECK=true` (or set it as a
user environment variable) skips it; Infisical recommends the same for
production.

## 4. Link the codebase

From the repo root:

```powershell
infisical init
```

This writes `.infisical.json` in the working directory. It holds local project
settings only — **no secrets** — and is safe to commit, so the whole team links
the same project by pulling it. (`infisical run --project-config-dir=<dir>`
handles monorepo layouts where the file lives elsewhere.)

## 5. Inject secrets at runtime

The wrapped command replaces the raw start command. This repo has no
`package.json`, `Makefile`, `Procfile`, or `Dockerfile` CMD to edit: its start
commands are the ones in the README, so the README's dev run line is now the
wrapped form:

```powershell
# the app (launches the pinned MT5 terminal, starts bridge polling)
infisical run --env=dev -- dotnet run --project src/DongGfx.App

# the loopback sidecar — reads no secrets, so it stays unwrapped
python bridge/mt5_sidecar.py
```

`--env=dev` selects the environment (accepted values are the project's
environment slugs: `dev`, `staging`, `prod`). The application keeps reading
`Environment.GetEnvironmentVariable` exactly as before, so no application code
changes. `--watch` (re-injects on secret change, restarting the child) is fine
for development and not recommended in production.

Only wrap processes that actually need secrets; wrapping the sidecar or the
test gate buys nothing and adds an Infisical login to their critical path.

## 6. CI/CD, Kubernetes, and production

Do **not** use interactive login outside local development. Instead:

1. **Access Control → Machine Identities → Create** an identity (e.g.
   `dongfx-ci`), and add it to the `dongfx` project with the least-privileged
   project role that can read the secrets it needs.
2. Configure **Universal Auth** on it and mint a **Client Secret**. The Client
   ID is non-sensitive; the Client Secret is the credential.
3. Store the pair in the platform's own secret store — for GitHub Actions that
   is repo/org secrets, alongside the existing `FLAKE_WEBHOOK_URL` and
   `METRICS_WEBHOOK_URL`, e.g. `INFISICAL_CLIENT_ID` and
   `INFISICAL_CLIENT_SECRET`.
4. Exchange them for a token at run time. A machine identity requires an
   explicit `--projectId`:

   ```bash
   export INFISICAL_TOKEN=$(infisical login --method=universal-auth \
     --client-id="$INFISICAL_CLIENT_ID" \
     --client-secret="$INFISICAL_CLIENT_SECRET" --silent --plain)
   infisical run --projectId=<project-id> --env=prod -- <command>
   ```

   `INFISICAL_DISABLE_UPDATE_CHECK=true` is recommended in production.
5. Scope the identity to the minimum project *and* environment it needs (a CI
   identity that only reads `dev`/`staging` should not hold a `prod` role), and
   give it a short token period. For workloads that must bootstrap with no
   static credential, use Universal Auth **periodic tokens** instead of a
   long-lived Client Secret.

The GitHub-side migration is: replace the `FLAKE_WEBHOOK_URL` /
`METRICS_WEBHOOK_URL` repo secrets with a machine-identity login step that
injects them from Infisical, then delete the old repo secrets.

## 7. Verify it works

There is no `.env` here to rename to `.env.backup`, so the equivalent proof is
that the *environment* — not a file on disk — is what the process receives:

```powershell
# prints a length, never the value
infisical run --env=dev -- pwsh -NoProfile -Command '"TF_LLM_MODEL length: " + $env:TF_LLM_MODEL.Length'

# same check from the Python side (what scripts/ai_alpha reads)
infisical run --env=dev -- python -c "import os; print('TF_LLM_MODEL length:', len(os.environ.get('TF_LLM_MODEL','')))"
```

Then start the app through the wrapper and confirm it comes up and still runs
its analyst cycle — the journal records one `AI_CALL` entry per cycle with the
model name and `llm`-or-`template` source, which proves the injected endpoint
was read. If `TF_LLM_API_KEY` is in the environment, verify its length the same
way.

Roll back by closing the app and re-running the plain `dotnet run` command; the
wrapper only affects the process it launches.

## 8. Cleanup and rotation

- `.env` and its variants are now ignored (`.gitignore`), while `.env.example`
  stays tracked as the template. Never commit, echo, or paste real values.
- **If the webhook URL or an LLM key was ever committed, rotate it now** —
  rewriting git history is the only way to remove it, so the value itself is
  the thing that must change. Both webhook URLs are paste-to-post credentials:
  delete the webhook in Discord/Slack and create a new one.
- Scan the tree and history for anything that leaked:

  ```powershell
  infisical scan .
  ```

  See <https://infisical.com/docs/cli/scanning-overview>.

## Day-to-day

```powershell
infisical run --env=dev -- dotnet run --project src/DongGfx.App   # app + analyst

# list key names only — never echo values into a transcript or a log
infisical secrets --env=dev --plain | ForEach-Object { ($_ -split '=')[0] }

infisical secrets set TF_LLM_MODEL=... --env=dev                   # update a secret
```

`scripts/ci-local.ps1` runs with `--env=dev`-injected variables only when you
invoke it that way; it needs no secrets itself (it sets `TF_DATA_DIR` to a
scratch directory so test teardowns never touch `%APPDATA%\tf\data`).
