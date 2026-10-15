#!/usr/bin/env pwsh
# Registers the "DongGfx fleet red alert" Windows scheduled task: runs
# fleet_status_digest.py --watch at logon and every 5 minutes, so a flip
# to RED in the task-fleet audit or watcher health posts to Discord
# immediately instead of waiting for Monday's digest. Same script, same
# probes and webhook as the digest — only the posting rule differs: the
# digest posts every run, the alert posts ONLY on a state flip (to RED,
# or back to GREEN after a RED), deduped by the state file under
# data\watcher\. AMBER (a broken probe) posts nothing.
#
# Reports, never gates: exit 0 = the pass completed (posted or not);
# exit 1 = a flip was detected and the POST failed — the state file is
# only saved after a confirmed post, so the next pass retries the flip.
#
# Same cmd wrapper + capture as the rest of the fleet (PYTHONIOENCODING
# + stdout/stderr into data\logs): a failed alert leaves evidence
# instead of a bare nonzero task result — the 403 lesson.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-fleet-alert.ps1 [-Remove] [-EveryMinutes 5]

[CmdletBinding()]
param(
    [switch]$Remove,
    [int]$EveryMinutes = 5,
    [string]$TaskName = 'DongGfx fleet red alert'
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
# Without this the "UNPOSTED, will retry" line goes to a console nobody
# has and a silently-failing alert looks healthy — the 403 lesson. The
# same cmd line also sets PYTHONIOENCODING=utf-8 — the fleet audit's own
# rule (cp1252 scheduled-stdout crash class, PR #153).
$logDir = Join-Path $env:APPDATA 'tf\data\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$outLog = Join-Path $logDir 'fleet-alert.log'
$errLog = Join-Path $logDir 'fleet-alert.err.log'
$gt = [char]62   # cmd redirection char built at runtime: keeps PS-ENC quiet
$arg = '/c set PYTHONIOENCODING=utf-8&& "' + $python + '" "' + $digest + '" --watch ' + $gt + ' "' + $outLog + '" 2' + $gt + ' "' + $errLog + '"'
$action = New-ScheduledTaskAction -Execute 'cmd.exe' `
    -Argument $arg `
    -WorkingDirectory $root

$triggerLogon = New-ScheduledTaskTrigger -AtLogOn
$triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes) -RepetitionDuration (New-TimeSpan -Days 3650)

# 10 min cap: both probes run back-to-back (<= 300 s each) plus the POST;
# a pass that long has hung, and IgnoreNew keeps the next poll from
# piling up behind it.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger @($triggerLogon, $triggerRepeat) -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': alert poller at logon + every $EveryMinutes min (posts ONLY on a GREEN/RED flip; verdict captured to fleet-alert.log)."
