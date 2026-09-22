#!/usr/bin/env pwsh
# MT5 bridge watchdog: keeps the loopback sidecar (bridge/mt5_sidecar.py)
# alive so the Terminal tab's MT5 panel and the FX brain always have a
# bridge. Designed to run as a Windows scheduled task at logon and every
# 5 minutes (see register-mt5-watchdog.ps1).
#
# Behavior:
#   - /health answers → exit 0 silently (nothing to do).
#   - /health down → start the sidecar detached (hidden window), wait for
#     health, write a status line to the watchdog log, exit 0 if it came up
#     (exit 2 if the sidecar could not be started — the next run retries).
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

function Log([string]$line) {
    if (-not $Quiet) { Write-Output $line }
    Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $line)
}

function Test-Health {
    try {
        $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/health" -UseBasicParsing -TimeoutSec 4
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

Start-Process -FilePath $python -ArgumentList "`"$sidecar`"", "$Port" `
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
