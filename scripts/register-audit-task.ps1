#!/usr/bin/env pwsh
# Registers the "DongGfx task-fleet audit" Windows scheduled task:
# runs scripts/audit_donggfx_tasks.py WEEKLY so encoding-wrapper and
# git-HEAD drift in the scheduled-task fleet is caught without a session.
#
# The audit is read-only (never edits tasks) and exits 1 when it finds
# drift; every run appends a deduped summary line to the task-audit
# history file (data/watcher/task-audit-history.json), so the next run
# or session reads when each finding first appeared and whether it
# persisted.
#
# The cmd wrapper (set PYTHONIOENCODING=utf-8&&) is the audit's OWN
# rule — the task must not be its first finding (the cp1252-scheduled-
# stdout crash class, PR #153). Audit passes take seconds; the weekly
# cadence bounds undetected drift to one week.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-audit-task.ps1 [-Remove] [-DayOfWeek Monday]

[CmdletBinding()]
param(
    [switch]$Remove,
    [string]$DayOfWeek = 'Monday',
    [string]$AtLocal = '08:30',
    [string]$TaskName = 'DongGfx task-fleet audit'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$audit = Join-Path $root 'scripts\audit_donggfx_tasks.py'
if (-not (Test-Path $audit)) { throw "Audit script not found: $audit" }

# The same interpreter the other tasks pin: python.exe (stdout-bearing)
# behind the cmd encoding wrapper — pythonw has no stdout to capture.
$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw 'python not found on PATH' }

$action = New-ScheduledTaskAction -Execute 'cmd.exe' `
    -Argument "/c set PYTHONIOENCODING=utf-8&& `"$python`" `"$audit`"" `
    -WorkingDirectory $root

$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek $DayOfWeek -At $AtLocal

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger $trigger -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': audit weekly on $DayOfWeek at $AtLocal local (findings land in the task-audit history; drift caught without a session)."
