# Guards the in-repo portable MT5 terminal (mt5_portable/) against drift from
# the source terminal it was copied from.
#
#   pwsh ./scripts/mt5_copy_integrity.ps1 -Record   # (re)build manifest from source
#   pwsh ./scripts/mt5_copy_integrity.ps1           # verify the in-repo copy
#   pwsh ./scripts/mt5_copy_integrity.ps1 -Restore  # repair copy from source
#
# The manifest (scripts/mt5-copy.sha256) is committed; the terminal itself is
# not (two of its executables exceed GitHub's 100 MB per-file limit). CI runs
# the verify leg on every push: runners carry the manifest but not the copy
# (mt5_portable/ is git-ignored), so there the check proves the manifest is
# present and parseable and reports "copy absent" as a skip. On a checkout
# that DOES carry the copy — this machine, a release box — any missing,
# extra or byte-changed file fails the run.
#
# Runtime paths (Tester/, Logs/, temp/, liveupdate/) are excluded from both
# sides: launching the terminal deletes obsolete tester files and appends logs,
# and that is expected behaviour, not drift.
#
# KNOWN MUTATION: MetaTrader 5 self-updates. A terminal that is launched from
# mt5_portable/ rewrites MetaEditor64.exe / metatester64.exe (and stages a new
# terminal64.exe) in the copy while the source keeps its original build — the
# verify leg then fails on exactly those files. Fix with -Restore (frozen copy,
# source state wins) or -Record after accepting the new build deliberately.
[CmdletBinding()]
param(
    # Rebuild the manifest from the source terminal instead of verifying.
    [switch]$Record,
    # Repair the in-repo copy from the source (changed/missing files only).
    # The source is never written to; only files INSIDE the copy are replaced.
    [switch]$Restore,
    # The terminal the in-repo copy was made from.
    [string]$Source = 'C:\Users\DELL\Documents\hit n run\mt5_portable',
    # The in-repo copy itself (repo root / mt5_portable) and the manifest.
    # Resolved in the body: $PSScriptRoot is empty inside param() defaults on
    # Windows PowerShell 5.1.
    [string]$Copy,
    [string]$Manifest
)

$ErrorActionPreference = 'Stop'

if (-not $Copy) { $Copy = Join-Path (Split-Path $PSScriptRoot -Parent) 'mt5_portable' }
if (-not $Manifest) { $Manifest = Join-Path $PSScriptRoot 'mt5-copy.sha256' }

# Paths the terminal itself rewrites while running — never treated as drift.
$RuntimePatterns = @('^\\Tester\\', '^\\Logs\\', '^\\temp\\', '^\\liveupdate\\')

function Test-IsRuntimePath {
    param([string]$RelativePath)
    foreach ($pattern in $RuntimePatterns) {
        if ($RelativePath -match $pattern) { return $true }
    }
    return $false
}

# relPath (leading backslash, separator-normalised) -> SHA-256 for every file
# under $root that is not runtime-mutable.
function Get-Tree {
    param([string]$Root)

    $tree = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Force) {
        $rel = $file.FullName.Substring($Root.Length) -replace '/', '\'
        if (Test-IsRuntimePath -RelativePath $rel) { continue }
        $tree[$rel] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $tree
}

function Write-Drift {
    param([string]$Label, [string[]]$Paths)
    foreach ($path in ($Paths | Sort-Object)) {
        Write-Host ("  {0} {1}" -f $Label, $path)
    }
}

if ($Restore) {
    foreach ($required in @($Source, $Copy)) {
        if (-not (Test-Path -LiteralPath $required)) {
            Write-Host "::error::Cannot restore: $required not found"
            exit 1
        }
    }

    $sourceTree = Get-Tree -Root $Source
    $copyTree = Get-Tree -Root $Copy
    $extras = @($copyTree.Keys | Where-Object { -not $sourceTree.ContainsKey($_) })
    $repaired = @()

    foreach ($rel in $sourceTree.Keys) {
        $target = Join-Path $Copy $rel.TrimStart('\')
        if (-not (Test-Path -LiteralPath $target) -or $copyTree[$rel] -ne $sourceTree[$rel]) {
            $origin = Join-Path $Source $rel.TrimStart('\')
            $parent = Split-Path $target -Parent
            if (-not (Test-Path -LiteralPath $parent)) {
                New-Item -ItemType Directory -Force -Path $parent | Out-Null
            }
            # Source is read from only; only files inside the copy are written.
            Copy-Item -LiteralPath $origin -Destination $target -Force
            $repaired += $rel
        }
    }

    foreach ($rel in $repaired) { Write-Host ("  repaired " + $rel) }
    if ($extras.Count -gt 0) {
        foreach ($rel in ($extras | Sort-Object)) { Write-Host ("  unexpected (remove by hand) " + $rel) }
        Write-Host "::warning::Restore repaired $($repaired.Count) file(s) but $($extras.Count) unexpected file(s) remain"
        exit 1
    }

    Write-Host "Restored $($repaired.Count) file(s) from source; copy matches the source tree again"
    exit 0
}

if ($Record) {
    if (-not (Test-Path -LiteralPath $Source)) {
        Write-Error "Source terminal not found: $Source (nothing to record from)"
        exit 1
    }

    $tree = Get-Tree -Root $Source
    $lines = @(
        '# DON G FX - SHA-256 manifest of the portable MT5 terminal copied into',
        '# mt5_portable/. Recorded from the source terminal; verify with',
        '# scripts/mt5_copy_integrity.ps1. Runtime paths are excluded.',
        '# <sha256>  <relative path>'
    )
    foreach ($rel in ($tree.Keys | Sort-Object)) {
        $lines += '{0}  {1}' -f $tree[$rel], $rel.TrimStart('\')
    }

    Set-Content -LiteralPath $Manifest -Value $lines -Encoding ascii
    Write-Host ("Recorded {0} files from {1}" -f $tree.Count, $Source)
    exit 0
}

# --- verify -----------------------------------------------------------------

if (-not (Test-Path -LiteralPath $Manifest)) {
    Write-Host "::error::MT5 copy manifest missing: scripts/mt5-copy.sha256 (run scripts/mt5_copy_integrity.ps1 -Record)"
    exit 1
}

$expected = @{}
foreach ($line in (Get-Content -LiteralPath $Manifest)) {
    if (-not $line.Trim() -or $line.StartsWith('#')) { continue }
    $hash, $rel = $line -split '\s+', 2
    if (-not $rel) {
        Write-Host "::error::Malformed manifest line: $line"
        exit 1
    }
    $expected[('\' + $rel.TrimStart('\'))] = $hash
}

if ($expected.Count -eq 0) {
    Write-Host "::error::MT5 copy manifest is empty — re-record it"
    exit 1
}

if (-not (Test-Path -LiteralPath $Copy)) {
    # CI runners and fresh clones carry the manifest but not the terminal
    # (mt5_portable/ is git-ignored): nothing to compare, so pass quietly.
    Write-Host "mt5 copy absent at $Copy — nothing to verify (manifest OK, $($expected.Count) entries)"
    exit 0
}

$actual = Get-Tree -Root $Copy
$missing = @($expected.Keys | Where-Object { -not $actual.ContainsKey($_) })
$extra   = @($actual.Keys   | Where-Object { -not $expected.ContainsKey($_) })
$changed = @($expected.Keys | Where-Object { $actual.ContainsKey($_) -and $actual[$_] -ne $expected[$_] })

if ($missing.Count + $extra.Count + $changed.Count -gt 0) {
    Write-Host "::error::In-repo MT5 copy drifted from its source ($($missing.Count) missing, $($extra.Count) unexpected, $($changed.Count) modified)"
    Write-Drift -Label 'missing   ' -Paths $missing
    Write-Drift -Label 'unexpected' -Paths $extra
    Write-Drift -Label 'modified  ' -Paths ($changed | ForEach-Object { $_ })
    Write-Host "Repair with: scripts/mt5_copy_integrity.ps1 -Restore (copies the source files back over the drifted ones;"
    Write-Host "            the source itself is never written to), or -Record if the source legitimately changed."
    exit 1
}

# The source itself may have moved on since the manifest was recorded — that
# is drift too, and it silently makes the manifest a lie.
if (Test-Path -LiteralPath $Source) {
    $sourceTree = Get-Tree -Root $Source
    $sourceDiff = @($expected.Keys | Where-Object {
        -not $sourceTree.ContainsKey($_) -or $sourceTree[$_] -ne $expected[$_]
    }) + @($sourceTree.Keys | Where-Object { -not $expected.ContainsKey($_) })

    if ($sourceDiff.Count -gt 0) {
        Write-Host "::error::Source terminal no longer matches the recorded manifest ($sourceDiff.Count entries)"
        Write-Drift -Label 'source    ' -Paths ($sourceDiff | Select-Object -First 20)
        Write-Host "Re-record with scripts/mt5_copy_integrity.ps1 -Record if the source changed on purpose."
        exit 1
    }
}

Write-Host "MT5 copy verified: $($actual.Count) files match the manifest"
exit 0
