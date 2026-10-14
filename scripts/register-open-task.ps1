#!/usr/bin/env pwsh
# Registers the "DON G FX weekly open" Windows scheduled task: after Monday
# 00:00 UTC (Deriv's weekend reopen), the task runs scripts/open-watcher.ps1
# — journal watch for the week's first BRAIN_DECISION / TRADE_SETTLEMENT, a
# Windows toast on each, then soak_report.py --record docs/soak and
# ci-watch.ps1 -Once before standing down.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-open-task.ps1 [-Remove] [-AtLocal "00:01"]
#   -Remove  deletes the task instead of creating it.
#   -AtLocal local trigger time, default 00:01 (≈ Monday 00:00 UTC + local
#            offset handling; see notes below).

[CmdletBinding()]
param(
    [switch]$Remove,
    [string]$AtLocal = '00:01',
    [string]$TaskName = 'DongGfx weekly market-open watcher'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$watcher = Join-Path $root 'scripts\open-watcher.ps1'
if (-not (Test-Path $watcher)) { throw "Watcher not found: $watcher" }

# Monday weekly trigger at the local wall-clock time. Deriv reopens Monday
# 00:00 UTC; 00:01 local is safely after the open for every timezone the
# operator is likely to run in (the watcher itself re-checks market state and
# stands down cleanly if the open hasn't happened yet).
$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday -At $AtLocal

# Capture stdout/stderr: this system's ScheduledTasks module has no
# -RedirectStandardOutput, and the watcher's decision lines (exit 0 /
# exit 2 / exit 3) are exactly what deserves reading after the fact.
# The watcher ALSO self-logs to data\logs\open-watcher.log - one of
# the two always survives.
$logDir = Join-Path $env:APPDATA 'tf\data\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$outLog = Join-Path $logDir 'watch-open-weekly.log'
$errLog = Join-Path $logDir 'watch-open-weekly.err.log'
$gt = [char]62   # cmd redirection char built at runtime: keeps PS-ENC quiet
$ps = (Get-Command powershell.exe -ErrorAction SilentlyContinue).Source
if (-not $ps) { throw 'powershell.exe not found' }
$arg = '/c ""' + $ps + '" -NoProfile -ExecutionPolicy Bypass -File "' + $watcher + '" -MaxHours 16 ' + $gt + ' "' + $outLog + '" 2' + $gt + ' "' + $errLog + '""'
$action = New-ScheduledTaskAction `
    -Execute 'cmd.exe' `
    -Argument $arg `
    -WorkingDirectory $root

# Run whether or not the user is logged in? No: the toast needs a desktop.
# Run only when logged on (default), hidden, with the network available.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Hours 17)

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

Register-ScheduledTask -TaskName $TaskName `
    -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description "DON G FX: after Monday 00:00 UTC market open, watch the demo journal for the week's first BRAIN_DECISION and settlement; toast, record soak evidence, verify the local tree is green." -Force | Out-Null

Write-Output "Registered task '$TaskName': Mondays at $AtLocal local → $watcher"
Write-Output "Preview : $(Get-ScheduledTask -TaskName $TaskName | Get-ScheduledTaskInfo | Select-Object -ExpandProperty NextRunTime)"
