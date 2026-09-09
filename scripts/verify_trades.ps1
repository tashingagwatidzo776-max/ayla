param([int]$ProcId = 10924)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcId)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Host "WINDOW_NOT_FOUND"; exit }
# find the Trades tab and select it
$tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Brain")
$tab = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
if ($tab) {
  $sel = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $sel.Select()
  Start-Sleep -Milliseconds 500
}
# dump all text
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($el in $all) {
  $n = $el.Current.Name
  if ($n -and $n.Trim().Length -gt 0) { Write-Host $n }
}
