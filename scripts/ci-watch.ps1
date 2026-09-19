# Background watch: run the local CI gate whenever the working tree goes
# quiet, so failures surface as you edit — not at push time.
#
# Usage:   pwsh ./scripts/ci-watch.ps1 [-QuietSeconds 8] [-InitialRun]
# Stop:    Ctrl+C
# Status:  .ci-watch-status (last line: "GREEN <utc>" / "RED <utc>: <failed>")
# Toasts:  RED always surfaces a Windows toast (fallback: message box),
#          GREEN too with -Toast; silence both with -NoToast.
#
# What runs per change set:
#   - docs (*.md) / scripts (*.py) only  -> workflow-lint + safety-audit (fast)
#   - anything else                      -> ci-local.ps1 -SkipBuild
#     (build once at startup unless -NoInitialBuild; tests run against the
#      last build, so a code-only rename that breaks the build is caught by
#      the next full push gate, not this watcher)
#
# Files written DURING a gate run are picked up by the next cycle; the
# watcher never runs two gates at once and never exits nonzero (it is a
# companion, not a gate — the push-time hook remains the gate).

[CmdletBinding()]
param(
    [int]$QuietSeconds = 8,
    [int]$PollSeconds = 2,
    [switch]$InitialRun,
    [switch]$NoInitialBuild,
    [switch]$NoToast,
    [switch]$Toast,
    [int]$MaxCycles = 0 # 0 = watch forever; N = run at most N gate cycles (smoke tests)
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item $PSScriptRoot).Parent.FullName
$statusFile = Join-Path $root '.ci-watch-status'
$ciLocal = Join-Path $PSScriptRoot 'ci-local.ps1'

function Show-Toast {
    param([string]$State, [string]$Detail, [switch]$Green)
    if ($NoToast) { return }
    $text = if ($Detail) { "ci-watch $State - $Detail" } else { "ci-watch $State" }
    try {
        # Primary: a real Windows 10/11 toast (works from any script, no STA
        # requirement - the AppUserModelID is arbitrary for script-sourced toasts).
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $texts = $xml.GetElementsByTagName('text')
        [void]$texts.Item(0).AppendChild($xml.CreateTextNode('Local CI gate'))
        [void]$texts.Item(1).AppendChild($xml.CreateTextNode($text))
        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Microsoft.Windows.PowerShell').Show($toast)
    }
    catch {
        # Fallback: blocking message box (no toast infrastructure available).
        $icon = if ($Green) { [System.Windows.MessageBoxImage]::Information } else { [System.Windows.MessageBoxImage]::Warning }
        try { Add-Type -AssemblyName PresentationFramework } catch { }
        [void][System.Windows.MessageBox]::Show($text, 'Local CI gate', [System.Windows.MessageBoxButton]::OK, $icon)
    }
}

# Plain substrings on normalized forward-slash paths — deliberately no
# regex: escaping through shells/editors has bitten this repo before.
$excludeFragments = @(
    '/.git/', '/bin/', '/obj/', '/publish/', '/coverage/',
    '/TestResults/', '__pycache__', '/.ci-watch-status'
)

function Write-Status {
    param([string]$State, [string]$Detail = '')
    $stamp = (Get-Date).ToUniversalTime().ToString('u')
    if ($Detail) { "$State $stamp $Detail" | Set-Content -Path $statusFile -Encoding ascii }
    else { "$State $stamp" | Set-Content -Path $statusFile -Encoding ascii }
}

function Get-TreeSnapshot {
    $rows = @()
    foreach ($f in [System.IO.Directory]::EnumerateFiles($root, '*', [System.IO.SearchOption]::AllDirectories)) {
        $norm = $f.Replace('\', '/')
        $skip = $false
        foreach ($frag in $excludeFragments) {
            if ($norm.Contains($frag)) { $skip = $true; break }
        }
        if ($skip) { continue }
        $fi = [System.IO.FileInfo]::new($f)
        $rows += "$norm|$($fi.LastWriteTimeUtc.Ticks)|$($fi.Length)"
    }
    return $rows
}

function Invoke-Gate {
    param([string[]]$ChangedRelative)

    $docsOnly = ($ChangedRelative.Count -gt 0) -and -not ($ChangedRelative | Where-Object { $_ -notmatch '[.]md$' -and $_ -notlike 'scripts/*.py' })
    $failed = @()

    if ($docsOnly) {
        Write-Host "`n[ci-watch] docs/scripts change -> lint + audit only" -ForegroundColor DarkCyan
        python (Join-Path $PSScriptRoot 'lint_workflows.py'); if ($LASTEXITCODE -ne 0) { $failed += 'workflow-lint' }
        python (Join-Path $PSScriptRoot 'check_safety_audit.py'); if ($LASTEXITCODE -ne 0) { $failed += 'safety-audit' }
        python (Join-Path $PSScriptRoot 'check_rail_traits.py'); if ($LASTEXITCODE -ne 0) { $failed += 'rail-traits' }
    }
    else {
        Write-Host "`n[ci-watch] source change -> full local gate (no build)" -ForegroundColor DarkCyan
        & $ciLocal -SkipBuild
        if ($LASTEXITCODE -ne 0) { $failed += 'ci-local' }
    }

    if ($failed.Count -gt 0) {
        Write-Host "[ci-watch] RED: $($failed -join ', ')" -ForegroundColor Red
        Write-Status 'RED' "($($failed -join ', '))"
        Show-Toast 'RED' ($failed -join ', ')
    }
    else {
        Write-Host "[ci-watch] GREEN" -ForegroundColor Green
        Write-Status 'GREEN'
        if ($Toast) { Show-Toast 'GREEN' -Green }
    }
}

if (-not $NoInitialBuild) {
    Write-Host '[ci-watch] initial build (skip with -NoInitialBuild)...' -ForegroundColor DarkCyan
    dotnet build (Join-Path $root 'DongGfx.sln') --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host '[ci-watch] initial build failed; watcher still armed' -ForegroundColor Yellow }
}

$cycles = 0
$last = Get-TreeSnapshot
Write-Host "[ci-watch] watching $root ($(@($last).Count) files, quiet ${QuietSeconds}s)... Ctrl+C to stop" -ForegroundColor Cyan
if ($InitialRun) {
    Invoke-Gate @('src/example.cs')  # force the full gate once
    $cycles++
    if ($MaxCycles -gt 0 -and $cycles -ge $MaxCycles) { return }
}

while ($true) {
    Start-Sleep -Seconds $PollSeconds
    $now = Get-TreeSnapshot
    $changed = @(Compare-Object @($last) @($now))
    if ($changed.Count -eq 0) { continue }

    # Wait for the tree to go quiet (editors write in bursts). Stability is
    # measured between CONSECUTIVE snapshots — comparing against the pre-change
    # baseline would wait forever, since the change itself is that baseline's
    # diff.
    while ($true) {
        Start-Sleep -Seconds $QuietSeconds
        $next = Get-TreeSnapshot
        if (@(Compare-Object @($now) @($next)).Count -eq 0) { break }
        $now = $next
    }

    $rel = @($changed | ForEach-Object {
        $row = $_.InputObject
        $sep = $row.IndexOf('|')
        if ($sep -gt 0) { $row.Substring(0, $sep).Substring($root.Length + 1) } else { $row }
    })
    Write-Host "[ci-watch] changes: $($rel.Count) file(s) (e.g. $(@($rel | Select-Object -First 3) -join ', '))" -ForegroundColor DarkGray

    Invoke-Gate $rel
    $cycles++
    $last = Get-TreeSnapshot
    if ($MaxCycles -gt 0 -and $cycles -ge $MaxCycles) {
        Write-Host "[ci-watch] MaxCycles ($MaxCycles) reached; stopping." -ForegroundColor Cyan
        break
    }
}
