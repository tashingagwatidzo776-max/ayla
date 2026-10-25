# GWEN — the local Qwen confluence brain

GWEN is the DON G FX desk's **advisory confluence analyst**: a Qwen3.5-4B
checkpoint (`C:\Users\DELL\Desktop\qwem`, an abliterated
Claude-Opus-distilled reasoning variant) imported into the local **Ollama**
server as the model **`gwen-fx`** with a forex system prompt. It judges the
trades the deterministic brains already proposed — it never proposes, places,
sizes or schedules anything.

```
deterministic brains          GWEN (this doc)              rails (unchanged)
regime → alphas → sizing  →   confluence gate: PASS/POISON →  cooldown → supervisor
FX_REGIME/FX_SIGNAL          FX_GWEN (prompt + verdict)      → venue order ± 2R TP
```

## The law it lives under

Same shape as the Exit Brain and Profit Brain: **advisory with veto power**.

- GWEN can only REFUSE a dispatch the deterministic brains proposed; a
  refusal journals `FX_GWEN` (Pass=false) plus an `FX_ORDER`
  "GWEN confluence veto" row — observable, auditable, never silent.
- It runs AFTER the roster/confidence gates and BEFORE the dispatch
  cooldown, so a vetoed signal is *observed* (it still counts toward the
  paper soak) but never *throttled* — the window a qualifying signal needs
  is untouched.
- **Fail-neutral**: model down, slow, or speaking nonsense → null verdict →
  the desk trades exactly as it did before GWEN existed, announced once in
  the journal (`GWEN unavailable`). A missing LLM must never become an
  unexplained trading halt.
- Loopback-only (`127.0.0.1:11434`), same rule as Mt5BridgeClient: the model
  is a local process, never a remote endpoint.

## The gate rules (pure, tested in FxGwenAdvisorTests)

A verdict is `{bias, confidence, rr_ok, reasons}`; the veto matrix:

| verdict                     | threshold | effect   |
|-----------------------------|-----------|----------|
| bias opposes the side       | conf ≥ 45 | **veto** |
| bias flat                   | conf ≥ 50 | **veto** |
| `rr_ok: false` (no 1:2)     | conf ≥ 55 | **veto** |
| anything else               | —         | pass     |

Confidence thresholds are deliberate: opposition vetoes at the lowest bar
(directional disagreement is the strongest evidence), indecision needs more.

## The venue-side 1:2 (`VenueRewardTargetR`)

"Actually close to 1:2" rides on the ORDER: brain-placed orders carry a
take-profit at **2× the sized stop distance**, anchored where the SL is
anchored (buy → bid, sell → ask), so the venue itself banks +2R even with
the app closed. The exit brain still manages anything sooner (stops,
partials, overrides); the venue TP is the backstop. Journaled on the fill
row as `Tp` / `TpR`. Arm/disarm in Settings → "Venue 1:2 take-profit on
brain orders" (`FxVenueRrTpArmed`, default ON). A TP inside the venue's
stops_level band is skipped with a journal line rather than risking an
invalid-stops rejection of the whole order.

## Fine-tuning: what is done and what is queued

**Done now (prompt-level "forex tuning"):**
- `scripts/gwen_system_prompt.txt` — the desk's rules in the model's
  system prompt (single source of truth: the registration script AND the
  dataset builder read this exact file).
- Deterministic, compact snapshot prompts (`FxGwenAdvisor.BuildPrompt`) so
  the same market state always renders the same text.

**Queued for weights (evidence-gated, needs real GPU hardware):**
- Every assessment journals its **verbatim prompt + the model's reply +
  the trade's realized R** (`FX_GWEN` rows carry `Prompt`; outcomes
  resolve like `scripts/fx_win_rate.py`).
- `python scripts/gwen_finetune_dataset.py` turns the journal into
  `(system, user, assistant)` JSONL: winners keep the model's reply,
  stopped-out concur-visions are corrected to `flat`, vetoed/unmatched/
  unresolved rows are **dropped, never guessed**. Run it after enough
  paper cycles accumulate; a LoRA run (axolotl/llama-factory) consumes the
  file directly.
- This separation is on purpose: a 4B LLM does not become a better trader
  because we wish it to — the dataset grows from live journal evidence,
  and weights change only when the rows say the model was wrong.

## Performance on this desk

The machine is an i7-5600U (2C/4T, 16 GB RAM) — the model runs **CPU-only**
(`PARAMETER num_gpu 0`) as the full-precision f16 GGUF (~7.9 GB resident;
a q4 build stays available via `ollama create … --quantize Q4_K_M` if
latency ever matters). One assessment is
seconds of generation; the client caches each market state for 10 minutes,
so the 60 s brain cycle never waits on a repeat, and `keep_alive 5m` gives
the RAM back when the desk is idle. The `GWEN` pill in the Terminal account
bar shows the last verdict (bias, confidence, ✓/✗) or why it is off.

## Setup / operations

```powershell
# one-time: register gwen-fx (reuses the store's converted GGUF when one is
# present — the fast path that needs ~0 extra disk; a full safetensors import
# only runs as fallback and then wants ~17 GB free on the store drive)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/register-gwen-model.ps1

# later: turn the gate on/off and the venue 1:2 in Settings (applies live)
# journal evidence for the fine-tune:
python scripts/gwen_finetune_dataset.py
```

Storage note: Ollama's model store lives at `E:\ollama-models`
(`OLLAMA_MODELS` user env var) because the system drive runs at 99%.
Import saga (2026-10-22, six attempts): the safetensors pipeline always
dies in its `verifying conversion` stage with *"not enough space on the
disk"* at ~30 GB used **with 8 GB free** — and a plain user write fills
the very same volume to the last byte, so the stage's demand (not the
volume) is what breaks. The register script therefore imports **from the
already-converted f16 GGUF blob** (content-addressed store: no convert,
no verify, ~0 extra disk); scratch lands on the store drive too, never
on C:.

## Journal rows

| row           | meaning                                                        |
|---------------|----------------------------------------------------------------|
| `FX_GWEN`     | one assessment: symbol, side, alpha, verdict, reasons, latency, pass flag, **verbatim prompt** |
| `FX_GWEN`     | `Unavailable=true` — the model could not answer (fail-neutral)  |
| `FX_ORDER`    | `GWEN confluence veto …` — the refusal that stopped a dispatch  |
| `FX_ORDER`    | fill rows carry `Tp` / `TpR` — the venue-side 1:2               |
