<#
Imports the local Qwen checkpoint into Ollama as the GWEN confluence model
(gwen-fx): the desk's forex-tuned analyst brain. The system prompt comes
from scripts/gwen_system_prompt.txt — the SAME file the fine-tuning dataset
builder (gwen_finetune_dataset.py) uses, so serving and training can never
drift apart.

Notes that matter on this desk:
  - num_gpu 0 (CPU): the machine's 2 GB GPU cannot hold the model; ollama
    would thrash VRAM. Generation is a seconds-scale, cached, advisory
    call — CPU is fine for that.
  - The import converts the safetensors to a full-precision GGUF (~9 GB)
    BEFORE quantizing (~3 GB): keep OLLAMA_MODELS on a drive with ~17 GB
    free (this machine: E:\ollama-models via the OLLAMA_MODELS user env
    var — the C: system drive was at 100%).
  - Nothing here trades or touches the app; it only prepares the model.

Usage:
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/register-gwen-model.ps1
  ... -Source D:\models\my-qwen -Name gwen-fx -Store E:\ollama-models

Exit codes: 0 = model registered and smoke-tested, 1 = failed.
#>
param(
    [string]$Source = "$env:USERPROFILE\Desktop\qwem",
    [string]$Name = "gwen-fx",
    [string]$Store = ""
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$promptFile = Join-Path $here "gwen_system_prompt.txt"

Write-Host "gwen-register: source=$Source name=$Name"

if (-not (Test-Path (Join-Path $Source "config.json"))) {
    Write-Error "Source is not a HuggingFace-format model directory (no config.json): $Source"
    exit 1
}
if (-not (Test-Path $promptFile)) {
    Write-Error "System prompt missing: $promptFile"
    exit 1
}

$ollama = (Get-Command ollama.exe -ErrorAction SilentlyContinue).Source
if (-not $ollama) { $ollama = "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe" }
if (-not (Test-Path $ollama)) {
    Write-Error "ollama not found - install Ollama first (https://ollama.com)"
    exit 1
}

if ($Store) {
    $env:OLLAMA_MODELS = $Store
    [Environment]::SetEnvironmentVariable("OLLAMA_MODELS", $Store, "User")
    Write-Host "gwen-register: OLLAMA_MODELS=$Store"
}

# Conversion scratch must not land on a full system drive.
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "gwen-ollama-scratch"
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch

# Server up? (start it ourselves if not)
$up = $false
try { Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 3 | Out-Null; $up = $true } catch { }
if (-not $up) {
    Write-Host "gwen-register: starting ollama serve..."
    Start-Process -FilePath $ollama -ArgumentList "serve" -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not $up) {
        try { Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 3 | Out-Null; $up = $true } catch { Start-Sleep 3 }
    }
}
if (-not $up) { Write-Error "Ollama server did not come up"; exit 1 }

$systemPrompt = (Get-Content $promptFile -Raw).TrimEnd("`n", "`r")
$modelfile = Join-Path $scratch "Modelfile.$Name"
@(
    "FROM $($Source -replace '\\', '/')"
    "PARAMETER temperature 0.1"
    "PARAMETER num_predict 160"
    "PARAMETER num_gpu 0"
    "SYSTEM $systemPrompt"
) | Set-Content -Path $modelfile -Encoding UTF8

Write-Host "gwen-register: importing (conversion + quantize; expect minutes on CPU)..."
$t0 = Get-Date
& $ollama create $Name -f $modelfile
if ($LASTEXITCODE -ne 0) {
    Write-Error "ollama create failed (exit $LASTEXITCODE) - if the log says 'not enough space', free ~17 GB on OLLAMA_MODELS and retry"
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
