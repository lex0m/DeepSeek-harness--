param(
  [string]$PkgDir = '',
  [string]$Version = '1.0.2903.40'
)

# Fetches the WebView2 SDK into launcher\wv2pkg\pkg so build.ps1 (or a CI
# checkout) can compile. That folder is git-ignored, so a fresh clone has no
# WebView2 assemblies and the build fails with "WebView2 package missing".
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File launcher\bootstrap-webview2.ps1
#
# Packages (nuget.org, Microsoft-owned):
#   Microsoft.Web.WebView2 -> managed assemblies (/reference:) + the native
#                             WebView2Loader.dll (build/native/x64) shipped next to the exe

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot          # repo root
$Launcher = Join-Path $Root 'launcher'
if (-not $PkgDir) { $PkgDir = Join-Path $Launcher 'wv2pkg\pkg' }
New-Item -ItemType Directory -Force -Path $PkgDir | Out-Null

$targets = @(
  @{ id = 'Microsoft.Web.WebView2'; ver = $Version; dest = $PkgDir }
)

foreach ($t in $targets) {
  $url = "https://www.nuget.org/api/v2/package/$($t.id)/$($t.ver)"
  $zip = Join-Path $env:TEMP ("$($t.id).$($t.ver).zip")
  Write-Host "==> download $($t.id) $($t.ver)"
  Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 300
  $tmp = Join-Path $env:TEMP ("$($t.id).x")
  if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
  Expand-Archive -Path $zip -DestinationPath $tmp -Force
  # nupkg layout mirrors the package: lib\, build\, runtimes\, ...
  Copy-Item (Join-Path $tmp '*') $t.dest -Recurse -Force
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $zip -Force -ErrorAction SilentlyContinue
}

$need = @(
  (Join-Path $PkgDir 'lib\net462\Microsoft.Web.WebView2.Core.dll'),
  (Join-Path $PkgDir 'lib\net462\Microsoft.Web.WebView2.WinForms.dll')
)
foreach ($f in $need) {
  if (-not (Test-Path $f)) { throw "missing after fetch: $f" }
  Write-Host "  ok  $f"
}
# ship the x64 loader next to the exe (package layout: build\native\x64\)
$loaderSrc = Join-Path $PkgDir 'build\native\x64\WebView2Loader.dll'
$loader = if (Test-Path $loaderSrc) { Get-Item $loaderSrc } else {
  Get-ChildItem -Path $PkgDir -Recurse -Filter 'WebView2Loader.dll' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'x64' } | Select-Object -First 1
}
if ($loader) { Write-Host "  ok  $($loader.FullName)" } else {
  Write-Host "  warn: WebView2Loader.dll not found under $PkgDir (runtime may need it next to the exe)" -ForegroundColor Yellow
}
Write-Host '==> WebView2 SDK ready. Now run: launcher\build.ps1'
