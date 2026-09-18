# First session walkthrough

What happens on the first launch of `Tf.exe`, step by step — and what to do
when the app "has no API token" even though you pasted one once.

## Before you launch

- [ ] `Tf.exe` downloaded from a release tag (self-contained — no install step).
- [ ] A Deriv **demo** API token ready. Deriv now issues tokens as
  **Personal Access Tokens (PATs)** from the developer dashboard:
  1. Log in at **developers.deriv.com**.
  2. Register an application (Dashboard → register app) and choose the
     **PAT** type — that is the desktop/manual-token-entry model. This
     generates a new App ID; legacy App IDs do not work with the new APIs.
  3. In the Dashboard's **API tokens** section, create a PAT and select the
     trading scopes (e.g. `trade`, plus the read/trading-information scopes
     the dashboard offers). Copy it immediately — it cannot be viewed again.
  A PAT created on a **virtual/demo** account only touches the virtual
  balance; the app cross-checks what the API says against what it is told.
  (Tokens created under the older app.deriv.com flow keep working on the
  classic WebSocket endpoint the app uses.)
- [ ] Know your symbol (default `frxEURUSD`).

## The wizard (first launch only)

On the first launch the app shows **"Welcome to Tf — First Run Setup"**
before the main window. Five fields:

1. **API Token (demo)** — paste the token. Save is **refused** when the
   field has fewer than 8 non-blank characters (an inline error appears and
   the wizard stays open) — setup can no longer complete token-less.
2. **Symbol** — default `frxEURUSD`.
3. **Trading Brain** — the recommended default is *Growth — deterministic
   $5 challenge*; the others (Trend Following, Breakout, Mean Reversion)
   are the manual/autonomy brains.
4. **Daily Budget ($)** — default `$5.00`.
5. **Autonomy** — leave **unchecked** for a first session: the brain then
   proposes instead of trading. The market-hours checkbox can stay on.

Click **Save & Connect** to finish setup now. This persists the token
(DPAPI-encrypted in app data), writes the `wizard_done.flag` marker, and
opens the main window on the Dashboard. *Skip for now* is a legitimate
defer: it writes the flag marked `skipped` so the wizard does not reappear
every launch, and you finish the token on the **Settings** tab — but a
first session needs the token, so saving here is the fast path.

Either way the wizard **merges** into whatever settings already exist: only
its five fields are overwritten, and `IsDemo` is forced true — a
pre-configured webhook, manual stake cap or staleness alert survives
completion. Add your account in the **Accounts** tab if the wizard token
did not already cover it, then let it run a session. After the session:

```bash
python scripts/soak_report.py --record docs/soak
```

The report is your session evidence (see `docs/soak/README.md`).

## Why "no token since Sep 12" can happen

The wizard runs **once**, gated by `%APPDATA%\tf\data\wizard_done.flag`.
A machine where that flag file does not exist (setup was skipped, or the
app crashed mid-wizard) shows an old `settings.json` from a previous
build — one predating the token field — and the app looks configured but
token-less. Diagnosis:

```bash
ls -la "$APPDATA/tf/data"          # wizard_done.flag present?
cat  "$APPDATA/tf/data/settings.json"   # an ApiToken key present?
```

- No flag **and** no `ApiToken` in the JSON → the wizard never completed.
  Delete `settings.json` (optional; the wizard merges into it on save) and
  relaunch `Tf.exe` to get the wizard again.
- Flag present but token missing → set the token on the **Settings** tab
  and save; it is the same storage the wizard writes.

## Pitfalls

- **"Skip for now" defers, it does not configure.** Since the skip
  writes the completion flag, the wizard will not come back on its own —
  finish the token on the **Settings** tab. A skipped machine still
  refuses to trade (every path needs the token) but looks unconfigured
  rather than broken.
- **Save refuses an empty or too-short token** with an inline error and
  the wizard stays open — the configured-but-token-less state (the
  "no token since Sep 12" failure mode) can no longer be produced by the
  wizard itself.
- **A paste that doesn't trade is usually scopes, not the token.** The
  token needs `read` + `trade` + `trading information`; an
  insufficiently-scoped token authenticates but cannot place anything.
