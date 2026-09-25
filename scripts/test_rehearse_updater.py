#!/usr/bin/env python3
"""Offline logic tests for scripts/rehearse_updater.py (no server, no build).

Run:  python scripts/test_rehearse_updater.py   (exit 0 = all pass)
"""
from __future__ import annotations

import io
import json
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import rehearse_updater as ru  # noqa: E402


def check(name: str, cond: bool, detail: str = "") -> None:
    mark = "PASS" if cond else "FAIL"
    print(f"{mark} {name}" + (f": {detail}" if detail and not cond else ""))
    if not cond:
        raise AssertionError(name)


def test_zip_contains_executable_and_subdir() -> None:
    body, checksum = ru.make_zip()
    with zipfile.ZipFile(io.BytesIO(body)) as z:
        names = z.namelist()
        check("zip has an exe (staged-package validation requires it)",
              any(n.endswith(".exe") for n in names), str(names))
        check("zip carries a subdirectory file (recursive-install drill)",
              any(n.startswith("runtimes/") for n in names), str(names))
        check("checksum is the zip's real sha256 (upper hex)",
              checksum == __import__("hashlib").sha256(body).hexdigest().upper())
        check("checksum is 64 hex chars", len(checksum) == 64)


def test_release_doc_announces_checksum_and_true_size() -> None:
    class FakeSrv:
        server_address = ("127.0.0.1", 12345)

    srv = ru.ReleaseServer.__new__(ru.ReleaseServer)  # no socket bind
    ru.ReleaseServer.__init__ = lambda self: None     # skip real init
    srv.body, srv.checksum = ru.make_zip()
    srv.server_address = ("127.0.0.1", 12345)   # normally bound by HTTPServer

    # Drive the handler's release branch directly (no network): stub the
    # three writer methods _send calls, capture the payload via wfile.
    captured = {}

    h = ru.Handler.__new__(ru.Handler)
    h.path = "/releases/latest"
    h.server = srv            # the attribute do_GET actually reads
    h.send_response = lambda code: captured.update(code=code)
    h.send_header = lambda k, v: captured.setdefault("headers", {}).update({k: v})
    h.end_headers = lambda: None
    h.wfile = io.BytesIO()

    ru.Handler.do_GET(h)
    doc = json.loads(h.wfile.getvalue())
    check("release doc serves 200", captured["code"] == 200)
    check("tag is the new version", doc["tag_name"] == "v9.9.9")
    asset = doc["assets"][0]
    check("asset size equals the real zip length", asset["size"] == len(srv.body))
    check("notes announce the checksum",
          srv.checksum.lower() in doc["body"].lower())


def test_paths_stay_inside_the_sandbox() -> None:
    check("sandbox root is bin-verify/rehearsal (gitignored)",
          ru.ROOT.name == "rehearsal" and ru.ROOT.parent.name == "bin-verify")
    check("update dir lives inside the sandbox app dir",
          str(ru.UPDATE_DIR).startswith(str(ru.APP_DIR)))
    check("old version differs from new (update must be offered)",
          ru.OLD_VERSION != ru.NEW_VERSION)


def main() -> int:
    test_zip_contains_executable_and_subdir()
    test_release_doc_announces_checksum_and_true_size()
    test_paths_stay_inside_the_sandbox()
    print("\nall rehearsal-logic tests passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
