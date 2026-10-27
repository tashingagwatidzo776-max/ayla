#!/usr/bin/env python3
"""Alpha Researcher step 1: ask the local LLM for one alpha proposal.

Offline by design: this script talks ONLY to the LLM daemon (Ollama on
127.0.0.1:11435 by default, OpenAI-compatible /v1/chat/completions — the
desk's BUILT-IN host serves that dialect directly, no third-party daemon) and
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
import subprocess
import sys
import time
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
PROPOSALS = HERE / "proposals"

DEFAULT_BASE_URL = os.environ.get("TF_LLM_BASE_URL", "http://127.0.0.1:11435/v1")
DEFAULT_MODEL = os.environ.get("TF_LLM_MODEL", "gwen-fx")

# The desk's built-in GWEN host. It is spawn-on-demand inside the app
# (GwenHostSupervisor), so an external caller into a quiet desk hits a
# dead port until something in-app has consulted it first.
HOST_PORT = 11435
HOST_READY_DEADLINE_S = 90


def _host_root(base_url: str) -> str | None:
    """Origin of the LOCAL desk endpoint, or None for a hosted provider
    (nothing local to ensure when TF_LLM_BASE_URL points off-box)."""
    url = base_url.rstrip("/")
    if "127.0.0.1" not in url and "localhost" not in url:
        return None
    return url[:-3] if url.endswith("/v1") else url


def _health_ok(base_url: str) -> bool:
    root = _host_root(base_url)
    if root is None:
        return True   # hosted provider: its reachability is its own
    try:
        with urllib.request.urlopen(f"{root}/health", timeout=3) as response:
            return response.status == 200
    except Exception:  # noqa: BLE001 - "not up yet" is the normal answer
        return False


def _resolve_host_exe() -> str | None:
    env = os.environ.get("TF_GWEN_HOST_EXE")
    if env:
        return env if pathlib.Path(env).exists() else None
    repo = pathlib.Path(__file__).resolve().parents[2]   # scripts/ai_alpha/ -> root
    candidates = [
        pathlib.Path(r"C:\Users\DELL\Desktop\DongGfx\app\DongGfx.GwenHost.exe"),
        repo / "publish" / "win-x64" / "DongGfx.GwenHost.exe",
        repo / "src" / "DongGfx.GwenHost" / "bin" / "Release" / "net8.0" / "win-x64" / "DongGfx.GwenHost.exe",
        repo / "src" / "DongGfx.GwenHost" / "bin" / "Release" / "net8.0" / "DongGfx.GwenHost.exe",
    ]
    return next((str(c) for c in candidates if c.exists()), None)


def _resolve_gguf() -> str | None:
    env = os.environ.get("TF_GWEN_GGUF")
    if env:
        return env if pathlib.Path(env).exists() else None
    # Mirror GwenHostSupervisor.ResolveGguf: Q4_K_M is canonical on this
    # 15 GB desk, f16 where RAM allows, legacy import last.
    candidates = [
        r"E:\DonGFX\models\gwen-fx.Q4_K_M.gguf",
        r"E:\DonGFX\models\gwen-fx.f16.gguf",
        r"E:\ollama-models\blobs\sha256-1f08950590a395a944ef92a6f21024387348a8d75ed0c388b1cab1590762e95a",
    ]
    return next((p for p in candidates if pathlib.Path(p).exists()), None)


def ensure_host(base_url: str) -> subprocess.Popen | None:
    """Bring the desk's GWEN host up when nothing serves it yet.

    Starts the SAME exe+args the in-app supervisor uses, waits for
    /health (the model still loads lazily inside the first chat), and
    returns the process so main() can stop exactly what it started.
    A host that was already up - the app's own - is left alone (None).
    """
    if _host_root(base_url) is None:
        return None
    if _health_ok(base_url):
        return None
    exe = _resolve_host_exe()
    gguf = _resolve_gguf()
    if exe is None or gguf is None:
        raise RuntimeError(
            "GWEN host unavailable: "
            + ("DongGfx.GwenHost.exe not found" if exe is None else "gwen-fx GGUF not found")
        )
    proc = subprocess.Popen([exe, gguf, "--port", str(HOST_PORT)],
                            cwd=str(pathlib.Path(exe).parent))
    deadline = time.time() + HOST_READY_DEADLINE_S
    while time.time() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(f"GwenHost exited with rc={proc.returncode} before /health")
        if _health_ok(base_url):
            return proc
        time.sleep(1)
    stop_host(proc)
    raise RuntimeError(f"GWEN host did not become healthy in {HOST_READY_DEADLINE_S}s")


def stop_host(proc: subprocess.Popen | None) -> None:
    """Kill ONLY a host this script started - never the app's own."""
    if proc is None:
        return
    try:
        if proc.poll() is None:
            proc.kill()
            proc.wait(timeout=15)
    except Exception:  # noqa: BLE001 - cleanup must never fail the loop
        pass

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
# 860s: this desk decodes at ~0.8 t/s (2026-10-25 bench: warm verdict
# ~245 s, cold 781 s) and the host is single-flight behind the live gate,
# so a proposal pays queue wait + prompt eval + a few hundred generated
# tokens - the old 540s budget died mid-generation on the 2026-10-26 run.
# 860 s stays under the host's 900 s GenerationDeadline and OVER
# soak_evening.py's subprocess budget, which is raised to 920 s to match.
def call_llm(base_url: str, model: str, api_key: str | None, timeout: float = 860.0) -> str:
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
    host_proc = None
    try:
        # Spawn-on-demand: a quiet desk has nothing on 11435 yet, and the
        # call below would just hit a dead port. Bring the host up first;
        # stop_host() then kills exactly what WE started.
        host_proc = ensure_host(args.base_url)
        reply = call_llm(args.base_url, args.model, args.api_key)
        proposal = validate(extract_json(reply))
    except Exception as exc:  # noqa: BLE001 - report, never crash the loop
        print(f"propose failed: {exc}", file=sys.stderr)
        return 1
    finally:
        stop_host(host_proc)

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
