param([int]$ProcId = 0)

# Dumps every named UIA element of the DongGfx main window. Useful for
# discovering automation names before writing UIA scripts.
# Usage: powershell -File scripts/dump_ui.ps1 [-ProcId 1234]
# Without -ProcId the first running DongGfx process is used.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement

if ($ProcId -eq 0) {
  $proc = Get-Process -Name DongGfx -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $proc) { Write-Host "APP_NOT_RUNNING"; exit 1 }
  $ProcId = $proc.Id
}

$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcId)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Host "WINDOW_NOT_FOUND (pid=$ProcId)"; exit }
Write-Host ("TITLE: " + $win.Current.Name)
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.Condition]::TrueCondition)
foreach ($el in $all) {
  $n = $el.Current.Name
  if ($n -and $n.Trim().Length -gt 0) { Write-Host $n }
}
