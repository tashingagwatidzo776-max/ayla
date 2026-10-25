<#
Registers the local Qwen checkpoint as the Ollama model gwen-fx (the GWEN
confluence model): the desk's advisory analyst brain. The system prompt
comes from scripts/gwen_system_prompt.txt — the SAME file the fine-tuning
dataset builder (gwen_finetune_dataset.py) uses, so serving and training
can never drift apart.

Import strategy (learned the hard way on 2026-10-22, six failed imports):
  1. FAST PATH — if the store already holds the converted f16 GGUF (a
     blob over 6 GB whose magic is "GGUF"), the Modelfile points straight at
     it. Ollama reuses the content-addressed blob: no conversion, no
     verify pass, ~0 extra disk. This is the path that actually works on
     this machine — the safetensors pipeline's "verifying conversion"
     stage died with "not enough space on the disk" at ~30 GB used with
     8 GB free (a plain user write fills the same volume to the last
     byte, so the stage's demand — not the volume — is what to avoid).
  2. FALLBACK — import from the HuggingFace directory (needs ~17 GB free
     on the store drive: safetensors + f16 GGUF + verify copy).

Notes that matter on this desk:
  - num_gpu 0 (CPU): the machine's 2 GB GPU cannot hold the model.
    Generation is a seconds-scale, cached, advisory call — CPU is fine.
  - The store lives on E:\ollama-models (OLLAMA_MODELS user env var);
    the C: system drive runs at 99%, so scratch lands on the store
    drive too, never on C:.
  - Nothing here trades or touches the app; it only prepares the model.

Usage:
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/register-gwen-model.ps1
  ... -Source D:\models\my-qwen -Name gwen-fx -Store E:\ollama-models

Exit codes: 0 = model registered and smoke-tested, 1 = failed.
#>
param(
    [string]$Source = "$env:USERPROFILE\Desktop\qwem",
    [string]$Name = "gwen-fx",
    [string]$Store = "E:\ollama-models"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$promptFile = Join-Path $here "gwen_system_prompt.txt"

Write-Host "gwen-register: source=$Source name=$Name store=$Store"

if (-not (Test-Path $promptFile)) {
    Write-Error "System prompt missing: $promptFile"
    exit 1
}

# Exe discovery: PATH first, then the desk's install locations (the box
# has had the default install and a custom E:\Ollama side-by-side at
# different times — never assume one path).
$ollama = (Get-Command ollama.exe -ErrorAction SilentlyContinue).Source
if (-not $ollama) {
    foreach ($cand in @(
        (Join-Path $Store "gwen-tools\ollama.exe"),
        "E:\Ollama\ollama.exe",
        "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe",
        "C:\Program Files\Ollama\ollama.exe"
    )) {
        if (Test-Path $cand) { $ollama = $cand; break }
    }
}
if (-not $ollama) {
    Write-Error "ollama not found - install Ollama first (https://ollama.com)"
    exit 1
}
Write-Host "gwen-register: exe=$ollama"

# Point every future server start (tray, logon, task) at the store, and
# put scratch on the store's drive — C: must not participate.
if ($Store) {
    $env:OLLAMA_MODELS = $Store
    [Environment]::SetEnvironmentVariable("OLLAMA_MODELS", $Store, "User")
    Write-Host "gwen-register: OLLAMA_MODELS=$Store (persisted for the user)"
}
$scratch = Join-Path $Store "gwen-scratch"
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

function Get-ServerModels {
    try {
        $tags = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 3
        return @($tags.models | ForEach-Object { $_.name })
    } catch { return @() }
}

# Server on the RIGHT store: a fresh install answers on 11434 with an
# empty (default C:) store — that counts as wrong here, so restart it
# with our env. Port-owner PID, never a name pattern.
$models = Get-ServerModels
if ($models.Count -eq 0) {
    $owner = Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty OwningProcess
    if ($owner) {
        Write-Host "gwen-register: restarting server PID $owner (empty store = wrong OLLAMA_MODELS)"
        try { Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue; Start-Sleep 2 } catch { }
    }
    Start-Process -FilePath $ollama -ArgumentList "serve" -WindowStyle Hidden `
        -WorkingDirectory (Split-Path -Parent $ollama)
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $models = Get-ServerModels
        if ($models.Count -gt 0) { break }
        Start-Sleep 3
    }
    if ($models.Count -eq 0) { Write-Error "Ollama server did not come up on $Store"; exit 1 }
}
Write-Host ("gwen-register: server sees: " + ($models -join ", "))

# FAST PATH: reuse the converted f16 GGUF if the store already has one
# (blobs are content-addressed, so pointing FROM at it costs nothing).
$gguf = $null
$blobDir = Join-Path $Store "blobs"
if (Test-Path $blobDir) {
    $gguf = Get-ChildItem -Path $blobDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -gt 6GB } |
        Sort-Object Length -Descending |
        Where-Object {
            $fs = [System.IO.File]::OpenRead($_.FullName)
            try {
                $b = New-Object byte[] 4
                [void]$fs.Read($b, 0, 4)
                ([System.Text.Encoding]::ASCII.GetString($b) -eq "GGUF")
            } finally { $fs.Close() }
        } |
        Select-Object -First 1
}

$systemPrompt = (Get-Content $promptFile -Raw).TrimEnd("`n", "`r")
$modelfile = Join-Path $scratch "Modelfile.$Name"
if ($gguf) {
    Write-Host ("gwen-register: FAST PATH - importing existing GGUF ({0:n1} GB): {1}" -f ($gguf.Length/1GB), $gguf.Name)
    "FROM $($gguf.FullName)" | Set-Content -Path $modelfile -Encoding UTF8
} else {
    if (-not (Test-Path (Join-Path $Source "config.json"))) {
        Write-Error "No GGUF in store and source is not a HuggingFace model directory (no config.json): $Source"
        exit 1
    }
    Write-Host "gwen-register: no GGUF in store - full import from the checkpoint (needs ~17 GB on the store drive)"
    "FROM $($Source -replace '\\', '/')" | Set-Content -Path $modelfile -Encoding UTF8
}
@(
    "PARAMETER temperature 0.1"
    "PARAMETER num_predict 160"
    "PARAMETER num_gpu 0"
    "SYSTEM $systemPrompt"
) | Add-Content -Path $modelfile -Encoding UTF8

# Scratch must live on the store drive for the create call too.
$env:TEMP = $scratch
$env:TMP = $scratch

Write-Host "gwen-register: importing..."
$t0 = Get-Date
& $ollama create $Name -f $modelfile
if ($LASTEXITCODE -ne 0) {
    Write-Error "ollama create failed (exit $LASTEXITCODE) - check free space on $Store (fast path needs ~0; full import ~17 GB)"
    exit 1
}
Write-Host ("gwen-register: created in {0:n0}s" -f ((Get-Date) - $t0).TotalSeconds)

# Timed smoke generation: proves the model answers AND reports the latency
# the app's confluence gate will see on this hardware.
$body = @{
    model = $Name; stream = $false; keep_alive = "10m"
    messages = @(@{ role = "user"; content = "SNAPSHOT: symbol=EURUSD tf=M1 session=late regime=Range adx=14 atr_pct=0.02 spread_pts=8. PROPOSED: buy, conf 0.62, stop 9.0 pips, target_r 2.0 (swing-high 1.08420 above). BARS 12:00:1.08310,1.08340,1.08290,1.08320 12:01:1.08320,1.08350,1.08300,1.08330" })
} | ConvertTo-Json -Depth 6
$t0 = Get-Date
try {
    $resp = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/chat" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 900
    $secs = ((Get-Date) - $t0).TotalSeconds
    Write-Host ("gwen-register: smoke reply in {0:n1}s" -f $secs)
    Write-Host ("gwen-register: " + $resp.message.content)
    Write-Host "gwen-register: OK - '$Name' is served on http://127.0.0.1:11434"
    exit 0
} catch {
    Write-Error "Smoke generation failed: $($_.Exception.Message)"
    exit 1
}
