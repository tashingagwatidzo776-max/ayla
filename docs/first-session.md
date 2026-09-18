# First session walkthrough

What happens on the first launch of `Tf.exe`, step by step — and what to do
when the app "has no API token" even though you pasted one once.

## Before you launch

- [ ] `Tf.exe` downloaded from a release tag (self-contained — no install step).
- [ ] A Deriv **demo** API token ready: Deriv → Settings → API token, scopes
  `read`, `trade`, `trading information`. Demo tokens come from a **virtual**
  account; the app cross-checks what the API says against what it is told.
- [ ] Know your symbol (default `frxEURUSD`).

## The wizard (first launch only)

On the first launch the app shows **"Welcome to Tf — First Run Setup"**
before the main window. Five fields:

1. **API Token (demo)** — paste the token. Save is accepted when the field
   has at least 8 non-blank characters.
2. **Symbol** — default `frxEURUSD`.
3. **Trading Brain** — the recommended default is *Growth — deterministic
   $5 challenge*; the others (Trend Following, Breakout, Mean Reversion)
   are the manual/autonomy brains.
4. **Daily Budget ($)** — default `$5.00`.
5. **Autonomy** — leave **unchecked** for a first session: the brain then
   proposes instead of trading. The market-hours checkbox can stay on.

Click **Save & Connect** (not *Skip for now* — see pitfalls below). This
persists the token (DPAPI-encrypted in app data), writes the
`wizard_done.flag` marker, and opens the main window on the Dashboard.
Add your account in the **Accounts** tab if the wizard token did not
already cover it, then let it run a session. After the session:

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
  Delete `settings.json` (optional; the wizard overwrites it on save) and
  relaunch `Tf.exe` to get the wizard again.
- Flag present but token missing → set the token on the **Settings** tab
  and save; it is the same storage the wizard writes.

## Pitfalls

- **"Skip for now" is not a save.** It closes the wizard without writing
  the flag or the token — the wizard reappears on every launch until a
  real save happens (or the flag is written by hand; avoid that, it can
  strand you token-less with no wizard).
- **The wizard save replaces settings wholesale** (it constructs a fresh
  settings object with only the five wizard fields). Anything configured
  *before* completing the wizard — webhook, governor cap, arm-staleness —
  should be re-checked on the Settings tab afterwards.
- **A paste that doesn't trade is usually scopes, not the token.** The
  token needs `read` + `trade` + `trading information`; an
  insufficiently-scoped token authenticates but cannot place anything.
