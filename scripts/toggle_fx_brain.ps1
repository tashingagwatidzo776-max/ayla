# Toggles the FX brain (paper mode) on a running DongGfx instance via UIA.
# Usage: powershell -File scripts/toggle_fx_brain.ps1 [-ProcId 1234]
# Without -ProcId the first running DongGfx process is used. UIA only —
# no mouse, no focus stealing.
param([int]$ProcId = 0)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if ($ProcId -eq 0) {
  $proc = Get-Process -Name DongGfx -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $proc) { Write-Host "APP_NOT_RUNNING"; exit 1 }
  $ProcId = $proc.Id
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcId)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Host "WINDOW_NOT_FOUND (pid=$ProcId)"; exit 1 }

# The FX BRAIN ON/OFF toggle lives in the Terminal tab's FX strip —
# select it so the button is realized in the UIA tree.
$tab = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "Terminal")))
if ($tab) {
  ($tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
  Start-Sleep -Milliseconds 400
}

$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "FX BRAIN ON/OFF")))
if ($btn) {
  ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
  Write-Host "TOGGLED (pid=$ProcId)"
} else {
  Write-Host "TOGGLE_BUTTON_NOT_FOUND"
  exit 1
}
