#!/usr/bin/env pwsh
# Registers the "DON G FX alpha validation" Windows scheduled task: Monday
# evening (after the Monday market-open session has accumulated tick data),
# it runs scripts/soak_evening.ps1 with the alpha leg forced — propose ->
# ticks_to_bars -> backtest over whatever the weekday archive holds, with
# proposals clearing the 5-trade bar called out as FINDINGS in the evidence
# doc. The analyst leg is left to the nightly 20:30 tf-soak-evening task;
# this task re-validates the week's Monday tape specifically.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-alpha-monday.ps1 [-Remove] [-AtLocal "21:10"]
#   -Remove   deletes the task instead of creating it.
#   -AtLocal  local trigger time, default 21:10 (Monday evening, after the
#             20:30 soak-evening run and a full Monday session of ticks).

[CmdletBinding()]
param(
    [switch]$Remove,
    [string]$AtLocal = '21:10',
    [string]$TaskName = 'DongGfx alpha validation Monday'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$wrapper = Join-Path $root 'scripts\soak_evening.ps1'
if (-not (Test-Path $wrapper)) { throw "Wrapper not found: $wrapper" }

# Monday weekly trigger at the local wall-clock time. The evidence doc is
# appended by soak_evening.py itself; the change stays UNCOMMITTED for human
# review (same rule as the nightly task — committing evidence is deliberate).
$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday -At $AtLocal

$action = New-ScheduledTaskAction `
    -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$wrapper`"" `
    -WorkingDirectory $root

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2)

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

Register-ScheduledTask -TaskName $TaskName `
    -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description 'DON G FX: Monday-evening alpha validation - rerun propose/backtest over the weekday tick archive and surface any proposal clearing the 5-trade bar in docs/soak.' | Out-Null

Write-Output "Registered task '$TaskName': Mondays at $AtLocal local -> $wrapper"
Write-Output "Preview : $(Get-ScheduledTask -TaskName $TaskName | Get-ScheduledTaskInfo | Select-Object -ExpandProperty NextRunTime)"
