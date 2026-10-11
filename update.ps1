# ============================================================================
#  DeepSeek Harness - update dsh / pnpm to the latest npm release
#  Double-click update.bat or run: powershell -ExecutionPolicy Bypass -File update.ps1
# ============================================================================
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
if (-not $Root) { $Root = (Get-Location).Path }

# --- 环境变量保护: 某些精简/受限环境下可能缺失 ---
if (-not $env:SystemRoot) { $env:SystemRoot = "$env:WINDIR" }
if (-not $env:COMSPEC) { $env:COMSPEC = "$env:SystemRoot\System32\cmd.exe" }
if (-not $env:PATHEXT) { $env:PATHEXT = ".COM;.EXE;.BAT;.CMD" }
if (-not $env:USERPROFILE) { $env:USERPROFILE = "$env:SystemDrive\Users\$([Environment]::UserName)" }
if (-not $env:APPDATA) { $env:APPDATA = "$env:USERPROFILE\AppData\Roaming" }
if (-not $env:LOCALAPPDATA) { $env:LOCALAPPDATA = "$env:USERPROFILE\AppData\Local" }
if (-not $env:HOME) { $env:HOME = $env:USERPROFILE }
if (-not $env:TEMP) { $env:TEMP = "$env:LOCALAPPDATA\Temp" }

# dsh 版本/通道：默认 npm latest（目前 0.2.0-rc.2）；要跟随 alpha 通道或钉死版本，
# 在 config\user.env 里写一行 DSH_VERSION=0.2.1-alpha.2（或 alpha / next / 精确版本号）。
$DshTag = if ($env:DSH_VERSION) { $env:DSH_VERSION } else { 'latest' }
$DshSpec = "@deepseek-ai/dsh@$DshTag"

$ToolsDir  = Join-Path $Root 'tools'
$NodeDir   = Join-Path $ToolsDir 'node'
$NodeExe   = Join-Path $NodeDir 'node.exe'
$GlobalDir = Join-Path $ToolsDir 'global'
$CacheDir  = Join-Path $ToolsDir 'npm-cache'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

if (-not (Test-Path $NodeExe)) {
  Write-Host 'Portable Node.js is missing. Run start.bat once to bootstrap first.' -ForegroundColor Yellow
  exit 1
}

$env:Path = "$NodeDir;$GlobalDir;$env:Path"
$env:npm_config_cache   = $CacheDir
$env:npm_config_prefix  = $GlobalDir
$env:npm_config_update_notifier = 'false'

$npmCmd = Join-Path $NodeDir 'npm.cmd'
if (-not (Test-Path $npmCmd)) { $npmCmd = 'npm.cmd' }

function Invoke-Npm([string[]]$npmArgs) {
  Push-Location $GlobalDir
  $argStr = $npmArgs -join ' '
  Write-Host "  npm $argStr" -ForegroundColor DarkGray
  $outLog = Join-Path $env:TEMP "dsh_npm_out.log"
  $errLog = Join-Path $env:TEMP "dsh_npm_err.log"
  $p = Start-Process -FilePath $npmCmd -ArgumentList $npmArgs -NoNewWindow -Wait -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog
  if (Test-Path $outLog) { $out = Get-Content $outLog -Raw; if ($out) { Write-Host $out.Trim() } }
  if (Test-Path $errLog) { $err = Get-Content $errLog -Raw; if ($err) { Write-Host $err.Trim() -ForegroundColor Yellow } }
  $code = $p.ExitCode
  Pop-Location
  return $code
}

# --- 允许原生模块编译脚本 (node-pty / koffi / bufferutil 等) ---
Write-Step 'Configuring npm allow-scripts ...'
& $npmCmd config set ignore-scripts false 2>$null
& $npmCmd approve-scripts --all 2>$null
if ($LASTEXITCODE -ne 0) {
  Write-Host '  (approve-scripts not available or nothing pending - continuing)' -ForegroundColor DarkGray
}

Write-Step "Updating $DshSpec ..."
$code = Invoke-Npm @('install', $DshSpec, '--force', '--no-fund', '--no-audit', '--foreground-scripts')
if ($code -ne 0) { throw "update failed (code $code)." }

# 重新补装可能新增的 peer 依赖 (cordis 插件体系依赖 peerDependencies)
Write-Step 'Checking peer dependencies ...'
$scan = Join-Path $Root 'scripts\scan-missing.mjs'
if (Test-Path $scan) {
  # 显式传 global 目录；脚本在目录不存在时会 exit 2，据此区分"扫描失败"与"无缺失"
  $scanOut = & $NodeExe $scan $GlobalDir 2>$null | Out-String
  $scanCode = $LASTEXITCODE
  if ($scanCode -eq 2) {
    Write-Host "  peer 依赖扫描失败：找不到 $GlobalDir\node_modules（已跳过）" -ForegroundColor Yellow
    $installLine = $null
  } else {
  $installLine = ($scanOut -split "`n" | Where-Object { $_ -match '^\s*INSTALL_LIST=' })
  if ($installLine) {
    $pkgs = (($installLine | Select-Object -First 1) -replace '^\s*INSTALL_LIST=','').Trim()
    if ($pkgs) {
      Write-Step "Installing missing peer dependencies:"
      $pkgArray = $pkgs -split '\s+'
      $code2 = Invoke-Npm (@('install') + $pkgArray + @('--force', '--no-fund', '--no-audit', '--foreground-scripts'))
      if ($code2 -ne 0) { Write-Host "peer install warning (code $code2) - dsh may still work" -ForegroundColor Yellow }
    } else {
      Write-Host '  No missing peer dependencies.' -ForegroundColor Green
    }
  } else {
    Write-Host '  No missing peer dependencies.' -ForegroundColor Green
  }
  }
}

Write-Step 'Updating pnpm ...'
$pnpmCode = Invoke-Npm @('install', '-g', 'pnpm@latest', '--prefix', $GlobalDir, '--cache', $CacheDir, '--foreground-scripts')
if ($pnpmCode -ne 0) { Write-Host "  pnpm update warning (code $pnpmCode)" -ForegroundColor Yellow }

$dsh = Join-Path $GlobalDir 'dsh.cmd'
if (-not (Test-Path $dsh)) { $dsh = Join-Path $GlobalDir 'node_modules\.bin\dsh.cmd' }
Write-Host ''
Write-Host "dsh version now: $(& $dsh --version)" -ForegroundColor Green
Write-Host 'Update finished. Run start.bat to launch.' -ForegroundColor Green
