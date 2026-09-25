# Rehearsing the updater against a fake release (end-to-end)

A hermetic drill of the full update path **on your machine** — the part
unit tests cannot pin: real HTTP, real zip, real `restart.bat`, real
tasklist-verified relaunch. Nothing touches the repo, `%APPDATA%\tf`, or
any real install; everything lives in `bin-verify/rehearsal/` (gitignored)
and is deleted with `--clean`.

## What it drills

1. Fake release server on loopback (announces SHA-256, serves a zip).
2. Real `AutoUpdater.CheckForUpdateAsync` — version verdict from the fake.
3. Real `DownloadUpdateAsync` — size + checksum verdicts on the zip.
4. Real `StageUpdateAsync` — staged-package validation (exe required).
5. Real `InstallUpdate` into a **sandbox app dir** (backup + recursive copy).
6. Real `BuildRestartScript` → run `restart.bat` → it must start the
   sandbox exe, confirm it via tasklist, and self-delete only on success.

## Run

```
python scripts/rehearse_updater.py            # full drill, keeps artifacts
python scripts/rehearse_updater.py --clean    # drill, then remove everything
python scripts/test_rehearse_updater.py       # offline logic tests (CI-safe)
```

Pass = every stage prints PASS and, when the smoke exe stays alive, the
restart script self-deletes. Fail = the stage that broke and its log line.

## Reference

The restart script's live contract (start → tasklist → retry ×3 →
`restart-failed.log` on failure, self-delete on success) is unit-pinned in
`tests/DongGfx.Core.Tests/AutoUpdaterTests.cs`; this rehearsal proves the
same contract under a real cmd.exe.
