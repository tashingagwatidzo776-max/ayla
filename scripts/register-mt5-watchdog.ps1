#!/usr/bin/env pwsh
# Registers the "DON G FX MT5 bridge watchdog" Windows scheduled task:
# runs scripts/mt5-watchdog.ps1 at logon and every 5 minutes, so the MT5
# sidecar is always up whenever the machine is running (the Terminal panel
# and the FX brain both depend on it). The watchdog is idempotent — when the
# sidecar is healthy it exits in ~1 s.
#
# Idempotent: re-registering replaces the task.
#
# Usage: pwsh -File scripts/register-mt5-watchdog.ps1 [-Remove] [-Port 53190]

[CmdletBinding()]
param(
    [switch]$Remove,
    [int]$Port = 53190,
    [string]$TaskName = 'DongGfx MT5 bridge watchdog'
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed task '$TaskName'."
    exit 0
}

$watcher = Join-Path $root 'scripts\mt5-watchdog.ps1'
if (-not (Test-Path $watcher)) { throw "Watchdog not found: $watcher" }

$pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $pwsh) { $pwsh = 'powershell' }

$action = New-ScheduledTaskAction -Execute $pwsh `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$watcher`" -Port $Port -Quiet" `
    -WorkingDirectory $root

$triggerLogon = New-ScheduledTaskTrigger -AtLogOn
$triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650)

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Trigger @($triggerLogon, $triggerRepeat) -Settings $settings -Force | Out-Null

Write-Output "Registered '$TaskName': watchdog at logon + every 5 min (port $Port)."
