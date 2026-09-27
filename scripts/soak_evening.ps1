# Evening soak routine wrapper — runs the analyst + alpha rerun and logs.
# Scheduled daily at 20:30 (schtasks task "tf-soak-evening").
# The evidence doc change (docs/soak/SOAK-<date>.md) is left UNCOMMITTED for
# human review — committing evidence is a deliberate act, never automatic.

$ErrorActionPreference = "Continue"
$logDir = Join-Path $env:LOCALAPPDATA "tf"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir "soak_evening.log"

"=== soak_evening $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Add-Content $log
$py = Join-Path (Split-Path (Get-Command python).Source) "python.exe"
& $py (Join-Path $PSScriptRoot "soak_evening.py") *>> $log
"exit: $LASTEXITCODE" | Add-Content $log
