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
    Invoke-Step 'build' { dotnet build DongGfx.sln --nologo -v q }
}
else {
    Write-Host 'Skipping build (-SkipBuild)'
}

Invoke-Step 'unit' {
    dotnet test tests/DongGfx.Core.Tests/DongGfx.Core.Tests.csproj --no-build --nologo -v q
}
Invoke-Step 'integration' {
    # Exclude Category=Uia: the UIA smoke launches the real app against the
    # live %APPDATA%\tf\data dir (freshening the journal), which both breaks
    # the Shared-DataDir suites' LiveSoakGuard and violates the smoke's own
    # contract — it runs in CI's dedicated uia-smoke job, not local gates.
    dotnet test tests/DongGfx.App.Tests/DongGfx.App.Tests.csproj --no-build --nologo -v q `
        --filter "Category!=Uia"
}
Invoke-Step 'workflow-lint' {
    python scripts/lint_workflows.py
}
Invoke-Step 'safety-audit' {
    python scripts/check_safety_audit.py
}
Invoke-Step 'rail-traits' {
    python scripts/check_rail_traits.py
}
# The in-repo portable MT5 terminal (mt5_portable/, git-ignored) must still
# match the SHA-256 manifest recorded from its source install. Skips cleanly
# when the copy is absent; fails on any modified/missing/unexpected file.
Invoke-Step 'mt5-copy integrity' {
    & "$PSScriptRoot/mt5_copy_integrity.ps1"
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
    # PS 5.1 + $ErrorActionPreference='Stop' turns any native stderr output
    # under 2>&1 into a terminating NativeCommandError — which would kill
    # this script before the exit-code classification below ran. The
    # ErrorRecord stream is captured into $out instead and replayed after.
    $ErrorActionPreference = 'Continue'
    $out = & $ghArgs[0] $ghArgs[1..($ghArgs.Count - 1)] @args 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $out | ForEach-Object { Write-Host $_ }
    switch ($code) {
        0 { Write-Host 'OK: merge preview' -ForegroundColor Green }
        1 {
            Write-Host 'FAILED: merge preview (PR cannot merge despite green CI - see findings above)' -ForegroundColor Red
            $failed += 'merge preview'
        }
        3 {
            # Remedial push: the pushed branch IS the open PR's head, so every
            # finding described the pre-push head. The push itself delivers the
            # next state; the merge gate re-checks at merge time. (This is why
            # delivering a rebase no longer needs git push --no-verify.)
            Write-Host 'DEFERRED: merge preview findings are remedial - this push delivers the PR head; merge gate re-checks at merge time.' -ForegroundColor Yellow
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
