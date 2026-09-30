#!/usr/bin/env pwsh
# Registers the "DON G FX profit-floor watcher" Windows scheduled task:
# runs scripts/watch_profit_floor.py at logon and every 4 minutes, so the
# journal is watched continuously and a VERIFIED profit-floor save (FX_EXIT
# override / deep giveback vote -> FX_RISK request -> close confirmation)
# alerts the configured webhook exactly once per event (dedup state under
# data/watcher/ survives restarts).
#
# The watcher is read-only (journal in, webhook out, never trades) and each
# pass exits in seconds; the 4-minute cadence bounds alert latency without
# meaningful journal-scan load.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-profit-floor-watch.ps1 [-Remove] [-EveryMinutes 4]

[CmdletBinding()]
param(
    [switch]$Remove,
    [int]$EveryMinutes = 4,
    [string]$TaskName = 'DongGfx profit-floor watcher'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$watcher = Join-Path $root 'scripts\watch_profit_floor.py'
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

Write-Output "Registered '$TaskName': watcher at logon + every $EveryMinutes min (webhook alert on a verified save)."
