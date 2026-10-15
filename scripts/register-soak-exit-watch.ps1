#!/usr/bin/env pwsh
# Registers the "DongGfx soak-exit watch" Windows scheduled task: runs
# scripts/soak_exit_watch.py at logon and every 15 minutes. The wrapper
# soak_evening.ps1 brackets every run with a start line and a
# timestamped exit line in %LOCALAPPDATA%\tf\soak_evening.log; a start
# with no matching exit line IS a termination (the Monday alpha task's
# 0x41306 signature — a killed process can never write its own exit
# line). The watcher tails that log and pages the webhook once per
# orphan start older than the grace window (default 270 min > the alpha
# task's 4 h limit), covering both soak tasks (nightly tf-soak-evening
# and the Monday alpha rerun — same log).
#
# 15-minute cadence: a pass reads one small file and exits in seconds;
# detection latency after the grace window is bounded by one poll.
# Same cmd wrapper + capture as the rest of the fleet so the verdict
# (and any POST failure) leaves evidence in data\logs.
#
# Reports, never gates: exit 0 = checked (alerted or not), exit 1 = an
# alert was needed and the POST failed (state saved only after a
# confirmed post, so the next pass retries).
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-soak-exit-watch.ps1 [-Remove] [-EveryMinutes 15] [-GraceMinutes 270]

[CmdletBinding()]
param(
    [switch]$Remove,
    [int]$EveryMinutes = 15,
    [int]$GraceMinutes = 270,
    [string]$TaskName = 'DongGfx soak-exit watch'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$watcher = Join-Path $root 'scripts\soak_exit_watch.py'
if (-not (Test-Path $watcher)) { throw "Watcher not found: $watcher" }

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw 'python not found on PATH' }

# Capture stdout/stderr: this system's ScheduledTasks module has no
# -RedirectStandardOutput on New-ScheduledTaskAction, so wrap in cmd.
# The same cmd line also sets PYTHONIOENCODING=utf-8 — the fleet audit's
# own rule (cp1252 scheduled-stdout crash class, PR #153).
$logDir = Join-Path $env:APPDATA 'tf\data\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$outLog = Join-Path $logDir 'soak-exit-watch.log'
$errLog = Join-Path $logDir 'soak-exit-watch.err.log'
$gt = [char]62   # cmd redirection char built at runtime: keeps PS-ENC quiet
$arg = '/c set PYTHONIOENCODING=utf-8&& "' + $python + '" "' + $watcher + '" --grace-minutes ' + $GraceMinutes + ' ' + $gt + ' "' + $outLog + '" 2' + $gt + ' "' + $errLog + '"'
$action = New-ScheduledTaskAction -Execute 'cmd.exe' `
    -Argument $arg `
    -WorkingDirectory $root

$triggerLogon = New-ScheduledTaskTrigger -AtLogOn
$triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes) -RepetitionDuration (New-TimeSpan -Days 3650)

# 3 min cap: one small log read plus at most one POST — a pass that long
# has hung.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger @($triggerLogon, $triggerRepeat) -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': soak-exit watch at logon + every $EveryMinutes min (pages once per orphan start older than $GraceMinutes min; verdict captured to soak-exit-watch.log)."
