# Local pre-PR gate: the same checks CI runs, before the push.
#
# Usage:  pwsh ./scripts/ci-local.ps1 [-SkipBuild] [-Pr 51]
#
# Steps: build -> unit tests -> integration tests -> workflow lint ->
# safety-audit coverage -> merge preview (requires gh auth; skipped cleanly
# when not on a PR branch). Mirrors the required status checks on main so a
# push never fails CI on something this script could have caught first.
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [int]$Pr
)

$ErrorActionPreference = 'Stop'
$failed = @()

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name" -ForegroundColor Red
        $script:failed += $Name
    }
    else {
        Write-Host "OK: $Name" -ForegroundColor Green
    }
}

if (-not $SkipBuild) {
    Invoke-Step 'build' { dotnet build Tf.sln --nologo -v q }
}
else {
    Write-Host 'Skipping build (-SkipBuild)'
}

Invoke-Step 'unit' {
    dotnet test tests/Tf.Core.Tests/Tf.Core.Tests.csproj --no-build --nologo -v q
}
Invoke-Step 'integration' {
    dotnet test tests/Tf.App.Tests/Tf.App.Tests.csproj --no-build --nologo -v q
}
Invoke-Step 'workflow-lint' {
    python scripts/lint_workflows.py
}
Invoke-Step 'safety-audit' {
    python scripts/check_safety_audit.py
}

# Merge preview: only meaningful when the branch has (or will have) a PR and
# gh is authenticated. Missing gh or no PR is a skip, not a failure; a BLOCKED
# verdict fails the gate so the push does not create a PR that cannot merge.
Write-Host "`n=== merge preview ===" -ForegroundColor Cyan
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host 'SKIP: gh not found'
}
else {
    $ghArgs = @('python', 'scripts/check_mergeability.py')
    if ($Pr -gt 0) { $ghArgs += "$Pr" }
    $out = & $ghArgs[0] $ghArgs[1..($ghArgs.Count - 1)] @args 2>&1
    $code = $LASTEXITCODE
    $out | ForEach-Object { Write-Host $_ }
    switch ($code) {
        0 { Write-Host 'OK: merge preview' -ForegroundColor Green }
        1 {
            Write-Host 'FAILED: merge preview (PR cannot merge despite green CI - see findings above)' -ForegroundColor Red
            $failed += 'merge preview'
        }
        default { Write-Host "SKIP: merge preview unavailable (exit $code)" -ForegroundColor Yellow }
    }
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "ci-local: $($failed.Count) check(s) failed: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host 'ci-local: all checks passed' -ForegroundColor Green
exit 0
