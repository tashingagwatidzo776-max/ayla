# tirekick: drive a running DongGfx window (UIA locate + real mouse
# injection) for manual tire-kick sessions — the same way a user's clicks
# land. The newest DongGfx process wins when several are running.
#
# Usage:  powershell -File scripts/tirekick.ps1 <cmd> [args]
#   dump                      print every named element with its rect
#   rect   -Name <n>          print the BoundingRectangle of a name match
#   rc|lc  -X <x> -Y <y>      right/left click at screen coords
#   drag   -X <x> -Y <y> -X2 <x2> -Y2 <y2> [-Button right|left]
#   shot   -Path <p>          full-screen PNG (a moment-in-time look)
#
# Notes: WPF popups (context menus) are separate top-level windows and do
# NOT appear in the main window's UIA tree; on-screen rects are the truth
# for click coordinates. Synthetic clicks need the target window truly
# foreground — activate it first or the events land on whatever overlaps.
param(
  [string]$ProcName = "DongGfx",
  [Parameter(Position=0)][string]$Cmd,
  [string]$Name, [int]$X, [int]$Y, [int]$X2, [int]$Y2,
  [string]$Button = "right", [string]$Path = "/tmp/shot.png"
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class M {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  public const uint DOWN=0x02, UP=0x04, RD=0x08, RU=0x10;
  public static void Click(int x,int y,bool right){ SetCursorPos(x,y); System.Threading.Thread.Sleep(80);
    uint d=right?RD:DOWN, u=right?RU:UP; mouse_event(d,0,0,0,UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    mouse_event(u,0,0,0,UIntPtr.Zero); }
  public static void Drag(int x,int y,int x2,int y2,bool right){ SetCursorPos(x,y); System.Threading.Thread.Sleep(80);
    uint d=right?RD:DOWN, u=right?RU:UP; mouse_event(d,0,0,0,UIntPtr.Zero); System.Threading.Thread.Sleep(90);
    int steps=14; for(int i=1;i<=steps;i++){ SetCursorPos(x+(x2-x)*i/steps, y+(y2-y)*i/steps); System.Threading.Thread.Sleep(28); }
    System.Threading.Thread.Sleep(70); mouse_event(u,0,0,0,UIntPtr.Zero); }
}
"@
$procs = Get-Process $ProcName -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending
if (-not $procs) { Write-Host "APP_NOT_FOUND"; exit 1 }
$pid2 = $procs[0].Id
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pid2)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Host "WINDOW_NOT_FOUND pid=$pid2"; exit 1 }

switch ($Cmd) {
  "dump" {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $i=0; foreach ($el in $all) { $c=$el.Current
      if ($c.Name -and $c.Name.Trim().Length -gt 0) {
        $r = $c.BoundingRectangle
        Write-Host ("[{0}] {1} | {2} | ({3},{4}) {5}x{6}" -f $i,$c.ControlType.ProgrammaticName,$c.Name,[int]$r.X,[int]$r.Y,[int]$r.Width,[int]$r.Height) }
      $i++ }
  }
  "rect" {
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    if ($el) { $r = $el.Current.BoundingRectangle; Write-Host ("{0},{1} {2}x{3}" -f [int]$r.X,[int]$r.Y,[int]$r.Width,[int]$r.Height) } else { Write-Host "NOT_FOUND" }
  }
  "rc" { [M]::Click($X,$Y,$true);  Write-Host "rc $X $Y done" }
  "lc" { [M]::Click($X,$Y,$false); Write-Host "lc $X $Y done" }
  "drag" { [M]::Drag($X,$Y,$X2,$Y2,($Button -eq "right")); Write-Host "drag done" }
  "shot" {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(0,0,0,0,$bmp.Size); $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); Write-Host "saved $Path" }
  default { Write-Host "unknown cmd $Cmd" }
}
