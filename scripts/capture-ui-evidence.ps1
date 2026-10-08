<#
.SYNOPSIS
    Repeatable UI-evidence capture: grab (or load) a screenshot, crop it to a
    named surface, stamp a label banner, and write a PNG under docs/soak/.
    With -JournalCopyFlow it also DRIVES the journal copy flow end to end
    against a seeded journal and saves a labeled screenshot per step.

.DESCRIPTION
    Soak and TP1 evidence screenshots used to be produced by hand-running an
    ad-hoc PowerShell snippet each time (crop rectangles drifting between
    runs). This tool makes that repeatable: the crop geometry and the label
    live in one place, so a screenshot can be regenerated on demand and stays
    comparable run to run.

    Modes:
      * live capture   - screen-grab the primary display (optionally launching
                         the app first with a seeded TF_DATA_DIR);
      * -SourceImage   - crop/label an already-captured full-size PNG, so a
                         fresh capture is not always required;
      * -JournalCopyFlow - the journal-copy regression: seed a scratch data dir
                         with APP_FAULT rows, launch the app on it, navigate
                         the dashboard fault notice into the journal, right-
                         click a row and pick "Copy details", then select a
                         second row and press Ctrl+C — verifying the clipboard
                         each time and saving a labeled screenshot per step.

    Surfaces are named presets defined as fractions of the image size, so they
    scale across resolutions. Override with -Crop "x,y,w,h" (absolute pixels)
    when a preset does not frame the surface on a given machine.

    This tool makes NO changes to the app or the live data: the copy flow runs
    its OWN scratch instance and refuses to touch an already-running app.

.PARAMETER Name
    Output base name (no extension). Written to <OutDir>\<Name>.png. The copy
    flow writes <Name>-selected / -context-menu / -context-copied / -ctrlc.

.PARAMETER Surface
    Preset crop region: dashboard-soak, tp1-banner, account-soak-pill,
    journal-grid, full.

.PARAMETER Crop
    Explicit "x,y,w,h" in pixels; overrides -Surface.

.PARAMETER Label
    Text drawn in the banner above the crop (defaults to "<Name> · <Surface>").

.PARAMETER OutDir
    Output directory (default: ..\docs\soak relative to this script).

.PARAMETER SourceImage
    Crop/label an existing PNG instead of capturing the screen.

.PARAMETER Launch
    Launch the app before capturing (see -AppExe / -StartupDelaySeconds).

.PARAMETER JournalCopyFlow
    Run the seeded journal right-click + Ctrl+C copy regression (see above).

.PARAMETER AppExe
    App executable (default: src\DongGfx.App\bin\Debug\net8.0-windows\DongGfx.exe).

.PARAMETER DataDir
    TF_DATA_DIR handed to the launched app / seeded for the copy flow.

.PARAMETER StartupDelaySeconds
    Seconds to wait after -Launch before capturing.

.PARAMETER SelfTest
    Do not touch the screen or the app: build a synthetic image, crop+label
    it, seed a scratch journal and verify both. Exercises the same code path.

.EXAMPLE
    pwsh -File scripts/capture-ui-evidence.ps1 -Name soak-timeline -Surface dashboard-soak
.EXAMPLE
    pwsh -File scripts/capture-ui-evidence.ps1 -Name tp1 -SourceImage docs/soak/full.png -Crop "0,0,1920,200" -Label "TP1 banner"
.EXAMPLE
    pwsh -File scripts/capture-ui-evidence.ps1 -JournalCopyFlow -Name journal-copy
.EXAMPLE
    pwsh -File scripts/capture-ui-evidence.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string]$Name = 'ui-evidence',
    [ValidateSet('dashboard-soak', 'tp1-banner', 'account-soak-pill', 'journal-grid', 'full')]
    [string]$Surface = 'full',
    [string]$Crop = '',
    [string]$Label = '',
    [string]$OutDir = '',
    [string]$SourceImage = '',
    [switch]$Launch,
    [switch]$JournalCopyFlow,
    [string]$AppExe = '',
    [string]$DataDir = '',
    [int]$StartupDelaySeconds = 12,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not available while parameter defaults are evaluated in
# Windows PowerShell, so resolve the script dir and path defaults here.
$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $OutDir) { $OutDir = Join-Path $ScriptDir '..\docs\soak' }
if (-not $AppExe) { $AppExe = Join-Path $ScriptDir '..\src\DongGfx.App\bin\Debug\net8.0-windows\DongGfx.exe' }

Add-Type -AssemblyName System.Drawing

if (-not ('System.Windows.Forms.Screen' -as [type])) {
    Add-Type -AssemblyName System.Windows.Forms
}

# Label banner height in pixels (kept constant so stacked evidence lines up).
$LabelBandHeight = 26

# Named surfaces as fractions of the captured image (x, y, w, h). Best-effort
# defaults tuned on a 1920x1080 desktop; override with -Crop when a machine's
# window layout differs. Fractions keep them resolution-independent.
$SurfacePresets = @{
    'full'              = @(0.00, 0.00, 1.00, 1.00)
    'dashboard-soak'    = @(0.00, 0.10, 0.46, 0.30)
    'tp1-banner'        = @(0.00, 0.00, 1.00, 0.14)
    'account-soak-pill' = @(0.55, 0.02, 0.45, 0.10)
    'journal-grid'      = @(0.00, 0.30, 1.00, 0.55)
}

function Resolve-CropRect {
    param([int]$ImageWidth, [int]$ImageHeight, [string]$Surface, [string]$Crop)

    if ($Crop) {
        $parts = $Crop.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
        if ($parts.Count -ne 4) { throw "-Crop must be 'x,y,w,h' (got '$Crop')" }
        $rect = @($parts | ForEach-Object { [int]$_ })
    }
    else {
        $frac = $SurfacePresets[$Surface]
        if (-not $frac) { throw "unknown surface '$Surface'" }
        $rect = @(
            [int][Math]::Round($frac[0] * $ImageWidth),
            [int][Math]::Round($frac[1] * $ImageHeight),
            [int][Math]::Round($frac[2] * $ImageWidth),
            [int][Math]::Round($frac[3] * $ImageHeight)
        )
    }

    $x, $y, $w, $h = $rect
    # Clamp inside the image so a slightly-off preset never throws.
    $x = [Math]::Max(0, [Math]::Min($x, $ImageWidth - 1))
    $y = [Math]::Max(0, [Math]::Min($y, $ImageHeight - 1))
    $w = [Math]::Max(1, [Math]::Min($w, $ImageWidth - $x))
    $h = [Math]::Max(1, [Math]::Min($h, $ImageHeight - $y))
    return @($x, $y, $w, $h)
}

function Get-ScreenCapture {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    }
    finally {
        $g.Dispose()
    }
    return $bmp
}

function New-CroppedLabeledImage {
    param(
        [System.Drawing.Image]$Source,
        [int]$X, [int]$Y, [int]$Width, [int]$Height,
        [string]$Label
    )

    $band = if ($Label) { $LabelBandHeight } else { 0 }
    $canvas = New-Object System.Drawing.Bitmap $Width, ($Height + $band)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $destRect = New-Object System.Drawing.Rectangle 0, $band, $Width, $Height
        $srcRect = New-Object System.Drawing.Rectangle $X, $Y, $Width, $Height
        $g.DrawImage($Source, $destRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)

        if ($band -gt 0) {
            $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 18, 18, 18))
            $g.FillRectangle($bg, 0, 0, $Width, $band)
            $bg.Dispose()
            $font = New-Object System.Drawing.Font('Consolas', 12, [System.Drawing.FontStyle]::Bold)
            $g.DrawString($Label, $font, [System.Drawing.Brushes]::White, 6, 3)
            $font.Dispose()
        }
    }
    finally {
        $g.Dispose()
    }
    return $canvas
}

# ── journal seed + UI Automation plumbing (for -JournalCopyFlow) ────────────
# Native input is the only way to produce a real right-click: UI Automation
# has no "click" primitive, and the app's right-click handler hit-tests the
# actual mouse event's OriginalSource.
function Initialize-NativeInput {
    # -TypeDefinition (not -MemberDefinition -Name, which lands the type in an
    # auto-generated namespace) so [DongGfxNativeInput] resolves by short name.
    if (([System.Management.Automation.PSTypeName]'DongGfxNativeInput').Type) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DongGfxNativeInput {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
'@
}

function Initialize-UiAutomation {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
}

# Seed a scratch TF_DATA_DIR with a handful of journal rows: two APP_FAULT
# rows carrying unique probe tokens (one for the right-click copy, one for
# Ctrl+C) plus background context rows, and settings that keep the engine
# off. Returns the tokens/details so the caller can verify the clipboard.
function New-JournalSeed {
    param([string]$DataDir)

    if (-not $DataDir) { throw 'seed data dir required' }
    $journalDir = Join-Path $DataDir 'journal'
    if (Test-Path $DataDir) { Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $journalDir | Out-Null

    $account = '11111111-1111-1111-1111-111111111111'
    $tokenA = [Guid]::NewGuid().ToString('N')
    $tokenB = [Guid]::NewGuid().ToString('N')
    $detailsA = "contained fault in fx-cycle: probe $tokenA"
    $detailsB = "contained fault in deal-feed: probe $tokenB"

    $now = [DateTimeOffset]::UtcNow
    $rows = @(
        @{ Timestamp = $now.AddMinutes(-9).ToString('o'); AccountId = $account; Category = 'APP_FAULT'; Details = 'contained fault in fx-supervisor: older context row' },
        @{ Timestamp = $now.AddMinutes(-7).ToString('o'); AccountId = $account; Category = 'FX_MODE'; Details = 'paper soak restored — seeded context row' },
        @{ Timestamp = $now.AddMinutes(-3).ToString('o'); AccountId = $account; Category = 'APP_FAULT'; Details = $detailsB },
        @{ Timestamp = $now.AddMinutes(-1).ToString('o'); AccountId = $account; Category = 'APP_FAULT'; Details = $detailsA }
    )

    # File.WriteAllLines writes UTF-8 WITHOUT a BOM: a BOM would corrupt the
    # first JSONL line for the app's per-line deserializer.
    $file = Join-Path $journalDir ("journal_{0}.jsonl" -f $now.ToString('yyyyMMdd'))
    $lines = @($rows | ForEach-Object { $_ | ConvertTo-Json -Compress })
    [System.IO.File]::WriteAllLines($file, $lines)
    [System.IO.File]::WriteAllText(
        (Join-Path $DataDir 'settings.json'), '{"FxBrainRunning":false}')

    return @{
        DataDir  = $DataDir
        TokenA   = $tokenA
        TokenB   = $tokenB
        DetailsA = $detailsA
        DetailsB = $detailsB
    }
}

function Get-AutomationWindow {
    param([int]$ProcessId, [int]$TimeoutSeconds = 45)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($win) { return $win }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Wait-AutomationElement {
    param($Scope, [string]$Name, [int]$TimeoutSeconds = 15)
    $cond = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $el = $Scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        if ((Get-Date) -ge $deadline) { return $null }
        Start-Sleep -Milliseconds 400
    }
}

# Finds a descendant of the given control type, with an optional name match.
function Wait-AutomationByType {
    param($Scope, $ControlType, [string]$Name = '', [int]$TimeoutSeconds = 15)
    $cond = if ($Name) {
        [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType),
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, $Name))
    }
    else {
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $el = $Scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        if ((Get-Date) -ge $deadline) { return $null }
        Start-Sleep -Milliseconds 400
    }
}

# A journal DataGrid row whose descendant text contains the probe token.
function Wait-AutomationRowContaining {
    param($Scope, [string]$Text, [int]$TimeoutSeconds = 20)
    $rowCond = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)
    $textCond = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $rows = $Scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $rowCond)
        foreach ($row in $rows) {
            if ($row.Current.Name -and $row.Current.Name.Contains($Text)) { return $row }
            $texts = $row.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)
            foreach ($t in $texts) {
                if ($t.Current.Name -and $t.Current.Name.Contains($Text)) { return $row }
            }
        }
        if ((Get-Date) -ge $deadline) { return $null }
        Start-Sleep -Milliseconds 400
    }
}

# The "Copy details" entry of the row context menu — a top-level popup, so it
# is searched from the desktop root and pinned to ControlType.MenuItem (the
# toolbar's own "Copy details" is a Button and must not match).
function Wait-AutomationMenuItem {
    param([string]$Name, [int]$TimeoutSeconds = 10)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::MenuItem))
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Invoke-AutomationElement {
    param($Element)
    $p = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $p.Invoke()
}

function Select-AutomationItem {
    param($Element)
    try {
        $p = $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $p.Select()
    }
    catch { }
    try { $Element.SetFocus() } catch { }
}

function Set-AppForeground {
    param($Process)
    try {
        $Process.Refresh()
        $hwnd = $Process.MainWindowHandle
        if ($hwnd -ne [IntPtr]::Zero) {
            [DongGfxNativeInput]::SetForegroundWindow($hwnd) | Out-Null
        }
    }
    catch { }
}

function Get-ClipboardText {
    try { return (Get-Clipboard -Raw -ErrorAction Stop) } catch { }
    try { return [System.Windows.Forms.Clipboard]::GetText() } catch { return '' }
}

# Screen-absolute crop region anchored just above the row (so the toolbar's
# Copy-details button + CopyStatus line are in frame) and a little below it.
function Get-JournalRegion {
    param($Row)
    $r = $Row.Current.BoundingRectangle
    # Parenthesise each subtraction: the comma operator binds tighter than
    # '-', so an unparenthesised "... - 12," would subtract the array.
    return @(
        ([int][Math]::Round($r.X) - 12),
        ([int][Math]::Round($r.Y) - 170),
        ([int][Math]::Round($r.Width) + 24),
        ([int][Math]::Round($r.Height) + 210)
    )
}

function Get-RowPoint {
    param($Row)
    $r = $Row.Current.BoundingRectangle
    return @(
        [int][Math]::Round($r.X + 60),
        [int][Math]::Round($r.Y + ($r.Height / 2))
    )
}

# A real mouse click at a screen point. UI Automation has no click primitive,
# and the app's right-click handler hit-tests the actual mouse event.
function Send-MouseClick {
    param([int]$X, [int]$Y, [ValidateSet('left', 'right')][string]$Button = 'left')
    [DongGfxNativeInput]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 250
    if ($Button -eq 'right') {
        [DongGfxNativeInput]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)   # RIGHTDOWN
        [DongGfxNativeInput]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)   # RIGHTUP
    }
    else {
        [DongGfxNativeInput]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # LEFT DOWN
        [DongGfxNativeInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # LEFT UP
    }
    Start-Sleep -Milliseconds 350
}

# Grow a crop region to include a second screen rectangle: the context-menu
# popup can open to the left of the grid, off the row-anchored crop.
function Join-Region {
    param([int[]]$Region, $Rect)
    $ux = [Math]::Min($Region[0], [Math]::Floor($Rect.X) - 16)
    $uy = [Math]::Min($Region[1], [Math]::Floor($Rect.Y) - 16)
    $ur = [Math]::Max($Region[0] + $Region[2], [Math]::Ceiling($Rect.Right) + 16)
    $ub = [Math]::Max($Region[1] + $Region[3], [Math]::Ceiling($Rect.Bottom) + 16)
    return @(
        [int]$ux,
        [int]$uy,
        ([int]$ur - [int]$ux),
        ([int]$ub - [int]$uy)
    )
}

function Save-JournalShot {
    param([string]$FileName, [string]$Label, [int[]]$Region, $Bounds)
    $shot = Get-ScreenCapture
    try {
        $crop = "{0},{1},{2},{3}" -f ($Region[0] - $Bounds.X), ($Region[1] - $Bounds.Y),
            $Region[2], $Region[3]
        $r = @(Resolve-CropRect -ImageWidth $shot.Width -ImageHeight $shot.Height `
            -Surface 'full' -Crop $crop)
        if (-not $Label) { $Label = $FileName }
        $final = New-CroppedLabeledImage -Source $shot -X $r[0] -Y $r[1] `
            -Width $r[2] -Height $r[3] -Label $Label
        if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
        $target = Join-Path $OutDir ("{0}.png" -f $FileName)
        $final.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
        $final.Dispose()
        Write-Output "wrote $target ($($r[2])x$($r[3] + $LabelBandHeight), crop $($r[0]),$($r[1]),$($r[2]),$($r[3]))"
    }
    finally {
        $shot.Dispose()
    }
}

# Drive the seeded journal copy flow: right-click → "Copy details", then
# select another row and Ctrl+C, verifying the clipboard at both steps and
# writing one labeled screenshot per step. Launch-and-drive only; the flow
# refuses to run against an already-running app.
function Invoke-JournalCopyFlow {
    param(
        [string]$AppExe,
        [string]$Name,
        [string]$OutDir,
        [string]$DataDir,
        [int]$StartupDelaySeconds
    )

    if (-not (Test-Path $AppExe)) { throw "app not found: $AppExe (build it first)" }

    # Never touch a live session — this flow seeds and drives its own instance.
    $existing = @(Get-Process -Name 'DongGfx' -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        foreach ($p in $existing) { $p.Dispose() }
        throw 'DongGfx.exe is already running — close it before the journal copy flow, which launches and drives its own seeded instance.'
    }

    Initialize-NativeInput
    Initialize-UiAutomation

    $seedDir = if ($DataDir) { $DataDir } else { Join-Path $env:TEMP 'tf-ui-evidence-journal' }
    $seed = New-JournalSeed -DataDir $seedDir
    Write-Output "seeded journal: $seedDir"

    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $env:TF_DATA_DIR = $seedDir
    $proc = $null
    try {
        Write-Output "launching $AppExe (TF_DATA_DIR=$seedDir)…"
        $proc = Start-Process -FilePath $AppExe -PassThru
        Start-Sleep -Seconds $StartupDelaySeconds

        $win = Get-AutomationWindow -ProcessId $proc.Id -TimeoutSeconds 45
        if (-not $win) { throw "no main window appeared for pid $($proc.Id)" }
        Write-Output "window: $($win.Current.Name)"

        # 1) Bring the seeded APP_FAULT rows up. Primary path is the dashboard
        # fault notice → its "View faults in the journal" button (which also
        # exercises ShowCategory selecting the newest fault row).
        $viewFaults = Wait-AutomationElement -Scope $win -Name 'View faults in the journal' -TimeoutSeconds 25
        if ($viewFaults) {
            Invoke-AutomationElement $viewFaults
            Write-Output 'journal: opened via the dashboard fault notice (APP_FAULT filter)'
        }
        else {
            $tab = Wait-AutomationElement -Scope $win -Name 'Journal' -TimeoutSeconds 20
            if (-not $tab) { throw 'Journal tab not found' }
            Select-AutomationItem $tab
            $refresh = Wait-AutomationElement -Scope $win -Name 'Refresh' -TimeoutSeconds 15
            if (-not $refresh) { throw 'Journal Refresh button not found' }
            Invoke-AutomationElement $refresh
            Write-Output 'journal: opened via the Journal tab Refresh button'
        }
        Start-Sleep -Milliseconds 800

        # 2) Pick the two seeded probe rows out of the grid by token.
        $rowA = Wait-AutomationRowContaining -Scope $win -Text $seed.TokenA -TimeoutSeconds 25
        $rowB = Wait-AutomationRowContaining -Scope $win -Text $seed.TokenB -TimeoutSeconds 15
        if (-not $rowA -or -not $rowB) {
            throw "seeded rows did not render (tokenA=$($seed.TokenA) tokenB=$($seed.TokenB))"
        }
        $region = Get-JournalRegion -Row $rowA

        # 3) Select row A with a REAL left click (user-like: focuses the grid
        # and shows the selection highlight) and capture the starting state.
        Set-AppForeground $proc
        $ptA = Get-RowPoint -Row $rowA
        Send-MouseClick -X $ptA[0] -Y $ptA[1] -Button left
        Start-Sleep -Milliseconds 300
        Save-JournalShot -FileName "$Name-selected" -Label 'journal copy · row selected' `
            -Region $region -Bounds $bounds

        # 4) Right-click row A → context menu → "Copy details".
        Set-AppForeground $proc
        Send-MouseClick -X $ptA[0] -Y $ptA[1] -Button right

        $menuItem = Wait-AutomationMenuItem -Name 'Copy details' -TimeoutSeconds 10
        if (-not $menuItem) { throw 'context menu did not open (no "Copy details" MenuItem found)' }
        Start-Sleep -Milliseconds 300
        # The popup can land off the row-anchored crop, so frame the union.
        $menuRegion = Join-Region -Region $region -Rect $menuItem.Current.BoundingRectangle
        Save-JournalShot -FileName "$Name-context-menu" `
            -Label 'journal copy · right-click context menu' -Region $menuRegion -Bounds $bounds

        Invoke-AutomationElement $menuItem
        Start-Sleep -Milliseconds 500
        $clip1 = Get-ClipboardText
        if ($clip1.TrimEnd("`r", "`n") -ne $seed.DetailsA) {
            throw "right-click copy mismatch: clipboard='$clip1' expected='$($seed.DetailsA)'"
        }
        Write-Output "right-click copy OK: $($clip1.Length) chars"
        Save-JournalShot -FileName "$Name-context-copied" `
            -Label 'journal copy · right-click copied' -Region $menuRegion -Bounds $bounds

        # 5) Select row B with a REAL left click — the user-like act that both
        # focuses the grid and selects the row — then send Ctrl+C with a real
        # key event. This proves the keyboard path copies the NEWLY selected
        # row, not the right-click leftover.
        $ptB = Get-RowPoint -Row $rowB
        Send-MouseClick -X $ptB[0] -Y $ptB[1] -Button left
        Set-AppForeground $proc
        Start-Sleep -Milliseconds 200
        [DongGfxNativeInput]::keybd_event(0x11, 0, 0x0000, [UIntPtr]::Zero)   # Ctrl down
        [DongGfxNativeInput]::keybd_event(0x43, 0, 0x0000, [UIntPtr]::Zero)   # C down
        [DongGfxNativeInput]::keybd_event(0x43, 0, 0x0002, [UIntPtr]::Zero)   # C up
        [DongGfxNativeInput]::keybd_event(0x11, 0, 0x0002, [UIntPtr]::Zero)   # Ctrl up
        Start-Sleep -Milliseconds 600
        $clip2 = Get-ClipboardText
        if ($clip2.TrimEnd("`r", "`n") -ne $seed.DetailsB) {
            throw "Ctrl+C copy mismatch: clipboard='$clip2' expected='$($seed.DetailsB)'"
        }
        Write-Output "Ctrl+C copy OK: $($clip2.Length) chars"
        Save-JournalShot -FileName "$Name-ctrlc" `
            -Label 'journal copy · Ctrl+C copied' -Region $region -Bounds $bounds

        Write-Output 'journal copy flow PASSED: right-click and Ctrl+C both copied the selected row'
    }
    finally {
        if ($proc -and -not $proc.HasExited) {
            Write-Output "closing the app instance this run launched (pid $($proc.Id))"
            try { $proc.CloseMainWindow() | Out-Null } catch { }
            Start-Sleep -Seconds 2
            if (-not $proc.HasExited) { try { $proc.Kill() } catch { } }
        }
    }
}

# ── self-test: exercise crop+label and the journal seed without the app ─────
if ($SelfTest) {
    $src = New-Object System.Drawing.Bitmap 400, 300
    $fill = [System.Drawing.Graphics]::FromImage($src)
    $fill.Clear([System.Drawing.Color]::SteelBlue)
    $fill.Dispose()

    $out = New-CroppedLabeledImage -Source $src -X 10 -Y 20 -Width 200 -Height 150 `
        -Label 'self-test'
    $ok = ($out.Width -eq 200) -and ($out.Height -eq (150 + $LabelBandHeight))
    $out.Dispose(); $src.Dispose()
    if (-not $ok) { throw "self-test failed: unexpected output dimensions" }

    # The seed writer must produce parseable JSONL lines the app can read.
    $seedDir = Join-Path $env:TEMP 'tf-ui-evidence-selftest'
    $seed = New-JournalSeed -DataDir $seedDir
    $journalFile = Get-ChildItem -Path (Join-Path $seedDir 'journal') -Filter 'journal_*.jsonl' |
        Select-Object -First 1
    if (-not $journalFile) { throw 'self-test failed: seed journal not written' }
    $lines = @(Get-Content -LiteralPath $journalFile.FullName |
        Where-Object { $_.Trim().Length -gt 0 })
    foreach ($line in $lines) { $null = $line | ConvertFrom-Json }
    $hasProbes = ($lines -join "`n").Contains($seed.TokenA) -and
                 ($lines -join "`n").Contains($seed.TokenB)
    Remove-Item -Recurse -Force $seedDir -ErrorAction SilentlyContinue
    if ($lines.Count -ne 4 -or -not $hasProbes) {
        throw "self-test failed: unexpected seed journal ($($lines.Count) lines)"
    }

    Write-Output 'self-test OK: crop+label produces 200x176 PNG; journal seed is 4 parseable rows'
    return
}

# ── seeded journal copy regression ──────────────────────────────────────────
if ($JournalCopyFlow) {
    Invoke-JournalCopyFlow -AppExe $AppExe -Name $Name -OutDir $OutDir `
        -DataDir $DataDir -StartupDelaySeconds $StartupDelaySeconds
    return
}

# ── optionally bring the app up on a seeded data dir ────────────────────────
$launched = $null
if ($Launch) {
    if (-not (Test-Path $AppExe)) { throw "app not found: $AppExe (build it first)" }
    if ($DataDir) { $env:TF_DATA_DIR = $DataDir }
    Write-Output "launching $AppExe (TF_DATA_DIR=$($env:TF_DATA_DIR))…"
    $launched = Start-Process -FilePath $AppExe -PassThru
    Start-Sleep -Seconds $StartupDelaySeconds
}

# ── load or grab the source image ───────────────────────────────────────────
$source = $null
$disposeSource = $false
try {
    if ($SourceImage) {
        if (-not (Test-Path $SourceImage)) { throw "source image not found: $SourceImage" }
        $source = [System.Drawing.Image]::FromFile((Resolve-Path $SourceImage).Path)
    }
    else {
        $source = Get-ScreenCapture
    }
    $disposeSource = $true

    $rect = @(Resolve-CropRect -ImageWidth $source.Width -ImageHeight $source.Height `
        -Surface $Surface -Crop $Crop)
    $x = $rect[0]; $y = $rect[1]; $w = $rect[2]; $h = $rect[3]
    if (-not $Label) { $Label = "$Name · $Surface" }

    $final = New-CroppedLabeledImage -Source $source -X $x -Y $y -Width $w -Height $h -Label $Label

    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
    $target = Join-Path $OutDir ("{0}.png" -f $Name)
    $final.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
    $final.Dispose()

    Write-Output "wrote $target ($($w)x$($h + $LabelBandHeight), crop $x,$y,$w,$h)"
}
finally {
    if ($disposeSource -and $source) { $source.Dispose() }
    if ($launched -and -not $launched.HasExited) {
        # Only close what we opened; never touch a pre-existing instance.
        Write-Output "closing the app instance this run launched (pid $($launched.Id))"
        try { $launched.CloseMainWindow() | Out-Null } catch { }
    }
}
