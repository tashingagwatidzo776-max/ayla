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

& dotnet $args
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)"
}

Write-Host ""
Write-Host "Published: $out" -ForegroundColor Green
if (-not $FrameworkDependent) {
    Write-Host "Exe: $(Join-Path $out 'Tf.exe')  (self-contained — run on any 64-bit Windows PC)" -ForegroundColor Green
} else {
    Write-Host "Exe: $(Join-Path $out 'Tf.exe')  (needs the .NET 8 Desktop Runtime installed)" -ForegroundColor Green
}
