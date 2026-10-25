#!/usr/bin/env pwsh
# Registers the "DongGfx GWEN soak watch" Windows scheduled task: runs
# scripts/gwen_soak_watch.py every N hours for M days (default every
# 4 h for 3 days — a weekend of samples), refreshing the dated
# docs/soak/GWEN-SOAK-<date>.md report from the live journal. One-shot
# report runs, not a daemon: the script self-logs to
# data\logs\gwen-soak-watch.log, so pythonw with no console.
#
# Measurement only — the watch arms nothing, tunes nothing: FX_GWEN
# verdicts are read and counted exactly as the desk journaled them.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-gwen-soak-watch.ps1 [-Remove]
#        [-EveryHours 4] [-ForDays 3]

[CmdletBinding()]
param(
    [switch]$Remove,
    [int]$EveryHours = 4,
    [int]$ForDays = 3,
    [string]$TaskName = 'DongGfx GWEN soak watch'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$watcher = Join-Path $root 'scripts\gwen_soak_watch.py'
if (-not (Test-Path $watcher)) { throw "Watcher not found: $watcher" }

$pythonw = (Get-Command pythonw -ErrorAction SilentlyContinue).Source
if (-not $pythonw) { throw 'pythonw not found on PATH' }

$action = New-ScheduledTaskAction -Execute $pythonw `
    -Argument "`"$watcher`" --days $ForDays" `
    -WorkingDirectory $root

$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Hours $EveryHours) `
    -RepetitionDuration (New-TimeSpan -Days $ForDays)

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger $trigger -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': every $EveryHours h for $ForDays days (report refreshes docs\soak\GWEN-SOAK-<date>.md)."
