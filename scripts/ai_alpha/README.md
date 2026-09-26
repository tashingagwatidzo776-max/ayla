# Alpha Researcher — offline AI alpha proposal harness

AI agent 5 of `docs/ai-agent-program.md`. The loop, in order:

1. **`propose.py`** asks the local LLM (Ollama/Qwen3 by default) for one
   alpha hypothesis in a fixed JSON schema → `proposals/PROP-*.json`.
2. **`backtest.py`** deterministically replays that proposal over bars
   (your exported CSV, or `--synthetic` for a smoke run) →
   `results/PROP-*.report.json`. The LLM never grades its own work.
3. **A human reads the report** and, only if it survives review, ports the
   alpha as deterministic C# into `src/DongGfx.Core/Fx/FxAlphas.cs` — where
   it inherits the per-symbol paper soak, the supervisor veto, scorecard
   grading and audit coverage every alpha has.

## Rails (non-negotiable)

- This directory has **no** access to the app, the bridge, the sidecar or
  any webhook. It reads bar files you export by hand and writes JSON.
- The model proposes; the backtester measures; **the human merges**. No
  step of this harness can place, size, or schedule a trade.
- A proposal is *never* auto-promoted. The C# port is a reviewed PR.

## Usage

```bash
# 1. propose (needs the Ollama daemon on 127.0.0.1:11434)
python scripts/ai_alpha/propose.py

# 2. backtest the newest proposal against exported bars…
python scripts/ai_alpha/backtest.py proposals/PROP-xxxx.json --bars data/bars.csv
#    …or just prove the pipeline with a synthetic random walk:
python scripts/ai_alpha/backtest.py proposals/PROP-xxxx.json --synthetic 500 --seed 7
```

Bar CSV columns: `ts,open,high,low,close` (MT5 export or tick-archive
aggregation; header row required).
