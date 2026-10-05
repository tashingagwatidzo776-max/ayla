#!/usr/bin/env python3
"""Alpha Researcher step 1: ask the local LLM for one alpha proposal.

Offline by design: this script talks ONLY to the LLM daemon (Ollama on
127.0.0.1:11434 by default, OpenAI-compatible /v1/chat/completions) and
writes a JSON proposal file. No bridge, no sidecar, no webhook, no trading
surface — the harness cannot place a trade even in principle.

The proposal is data, not code: the backtester decides what it means, and a
human decides what ships. See scripts/ai_alpha/README.md for the loop.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import sys
import time
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
PROPOSALS = HERE / "proposals"

DEFAULT_BASE_URL = os.environ.get("TF_LLM_BASE_URL", "http://127.0.0.1:11434/v1")
DEFAULT_MODEL = os.environ.get("TF_LLM_MODEL", "qwen3:0.6b")

SCHEMA_HINT = """{
  "name": "short_snake_case_name",
  "hypothesis": "one sentence: what market behavior this exploits",
  "symbol_hint": "one FX symbol from: XAUUSDmicro, EURUSD, GBPUSD, USDJPY",
  "direction": "long_or_short",
  "entry": {"indicator": "donchian|sma_cross|rsi", "params": {"window": 20}},
  "exit": {"indicator": "donchian|sma_cross|fixed_bars", "params": {"window": 10, "bars": 24}},
  "risk": {"stop_bars": 2.0}
}"""

SYSTEM_PROMPT = (
    "You propose mechanical forex trading hypotheses for offline backtesting. "
    "Answer with ONE JSON object only - no markdown, no commentary. Use exactly this schema:\n"
    + SCHEMA_HINT
)

USER_PROMPT = (
    "Propose one alpha for a deterministic backtester. The available indicator "
    "vocabulary is: donchian channel breakout (window), simple moving average "
    "cross (fast_window, slow_window), RSI threshold (window, level). "
    "The strategy must be fully mechanical and unambiguous."
)


# 540s: on the 2-core soak box the model cold-loads inside this request
# (~180s) and then generates (~250s warm) - measured. The old 60s budget
# could never succeed there and made every alpha run report "propose
# failed". Must stay under soak_evening.py's 600s subprocess timeout.
def call_llm(base_url: str, model: str, api_key: str | None, timeout: float = 540.0) -> str:
    body = json.dumps({
        "model": model,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": USER_PROMPT},
        ],
        "temperature": 0.8,
        "max_tokens": 700,  # thinking models burn budget reasoning first
    }).encode()
    request = urllib.request.Request(
        f"{base_url.rstrip('/')}/chat/completions",
        data=body,
        headers={"Content-Type": "application/json",
                 **({"Authorization": f"Bearer {api_key}"} if api_key else {})},
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        payload = json.loads(response.read().decode())
    return payload["choices"][0]["message"]["content"]


def extract_json(text: str) -> dict:
    """Best-effort extraction of the JSON object from an LLM reply.
    Rejects anything that is not one clean object - the backtester only
    consumes structured data.

    Reasoning models (qwen3) wrap their answer in <think>...</think> and
    the thinking itself is full of braces, so think blocks are stripped
    first; then the fenced block, the outer brace slice, and a raw_decode
    scan are tried in order. raw_decode at each '{' survives prose around
    the object and trailing commentary."""
    text = re.sub(r"<think>.*?</think>", "", text, flags=re.S)
    text = re.sub(r"<think>.*\Z", "", text, flags=re.S)  # truncated thinking
    fenced = re.search(r"```(?:json)?\s*(\{.*?\})\s*```", text, re.S)
    candidates = [fenced.group(1)] if fenced else []
    if "{" in text and "}" in text:
        candidates.append(text[text.find("{"): text.rfind("}") + 1])
    decoder = json.JSONDecoder()
    for candidate in candidates:
        try:
            parsed = json.loads(candidate)
            if isinstance(parsed, dict):
                return parsed
        except json.JSONDecodeError:
            continue
    def acceptable(parsed: object) -> bool:
        # The aggressive raw_decode scan must not accept a stray nested
        # fragment (e.g. {"indicator": ...}); only a proposal-shaped dict
        # counts. The fenced/outer-slice candidates stay permissive.
        return isinstance(parsed, dict) and "entry" in parsed and "exit" in parsed

    for start in [m.start() for m in re.finditer(r"\{", text)][:50]:
        try:
            parsed, _ = decoder.raw_decode(text[start:])
            if acceptable(parsed):
                return parsed
        except json.JSONDecodeError:
            continue
    raise ValueError("no JSON object found in the LLM reply")


def normalize_indicator(value) -> str:
    """Map LLM phrasing onto the fixed indicator enum. Deterministic string
    preprocessing - the model still proposes, this just translates."""
    text = str(value).lower()
    if "donchian" in text or "channel" in text or "breakout" in text:
        return "donchian"
    if "rsi" in text or "relative strength" in text:
        return "rsi"
    if "sma" in text or "moving average" in text or "cross" in text:
        return "sma_cross"
    return text.strip()


def validate(proposal: dict) -> dict:
    """Schema-validate the fields the backtester will rely on."""
    required = ["name", "hypothesis", "direction", "entry", "exit"]
    missing = [k for k in required if k not in proposal]
    if missing:
        raise ValueError(f"proposal missing fields: {missing}")
    proposal["name"] = re.sub(r"[^a-z0-9_]", "_", str(proposal["name"]).lower())[:40]
    if proposal["direction"] not in ("long", "short", "long_or_short"):
        raise ValueError(f"bad direction: {proposal['direction']!r}")
    proposal["entry"]["indicator"] = normalize_indicator(proposal["entry"].get("indicator"))
    proposal["exit"]["indicator"] = normalize_indicator(proposal["exit"].get("indicator"))
    if proposal["entry"]["indicator"] not in ("donchian", "sma_cross", "rsi"):
        raise ValueError(f"bad entry indicator: {proposal['entry']}")
    if proposal["exit"].get("indicator") not in ("donchian", "sma_cross", "fixed_bars"):
        raise ValueError(f"bad exit indicator: {proposal['exit']}")
    return proposal


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--model", default=DEFAULT_MODEL)
    parser.add_argument("--api-key", default=os.environ.get("TF_LLM_API_KEY"))
    parser.add_argument("--out", help="write to this file instead of proposals/PROP-*.json")
    args = parser.parse_args()

    print(f"proposing via {args.model} @ {args.base_url} ...")
    started = time.time()
    try:
        reply = call_llm(args.base_url, args.model, args.api_key)
        proposal = validate(extract_json(reply))
    except Exception as exc:  # noqa: BLE001 - report, never crash the loop
        print(f"propose failed: {exc}", file=sys.stderr)
        return 1

    PROPOSALS.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    path = pathlib.Path(args.out) if args.out else PROPOSALS / f"PROP-{stamp}-{proposal['name']}.json"
    path.write_text(json.dumps({
        **proposal,
        "model": args.model,
        "proposed_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "llm_latency_ms": int((time.time() - started) * 1000),
    }, indent=2) + "\n", encoding="utf-8")

    print(f"proposal: {path}")
    print(f"  {proposal['name']}: {proposal['hypothesis'][:110]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
