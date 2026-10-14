#!/usr/bin/env pwsh
# Registers the "DongGfx TP1 first-arm pager" Windows scheduled task:
# runs scripts/watch_tp1_first_arm.py at logon and every 5 minutes, so
# a newly armed TP1 rung pages the configured webhook within one
# trigger (dedup state under data/watcher/ pages each event once).
#
# This task used to be registered by a stray script under .git\ (never
# versioned): cmd encoding wrapper, but no redirection, so its output
# went to a console nobody has - the exact silent-failure shape the
# Discord 403 loop exposed. Same capture + PYTHONIOENCODING wrapper the
# fleet audit demands for every cmd-wrapper task.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-tp1-first-arm-pager.ps1 [-Remove] [-EveryMinutes 5]

[CmdletBinding()]
param(
    [switch]$Remove,
    [int]$EveryMinutes = 5,
    [string]$TaskName = 'DongGfx TP1 first-arm pager'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$watcher = Join-Path $root 'scripts\watch_tp1_first_arm.py'
if (-not (Test-Path $watcher)) { throw "Watcher not found: $watcher" }

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw 'python not found on PATH' }

# Capture stdout/stderr: this system's ScheduledTasks module has no
# -RedirectStandardOutput on New-ScheduledTaskAction, so wrap in cmd.
# Without this the alert line goes to a console nobody has and the
# pager looks healthy while never delivering - the 403 lesson.
# The same cmd line also sets PYTHONIOENCODING=utf-8 - the fleet
# audit's own rule (cp1252 scheduled-stdout crash class, PR #153): a
# non-ASCII print under the ANSI codepage raises UnicodeEncodeError.
$logDir = Join-Path $env:APPDATA 'tf\data\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$outLog = Join-Path $logDir 'watch-tp1-first-arm.log'
$errLog = Join-Path $logDir 'watch-tp1-first-arm.err.log'
$gt = [char]62   # cmd redirection char built at runtime: keeps PS-ENC quiet
$arg = '/c set PYTHONIOENCODING=utf-8&& "' + $python + '" "' + $watcher + '" ' + $gt + ' "' + $outLog + '" 2' + $gt + ' "' + $errLog + '"'
$action = New-ScheduledTaskAction -Execute 'cmd.exe' `
    -Argument $arg `
    -WorkingDirectory $root

$triggerLogon = New-ScheduledTaskTrigger -AtLogOn
$triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes) -RepetitionDuration (New-TimeSpan -Days 3650)

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger @($triggerLogon, $triggerRepeat) -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': pager at logon + every $EveryMinutes min (output captured to watch-tp1-first-arm.log)."
