# 模型引擎更新速查（Ollama / llama.cpp）

> 2026-10-11 实测的官方版本事实与下载通道。**只有确认过的事实**写在这里，
> 没验证的会明确标注。启动器集成方式见 `docs\UPGRADE-dsh-0.2.md` §7。

## 当前官方版本（2026-10-11 查）

| 引擎 | 最新稳定 | 发布时间 | Windows 取法 |
|---|---|---|---|
| Ollama | **v0.40.3** | 2026-10-11 | GitHub release 资产里的 zip，见下 |
| llama.cpp | **v0.6.0** | 2026-10-05 | `v*` tag 通常**不带** Windows 资产；Windows 二进制走 CI nightly 资产（`b*` tag，README 徽章：Nightly → `releases?q=b`） |

## Ollama

官方 release 页：https://github.com/ollama/ollama/releases/latest

v0.40.3 的 Windows 资产（已核实名称与体积）：

| 资产 | 体积 | 适用 |
|---|---|---|
| `ollama-windows-amd64.zip` | 1400.0 MB | **amd64（含 NVIDIA/通用，首选）** |
| `ollama-windows-amd64-mlx.zip` | 1338.2 MB | AMD 平台 MLX 构建 |
| `ollama-windows-amd64-rocm.zip` | 243.6 MB | AMD ROCm 构建 |
| `ollama-windows-arm64.zip` | 200.2 MB | ARM64（骁龙 X 之类） |

```powershell
$ver = 'v0.40.3'
$url = "https://github.com/ollama/ollama/releases/download/$ver/ollama-windows-amd64.zip"
$zip = "$env:TEMP\ollama-$ver.zip"
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing     # 1.4GB，慢
# 解压到临时目录再覆盖到 DSH_OLLAMA_EXE 所在目录（先备份旧文件！）
```

要点：
- **权重不在程序目录**：`OLLAMA_MODELS` 或默认 `%USERPROFILE%\.ollama\models`。换程序不动模型。
- 现在跑的若是 `ollama serve` 由启动器拉起（`start-model-server.ps1`），
  更新前先关掉启动器窗口/停 ollama，再替换文件，否则 Windows 会锁住 exe/dll。
- **升级到 v0.40.x 后模型可能需要重新拉取**：Ollama 偶尔改权重格式/引擎需求，
  `ollama list` 里旧模型若加载失败，`ollama pull <name>` 重新下。`[未验证]`——没在真机上试过 v0.40.x。

## llama.cpp

- Release 页：https://github.com/ggml-org/llama.cpp/releases
- `v0.6.0`（2026-10-05）的 release **只带一个 `nightly-tag.txt`**（已实测），
  说明稳定 tag 不附 Windows 二进制；README 指向 nightly 徽章
  `https://github.com/ggml-org/llama.cpp/releases?q=b` 取 `bXXXX` 构建。
- 取 Windows 资产时按显卡选：`llama-*-bin-win-cuda-x64.zip`（NVIDIA）/ `...-win-cpu-x64.zip`（无独显）/ `...-win-vulkan-x64.zip`。
- **成套替换**：`llama-server.exe` + 同 release 的 `llama.dll`、`ggml*.dll`、`mtmd.dll` 等，
  丢进 `DSH_LLAMA_DIR`（脚本默认在盘上一层的 `..\llama.cpp`）。混用不同版本的 dll 会直接崩。
- 启动参数由脚本给：`-m <gguf> --port <11435/你设的> -c <ctx> -ngl <gpu layers> --host 127.0.0.1`，
  `DSH_LLAMA_EXTRA_ARGS` 可追加。**gguf 模型不用动。**

## 集成验收（两条都要 200）

```powershell
Invoke-WebRequest http://127.0.0.1:11434/api/tags   -UseBasicParsing | Select StatusCode  # ollama
Invoke-WebRequest http://127.0.0.1:11435/v1/models  -UseBasicParsing | Select StatusCode  # llama.cpp
```

健康检查通过这些端口做，正是 auto-config 探测用的同一批端点 → 端口通了就等于接上启动器，
不需要改任何配置或代码。若后端/路径有变，只改 `config\user.env` 里的 `DSH_*` 变量。

## 待办/未验证

- `[未验证]` 真机上下载替换、v0.40.x 模型兼容性、nightly 资产名的精确后缀（每次构建名会变，用页面实时确认）。
- 没找到「llama.cpp 官方 Windows 稳定二进制直链」这种东西——它就是不提供，别去找。
