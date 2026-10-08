#!/usr/bin/env pwsh
# Registers the "DongGfx closed-row watch" Windows scheduled task:
# runs scripts/watch_closed_row.py at logon and every 5 minutes, so the
# journal is watched continuously and any floor/SL exit that closes
# WITHOUT an in-app `closed #` row pages the configured webhook exactly
# once per finding (dedup state under data/watcher/ survives restarts).
#
# The watcher is read-only (journal + loopback venue reads in, webhook
# out, never trades) and each pass exits in seconds; the 5-minute
# cadence bounds alert latency without meaningful journal-scan load.
#
# The FIRST pass primes a silent baseline of pre-fix history (the
# operator-reconcile rows), so only regressions after registration page.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-closed-row-watch.ps1 [-Remove] [-EveryMinutes 5]

[CmdletBinding()]
param(
    [switch]$Remove,
    [int]$EveryMinutes = 5,
    [string]$TaskName = 'DongGfx closed-row watch'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$watcher = Join-Path $root 'scripts\watch_closed_row.py'
if (-not (Test-Path $watcher)) { throw "Watcher not found: $watcher" }

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw 'python not found on PATH' }

$action = New-ScheduledTaskAction -Execute $python `
    -Argument "`"$watcher`"" `
    -WorkingDirectory $root

$triggerLogon = New-ScheduledTaskTrigger -AtLogOn
$triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes) -RepetitionDuration (New-TimeSpan -Days 3650)

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger @($triggerLogon, $triggerRepeat) -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': watcher at logon + every $EveryMinutes min (webhook page on a close without an in-app closed # row)."
