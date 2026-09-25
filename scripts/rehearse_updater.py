#!/usr/bin/env python3
"""End-to-end rehearsal of the DON G FX updater against a fake release.

Drills the real update path in a sandbox under bin-verify/rehearsal/:
fake loopback release server -> AutoUpdater check/download/checksum ->
stage -> install into a sandbox app dir -> real restart.bat (tasklist-
verified relaunch, self-delete on success). No repo files, no
%APPDATA%, no real install are touched.

Run:  python scripts/rehearse_updater.py [--clean]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
ROOT = REPO / "bin-verify" / "rehearsal"

APP_DIR = ROOT / "app"              # sandbox "installed" app
UPDATE_DIR = ROOT / "app" / "updates"   # the updater's own dir (inside sandbox)
STAGED = UPDATE_DIR / "staged"
LOG = ROOT / "rehearsal.log"

OLD_VERSION, NEW_VERSION = "1.0.0", "9.9.9"
CHECK_URL = "http://127.0.0.1:0/releases/latest"  # port bound at runtime


def log(msg: str) -> None:
    line = f"[{time.strftime('%H:%M:%S')}] {msg}"
    print(line, flush=True)
    LOG.parent.mkdir(parents=True, exist_ok=True)
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def fail(stage: str, detail: str) -> int:
    log(f"FAIL {stage}: {detail}")
    print(f"\nREHEARSAL FAILED at {stage}: {detail}\nSee {LOG}", file=sys.stderr)
    return 1


def reset_sandbox() -> None:
    if ROOT.exists():
        shutil.rmtree(ROOT)
    APP_DIR.mkdir(parents=True)
    (APP_DIR / "DongGfx.dll").write_text("old dll", encoding="utf-8")
    (APP_DIR / "runtimes" / "win-x64").mkdir(parents=True)
    (APP_DIR / "runtimes" / "win-x64" / "native.dll").write_text("old native", encoding="utf-8")


# ── fake release server ───────────────────────────────────────────────

def make_zip() -> tuple[bytes, str]:
    """New-package zip: exe + updated dll + a new subdirectory file."""
    import io
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("DongGfx.exe", "MZWG\\new build exe image")
        z.writestr("DongGfx.dll", "new dll")
        z.writestr("runtimes/win-x64/native.dll", "new native")
    body = buf.getvalue()
    return body, hashlib.sha256(body).hexdigest().upper()


class ReleaseServer(ThreadingHTTPServer):
    def __init__(self) -> None:
        super().__init__(("127.0.0.1", 0), Handler)
        self.body, self.checksum = make_zip()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a) -> None:  # quiet
        pass

    def _send(self, code: int, payload: bytes, ctype: str) -> None:
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self) -> None:  # noqa: N802
        srv: ReleaseServer = self.server  # type: ignore[assignment]
        if self.path.startswith("/releases/latest"):
            doc = {
                "tag_name": f"v{NEW_VERSION}",
                "body": f" rehearsal release\n\nsha256: {srv.checksum}  tf-{NEW_VERSION}-win-x64.zip\n",
                "assets": [{
                    "name": f"tf-{NEW_VERSION}-win-x64.zip",
                    "browser_download_url": f"http://127.0.0.1:{srv.server_address[1]}/tf.zip",
                    "size": len(srv.body),
                }],
            }
            self._send(200, json.dumps(doc).encode(), "application/json")
        elif self.path.startswith("/tf.zip"):
            self._send(200, srv.body, "application/zip")
        else:
            self._send(404, b"{}", "application/json")


# ── the drill stages ──────────────────────────────────────────────────

def write_probe_cs() -> Path:
    """A minimal WinExe that stays alive ~90s (long enough for tasklist
    to confirm it, short enough that a crashed run cannot linger)."""
    probe = APP_DIR / "probe" / "Probe.cs"
    probe.parent.mkdir(parents=True, exist_ok=True)
    probe.write_text(
        "using System;\n"
        "using System.Threading;\n"
        "static class P {\n"
        "  [STAThread]\n"
        "  static void Main() {\n"
        "    Console.WriteLine(\"probe alive\");\n"
        "    Thread.Sleep(90_000);\n"
        "  }\n"
        "}\n", encoding="utf-8")
    return probe


def probe_project(cs: Path) -> Path:
    csproj = cs.parent / "Probe.csproj"
    csproj.write_text(
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        '  <PropertyGroup>\n'
        '    <OutputType>WinExe</OutputType>\n'
        '    <TargetFramework>net8.0-windows</TargetFramework>\n'
        '    <ImplicitUsings>enable</ImplicitUsings>\n'
        '    <Nullable>disable</Nullable>\n'
        '    <AssemblyName>subtestapp</AssemblyName>\n'
        '    <UseWindowsForms>false</UseWindowsForms>\n'
        '  </PropertyGroup>\n'
        '</Project>\n', encoding="utf-8")
    return csproj


def stage_check_and_download(srv: ReleaseServer) -> None:
    url = f"http://127.0.0.1:{srv.server_address[1]}/releases/latest"
    proj = REPO / "src" / "DongGfx.Core" / "DongGfx.Core.csproj"
    runner = ROOT / "runner"
    runner.mkdir(exist_ok=True)
    # A tiny console host references DongGfx.Core and drives AutoUpdater
    # end-to-end against the fake server (real HTTP, real paths).
    (runner / "Runner.cs").write_text(
        "using DongGfx.Core.Update;\n"
        "var url = args[0];\n"
        "var upd = new AutoUpdater(\"" + OLD_VERSION + "\", updateDir: args[1]);\n"
        "var info = await upd.CheckForUpdateAsync(url);\n"
        "Console.WriteLine(info is null ? \"NO-UPDATE\" : $\"OK {info.Version}\");\n"
        "if (info is null) return 2;\n"
        "var zip = await upd.DownloadUpdateAsync(info);\n"
        "var staged = await upd.StageUpdateAsync(zip);\n"
        "Console.WriteLine(\"STAGED \" + staged);\n"
        "return 0;\n", encoding="utf-8")
    (runner / "Runner.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        '  <PropertyGroup>\n'
        '    <OutputType>Exe</OutputType>\n'
        '    <TargetFramework>net8.0</TargetFramework>\n'
        '    <ImplicitUsings>enable</ImplicitUsings>\n'
        '    <Nullable>enable</Nullable>\n'
        '  </PropertyGroup>\n'
        '  <ItemGroup>\n'
        f'    <ProjectReference Include="{proj}" />\n'
        '  </ItemGroup>\n'
        '</Project>\n', encoding="utf-8")

    r = subprocess.run(
        ["dotnet", "run", "--project", str(runner), "--", url, str(UPDATE_DIR)],
        capture_output=True, text=True, timeout=420)
    out = (r.stdout or "") + (r.stderr or "")
    if r.returncode != 0 or "STAGED" not in out:
        fail("check/download/stage", out.strip()[-600:])
        sys.exit(1)
    log("PASS check+download+checksum+stage (real AutoUpdater against fake release)")


def _build_and_copy(project: Path, out_dir: Path, names: list[str],
                    stage: str) -> None:
    """Build WITHOUT -o (an output dir outside the project folder makes
    MSBuild's DefaultItemExcludes emit a ..\\** glob that silently drops
    the project's own sources → CS5001), then copy the wanted outputs
    into out_dir."""
    r = subprocess.run(
        ["dotnet", "build", str(project), "-c", "Release", "--nologo", "-v", "q"],
        capture_output=True, text=True, timeout=420)
    if r.returncode != 0:
        fail(stage, ((r.stdout or "") + (r.stderr or ""))[-600:])
        sys.exit(1)
    out_dir.mkdir(parents=True, exist_ok=True)
    for name in names:
        hits = list(project.parent.rglob(name))   # project/bin/**/name
        if not hits:
            fail(stage, f"build output {name} not found under {project.parent}")
            sys.exit(1)
        shutil.copy2(hits[0], out_dir / name)


def stage_install() -> None:
    """InstallUpdate replaces files in AppContext.BaseDirectory, so the
    runner must EXECUTE from the sandbox app dir — built to its own bin
    and copied in (see _build_and_copy)."""
    runner = ROOT / "runner"
    (runner / "Runner.cs").write_text(
        "using DongGfx.Core.Update;\n"
        "var upd = new AutoUpdater(\"" + OLD_VERSION + "\", updateDir: args[0]);\n"
        "var ok = upd.InstallUpdate(args[1]);\n"
        "Console.WriteLine(ok ? \"INSTALLED\" : \"INSTALL-REFUSED\");\n"
        "return ok ? 0 : 3;\n", encoding="utf-8")
    _build_and_copy(runner / "Runner.csproj", APP_DIR,
                    ["Runner.exe", "Runner.dll", "Runner.runtimeconfig.json",
                     "DongGfx.Core.dll"], "install build")
    r = subprocess.run(
        [str(APP_DIR / "Runner.exe"), str(UPDATE_DIR), str(STAGED)],
        capture_output=True, text=True, timeout=420, cwd=str(APP_DIR))
    out = (r.stdout or "") + (r.stderr or "")
    if r.returncode != 0 or "INSTALLED" not in out:
        fail("install", out.strip()[-600:])
        sys.exit(1)
    dll = (APP_DIR / "DongGfx.dll").read_text(encoding="utf-8")
    native = (APP_DIR / "runtimes" / "win-x64" / "native.dll").read_text(encoding="utf-8")
    if dll != "new dll" or native != "new native":
        fail("install", f"files not replaced: dll={dll!r} native={native!r}")
        sys.exit(1)
    backups = list(UPDATE_DIR.glob("backup-*"))
    if not backups:
        fail("install", "no backup dir created")
        sys.exit(1)
    log("PASS install (recursive copy + backup created)")


def stage_restart_script() -> None:
    """Real restart.bat: start the sandbox exe, tasklist-verify, self-delete."""
    # The probe exe the script must launch (the "new build"), built to its
    # own bin and copied into the app dir (see _build_and_copy).
    cs = write_probe_cs()
    _build_and_copy(probe_project(cs), APP_DIR,
                    ["subtestapp.exe", "subtestapp.dll", "subtestapp.runtimeconfig.json"],
                    "probe build")

    # Render the same script the app ships (no reimplementation): call the
    # real BuildRestartScript through the runner and capture its output.
    runner = ROOT / "runner"
    (runner / "Runner.cs").write_text(
        "using DongGfx.Core.Update;\n"
        "Console.Write(AutoUpdater.BuildRestartScript(args[0], args[1]));\n", encoding="utf-8")
    r = subprocess.run(
        ["dotnet", "run", "--project", str(runner), "--",
         str(APP_DIR / "subtestapp.exe"), str(APP_DIR)],
        capture_output=True, text=True, timeout=420)
    if r.returncode != 0:
        fail("render restart script", ((r.stdout or "") + (r.stderr or ""))[-600:])
        sys.exit(1)
    bat = UPDATE_DIR / "restart.bat"
    bat.write_text(r.stdout, encoding="utf-8", newline="\r\n")

    # Launch it hidden, like the app does.
    proc = subprocess.Popen(
        ["cmd", "/c", str(bat)], cwd=str(APP_DIR),
        creationflags=subprocess.CREATE_NO_WINDOW)

    # Wait for the script to confirm the probe via tasklist and self-delete.
    deadline = time.time() + 45
    while time.time() < deadline:
        if not bat.exists():
            break
        if proc.poll() is not None:
            break
        time.sleep(0.5)

    if bat.exists():
        proc.kill()
        faillog = UPDATE_DIR / "restart-failed.log"
        detail = faillog.read_text(encoding="utf-8") if faillog.exists() else "script did not self-delete"
        fail("restart", f"restart.bat still present after 45s — {detail}")
        sys.exit(1)

    # tasklist cross-check from this side too.
    tl = subprocess.run(["tasklist", "/FI", "IMAGENAME eq subtestapp.exe"],
                        capture_output=True, text=True)
    if "subtestapp.exe" not in (tl.stdout or ""):
        fail("restart", "script self-deleted but the probe exe is not running")
        sys.exit(1)
    log("PASS restart (script verified the probe via tasklist and self-deleted)")
    # Leave the probe running: visible proof in Task Manager until it exits.


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--clean", action="store_true", help="remove the sandbox after the drill")
    args = ap.parse_args()

    if sys.platform != "win32":
        print("Windows-only (the restart script is cmd.exe).", file=sys.stderr)
        return 2

    log("=== updater rehearsal start ===")
    reset_sandbox()
    srv = ReleaseServer()
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    try:
        stage_check_and_download(srv)
        stage_install()
        stage_restart_script()
        log("=== rehearsal PASSED — full update path verified end-to-end ===")
        return 0
    finally:
        srv.shutdown()
        if args.clean:
            # The probe exe may still be running from inside the sandbox.
            subprocess.run(["taskkill", "/F", "/IM", "subtestapp.exe"],
                           capture_output=True)
            time.sleep(1)
            shutil.rmtree(ROOT, ignore_errors=True)
            log("sandbox removed")


if __name__ == "__main__":
    sys.exit(main())
