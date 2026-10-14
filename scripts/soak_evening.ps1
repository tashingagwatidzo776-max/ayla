# Evening soak routine wrapper — runs the analyst + alpha rerun and logs.
# Scheduled daily at 20:30 (schtasks task "tf-soak-evening").
# The evidence doc change (docs/soak/SOAK-<date>.md) is left UNCOMMITTED for
# human review — committing evidence is a deliberate act, never automatic.

$ErrorActionPreference = "Continue"
$logDir = Join-Path $env:LOCALAPPDATA "tf"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir "soak_evening.log"

$started = Get-Date
"=== soak_evening $($started.ToString('yyyy-MM-dd HH:mm:ss')) ===" | Add-Content $log -Encoding utf8
$py = Join-Path (Split-Path (Get-Command python).Source) "python.exe"
# Out-File -Encoding utf8: bare *>> defaults to UTF-16LE, which reads as
# mojibake in every editor and breaks the emoji narratives.
& $py (Join-Path $PSScriptRoot "soak_evening.py") 2>&1 |
    ForEach-Object { "$_" } | Out-File $log -Append -Encoding utf8
# Timestamped + duration so terminations are measurable: the Monday
# alpha task died as SCHED_S_TASK_TERMINATED (0x41306) leaving only a
# start line - a start WITHOUT its matching exit line IS the
# termination marker, and completed runs now show how long they take.
$ended = Get-Date
$mins = [math]::Round(($ended - $started).TotalMinutes, 1)
"exit: $LASTEXITCODE at $($ended.ToString('yyyy-MM-dd HH:mm:ss')) (duration $mins min)" | Add-Content $log -Encoding utf8
