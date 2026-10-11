# ============================================================================
#  Build DeepSeekHarness.exe (WinForms + WebView2, .NET Framework 4.8)
#  Compiles all sources under launcher\src into a single exe.
#  默认嵌入 asInvoker manifest：普通权限启动（不弹 UAC）；如需管理员权限可右键"以管理员身份运行"。
#  Usage:  .\build.ps1
# ============================================================================
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$SrcDir = Join-Path $PSScriptRoot "src"
$Pkg    = Join-Path $PSScriptRoot "wv2pkg\pkg"
$Out    = Join-Path $Root "DeepSeekHarness.exe"
$Manifest = Join-Path $PSScriptRoot "app.manifest"

if (-not (Test-Path $Csc)) { throw "csc.exe not found: $Csc" }
if (-not (Test-Path (Join-Path $Pkg "lib\net462\Microsoft.Web.WebView2.WinForms.dll"))) {
  throw "WebView2 package missing. Run the WebView2 download step first."
}

# Gather all .cs sources
$Sources = Get-ChildItem -Path $SrcDir -Recurse -Filter "*.cs" | Select-Object -ExpandProperty FullName
if (-not $Sources) { throw "No .cs sources under $SrcDir" }

$Refs = @(
  "System.dll",
  "System.Core.dll",
  "System.Drawing.dll",
  "System.Windows.Forms.dll",
  "System.Web.Extensions.dll",
  "System.Security.dll",   # ProtectedData（DPAPI 凭据加密）所在程序集
  (Join-Path $Pkg "lib\net462\Microsoft.Web.WebView2.Core.dll"),
  (Join-Path $Pkg "lib\net462\Microsoft.Web.WebView2.WinForms.dll")
)
$RefArgs = @()
foreach ($r in $Refs) { $RefArgs += "/reference:$r" }

Write-Host "==> Compiling DeepSeekHarness.exe [asInvoker] ..."
# /deterministic: 相同输入产出相同 exe（PE 时间戳/MVID 固定），便于比对与回滚
& $Csc /nologo /target:winexe /platform:x64 /optimize+ /deterministic /win32manifest:"$Manifest" /out:$Out $RefArgs $Sources
if ($LASTEXITCODE -ne 0) { throw "Compile failed with code $LASTEXITCODE" }

Write-Host "==> Done: $Out ($((Get-Item $Out).Length) bytes)"
