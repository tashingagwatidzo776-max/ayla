#!/usr/bin/env pwsh
# Registers the "DongGfx close-price watcher" Windows scheduled task:
# runs scripts/watch_close_price.py at logon and every 5 minutes, so the
# journal's outcome-source coverage is watched continuously and a NEW
# close row landing OutcomeSource=unknown (or with no payload at all)
# pages the configured webhook exactly once per event (dedup state under
# data/watcher/ survives restarts — a background process would not: the
# 2026-10-08 session-cleanup deaths are why this task exists).
#
# The watcher is read-only (journal in, webhook out, never trades) and each
# pass exits in seconds; the 5-minute cadence bounds alert latency without
# meaningful journal-scan load.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-close-price-watch.ps1 [-Remove] [-EveryMinutes 5]

[CmdletBinding()]
param(
    [switch]$Remove,
    [int]$EveryMinutes = 5,
    [string]$TaskName = 'DongGfx close-price watcher'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$watcher = Join-Path $root 'scripts\watch_close_price.py'
if (-not (Test-Path $watcher)) { throw "Watcher not found: $watcher" }

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw 'python not found on PATH' }

# Capture stdout/stderr: this system's ScheduledTasks module has no
# -RedirectStandardOutput on New-ScheduledTaskAction, so wrap in cmd.
# Without this the Discord 403 loop (2026-10-08/09) stayed invisible -
# the "alert FAILED" line went to a console nobody has, and the
# watcher looked healthy while never delivering an alert.
# The same cmd line also sets PYTHONIOENCODING=utf-8 - the fleet audit's
# own rule (cp1252 scheduled-stdout crash class, PR #153): a non-ASCII
# print under the ANSI codepage raises UnicodeEncodeError and dies.
$logDir = Join-Path $env:APPDATA 'tf\data\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$outLog = Join-Path $logDir 'watch-close-price.log'
$errLog = Join-Path $logDir 'watch-close-price.err.log'
$gt = [char]62   # cmd redirection char built at runtime: keeps PS-ENC quiet
$arg = '/c set PYTHONIOENCODING=utf-8&& "' + $python + '" "' + $watcher + '" ' + $gt + ' "' + $outLog + '" 2' + $gt + ' "' + $errLog + '"'
$action = New-ScheduledTaskAction -Execute 'cmd.exe' `
    -Argument $arg `
    -WorkingDirectory $root

$triggerLogon = New-ScheduledTaskTrigger -AtLogOn
$triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes) -RepetitionDuration (New-TimeSpan -Days 3650)

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger @($triggerLogon, $triggerRepeat) -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': watcher at logon + every $EveryMinutes min (webhook alert on a NEW unknown close)."
