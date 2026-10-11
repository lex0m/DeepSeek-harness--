# 把已有外接盘部署升级到 dsh 0.2.x
 
> 适用对象：**已经交付/已经在跑的盘**（部署目录 `<盘>\deepseek-harness\`）。
> 交付包 `DeepSeekHarness-Launcher-Project\src\` 里的源码按同一份说明改。
> 冻结的 `DeepSeekHarness.exe` 本体**不需要重编译**：本次升级不动 exe 代码，
> 只更新 `tools\global` 里的 dsh 与 `scripts\` 里的配置脚本。
 
---
 
## 0. 先看清楚现在盘上是什么
 
```powershell
$D = 'E:\deepseek-harness'                 # ← 改成你的盘符
& "$D\tools\node\node.exe" --version
& "$D\tools\global\dsh.cmd" --version
Test-Path "$D\home\settings.yaml"
```
 
三个结果记下来（回滚判断要用）：
 
| 结果 | 含义 |
|---|---|
| `dsh` 版本 `0.1.x` | 当前是 0.1 时代部署：`home\settings.yaml` 才是活配置 |
| `settings.yaml` 存在 | 本地模型 provider 现在写在这里（0.2 起会被改名 `*.imported`） |
| `node` 版本 ≥ 20 | 0.2.x 需要现代 Node；便携 Node 是 LTS，一般无需动 |
 
---
 
## 1. 升级前备份（必须，60 秒）
 
```powershell
$D = 'E:\deepseek-harness'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Copy-Item "$D\tools\global" "$D\tools\global.bak-$stamp" -Recurse -Force
Copy-Item "$D\home"         "$D\home.bak-$stamp"         -Recurse -Force
```
 
回滚就是把 `global.bak-*` 改回 `global`、`home.bak-*` 改回 `home`（见 §5）。
 
---
 
## 2. 选版本通道（这是本次升级最容易踩的坑）
 
2026-10-11 实测 npm 上的 `@deepseek-ai/dsh`：
 
| tag | 版本 | 发布时间 | 说明 |
|---|---|---|---|
| `latest` | **0.2.0-rc.2** | 2026-09-29 | 官方 release 页标为正式候选版，最稳 |
| `alpha` | **0.2.1-alpha.2** | 2026-10-09 | 最新；含 0.2.0 之后的一批修复与新插件 |
 
**默认走 `latest`（0.2.0-rc.2）**；要跟 alpha 就在 `config\user.env` 里加一行：
 
```
DSH_VERSION=0.2.1-alpha.2
```
 
> `update.ps1` 默认写死 `@latest`（旧版行为），已改为读 `DSH_VERSION`（缺省仍 `latest`）。
> 不打这一针的话，每点一次"更新"都会被拉回 latest，alpha 装不住。
 
---
 
## 3. 执行升级
 
```powershell
cd $D
.\update.bat
```
 
`update.ps1` 会做四件事：放开原生模块编译脚本 → `npm install <dsh spec> --force` →
重扫补装 peer 依赖（`scripts\scan-missing.mjs`）→ 更新 pnpm。
 
**验收（三条全过才算成功）**：
 
```powershell
& "$D\tools\global\dsh.cmd" --version      # 期望 0.2.0-rc.2（或你钉的版本）
Test-Path "$D\tools\global\node_modules\@deepseek-ai\dsh\node_modules\@deepseek-ai\dsh-base"   # 期望 True
Select-String -Path "$D\tools\global\dsh.cmd" -Pattern 'dsh'
```
 
启动器窗口里还要看到 `Updating @deepseek-ai/dsh@<spec>` 与 `No missing peer dependencies.`
或补装清单——报错原文都在 `%TEMP%\dsh_npm_out.log` / `dsh_npm_err.log`。
 
---
 
## 4. 0.2.x 的活配置搬家（**不处理会静默丢本地模型**）
 
0.2 起：
 
- 旧的 `$DSH_HOME\settings.yaml` **只被导入一次**，随后改名 `settings.yaml.imported`；
  导入只认 `ui-developer-tools`→`ui-settings`、`ui-onboarding`→`ui-settings-general`、
  `shell`→当前平台 shell 执行器 这几个 section；
- **`llm-pi-ai`（本地模型 provider）与 `agent-default-model` 不在导入名单里**，
  导入时会被判为"当前组合不接受"→ 只记一条日志、留在改名后的文件里；
- 之后所有活配置落进 **活动 profile 自己的 patch**：
  `$DSH_HOME\profiles\web\cordis.patch.yml`（`- id: <条目>` 形式的数组）。
 
所以升级后必须让 **auto-config 把 provider 写进 profile patch**（本次已修）。
 
### 4.1 修好的 `scripts\auto-config.mjs` 行为
 
| 项 | 旧行为 | 新行为 |
|---|---|---|
| providers 落点 | `$DSH_HOME\settings.yaml` 的 `llm-pi-ai:` 段 | `profiles\web\cordis.patch.yml` 的 `- id: llm-pi-ai` 条目 `config.providers` |
| 默认模型 | `settings.yaml` 的 `agent-default-model:` 段 | patch 的 `- id: agent-default-model` 条目；**仅当 patch 里没有 model 时才写**，不覆盖用户选择 |
| 旧配置 | —— | 首次运行会把 `settings.yaml`（或已改名的 `.imported`）里的 provider **并进** patch，不丢 |
| 其它条目/注释 | —— | 逐字保留（只动它自己那两个条目），写前留 `.auto-backup-<时间戳>` |
 
### 4.2 把补丁打到盘上
 
```powershell
# 在源码仓库里（含 scripts\patch-deployment.ps1 的版本）
cd DeepSeekHarness-Launcher-Project\src
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\patch-deployment.ps1 -Deployment E:\deepseek-harness
```
 
不带参数就是在"给我现在跑的盘打补丁 + 备份"；要顺手钉版本就加 `-DshVersion 0.2.0-rc.2`。
 
### 4.3 启动并验证搬家成功
 
1. 双击 `DeepSeekHarness.exe` → 点「启动服务」；
2. 控制台里应出现 `Profile patch written: <...>\home\profiles\web\cordis.patch.yml`，
   以及 `Configured providers:` 清单；
3. 打开 `http://127.0.0.1:3080` → 设置 → 模型：本地 provider（`auto-ollama` / `auto-lmstudio` …）
   应出现在列表里；
4. 检查文件：
   ```powershell
   Get-Content "$D\home\profiles\web\cordis.patch.yml"
   Test-Path "$D\home\settings.yaml.imported"     # 期望 True（被 0.2 改名）
   Get-ChildItem "$D\home\settings.yaml*"         # 不该再有活着的 settings.yaml
   ```
 
---
 
## 5. 回滚
 
```powershell
$D = 'E:\deepseek-harness'; $stamp = '<你备份时的时间戳>'
Remove-Item "$D\tools\global" -Recurse -Force
Move-Item   "$D\tools\global.bak-$stamp" "$D\tools\global"
Remove-Item "$D\home" -Recurse -Force
Move-Item   "$D\home.bak-$stamp" "$D\home"
```
 
旧 exe 专项回滚见 `docs\`（`DeepSeekHarness.exe` 备份在交付包 `dist\` 与盘的旧版备份目录）。
 
---
 
## 7. 顺带：盘上的模型引擎（Ollama / llama.cpp）

启动器只是**拉起**它们，不负责它们的版本：`scripts\start-model-server.ps1` 用
`DSH_MODEL_BACKEND`（`ollama` | `llama-cpp`，默认 `llama-cpp`）+ 一组 `DSH_*` 变量决定拉哪个。
所以升级引擎＝就地替换 exe/库，**配置不用改**（auto-config 只认 `127.0.0.1:11434` 和 `:11435` 这些端口）。

```powershell
$D = 'E:\deepseek-harness'
# 先问清楚现在装的是哪套、哪个版本
Get-Content "$D\config\user.env" | Select-String 'DSH_MODEL_BACKEND|DSH_OLLAMA_EXE|DSH_LLAMA_DIR'
```

- **Ollama**（后端=ollama）：官方 release 直接给 Windows zip，解到 `DSH_OLLAMA_EXE` 那个目录即可；
  **模型不会动**（权重在 `OLLAMA_MODELS` 或默认 `%USERPROFILE%\.ollama\models`，不在程序目录里）。
  注意 v0.40.x 的 Windows 包体积很大（amd64 约 1.4GB），下载留足时间。
- **llama.cpp**（后端=llama-cpp）：只需要 `llama-server.exe` 与同 release 的 `llama.dll`/`ggml*.dll`
  成套替换（`DSH_LLAMA_DIR` 目录，默认在盘上一层的 `..\llama.cpp`）；
  换完**核对 `DSH_LLAMA_MODEL` 指向的 gguf 还在**（脚本会先 check 文件存在）。

**验收**：
```powershell
& "$D\config\..."                      # 不需要
# 后端是 ollama：
Invoke-WebRequest http://127.0.0.1:11434/api/tags -UseBasicParsing | Select-Object -Expand StatusCode
# 后端是 llama.cpp：
Invoke-WebRequest http://127.0.0.1:11435/v1/models -UseBasicParsing | Select-Object -Expand StatusCode
```
两个都返回 200，且启动器窗口里 `Configured providers:` 有对应 provider，就算接上了。
不放心就再点一次「启动服务」：`start-model-server.ps1` 见到端口已监听会跳过启动（幂等）。

---

## 8. 仍未验证的点（别当已验证）
 
- **Windows 上真跑**：`[未验证]`。本说明与脚本改动是在 Android 侧完成与静态/隔离验证的，
  没有 Windows 机器执行过 `update.ps1`、也没有跑过 0.2.x 的 dsh 启动。
- dsh 0.2.x 对 `providers` 的 schema 校验：官方 README 给出的键（`apiKeyEnv` / `baseURL` /
  `models` / `reasoning` …）与 auto-config 的输出一致，但**未在 0.2.1-alpha.2 上实跑过 provider 请求**。
- alpha 通道的包体可能引入新的 peer 依赖：靠 `scan-missing.mjs` 兜底，需在验收时看它有没有补装。
