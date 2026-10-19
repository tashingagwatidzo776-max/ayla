#!/usr/bin/env pwsh
# MT5 bridge watchdog: keeps the loopback sidecar (bridge/mt5_sidecar.py)
# alive so the Terminal tab's MT5 panel and the FX brain always have a
# bridge. Designed to run as a Windows scheduled task at logon and every
# 5 minutes (see register-mt5-watchdog.ps1).
#
# Behavior:
#   - /ping answers → exit 0 silently (nothing to do). /ping is served
#     WITHOUT the MT5 lock, so a busy bridge still answers instantly and
#     can never look wedged.
#   - /ping silent → the sidecar is wedged: it still holds the port, so
#     the port owner is KILLED first (a fresh bind would fail with 10048
#     otherwise), then the sidecar is started detached (hidden window),
#     wait for health, write a status line to the watchdog log, exit 0
#     if it came up (exit 2 if the sidecar could not be started — the
#     next run retries).
#
# Usage: pwsh -File scripts/mt5-watchdog.ps1 [-Port 53190] [-Quiet]
[CmdletBinding()]
param(
    [int]$Port = 53190,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName
$sidecar = Join-Path $root 'bridge\mt5_sidecar.py'
$logDir = Join-Path $env:APPDATA 'tf\data\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'mt5-watchdog.log'

# The app publishes data/mt5-bridge.json (resolved terminal path + port) at
# startup; honor it so watchdog, sidecar and app all target the SAME MT5
# install. Absent file -> the defaults below (auto-discover).
$TerminalPath = $null
$cfg = Join-Path $env:APPDATA 'tf\data\mt5-bridge.json'
if (Test-Path $cfg) {
    try {
        $j = Get-Content $cfg -Raw | ConvertFrom-Json
        if ($j.port) { $Port = [int]$j.port }
        if ($j.terminalPath) { $TerminalPath = [string]$j.terminalPath }
    } catch {
        Log "mt5-bridge.json unreadable - using defaults"
    }
}

function Log([string]$line) {
    if (-not $Quiet) { Write-Output $line }
    Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $line) -Encoding utf8
}

function Test-Health {
    try {
        $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/ping" -UseBasicParsing -TimeoutSec 4
        return $resp.StatusCode -eq 200
    } catch {
        return $false
    }
}

if (Test-Health) { exit 0 }

if (-not (Test-Path $sidecar)) {
    Log "sidecar script missing: $sidecar"
    exit 2
}

# Start detached so the watchdog process can exit while the sidecar lives.
$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) {
    Log "python not found on PATH - cannot start sidecar"
    exit 2
}

# A WEDGED sidecar still LISTENS on the port (its accept loop is frozen,
# so it can never answer /ping), and a fresh bind would fail with 10048 -
# the bridge then stayed dead until a human noticed (2026-10-19 weekend
# freeze). Kill the port owner before respawning, like the Python
# watchdog does.
$owner = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -First 1
if ($owner) {
    Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Log "killed wedged sidecar (PID $owner) still holding port $Port"
}

$argList = @("`"$sidecar`"", "$Port")
if ($TerminalPath) { $argList += @('--terminal', "`"$TerminalPath`"") }
Start-Process -FilePath $python -ArgumentList $argList `
    -WindowStyle Hidden -WorkingDirectory (Join-Path $root 'bridge') | Out-Null

# Wait up to 20 s for health.
$up = $false
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 2
    if (Test-Health) { $up = $true; break }
}

if ($up) {
    Log "sidecar was down - restarted on port $Port (health OK)"
    exit 0
}

Log "sidecar restart did not become healthy on port $Port - will retry next run"
exit 2
