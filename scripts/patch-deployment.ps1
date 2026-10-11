param(
  [string]$Deployment = (Split-Path -Parent $PSScriptRoot),
  [string]$DshVersion = ''
)

# Refreshes an already-deployed drive (frozen exe + tools\global + scripts\)
# with the current repo scripts, without rebuilding the launcher exe:
#   1. backs up the files it replaces,
#   2. copies scripts\*.ps1 / scripts\*.mjs from the repo over the deployment,
#   3. records the dsh channel/version in config\user.env so update.ps1 stops
#      silently following whatever npm tags as latest.
#
# Usage (from the repo checkout, with the drive's deployment folder as arg):
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\patch-deployment.ps1 -Deployment E:\deepseek-harness
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\patch-deployment.ps1 -Deployment E:\deepseek-harness -DshVersion 0.2.1-alpha.2

$ErrorActionPreference = 'Stop'

# Resolve the repo root: the script may sit in the repo's own scripts\ folder or
# be copied next to the deployment. Prefer the folder that actually looks like
# the repo; fall back to the script's parent (the deployment folder).
$ScriptDir = $PSScriptRoot
$RepoRoot = $null
foreach ($cand in @($ScriptDir, (Split-Path -Parent $ScriptDir))) {
  if ($cand -and (Test-Path (Join-Path $cand 'launcher\web'))) { $RepoRoot = $cand; break }
}
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $ScriptDir }
if (-not (Test-Path (Join-Path $RepoRoot 'scripts\auto-config.mjs'))) { $RepoRoot = $ScriptDir }

if (-not (Test-Path $Deployment)) { throw "deployment folder not found: $Deployment" }
$DeployScripts = Join-Path $Deployment 'scripts'
$DeployConfig  = Join-Path $Deployment 'config'
$UserEnv       = Join-Path $DeployConfig 'user.env'
$stamp         = Get-Date -Format 'yyyyMMdd-HHmmss'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

Write-Step "Patching deployment: $Deployment"
if (-not (Test-Path $DeployScripts)) { throw "no scripts folder in the deployment: $DeployScripts" }

# 1. back up + replace the files this patch owns
$files = @(
  @{ src = Join-Path $RepoRoot 'scripts\auto-config.mjs';       dst = Join-Path $DeployScripts 'auto-config.mjs' },
  @{ src = Join-Path $RepoRoot 'scripts\scan-missing.mjs';      dst = Join-Path $DeployScripts 'scan-missing.mjs' },
  @{ src = Join-Path $RepoRoot 'scripts\start-model-server.ps1'; dst = Join-Path $DeployScripts 'start-model-server.ps1' },
  @{ src = Join-Path $RepoRoot 'update.ps1';                    dst = Join-Path $Deployment 'update.ps1' },
  @{ src = Join-Path $RepoRoot 'bootstrap.ps1';                 dst = Join-Path $Deployment 'bootstrap.ps1' },
  @{ src = Join-Path $RepoRoot 'start.ps1';                     dst = Join-Path $Deployment 'start.ps1' }
)
foreach ($f in $files) {
  if (-not (Test-Path $f.src)) { Write-Host "  skip (not in repo): $($f.src)" -ForegroundColor Yellow; continue }
  if (Test-Path $f.dst) {
    $backup = "$($f.dst).bak-$stamp"
    Copy-Item $f.dst $backup -Force
  }
  Copy-Item $f.src $f.dst -Force
  Write-Host "  updated $($f.dst)"
}

# 2. record the dsh channel/version so updates are deliberate
if ($DshVersion) {
  New-Item -ItemType Directory -Force -Path $DeployConfig | Out-Null
  $line = "DSH_VERSION=$DshVersion"
  $existing = if (Test-Path $UserEnv) { Get-Content $UserEnv -Raw } else { '' }
  if ($existing -match '(?m)^\s*DSH_VERSION\s*=') {
    $updated = [Regex]::Replace($existing, '(?m)^\s*DSH_VERSION\s*=.*$', $line)
    Set-Content -Path $UserEnv -Value $updated.TrimEnd() -Encoding UTF8
    Write-Host "  DSH_VERSION updated to $DshVersion in $UserEnv"
  } else {
    $header = @()
    if (-not (Test-Path $UserEnv)) { $header += '# DeepSeek Harness portable deployment - user environment' }
    $header += "# pinned dsh release (update.ps1 installs this instead of npm latest; remove to follow latest)"
    $header += $line
    Set-Content -Path $UserEnv -Value (($header + $existing.TrimEnd()) -join "`r`n") -Encoding UTF8
    Write-Host "  DSH_VERSION=$DshVersion added to $UserEnv"
  }
} else {
  Write-Host '  (no -DshVersion given: update.ps1 keeps following npm latest)' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'Patched. Next steps on this drive:' -ForegroundColor Green
Write-Host '  1. run update.bat once  (moves tools\global to the dsh release you want)'
Write-Host '  2. launch DeepSeekHarness.exe: auto-config now writes llm-pi-ai + agent-default-model'
Write-Host '     into <DSH_HOME>\profiles\web\cordis.patch.yml (dsh >= 0.2 ignores settings.yaml)'
Write-Host '  3. verify: Settings -> Models shows the local providers; Start menu still works'
