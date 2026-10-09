# DongGfx deploy — stop, swap binaries, restart, with the keep-alive
# suspended for the WHOLE window.
#
# The race this closes (seen on the 2026-10-07 deploys): the raw flow
# (stop-app.ps1 -> copy -> schtasks /Run) ran with "DongGfx app
# autostart" (5-min keep-alive; IgnoreNew only while ITS instance runs)
# ENABLED, so a trigger could fire:
#   * between stop and copy   -> starts the OLD binary mid-copy (file
#     locks / partially written assemblies),
#   * between copy and start  -> two starts at once -> twin churn, and
#     "DongGfx twin killer" keeps the OLDEST process, so a lingering old
#     instance could get the NEW one killed.
# And if the manual start was missed, nothing restarted the app until
# the next keep-alive tick — worst case the app sat down.
# book_recovery.ps1 already suspends the keep-alive around its surgery;
# this brings the same discipline to every deploy.
#
# Guarantees:
#   1. keep-alive DISABLED from before the first stop until the binaries
#      are swapped; re-enabled in a finally on EVERY path (success,
#      throw, Ctrl+C on a fresh run heals a leftover disable first) —
#      so the app can never be left down permanently: worst case the
#      5-min tick brings the previous binary back.
#   2. start only happens after a verified ZERO DongGfx processes, and
#      only via schtasks /Run so the process is user-level and killable
#      (starting it from an elevated shell made it unkillable by the
#      user-level tasks — 2026-10-07 21:05 abort).
#   3. the result is verified: process present, started AFTER the copy,
#      autostart task enabled again — all logged to deploy.log.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File deploy-app.ps1 `
#       -Source <publish dir containing DongGfx.exe>
#   -NoBackup : skip the app-backup-<stamp> copy
#   -NoStart  : swap only (the keep-alive tick starts the app later)
# Exit codes: 0 = deployed (and up, unless -NoStart); 1 = failed — but
# the keep-alive is always left ENABLED, so the app self-recovers.
param(
  [Parameter(Mandatory = $true)][string]$Source,
  [switch]$NoBackup,
  [switch]$NoStart
)
$ErrorActionPreference = 'Stop'

$autostart = 'DongGfx app autostart'
$appRoot   = 'C:\Users\DELL\Desktop\DongGfx\app'
$exe       = Join-Path $appRoot 'DongGfx.exe'
$logDir    = Join-Path $env:APPDATA 'tf\data\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log  = Join-Path $logDir 'deploy.log'
# While the keep-alive is suspended, deploy.lock exists so that
# 'deploy-guard.ps1' (scheduled every 2 min) can tell an ACTIVE deploy
# from one that was hard-killed mid-window.
$lock = Join-Path $logDir 'deploy.lock'
function Note([string]$m) {
  $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $m
  Add-Content -Path $log -Value $line -Encoding utf8
  Write-Output $line
}
# Live DongGfx processes ONLY: a terminated-but-handle-held entry stays
# enumerable (observed 2026-10-08 02:16: dead PID 11808 lingered ~60 s
# and made a successful kill look like "app still running").
function Get-App {
  @(Get-Process -Name DongGfx -ErrorAction SilentlyContinue |
      Where-Object { -not $_.HasExited })
}

# --- 0. validate inputs -------------------------------------------------
$Source = (Resolve-Path -LiteralPath $Source -ErrorAction Stop).Path
if (-not (Test-Path (Join-Path $Source 'DongGfx.exe'))) {
  Write-Output "ERROR: no DongGfx.exe in $Source"
  exit 1
}
if (-not (Test-Path $exe)) { Write-Output "ERROR: app missing: $exe"; exit 1 }
Note ("deploy from $Source")

# Heal a leftover DISABLE from a crashed prior deploy before we take our
# own hold (ENABLE on an enabled task is a harmless no-op).
schtasks /Change /TN $autostart /ENABLE | Out-Null

# --- 1. suspend the keep-alive for the whole surgery window -------------
schtasks /Change /TN $autostart /DISABLE | Out-Null
$disabled = ($LASTEXITCODE -eq 0)
if (-not $disabled) {
  Note "WARN: could not disable autostart - stop passes absorb the race, but a mid-copy start is still possible"
}

$failed = $false
try {
  if ($disabled) {
    Set-Content -Path $lock -Value ("{0:o} {1}" -f (Get-Date), $Source) -Encoding utf8
  }

  # --- 2. stop, with retries (absorbs a respawn if the disable failed) --
  # Pass 1 asks nicely (graceful close lets the app finish its journal
  # append); passes 2-3 force. Same escalation stop-app.ps1 uses. Stop
  # ERRORS are logged - the 2026-10-08 02:16 abort swallowed them and
  # left 'app still running' unexplained.
  for ($i = 0; $i -lt 3; $i++) {
    $procs = Get-App
    if ($procs.Count -eq 0) { break }
    if ($i -eq 0) {
      foreach ($p in $procs) { try { $p.CloseMainWindow() | Out-Null } catch {} }
      Start-Sleep -Seconds 3
    } else {
      foreach ($p in $procs) {
        $stopErr = $null
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue -ErrorVariable stopErr
        if ($stopErr) {
          Note ("stop pass {0} failed for PID {1}: {2}" -f ($i + 1), $p.Id,
                (($stopErr | ForEach-Object { $_.Exception.Message }) -join '; '))
        }
      }
      Start-Sleep -Seconds 3
    }
  }
  # Settle poll: a graceful close (or a slow force) can take a few more
  # seconds; the hard gate is ZERO processes before the binary swap.
  for ($i = 0; $i -lt 8; $i++) {
    if ((Get-App).Count -eq 0) { break }
    Start-Sleep -Seconds 2
  }
  $left = Get-App
  if ($left.Count -gt 0) {
    # finally re-enables the keep-alive -> previous binary comes back
    Note ("abort: app still running after stop passes (PIDs " + (($left.Id) -join ',') + ") - keep-alive re-enabled, it will recover the old build")
    $failed = $true
    exit 1
  }
  Note "app stopped (0 DongGfx processes)"

  # --- 3. backup ---------------------------------------------------------
  if (-not $NoBackup) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $bak = Join-Path (Split-Path $appRoot -Parent) "app-backup-$stamp"
    Copy-Item -LiteralPath $appRoot -Destination $bak -Recurse -Force -ErrorAction Stop
    Note "backup -> $bak"
  }

  # --- 4. swap the binaries (app is down; nothing can lock the files) ----
  # robocopy /E (no /MIR): merge like `cp -rf src/. dst/`, never deletes
  # app-local files. Exit codes 0-7 are success, >= 8 is a failure.
  #
  # First-attempt flap (observed 3x on 2026-10-08): the copy hits
  # sharing violations ~5-9s in — hash of the sandwiched backups shows
  # exactly DongGfx.exe + DongGfx.pdb still byte-old while every other
  # changed file copied (exit 11 = 1+2+8) — and the handles clear within
  # ~30s, so an immediate manual retry always succeeded. Wait once and
  # retry INSIDE the deploy instead of leaving the app half-swapped and
  # relying on a human. File names always go to deploy-robocopy.log
  # (/NFL removed) so any residual failure is NAMED, not exit-11 folklore.
  #
  # Drill 2026-10-08 22:28: first attempt failed as designed (held pdb,
  # exit 11), but the FIXED 15s wait lost the race to the dead image of
  # the just-killed app: DongGfx.exe stayed locked ~54s after the stop
  # (ERROR 32 through 22:28:54), the retry failed too, and the deploy
  # aborted. Replace the fixed sleep with a bounded POLL for the exe to
  # become writable (the documented offender) — retry as soon as it is,
  # give up after 120s and take the abort path.
  $rcLog = Join-Path $logDir 'deploy-robocopy.log'
  $rc = 0
  robocopy $Source $appRoot /E /R:2 /W:2 /NJH /NJS /NP /LOG+:$rcLog | Out-Null
  $rc = $LASTEXITCODE
  if ($rc -ge 8) {
    Note "robocopy failed (exit $rc) - polling up to 120s for image handles to clear, then retrying once (file log: $rcLog)"
    $cleared = $false
    for ($i = 0; $i -lt 24; $i++) {
      Start-Sleep -Seconds 5
      try {
        $probe = [System.IO.File]::Open($exe, 'Open', 'Write', 'None')
        $probe.Dispose()
        $cleared = $true
        break
      } catch { }
    }
    if ($cleared) {
      Note "image handle clear after $((($i + 1) * 5))s - retrying robocopy now"
    } else {
      Note "exe still locked after 120s - retrying anyway (abort path follows if it fails)"
    }
    robocopy $Source $appRoot /E /R:2 /W:2 /NJH /NJS /NP /LOG+:$rcLog | Out-Null
    $rc = $LASTEXITCODE
  }
  if ($rc -ge 8) {
    Note "abort: robocopy failed (exit $rc) after in-deploy retry - failing file names in $rcLog; keep-alive re-enabled; restore from the app-backup-<stamp> dir if the app does not start"
    $failed = $true
    exit 1
  }
  if (-not (Test-Path $exe)) {
    Note "abort: $exe missing after copy - keep-alive re-enabled; restore from the app-backup-<stamp> dir"
    $failed = $true
    exit 1
  }
  $swapAt = Get-Date
  Note "binaries swapped (robocopy exit $rc)"
}
finally {
  if ($disabled) { schtasks /Change /TN $autostart /ENABLE | Out-Null }
  Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
  if (-not $failed) { Note "keep-alive re-enabled" }
}

if ($NoStart) { Note "-NoStart: leaving start to the keep-alive tick"; exit 0 }

# --- 5. start via the task (user-level, killable) and verify -------------
function Wait-App([int]$seconds) {
  for ($i = 0; $i -lt $seconds; $i += 2) {
    Start-Sleep -Seconds 2
    $p = Get-App
    if ($p.Count -gt 0) { return $p }
  }
  return @()
}

schtasks /Run /TN $autostart | Out-Null
$up = Wait-App 40
if ($up.Count -eq 0) {
  Note "WARN: first start produced no process - retrying /Run"
  schtasks /Run /TN $autostart | Out-Null
  $up = Wait-App 30
}
if ($up.Count -eq 0) {
  Note "WARN: app not up after deploy - keep-alive tick will retry within 5 min"
  exit 1
}

# A process older than the swap means a lingering OLD instance survived
# the stop passes (twin killer keeps the oldest -> it would kill the new
# one). Stop it and start fresh so the new build is what runs.
$old = @($up | Where-Object { $_.StartTime -lt $swapAt })
if ($old.Count -gt 0) {
  Note ("stale pre-swap instance(s) detected (" + (($old.Id) -join ',') + ") - stopping before restart")
  foreach ($p in $old) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 3
  if ((Get-App).Count -eq 0) {
    schtasks /Run /TN $autostart | Out-Null
    $up = Wait-App 40
  } else { $up = @() }
  if ($up.Count -eq 0) {
    Note "WARN: restart after stale-instance cleanup failed - keep-alive tick will retry within 5 min"
    exit 1
  }
}

$proc = $up | Sort-Object StartTime | Select-Object -First 1
Note ("app up: PID {0} started {1:yyyy-MM-dd HH:mm:ss} (after swap {2:yyyy-MM-dd HH:mm:ss})" -f `
      $proc.Id, $proc.StartTime, $swapAt)

# Final keep-alive check by ACTION, not by parsing query output: a
# disabled task still reports "Status: Running" while its current
# instance (the app) is alive, so /FO LIST can never prove enabled.
# /ENABLE is idempotent; its exit code is the proof.
schtasks /Change /TN $autostart /ENABLE | Out-Null
if ($LASTEXITCODE -ne 0) {
  Note "WARN: keep-alive re-enable FAILED - run: schtasks /Change /TN '$autostart' /ENABLE"
  exit 1
}
Note "keep-alive verified ENABLED"
exit 0
