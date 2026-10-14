#!/usr/bin/env pwsh
# Registers the "DongGfx exit cycle watch" Windows scheduled task:
# runs scripts/exit_cycle_watch.py at logon and every 5 minutes. The
# script is a 24/7 daemon (pythonw, single-instance PID lock, self-
# logs to data\logs\exit-cycle-watch.log), so a dead instance is
# respawned within one trigger while the lock keeps a single copy.
#
# The script USED to live in %TEMP% only - invisible to git and to the
# fleet audit, whose "python task with no repo script path" finding it
# was. Importing it here (and pointing the task at this repo copy) is
# what turns that finding green; the audit now checks this file against
# git HEAD like every other repo script.
#
# pythonw on purpose: the daemon writes its own log, so there is no
# stdout to capture - the same shape the badge and brain watchers use.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-exit-cycle-watch.ps1 [-Remove] [-EveryMinutes 5]

[CmdletBinding()]
param(
    [switch]$Remove,
    [int]$EveryMinutes = 5,
    [string]$TaskName = 'DongGfx exit cycle watch'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$watcher = Join-Path $root 'scripts\exit_cycle_watch.py'
if (-not (Test-Path $watcher)) { throw "Watcher not found: $watcher" }

$pythonw = (Get-Command pythonw -ErrorAction SilentlyContinue).Source
if (-not $pythonw) { throw 'pythonw not found on PATH' }

$action = New-ScheduledTaskAction -Execute $pythonw `
    -Argument "`"$watcher`"" `
    -WorkingDirectory $root

$triggerLogon = New-ScheduledTaskTrigger -AtLogOn
$triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes) -RepetitionDuration (New-TimeSpan -Days 3650)

# 72 h cap matches the original registration: a daemon restart every 3
# days is also when it picks up a new script version. IgnoreNew plus
# the script's own PID lock keep instances single.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 72) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger @($triggerLogon, $triggerRepeat) -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': daemon at logon + every $EveryMinutes min (self-logs to exit-cycle-watch.log)."
