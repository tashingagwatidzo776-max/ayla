# Market-open watcher: runs from Monday 00:00 UTC (00:01 local safe margin)
# until the week's first settlement, then records evidence and stands down.
#
# What it does, in order:
#   1. Tails today's journal (%APPDATA%\tf\data\journal\journal_YYYYMMDD.jsonl)
#      for the app's decision/settlement categories.
#   2. BRAIN_DECISION  -> one informational toast ("market open, engine deciding").
#   3. TRADE_SETTLEMENT-> a settlement toast, then the follow-up work:
#        - soak_report.py --record docs/soak (weekly evidence, committed by the
#          operator later; the watcher only generates the report file)
#        - ci-watch.ps1 -Once (verifies the local tree is still green)
#   4. Exits 0 after the first settlement; exits 2 if -MaxHours passes with no
#      settlement (so a scheduled task can alert on "market opened, nothing
#      traded"); exits 3 when the app itself isn't running (nothing to watch).
#
# The journal schema (see TradeJournal): one JSON object per line —
#   { "Timestamp": "...", "AccountId": "...", "Category": "...", "Details": "..." }
# Details is itself a JSON string; the watcher parses it for symbol/stake/PnL
# when present but never depends on it.

[CmdletBinding()]
param(
    # Stop waiting after this many hours (default: the whole trading day).
    [double]$MaxHours = 16,

    # How often to re-read the journal, in seconds.
    [int]$PollSeconds = 30,

    # Process name the app runs under (both the old and new binary use it).
    [string]$ProcessName = 'DongGfx',

    # Skip the soak/ci-watch follow-up (toast only) — used by the parser tests.
    [switch]$ToastOnly,

    # Test seam: pretend the market is open even when Deriv says otherwise.
    # Keeps the parser paths checkable on any day of the week.
    [switch]$AssumeOpen,

    # Test seam: skip the app-process check (for runs without the app up).
    [switch]$AssumeApp,

    # Test seam: journal file to watch instead of today's real one.
    [string]$JournalPath = ''
)

$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:APPDATA 'tf\data'
$root = (Get-Item $PSScriptRoot).Parent.FullName

function Show-WatcherToast {
    param([string]$Title, [string]$Body, [switch]$Green)
    # Headless runs (tests, SSH sessions) set DG_WATCHER_NOTOAST to suppress
    # both the toast and its blocking message-box fallback.
    if ($env:DG_WATCHER_NOTOAST) { return }
    try {
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $texts = $xml.GetElementsByTagName('text')
        [void]$texts.Item(0).AppendChild($xml.CreateTextNode($Title))
        [void]$texts.Item(1).AppendChild($xml.CreateTextNode($Body))
        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Microsoft.Windows.PowerShell').Show($toast)
    }
    catch {
        try { Add-Type -AssemblyName PresentationFramework } catch { }
        $icon = if ($Green) { [System.Windows.MessageBoxImage]::Information } else { [System.Windows.MessageBoxImage]::Warning }
        [void][System.Windows.MessageBox]::Show($Body, $Title, [System.Windows.MessageBoxButton]::OK, $icon)
    }
}

function Get-TodayJournalPath {
    param([string]$Dir, [datetime]$NowUtc)
    # The app names journals by UTC date (journal_20260919.jsonl).
    return Join-Path $Dir ("journal_{0:yyyyMMdd}.jsonl" -f $NowUtc)
}

function Read-JournalEvents {
    # Returns the typed events the watcher cares about, in file order.
    # Tolerant by design: unparsable lines are skipped, missing files yield
    # an empty list — a weekend journal or a mid-file crash must never kill
    # the watch.
    param([string]$Path)
    $events = @()
    if (-not (Test-Path $Path)) { return $events }
    foreach ($line in [IO.File]::ReadLines($Path)) {
        if (-not $line.Trim()) { continue }
        # One try per line: a malformed line (bad JSON, bad timestamp) is
        # skipped, never fatal — a partial write mid-settlement must not
        # kill the watch.
        try {
            $obj = $line | ConvertFrom-Json
            if (-not $obj.Category) { continue }
            $details = $null
            if ($obj.Details) { $details = $obj.Details | ConvertFrom-Json }
            $events += [pscustomobject]@{
                Timestamp = [datetime]::Parse($obj.Timestamp).ToUniversalTime()
                Category  = [string]$obj.Category
                Details   = $details
            }
        }
        catch { continue }
    }
    return $events
}

function Get-MarketOpenState {
    # Deriv's synthetic indices trade continuously except the weekend:
    # closed from Friday 23:59:50 UTC until Sunday 23:59:50 UTC. Trading
    # decisions therefore only appear from Monday 00:00 UTC onwards.
    param([datetime]$NowUtc)
    $day = [int]$NowUtc.DayOfWeek
    if ($day -eq 6) { return $false }                 # Saturday
    if ($day -eq 0 -and $NowUtc.TimeOfDay -lt [timespan]::FromHours(24) - [timespan]::FromSeconds(10)) { return $false } # Sunday before 23:59:50
    return $true
}

# ---- main loop ----------------------------------------------------------

# UTC throughout: mixing local Get-Date with UTC journal timestamps made the
# deadline drift by the machine's timezone offset.
$deadline = [DateTime]::UtcNow.AddHours($MaxHours)
$firstDecisionToasted = $false
$seenDecisionKeys = @{}
$seenSettlementKeys = @{}
$followUpDone = $false

Write-Output ("open-watcher: started {0:u}, deadline {1:u}, poll {2}s" -f (Get-Date).ToUniversalTime(), $deadline, $PollSeconds)

while ($true) {
    $nowUtc = (Get-Date).ToUniversalTime()

    $appRunning = [bool](Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if (-not $appRunning -and -not $AssumeApp) {
        Write-Output ("{0:u}  app not running - exiting 3" -f $nowUtc)
        exit 3
    }

    $marketOpen = $AssumeOpen -or (Get-MarketOpenState $nowUtc)
    $journal = if ($JournalPath) { $JournalPath } else { Get-TodayJournalPath $dataDir $nowUtc }
    $events = Read-JournalEvents $journal

    $decisions = @($events | Where-Object Category -eq 'BRAIN_DECISION')
    $settlements = @($events | Where-Object Category -eq 'TRADE_SETTLEMENT')

    foreach ($d in $decisions) {
        # Key on timestamp+account so a re-read never double-toasts.
        $key = '{0:o}|{1}' -f $d.Timestamp, $d.Details.contract_id
        if ($seenDecisionKeys.ContainsKey($key)) { continue }
        $seenDecisionKeys[$key] = $true
        if (-not $firstDecisionToasted) {
            $firstDecisionToasted = $true
            Write-Output ("{0:u}  first BRAIN_DECISION of the week" -f $nowUtc)
            Show-WatcherToast -Title 'DON G FX - market open' -Body 'The engine is receiving live prices and made its first decision of the week. Journal is recording.' -Green
        }
    }

    foreach ($s in $settlements) {
        $key = '{0:o}|{1}' -f $s.Timestamp, $s.Details.contract_id
        if ($seenSettlementKeys.ContainsKey($key)) { continue }
        $seenSettlementKeys[$key] = $true
        $pnl = ''
        if ($s.Details -and $null -ne $s.Details.Pnl) { $pnl = ' ({0})' -f $s.Details.Pnl }
        Write-Output ("{0:u}  TRADE_SETTLEMENT{1}" -f $nowUtc, $pnl)
        Show-WatcherToast -Title 'DON G FX - first settlement of the week' -Body ("A trade settled{0}. Journal and digest are up to date." -f $pnl) -Green

        if (-not $ToastOnly -and -not $followUpDone) {
            $followUpDone = $true
            # Soak evidence: the report generator reads the journal and writes
            # docs/soak/SOAK-<date>.md. Failure here must not fail the watch.
            try {
                Write-Output 'running soak_report.py --record docs/soak ...'
                & python (Join-Path $PSScriptRoot 'soak_report.py') --record (Join-Path $root 'docs\soak') 2>&1 | ForEach-Object { Write-Output "  soak: $_" }
            }
            catch { Write-Output ("  soak failed (non-fatal): " + $_.Exception.Message) }

            # Local CI gate, once: confirms the tree the release will come
            # from is still green. Non-fatal: its own status file + toasts
            # already surface RED states.
            try {
                Write-Output 'running ci-watch.ps1 -Once ...'
                & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'ci-watch.ps1') -Once 2>&1 | Select-Object -Last 5 | ForEach-Object { Write-Output "  ci: $_" }
            }
            catch { Write-Output ("  ci-watch failed (non-fatal): " + $_.Exception.Message) }
        }
        Write-Output ("{0:u}  watch complete - exit 0" -f (Get-Date).ToUniversalTime())
        exit 0
    }

    if ($marketOpen -and -not $firstDecisionToasted -and $decisions.Count -eq 0) {
        # Quietly keep waiting; the deadline handles the "opened but nothing
        # traded" case. Only log sparsely so the transcript stays readable.
        if ($nowUtc.Minute % 30 -eq 0 -and $nowUtc.Second -lt $PollSeconds) {
            Write-Output ("{0:u}  market open, no decisions yet ({1} journal events)" -f $nowUtc, $events.Count)
        }
    }

    if ([DateTime]::UtcNow -gt $deadline) {
        Write-Output ("{0:u}  deadline reached: {1} decisions, {2} settlements - exit 2" -f (Get-Date).ToUniversalTime(), $decisions.Count, $settlements.Count)
        exit 2
    }

    Start-Sleep -Seconds $PollSeconds
}
