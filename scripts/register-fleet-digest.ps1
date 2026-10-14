#!/usr/bin/env pwsh
# Registers the "DongGfx fleet status digest" Windows scheduled task:
# runs scripts/fleet_status_digest.py weekly — Monday 09:00, right
# after the 08:30 task-fleet audit — so the combined audit +
# watcher-health verdict lands in Discord: green/red visible without
# opening a session. The digest reports; ci-local and the watcher
# task-health task remain the gates.
#
# No AtLogOn trigger on purpose: a digest is a weekly report, not
# something to re-post every boot. Same cmd wrapper as the rest of the
# fleet (PYTHONIOENCODING + stdout/stderr into data\logs) so a failed
# POST leaves evidence instead of a bare nonzero task result.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-fleet-digest.ps1 [-Remove] [-DayOfWeek Monday] [-AtLocal "09:00"]

[CmdletBinding()]
param(
    [switch]$Remove,
    [string]$DayOfWeek = 'Monday',
    [string]$AtLocal = '09:00',
    [string]$TaskName = 'DongGfx fleet status digest'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$digest = Join-Path $root 'scripts\fleet_status_digest.py'
if (-not (Test-Path $digest)) { throw "Digest script not found: $digest" }

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw 'python not found on PATH' }

# Capture stdout/stderr: this system's ScheduledTasks module has no
# -RedirectStandardOutput on New-ScheduledTaskAction, so wrap in cmd.
# Without this a POST failure (no webhook, Discord 1010-style ban,
# network) goes to a console nobody has and the digest looks like it
# never ran — the 403 lesson. The same cmd line also sets
# PYTHONIOENCODING=utf-8 - the fleet audit's own rule (cp1252
# scheduled-stdout crash class, PR #153).
$logDir = Join-Path $env:APPDATA 'tf\data\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$outLog = Join-Path $logDir 'fleet-digest.log'
$errLog = Join-Path $logDir 'fleet-digest.err.log'
$gt = [char]62   # cmd redirection char built at runtime: keeps PS-ENC quiet
$arg = '/c set PYTHONIOENCODING=utf-8&& "' + $python + '" "' + $digest + '" ' + $gt + ' "' + $outLog + '" 2' + $gt + ' "' + $errLog + '"'
$action = New-ScheduledTaskAction -Execute 'cmd.exe' `
    -Argument $arg `
    -WorkingDirectory $root

$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek $DayOfWeek -At $AtLocal

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger $trigger -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': digest weekly on $DayOfWeek at $AtLocal local (audit + health verdicts to the webhook; reports, never gates)."
