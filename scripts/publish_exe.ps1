# Publishes the native Windows exe.
#
# Default: self-contained single-file "DongGfx.exe" (win-x64, no .NET needed on
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
$proj = Join-Path $root "src\DongGfx.App\DongGfx.App.csproj"
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
$stderrFile = Join-Path $env:TEMP "tf-publish-describe-stderr.txt"
# PS 5.1 wraps native stderr in error records under EAP=Stop (fatal even when
# redirected), so relax EAP for these probes and decide on $LASTEXITCODE.
$eapSaved = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$tag = git describe --exact-match --tags 2> $stderrFile
if ($LASTEXITCODE -ne 0) { $tag = $null }
$ErrorActionPreference = $eapSaved
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
         else {
             $eapSaved = $ErrorActionPreference
             $ErrorActionPreference = "Continue"
             $describeLong = git describe --tags --long --always --dirty 2> $stderrFile
             if ($LASTEXITCODE -ne 0) { $describeLong = $null }
             $ErrorActionPreference = $eapSaved
         }
if ($stamp) { Write-Host "Stamping version: $stamp" }

# Clean the output dir first: dotnet publish merges into whatever is
# already there, so a previous (possibly stale or failed) publish's files
# would survive into this run's output. A published exe must contain only
# this run's bytes.
if (Test-Path $out) {
    Remove-Item -Recurse -Force $out
    Write-Host "Cleaned stale publish output: $out"
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
if ($stamp) {
    $args += "-p:InformationalVersion=$stamp"
}

& dotnet $args
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)"
}

# The GWEN host runner MUST ship with the app. GwenHostSupervisor's
# primary resolve path is "DongGfx.GwenHost.exe beside the app" (no
# TF_GWEN_HOST_EXE override is set on the desk), and a deploy without
# the host leaves GWEN silently disabled - fail-neutral by contract,
# but the confluence gate would never run on the live desk. Publishing
# it into the SAME output dir means deploy-app.ps1's robocopy carries
# both binaries together, and a publish can never again produce an app
# without its model runner (see the throw below).
$hostProj = Join-Path $root "src\DongGfx.GwenHost\DongGfx.GwenHost.csproj"
$hostArgs = @(
    "publish", $hostProj,
    "-c", $Configuration,
    "-r", $rid,
    "-o", $out,
    "--self-contained", $(if ($FrameworkDependent) { "false" } else { "true" })
)
# The host is ALWAYS multi-file, even when the app is single-file:
# LLamaSharp loads llama-bllas itself from disk (AppContext.BaseDirectory)
# and cannot see a native library hidden inside a single-file bundle or
# its self-extract temp dir. Observed 2026-10-24: a single-file host build
# failed every request with TypeInitializationException (NativeApi) -
# probe FAILED in 0.2s - while this multi-file layout is exactly what the
# desk's passing E2E runs execute from bin\Release\net8.0. robocopy /E
# carries the whole folder to the app dir beside DongGfx.exe, which is
# where GwenHostSupervisor.ResolveHostExe looks.
& dotnet $hostArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (DongGfx.GwenHost) failed (exit $LASTEXITCODE)"
}
if (-not (Test-Path (Join-Path $out 'DongGfx.GwenHost.exe'))) {
    throw "DongGfx.GwenHost.exe missing from $out after host publish - refusing to ship an app without its GWEN runner"
}

# LLamaSharp's package copies its five native libs (llama, ggml,
# ggml-base, ggml-cpu, mtmd) to the BUILD output root, but they are not
# content items, so `dotnet publish -o` does not transport them - while
# the loader only probes the app root. Observed 2026-10-24: without this
# copy every /api/chat fails in 0.1s with TypeInitializationException
# (NativeApi), and `runtimes\win-x64\native\avx2\llama.dll` (where
# publish DOES put them) is not a path LLamaSharp searches. Copy the
# exact libs the build selected (avx2 on this desk) beside the published
# exe - byte-identical to the bin layout the passing E2E runs use.
$nativeFrom = Join-Path $root "src\DongGfx.GwenHost\bin\$Configuration\net8.0\$rid"
$nativeLibs = @('llama.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-cpu.dll', 'mtmd.dll')
foreach ($n in $nativeLibs) {
    $ns = Join-Path $nativeFrom $n
    if (-not (Test-Path $ns)) {
        throw "LLamaSharp native $n not found in $nativeFrom - host would ship unloadable; check the LLamaSharp.Backend package layout"
    }
    Copy-Item -LiteralPath $ns -Destination $out -Force
}
if (-not (Test-Path (Join-Path $out 'llama.dll'))) {
    throw "llama.dll missing from $out after native copy"
}

if ($isReleaseTag) {
    Write-Host "Shipped from release tag '$tag' - real-money gate drill was green on this commit." -ForegroundColor Green
}

Write-Host ""
Write-Host "Published: $out" -ForegroundColor Green
if (-not $FrameworkDependent) {
    Write-Host "Exe: $(Join-Path $out 'DongGfx.exe')  (self-contained - run on any 64-bit Windows PC)" -ForegroundColor Green
} else {
    Write-Host "Exe: $(Join-Path $out 'DongGfx.exe')  (needs the .NET 8 Desktop Runtime installed)" -ForegroundColor Green
}
Write-Host "GWEN host: $(Join-Path $out 'DongGfx.GwenHost.exe')  (ships beside the app - supervisor resolves it there)" -ForegroundColor Green
