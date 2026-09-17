# Publishes the native Windows exe.
#
# Default: self-contained single-file "Tf.exe" (win-x64, no .NET needed on
# the target PC). Pass -FrameworkDependent for a small exe + shared runtime.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\publish_exe.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\publish_exe.ps1 -FrameworkDependent

param(
    [switch]$FrameworkDependent,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\Tf.App\Tf.App.csproj"
$rid = "win-x64"
$outRoot = Join-Path $root "publish"
$out = if ($FrameworkDependent) { Join-Path $outRoot "$rid-framework-dependent" } else { Join-Path $outRoot $rid }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet CLI not found on PATH. Install the .NET 8 SDK first."
}

# Release preflight: the real-money gate is the rail between this build and
# live funds, so a shipped binary must come from a commit whose gate drill
# passed. Release tags get that proof from the `release-gate` job in
# gate-drill.yml (CI runs the drill on every v* tag); this script simply
# refuses to publish from a tag whose drill failed or never ran. Non-release
# builds (no tag, local iteration) skip the check entirely.
$tag = git describe --exact-match --tags 2>$null
$isReleaseTag = $false
$drillOk = $false
if ($LASTEXITCODE -eq 0 -and $tag -match '^v') {
    $isReleaseTag = $true
    $run = gh run list --workflow gate-drill.yml --branch $tag --limit 5 --json databaseId,conclusion | ConvertFrom-Json
    $drillOk = @($run | Where-Object { $_.conclusion -eq 'success' }).Count -gt 0
    if (-not $drillOk) {
        $msg = @(
            "",
            "  RELEASE BLOCKED: no passing real-money gate drill for tag '$tag'.",
            "",
            "  Re-run the drill:  gh workflow run gate-drill.yml --ref $tag",
            "  Watch it:          gh run watch (then re-run this script)",
            "",
            "  A release may not ship until the gate lifecycle rehearsal (locked",
            "  start, unlock, mid-session stop) passes on the tagged commit.",
            ""
        ) -join [Environment]::NewLine
        throw $msg
    }
    Write-Host "Gate drill verified green for $tag - release may proceed." -ForegroundColor Green
}

# Stamp the binary's identity (window title + About box): the release tag
# when publishing from one, else the git describe of the current commit.
$stamp = if ($isReleaseTag) { "$tag+$((git rev-parse HEAD).Trim())" }
         else { (git describe --tags --long --always --dirty 2>$null) }
if ($stamp) { Write-Host "Stamping version: $stamp" }

$args = @(
    "publish", $proj,
    "-c", $Configuration,
    "-r", $rid,
    "-o", $out,
    "--self-contained", $(if ($FrameworkDependent) { "false" } else { "true" })
)

if (-not $FrameworkDependent) {
    # Single-file exe: the runtime is embedded and extracted on first run.
    $args += @(
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true"
    )
}
if ($stamp) {
    $args += "-p:InformationalVersion=$stamp"
}

& dotnet $args
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)"
}

if ($isReleaseTag) {
    Write-Host "Shipped from release tag '$tag' - real-money gate drill was green on this commit." -ForegroundColor Green
}

Write-Host ""
Write-Host "Published: $out" -ForegroundColor Green
if (-not $FrameworkDependent) {
    Write-Host "Exe: $(Join-Path $out 'Tf.exe')  (self-contained - run on any 64-bit Windows PC)" -ForegroundColor Green
} else {
    Write-Host "Exe: $(Join-Path $out 'Tf.exe')  (needs the .NET 8 Desktop Runtime installed)" -ForegroundColor Green
}
