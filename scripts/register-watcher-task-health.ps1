#!/usr/bin/env pwsh
# Registers the "DongGfx watcher task health" Windows scheduled task:
# runs scripts/check_watcher_task_health.py at logon and hourly - the
# watch-the-watcher that fails when any alerting watcher task goes
# stale or shows persistent nonzero completions.
#
# Registered ad-hoc until now (stray script under .git\, never
# versioned): cmd encoding wrapper, but no redirection, so the health
# verdict itself went to a console nobody has - if the check failed,
# nobody saw why. Same capture + PYTHONIOENCODING wrapper the fleet
# audit demands for every cmd-wrapper task.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-watcher-task-health.ps1 [-Remove] [-EveryMinutes 60]

[CmdletBinding()]
param(
    [switch]$Remove,
    [int]$EveryMinutes = 60,
    [string]$TaskName = 'DongGfx watcher task health'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$watcher = Join-Path $root 'scripts\check_watcher_task_health.py'
if (-not (Test-Path $watcher)) { throw "Watcher not found: $watcher" }

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw 'python not found on PATH' }

# Capture stdout/stderr: this system's ScheduledTasks module has no
# -RedirectStandardOutput on New-ScheduledTaskAction, so wrap in cmd.
# Without this the health FAIL line goes to a console nobody has and
# the check looks like it never ran - the 403 lesson, one level up.
# The same cmd line also sets PYTHONIOENCODING=utf-8 - the fleet
# audit's own rule (cp1252 scheduled-stdout crash class, PR #153): a
# non-ASCII print under the ANSI codepage raises UnicodeEncodeError.
$logDir = Join-Path $env:APPDATA 'tf\data\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$outLog = Join-Path $logDir 'watch-task-health.log'
$errLog = Join-Path $logDir 'watch-task-health.err.log'
$gt = [char]62   # cmd redirection char built at runtime: keeps PS-ENC quiet
$arg = '/c set PYTHONIOENCODING=utf-8&& "' + $python + '" "' + $watcher + '" ' + $gt + ' "' + $outLog + '" 2' + $gt + ' "' + $errLog + '"'
$action = New-ScheduledTaskAction -Execute 'cmd.exe' `
    -Argument $arg `
    -WorkingDirectory $root

$triggerLogon = New-ScheduledTaskTrigger -AtLogOn
$triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes) -RepetitionDuration (New-TimeSpan -Days 3650)

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger @($triggerLogon, $triggerRepeat) -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': health check at logon + every $EveryMinutes min (verdict captured to watch-task-health.log)."
