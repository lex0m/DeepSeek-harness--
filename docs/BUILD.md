# 在没有 Windows 开发机的情况下改 C# 并产出 exe

> 结论先行：**改代码可以在任何地方；编译 exe 有三条路，按推荐度排序如下。**
> 本文只写可核实的事实，含"未验证"标记的请上机实测后回填。

## 0. 为什么手机默认编不了

`launcher/build.ps1` 调的是 **Windows 自带的旧编译器**
`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，目标 `.NET Framework 4.8 + WinForms + WebView2`，
并支持 `/win32manifest`。这台编译器只存在于 Windows；Android 上既没有它，也没有 `dotnet`。

## 1. 【推荐】GitHub Actions：零本地依赖，直接在云端出 exe

仓库已开 Actions（`enabled: true, allowed_actions: all`），公开仓库的 standard runner **免费**。

工作流：`.github/workflows/build-launcher.yml`

- 触发器：`launcher/**`、`bootstrap.ps1`、`start.ps1`、`update.ps1` 有改动时 push/PR，或手动 `workflow_dispatch`；
- 跑在 `windows-latest`，**用和交付 exe 完全相同的 `csc.exe` + `build.ps1`**（因此这是"权威构建"）；
- 先跑 `launcher/bootstrap-webview2.ps1` 取 WebView2 SDK（该目录不在仓库里，见下）；
- 验收四条：exe ≥60KB、内嵌 `PerMonitorV2` 清单、PE 头合法、`AssemblyName.GetAssemblyName()` 能读元数据；
- 产出 artifact **DeepSeekHarness-win-x64**：exe + `WebView2Loader.dll` + sha256（打 tag 时自动附带进 Release）。

取产物：手机上打开仓库 → **Actions → 选那次 run → Artifacts → 下载**，Windows 上解压覆盖到盘
（或直接 `gh run download -n DeepSeekHarness-win-x64`）。**全程不需要本地 Windows 开发机。**

## 2. WebView2 SDK 不在仓库里（新加的补齐脚本）

`launcher/wv2pkg/` 被 gitignore，`build.ps1` 找不到就会报
`WebView2 package missing`。新加：

```powershell
# 任何一台 Windows（或 CI）上，克隆后先跑这个
powershell -NoProfile -ExecutionPolicy Bypass -File launcher\bootstrap-webview2.ps1
.\launcher\build.ps1
```

它从 nuget.org 取 `Microsoft.Web.WebView2`（含 `lib\net462\*.dll` 与 `build\native\x64\WebView2Loader.dll`），
铺成 `build.ps1` 期望的目录结构。想升 WebView2 SDK 就改 `-Version`。

## 3. 【进阶，未验证】手机/proot 上交叉编译出 net48 exe

理论上完全可行：**net48 目标不需要 Windows——NuGet 提供"参考程序集"
`Microsoft.NETFramework.ReferenceAssemblies.net48`（MIT，微软官方 NuGet 包），Roslyn 本身跨平台**，
所以 Linux/arm64 上也能编出面向 .NET Framework 4.8 的 WinForms exe。

脚本：`launcher/cross-build-linux.sh`（用 `dotnet` SDK + `FrameworkPathOverride` 指向参考程序集，
参数与 `build.ps1` 对齐：WinExe / x64 / optimize / 嵌入 `app.manifest`）。

Android 上的前置条件（本机实测：已有 `qemu-aarch64`，**没有 proot**，需要用 Termux 装）：

```bash
# Termux（F-Droid 版）+ 存储权限
pkg install proot-distro          # 本机未装，需自行安装；网络到 nuget.org/github.com 通
proot-distro install debian
proot-distro login debian
apt-get update && apt-get install -y curl unzip
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
export PATH="$HOME/.dotnet:$PATH"
# 拉仓库后：
bash launcher/cross-build-linux.sh ~/out
```

**注意与边界**：
- 这只是"再产出一份 exe"，**不等于 Windows 真机验证**；它仍然跑不了 WebView2/DPAPI/Job Object；
- 与 Windows `csc.exe` 的产物**不保证字节一致**（不同 Roslyn 版本/编译器选项），所以**发布用 CI 的产物**，
  交叉编译产物只作"手机上能编出可运行 exe"的便利路径；
- `.NET Framework 4.8 运行时不在本机 → 编出来也无法在手机/容器里运行**；
- 想让 exe 在手机上跑（Wine/Mono）不可行：Wine 的 Android 端是 x86_64 模拟、对 WinForms + 现代 .NET Framework 支持很差，
  Mono 则没有 WebView2——**这条别投入时间**。

## 4. 验收清单（每次产出 exe 后照打勾）

```powershell
$exe = 'DeepSeekHarness.exe'
(Get-Item $exe).Length                                  # 85–95 KB 之间；远小 => 编了旧代码
[System.Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($exe)) -match 'PerMonitorV2'   # 必须 True
[System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path $exe))  # 能读出程序集名
(Get-FileHash $exe -Algorithm SHA256).Hash               # 记录，回滚/比对用
```

上线前还要在盘上做一次端到端：启动器起服务 → 网页 `设置 → 模型` 有 provider → 关掉启动器后
`node`/`llama-server` 不残留（Job Object 生效）。

## 5. 待验证清单

- `[未验证]` CI 首次运行（还从没在 Actions 上跑过）；
- `[未验证]` proot + dotnet SDK + 交叉编译脚本在本机（Android 15 / aarch64）的实际结果；
- `[未验证]` `bootstrap-webview2.ps1` 在 PowerShell 5.1 下的落盘结构（本机无 Windows/PowerShell）。
