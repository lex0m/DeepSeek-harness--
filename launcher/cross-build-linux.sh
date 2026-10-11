#!/usr/bin/env bash
# Cross-compile DeepSeekHarness.exe (WinForms + WebView2, .NET Framework 4.8)
# from Linux/macOS (and therefore from Android via Termux/proot-distro).
#
# The .NET Framework 4.8 target is fully reproducible off-Windows because it only
# needs *reference* assemblies + Roslyn: NuGet ships the reference assemblies
# (Microsoft.NETFramework.ReferenceAssemblies.net48, MIT) and Roslyn is
# cross-platform. This mirrors launcher/build.ps1 flag for flag:
#   /target:winexe /platform:x64 /optimize+ /win32manifest:app.manifest
#
# Requirements (DEBIAN/Ubuntu arm64 or x64):
#   apt-get install -y dotnet-sdk-8.0          # or install via dotnet-install.sh
#   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
#
# Usage:  bash launcher/cross-build-linux.sh [outdir]
# Output: <outdir>/DeepSeekHarness.exe  (+ sha256)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LAUNCHER="$ROOT/launcher"
SRC="$LAUNCHER/src"
MANIFEST="$LAUNCHER/app.manifest"
OUTDIR="${1:-$ROOT/build}"
WORK="${TMPDIR:-/tmp}/dsh-cross-build.$$"

DOTNET="${DOTNET:-dotnet}"
command -v "$DOTNET" >/dev/null || { echo "dotnet not found; see header for install" >&2; exit 1; }

REF_PKG="Microsoft.NETFramework.ReferenceAssemblies.net48"
REF_VER="1.0.3"
WC="Microsoft.WindowsDesktop.App.Ref"   # not needed for net48, kept out on purpose

mkdir -p "$OUTDIR" "$WORK"

echo "==> fetching reference assemblies ($REF_PKG $REF_VER)"
curl -sSL -o "$WORK/ref.zip" "https://www.nuget.org/api/v2/package/$REF_PKG/$REF_VER"
unzip -q -o "$WORK/ref.zip" -d "$WORK/ref"
REFDIR="$(find "$WORK/ref/build/.NETFramework" -maxdepth 1 -type d -name 'v4.8' | head -1)"
[ -n "$REFDIR" ] || { echo "reference assemblies not found in package" >&2; exit 1; }

echo "==> fetching WebView2 SDK"
WV2="$WORK/wv2pkg"
mkdir -p "$WV2"
curl -sSL -o "$WORK/webview2.zip" "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/1.0.2903.40"
unzip -q -o "$WORK/webview2.zip" -d "$WV2"
# layout: lib/net462 (managed) + build/native/x64 (WebView2Loader.dll)

echo "==> compiling"
SOURCES=$(find "$SRC" -name '*.cs' | sort)
[ -n "$SOURCES" ] || { echo "no C# sources under $SRC" >&2; exit 1; }

# Roslyn via the SDK: build a net48 library and then link it into a winexe. The
# simplest faithful route is a throwaway csproj that points at the same inputs.
PROJ="$WORK/cross.csproj"
cat > "$PROJ" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <AssemblyName>DeepSeekHarness</AssemblyName>
    <RootNamespace>DeepSeekHarness</RootNamespace>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <Optimize>true</Optimize>
    <DebugType>none</DebugType>
    <ApplicationManifest>$MANIFEST</ApplicationManifest>
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <EnableFrameworkPathOverride>true</EnableFrameworkPathOverride>
    <FrameworkPathOverride>$REFDIR</FrameworkPathOverride>
    <NoWarn>MSB3644;CS0618</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$SRC/**/*.cs" />
    <Reference Include="System" />
    <Reference Include="System.Core" />
    <Reference Include="System.Drawing" />
    <Reference Include="System.Windows.Forms" />
    <Reference Include="System.Web.Extensions" />
    <Reference Include="System.Security" />
    <Reference Include="Microsoft.Web.WebView2.Core">
      <HintPath>$WV2/lib/net462/Microsoft.Web.WebView2.Core.dll</HintPath>
    </Reference>
    <Reference Include="Microsoft.Web.WebView2.WinForms">
      <HintPath>$WV2/lib/net462/Microsoft.Web.WebView2.WinForms.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
EOF

"$DOTNET" build "$PROJ" -c Release -o "$WORK/out" -v minimal

cp "$WORK/out/DeepSeekHarness.exe" "$OUTDIR/DeepSeekHarness.exe"
cp -f "$WV2"/lib/net462/*.dll "$OUTDIR/" 2>/dev/null || true
cp -f "$WV2"/build/native/x64/WebView2Loader.dll "$OUTDIR/WebView2Loader.dll" 2>/dev/null || true

echo "==> done"
ls -l "$OUTDIR/DeepSeekHarness.exe"
( cd "$OUTDIR" && sha256sum DeepSeekHarness.exe | tee DeepSeekHarness.exe.sha256 )
echo "NOTE: verify the PE manifest contains PerMonitorV2 (same acceptance check as build.ps1)."
echo "NOTE: byte-identity with the Windows csc.exe build is NOT guaranteed; treat this as a"
echo "      convenience/reproducibility build, and CI (windows-latest) as the release build."
rm -rf "$WORK"
