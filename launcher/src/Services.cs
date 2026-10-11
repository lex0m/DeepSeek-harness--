using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    // =====================================================================
    //  路径管理：全部相对于可执行文件所在目录解析（便携部署）
    // =====================================================================
    public static class Paths
    {
        public static string Root;
        public static string Tools, NodeDir, NodeExe, GlobalDir, DshHome, WsDir, ConfigDir, UserEnv, LogsDir;
        public static string DshJsBin;          // node_modules/@deepseek-ai/dsh/lib/bin.js
        public static string AutoConfigJs;      // scripts/auto-config.mjs
        public static string ModelStarterPs1;   // scripts/start-model-server.ps1
        public static string BootstrapPs1;      // bootstrap.ps1

        public static void Init(string root)
        {
            Root = root;
            Tools      = Path.Combine(Root, "tools");
            NodeDir    = Path.Combine(Tools, "node");
            NodeExe    = Path.Combine(NodeDir, "node.exe");
            GlobalDir  = Path.Combine(Tools, "global");
            DshHome    = Path.Combine(Root, "home");
            WsDir      = Path.Combine(Root, "workspace");
            ConfigDir  = Path.Combine(Root, "config");
            UserEnv    = Path.Combine(ConfigDir, "user.env");
            LogsDir    = Path.Combine(Root, "logs");
            DshJsBin   = Path.Combine(GlobalDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            AutoConfigJs = Path.Combine(Root, "scripts", "auto-config.mjs");
            ModelStarterPs1 = Path.Combine(Root, "scripts", "start-model-server.ps1");
            BootstrapPs1 = Path.Combine(Root, "bootstrap.ps1");
        }

        public static void EnsureDirs()
        {
            foreach (var d in new[] { Tools, GlobalDir, DshHome, WsDir, ConfigDir, LogsDir })
            {
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            }
        }
    }

    // =====================================================================
    //  服务状态
    // =====================================================================
    public enum ServerState { Stopped, Starting, Running, Error, Stopping }

    // =====================================================================
    //  简单 Key=Value 配置读写（config\user.env）
    // =====================================================================
    public class ConfigManager
    {
        private Dictionary<string, string> _v = new Dictionary<string, string>();
        private string _file;

        public ConfigManager(string file) { _file = file; Load(); }

        public void Load()
        {
            _v.Clear();
            if (!File.Exists(_file)) return;
            foreach (var raw in File.ReadAllLines(_file, Encoding.UTF8))
            {
                var line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                string k = line.Substring(0, idx).Trim();
                string v = line.Substring(idx + 1).Trim().Trim('"');
                if (IsValidKey(k)) _v[k] = v;
            }
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DeepSeek Harness - user settings (managed by DeepSeekHarness.exe)");
            sb.AppendLine("# Syntax: KEY=VALUE per line, '#' starts a comment.");
            sb.AppendLine();
            // 固定的键顺序，便于阅读
            string[] order = {
                // 密钥不在此列 —— 只进 DPAPI 加密库，见 SecretStore.cs
                "DSH_PORT", "DSH_NO_OPEN", "DSH_SYSTEM_NODE",
                "DSH_AUTO_START_MODEL", "DSH_MODEL_BACKEND",
                "DSH_LLAMA_DIR", "DSH_LLAMA_MODEL", "DSH_LLAMA_PORT", "DSH_LLAMA_CONTEXT", "DSH_LLAMA_GPU_LAYERS", "DSH_LLAMA_EXTRA_ARGS",
                "DSH_OLLAMA_EXE", "DSH_OLLAMA_MODEL", "DSH_EXTRA_ENDPOINTS"
            };
            var written = new HashSet<string>();
            foreach (var k in order)
            {
                if (_v.ContainsKey(k)) { sb.AppendLine(k + "=" + _v[k]); written.Add(k); }
            }
            // 其余键
            foreach (var kv in _v)
            {
                if (written.Contains(kv.Key)) continue;
                if (SecretMigration.IsSecret(kv.Key)) continue;   // 密钥绝不落明文
                sb.AppendLine(kv.Key + "=" + kv.Value);
            }
            File.WriteAllText(_file, sb.ToString(), new UTF8Encoding(false));
        }

        public string Get(string key, string def)
        {
            string v; return _v.TryGetValue(key, out v) ? v : def;
        }

        public void Set(string key, string value)
        {
            _v[key] = value ?? "";
        }

        public bool GetBool(string key)
        {
            return Get(key, "0") == "1";
        }

        private static bool IsValidKey(string k)
        {
            if (k.Length == 0) return false;
            foreach (char c in k)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }
    }

    // =====================================================================
    //  通用工具
    // =====================================================================
    public static class Util
    {
        public static bool IsPortListening(int port)
        {
            try
            {
                var tcp = new TcpClient();
                var iar = tcp.BeginConnect("127.0.0.1", port, null, null);
                bool ok = iar.AsyncWaitHandle.WaitOne(600, false);
                if (ok && tcp.Connected) { tcp.EndConnect(iar); tcp.Close(); return true; }
                tcp.Close();
            }
            catch { }
            return false;
        }

        public static string GetHttpString(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.Method = "GET";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        public static string RunExe(string exe, string args, string workDir, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.WorkingDirectory = workDir;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                    return outp;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 运行外部命令并分别收全 stdout / stderr。
        ///
        /// 存在的理由：进程里的 psi 同时设置了 RedirectStandardOutput 与 RedirectStandardError，
        /// 而顺序 ReadToEnd 会在子进程写满 stderr 管道（Windows 默认 4KB）时互相死锁 ——
        /// npm 与 powershell 的输出量经常超过这个值，所以那不是理论风险。
        /// 这里两个流并发读取，并在超时后强制结束整棵进程树。
        /// </summary>
        public static bool RunCaptured(ProcessStartInfo psi, int timeoutMs, out string stdout, out string stderr, out int exitCode)
        {
            stdout = "";
            stderr = "";
            exitCode = -1;
            try
            {
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                var sbOut = new StringBuilder();
                var sbErr = new StringBuilder();
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };
                    if (!p.Start()) { stderr = "无法启动进程：" + psi.FileName; return false; }
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    bool exited = p.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        try { KillTreeForce(p.Id); } catch { }
                        stdout = sbOut.ToString();
                        stderr = sbErr.ToString() + Environment.NewLine + "（已超时 " + timeoutMs + "ms，进程树已终止）";
                        return false;
                    }
                    p.WaitForExit();   // 再等一次，确保异步读取把缓冲刷完
                    stdout = sbOut.ToString();
                    stderr = sbErr.ToString();
                    try { exitCode = p.ExitCode; } catch { exitCode = -1; }
                    return true;
                }
            }
            catch (Exception ex) { stderr = ex.Message; return false; }
        }

        public static bool RunCaptured(ProcessStartInfo psi, int timeoutMs, out string stdout, out string stderr)
        {
            int ec;
            return RunCaptured(psi, timeoutMs, out stdout, out stderr, out ec);
        }

        public static bool RunCaptured(ProcessStartInfo psi, int timeoutMs)
        {
            string a, b; int ec;
            return RunCaptured(psi, timeoutMs, out a, out b, out ec);
        }

        public static void KillTree(int pid)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch { }
        }

        public static void KillTreeForce(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe",
                    string.Format("/PID {0} /T /F", pid));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch { }
        }

        public static string FileVersionOrDash(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "-";
            try { return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "-"; }
            catch { return "-"; }
        }
    }

    // =====================================================================
    //  dsh 服务管理（启动/停止 Harness Web UI）
    // =====================================================================
    public class DshService
    {
        public ServerState State { get; private set; }
        public Process Proc { get; private set; }
        public int Port { get; set; }
        public event EventHandler StateChanged;
        public event Action<string> LogLine;

        private readonly object _lock = new object();

        public DshService() { Port = 3080; State = ServerState.Stopped; }

        public void SetState(ServerState s)
        {
            lock (_lock)
            {
                if (State == s) return;
                State = s;
            }
            var h = StateChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void Emit(string line)
        {
            var h = LogLine;
            if (h != null) h("[dsh] " + line);
        }

        public bool Ready()
        {
            return State == ServerState.Running && Util.IsPortListening(Port);
        }

        // 若 user.env 配置了 DSH_PERMISSION，则写进 dsh 当前真正读取的配置文件。
        //
        // dsh >= 0.2 把活配置从 $DSH_HOME/settings.yaml 搬到了
        // $DSH_HOME/profiles/<profile>/cordis.patch.yml（`- id:` 数组）；settings.yaml 只在首次启动
        // 被导入一次，且只认 ui-*/shell 几个 section，自造的 permissionPresets 会被忽略。
        // 旧写法继续写 settings.yaml，会让 DSH_PERMISSION 在 0.2+ 上静默失效（新会话回落到
        // workspace-write + ask）。所以：profile patch 存在 => 补/更新 `permission` 条目的
        // config.defaultPreset，其它字节不动；否则退回旧写法，保证 0.1.x 仍可用。
        // 仅在“完全没有权限配置”时写入一次，用户在 dsh UI 内改过的选择不会被覆盖。
        private void EnsureDshPermission()
        {
            try
            {
                var cfg = new ConfigManager(Paths.UserEnv);
                string perm = cfg.Get("DSH_PERMISSION", "").Trim();
                if (string.IsNullOrEmpty(perm)) return;

                string patch = ProfilePatchPath();
                if (patch != null && File.Exists(patch))
                {
                    string text = File.ReadAllText(patch, System.Text.Encoding.UTF8);
                    if (PatchHasPermissionDefault(text)) return;   // 尊重既有选择
                    string next;
                    if (TryUpsertPermissionPreset(text, perm, out next))
                    {
                        string backup = patch + ".perm-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        try { File.Copy(patch, backup, true); } catch { }
                        AtomicWriteAllText(patch, next);
                        Emit("已写入 profile patch 默认权限：" + perm + " -> " + patch);
                    }
                    else
                    {
                        Emit("profile patch 结构无法识别，未改动权限（请手动设置）：" + patch);
                    }
                    return;
                }

                // 旧版（0.1.x）：settings.yaml 就是活配置
                string settings = Path.Combine(Paths.DshHome, "settings.yaml");
                if (!File.Exists(settings)) return;
                string yaml = File.ReadAllText(settings, System.Text.Encoding.UTF8);
                if (yaml.Contains("permissionPresets")) return;
                File.AppendAllText(settings,
                    "\npermissionPresets:\n  defaultPreset: " + perm + "\n",
                    new System.Text.UTF8Encoding(false));
                Emit("已设置 dsh 默认权限模式（settings.yaml，旧版）：" + perm);
            }
            catch { }
        }

        // profile patch 路径：优先 web，其次唯一的那个 profile。找不到返回 null。
        private static string ProfilePatchPath()
        {
            string profiles = Path.Combine(Paths.DshHome, "profiles");
            if (!Directory.Exists(profiles)) return null;
            string web = Path.Combine(profiles, "web", "cordis.patch.yml");
            if (File.Exists(web)) return web;
            try
            {
                string[] dirs = Directory.GetDirectories(profiles);
                if (dirs.Length == 1)
                {
                    string p = Path.Combine(dirs[0], "cordis.patch.yml");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        // 已有 defaultPreset（不论在哪条 id 下）或旧格式残留，都算用户已配置。
        private static bool PatchHasPermissionDefault(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Contains("permissionPresets")) return true;
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.TrimStart().StartsWith("defaultPreset:")) return true;
            }
            return false;
        }

        // 在 patch 里补/更新 `- id: permission` 条目的 config.defaultPreset。
        // 逐行处理，只动该条目；false 表示结构不是我们认识的那种（调用方不改文件）。
        private static bool TryUpsertPermissionPreset(string text, string perm, out string result)
        {
            result = text;
            if (text == null || text.Trim().Length == 0) return false;
            string nl = text.IndexOf("\r\n") >= 0 ? "\r\n" : "\n";
            List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));

            int start = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimEnd() == "- id: permission") { start = i; break; }
            }
            if (start < 0)
            {
                int end = lines.Count;
                while (end > 0 && lines[end - 1].Trim().Length == 0) end--;
                if (end > 0 && lines[end - 1].Trim().Length != 0) lines.Insert(end++, ""); // 空行分隔，便于阅读
                lines.Insert(end, "- id: permission");
                lines.Insert(end + 1, "  name: \"@deepseek-ai/dsh-permission-presets\"");
                lines.Insert(end + 2, "  config:");
                lines.Insert(end + 3, "    defaultPreset: " + perm);
                result = string.Join(nl, lines.ToArray());
                return true;
            }

            int entryEnd = lines.Count;
            for (int i = start + 1; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("- ")) { entryEnd = i; break; }
            }

            int cfgAt = -1;
            for (int i = start + 1; i < entryEnd; i++)
            {
                if (lines[i].TrimEnd() == "  config:") { cfgAt = i; break; }
            }
            if (cfgAt < 0)
            {
                int nameAt = -1;
                for (int i = start + 1; i < entryEnd; i++)
                {
                    if (lines[i].TrimStart().StartsWith("name:")) { nameAt = i; break; }
                }
                int at = nameAt >= 0 ? nameAt + 1 : start + 1;
                lines.Insert(at, "  config:");
                lines.Insert(at + 1, "    defaultPreset: " + perm);
            }
            else
            {
                int dp = -1;
                for (int i = cfgAt + 1; i < entryEnd; i++)
                {
                    string s = lines[i].TrimStart();
                    if (s.Length == 0) continue;
                    if (lines[i].Length > 0 && lines[i][0] != ' ') break; // 条目结束
                    if (s.StartsWith("defaultPreset:"))
                    {
                        lines[i] = "    defaultPreset: " + perm;
                        dp = i;
                        break;
                    }
                }
                if (dp < 0) lines.Insert(cfgAt + 1, "    defaultPreset: " + perm);
            }
            result = string.Join(nl, lines.ToArray());
            return true;
        }

        // 原子写文本：同目录临时文件 + File.Replace，避免便携盘中途拔出留下半截文件。
        private static void AtomicWriteAllText(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
            try { File.Replace(tmp, path, null); }
            catch { File.Copy(tmp, path, true); File.Delete(tmp); }
        }

        // 端口自动探测：从首选端口起，返回第一个空闲端口（避免硬编码 3080 被占用）
        public static int DetectAvailablePort(int preferred)
        {
            if (preferred < 1024) preferred = 3080;
            for (int p = preferred; p < preferred + 30; p++)
            {
                if (!Util.IsPortListening(p)) return p;
            }
            return preferred;
        }

        // 启动：直接调用便携 node + dsh bin.js，设置 DSH_HOME 等环境变量
        public void Start()
        {
            lock (_lock)
            {
                if (State == ServerState.Running || State == ServerState.Starting) return;
                SetState(ServerState.Starting);
            }
            Emit("正在启动 DeepSeek Harness ...");

            Paths.EnsureDirs();

            // 可选：按 user.env 中 DSH_AUTO_START_MODEL=1 自动拉起本地模型服务（默认关闭）
            try
            {
                var autoCfg = new ConfigManager(Paths.UserEnv);
                if (autoCfg.GetBool("DSH_AUTO_START_MODEL"))
                {
                    Emit("检测到「自动加载本地模型」已开启，自动拉起模型服务 ...");
                    RunModelStarter();
                }
                else
                {
                    Emit("本地模型为按需加载（默认关闭），如需自动加载请在「模型与 API」开启。");
                }
            }
            catch { }

            // 首次运行检测：便携 node 或 dsh 缺失 -> 调用 bootstrap
            if (!File.Exists(Paths.NodeExe) || !File.Exists(Paths.DshJsBin))
            {
                Emit("便携运行时缺失，开始自动部署（bootstrap）...");
                var bootstrapOk = RunBootstrap();
                if (!bootstrapOk) { SetState(ServerState.Error); Emit("自动部署失败，请检查网络后重试。"); return; }
            }

            if (!File.Exists(Paths.NodeExe))
            {
                SetState(ServerState.Error);
                Emit("未找到便携 Node.js（tools\\node\\node.exe）。请运行 bootstrap.ps1 或设置 DSH_SYSTEM_NODE=1。");
                return;
            }
            if (!File.Exists(Paths.DshJsBin))
            {
                SetState(ServerState.Error);
                Emit("未找到 dsh 内核（tools\\global\\node_modules\\@deepseek-ai\\dsh）。请运行 bootstrap.ps1。");
                return;
            }

            // 若 user.env 配置了 DSH_PERMISSION，且 dsh settings.yaml 尚无权限配置，则写入默认权限（避免新会话回落到 workspace-write+ask）
            EnsureDshPermission();

            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Paths.NodeExe;
                // 显式绑定回环地址。dsh 默认值本就是 127.0.0.1（dsh-web-app/cordis.patch.yml），
                // 但本部署配置为 danger-full-access，显式写出可避免将来默认值变化导致局域网暴露。
                psi.Arguments = "\"" + Paths.DshJsBin + "\" web --host 127.0.0.1 --port " + Port + " --no-open";
                psi.WorkingDirectory = Paths.WsDir;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                var env = psi.EnvironmentVariables;
                env["DSH_HOME"] = Paths.DshHome;
                env["PATH"] = Paths.NodeDir + ";" + Paths.GlobalDir + ";" + Environment.GetEnvironmentVariable("PATH");
                env["npm_config_cache"] = Path.Combine(Paths.Tools, "npm-cache");
                env["npm_config_prefix"] = Paths.GlobalDir;
                env["npm_config_update_notifier"] = "false";
                // 注入 user.env 中的其它变量（如 DEEPSEEK_API_KEY、DSH_EXTRA_ENDPOINTS）
                InjectUserEnv(env);

                Proc = new Process();
                Proc.StartInfo = psi;
                Proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Emit(e.Data); };
                Proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Emit(e.Data); };
                Proc.Exited += (s, e) =>
                {
                    SetState(ServerState.Stopped);
                    Emit("dsh 进程已退出。");
                };
                Proc.EnableRaisingEvents = true;
                if (!Proc.Start())
                {
                    SetState(ServerState.Error);
                    Emit("无法启动 dsh 进程。");
                    return;
                }
                Proc.BeginOutputReadLine();
                Proc.BeginErrorReadLine();
                ProcessSupervisor.Assign(Proc);   // 双保险：自身已在 Job 时子进程本就会继承
                Emit(string.Format("dsh started (PID {0}), waiting for port {1} ...", Proc.Id, Port));
                System.Threading.Tasks.Task.Run(() => WaitForPortReady(Proc.Id));
            }
            catch (Exception ex)
            {
                SetState(ServerState.Error);
                Emit("启动失败: " + ex.Message);
            }
        }

        private void InjectUserEnv(System.Collections.Specialized.StringDictionary env)
        {
            try
            {
                var cfg = new ConfigManager(Paths.UserEnv);
                // 非密钥项从 user.env 注入
                foreach (var key in new[] { "DSH_EXTRA_ENDPOINTS", "DSH_LLAMA_PORT", "DSH_LLAMA_DIR" })
                {
                    string v = cfg.Get(key, "");
                    if (v.Length > 0) env[key] = v;
                }
                // 密钥只从 DPAPI 加密库取，并且只注入子进程环境变量，不落盘。
                // dsh 的 credentials 解析顺序为 env -> .credentials.yaml -> .env（dsh-credentials-local），
                // 环境变量优先级最高，因此无需再写任何文件。
                string apiKey;
                if (SecretStore.TryGet("DEEPSEEK_API_KEY", out apiKey) && apiKey.Length > 0)
                    env["DEEPSEEK_API_KEY"] = apiKey;
            }
            catch { }
        }

        private bool RunBootstrap()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\"", Paths.BootstrapPs1));
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    var buffer = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) { buffer.AppendLine(e.Data); Emit(e.Data); } };
                    p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Emit(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(600000)) { p.Kill(); return false; }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // 按需自动拉起本地模型服务（仅当用户在设置中开启自动加载）
        private void RunModelStarter()
        {
            try
            {
                if (!File.Exists(Paths.ModelStarterPs1)) return;
                var psi = new ProcessStartInfo("powershell.exe",
                    string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\"", Paths.ModelStarterPs1));
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.EnvironmentVariables["DSH_HOME"] = Paths.DshHome;
                psi.EnvironmentVariables["PATH"] = Paths.NodeDir + ";" + Paths.GlobalDir + ";" + Environment.GetEnvironmentVariable("PATH");
                var cfg = new ConfigManager(Paths.UserEnv);
                foreach (var key in new[] { "DSH_AUTO_START_MODEL", "DSH_MODEL_BACKEND", "DSH_LLAMA_DIR",
                    "DSH_LLAMA_MODEL", "DSH_LLAMA_PORT", "DSH_LLAMA_CONTEXT", "DSH_LLAMA_GPU_LAYERS",
                    "DSH_LLAMA_EXTRA_ARGS", "DSH_OLLAMA_EXE", "DSH_OLLAMA_MODEL" })
                {
                    string v = cfg.Get(key, "");
                    if (v.Length > 0) psi.EnvironmentVariables[key] = v;
                }
                // 并发读双流 + 超时强杀，替代原来的顺序 ReadToEnd（stderr 写满即死锁）
                string outp, errp;
                Util.RunCaptured(psi, 300000, out outp, out errp);
                if (!string.IsNullOrEmpty(outp)) Emit(outp.Trim());
                if (!string.IsNullOrEmpty(errp)) Emit("[model-server] " + errp.Trim());
            }
            catch (Exception ex) { Emit("自动加载模型服务失败: " + ex.Message); }
        }
        // Background wait for port ready: up to 60s, check every 500ms; switch to Running when ready
        private void WaitForPortReady(int pid)
        {
            try
            {
                for (int i = 0; i < 120; i++)
                {
                    System.Threading.Thread.Sleep(500);
                    try { Process.GetProcessById(pid); } catch { return; }
                    if (Util.IsPortListening(Port))
                    {
                        if (State == ServerState.Starting) { SetState(ServerState.Running); Emit("Service ready: http://127.0.0.1:" + Port); }
                        return;
                    }
                }
                if (State == ServerState.Starting) { Emit("Port ready timeout (60s), service may have failed to start, check logs."); }
            }
            catch { }
        }



        public void Stop()
        {
            lock (_lock)
            {
                if (State == ServerState.Stopped) return;
                SetState(ServerState.Stopping);
            }
            Emit("正在停止 dsh ...");
            if (Proc != null)
            {
                try
                {
                    Util.KillTreeForce(Proc.Id);
                }
                catch { }
                Proc = null;
            }
            SetState(ServerState.Stopped);
            Emit("dsh 已停止。");
        }

        // 运行 auto-config 探测本地模型端点（轻量，不启动模型）
        public void RunAutoConfig()
        {
            try
            {
                if (!File.Exists(Paths.AutoConfigJs)) return;
                var psi = new ProcessStartInfo(Paths.NodeExe, "\"" + Paths.AutoConfigJs + "\"");
                psi.WorkingDirectory = Paths.Root;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.EnvironmentVariables["DSH_HOME"] = Paths.DshHome;
                psi.EnvironmentVariables["PATH"] = Paths.NodeDir + ";" + Paths.GlobalDir + ";" + Environment.GetEnvironmentVariable("PATH");
                InjectUserEnv(psi.EnvironmentVariables);
                string outp, err;
                Util.RunCaptured(psi, 90000, out outp, out err);
                if (!string.IsNullOrEmpty(outp)) Emit(outp.Trim());
                if (!string.IsNullOrEmpty(err)) Emit("[auto-config] " + err.Trim());
            }
            catch (Exception ex) { Emit("auto-config 失败: " + ex.Message); }
        }
    }

    // =====================================================================
    //  本地模型服务（Ollama / llama.cpp）—— 按需启动，不随应用启动
    // =====================================================================
    public class LocalModelService
    {
        public event Action<string> LogLine;
        private Process _ollamaProc;
        private Process _llamaProc;
        public int LlamaPort { get; set; }

        public LocalModelService() { LlamaPort = 11435; }

        private void Emit(string line)
        {
            var h = LogLine;
            if (h != null) h(line);
        }

        // ---------- Ollama ----------
        public string DetectOllamaPath()
        {
            string[] candidates = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
                @"C:\Program Files\Ollama\ollama.exe",
                @"C:\Program Files (x86)\Ollama\ollama.exe",
                @"D:\Ollama\ollama.exe",
                @"D:\ollama\ollama.exe"
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            // PATH 搜索
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string f = Path.Combine(dir.Trim('"'), "ollama.exe");
                    if (File.Exists(f)) return f;
                }
            }
            catch { }
            return "";
        }

        public bool OllamaRunning() { return Util.IsPortListening(11434); }

        public List<string> ListOllamaModels()
        {
            var list = new List<string>();
            try
            {
                string json = Util.GetHttpString("http://127.0.0.1:11434/api/tags", 3000);
                if (string.IsNullOrEmpty(json)) return list;
                // 极简解析：匹配 "name":"..."
                var re = new System.Text.RegularExpressions.Regex("\"name\"\\s*:\\s*\"([^\"]+)\"");
                foreach (System.Text.RegularExpressions.Match m in re.Matches(json))
                    list.Add(m.Groups[1].Value);
                list.Sort();
            }
            catch { }
            return list;
        }

        public bool StartOllama(string exe)
        {
            if (OllamaRunning()) { Emit("Ollama 已在运行（端口 11434）。"); return true; }
            if (!File.Exists(exe)) { Emit("未找到 ollama.exe：" + exe); return false; }
            try
            {
                var psi = new ProcessStartInfo(exe, "serve");
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                _ollamaProc = Process.Start(psi);
                _ollamaProc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Emit("[ollama] " + e.Data); };
                _ollamaProc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Emit("[ollama] " + e.Data); };
                _ollamaProc.BeginOutputReadLine();
                _ollamaProc.BeginErrorReadLine();
                Emit("Ollama 服务已启动，等待就绪 ...");
                return true;
            }
            catch (Exception ex) { Emit("启动 Ollama 失败: " + ex.Message); return false; }
        }

        public void StopOllama()
        {
            if (_ollamaProc != null)
            {
                try { Util.KillTreeForce(_ollamaProc.Id); } catch { }
                _ollamaProc = null;
            }
            else
            {
                // 原来这里会杀掉系统上所有名为 ollama 的进程 —— 会误杀用户手动启动的实例。
                // 改为不处理；若本进程已加入 Job Object，子进程退出时会由内核回收。
                Emit("Ollama 不是由本启动器启动的，未做处理（避免误杀你自己启动的实例）。");
                return;
            }
            Emit("Ollama 服务已停止。");
        }

        // 预载模型：向 ollama 的 /api/generate 发一次空 prompt 请求，带 keep_alive 让服务
        // 把权重读进显存后立即返回。
        //
        // 绝对不要用 `ollama run <model>` —— 那会进入交互式 REPL 并等待 stdin，
        // 配合 ReadToEnd() 就是永久挂起（原实现的 bug：该命令永远不返回，
        // 前端 60s 后超时，后台线程泄漏）。HTTP 方式同时也能反映真实的加载失败。
        public bool LoadOllamaModel(string exe, string model)
        {
            if (string.IsNullOrEmpty(model)) { Emit("未选择模型。"); return false; }
            try
            {
                string esc = model.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string body = "{\"model\":\"" + esc + "\",\"prompt\":\"\",\"stream\":false,\"keep_alive\":\"30m\"}";
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:11434/api/generate");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 900000;          // 首次加载大模型可能很慢（要从盘上读数 GB）
                req.ReadWriteTimeout = 900000;
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    sr.ReadToEnd();
                }
                Emit("模型已预载（keep_alive=30m）：" + model);
                return true;
            }
            catch (Exception ex)
            {
                Emit("预载模型失败：" + ex.Message + "（不影响使用，模型会在首次请求时按需加载）");
                return false;
            }
        }

        // ---------- llama.cpp ----------
        public string DetectLlamaDir()
        {
            string[] candidates = {
                @"D:\llama.cpp", @"D:\llama-cpp", @"D:\llama.cpp-master",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "llama.cpp")
            };
            foreach (var c in candidates)
            {
                if (Directory.Exists(c) && File.Exists(Path.Combine(c, "llama-server.exe"))) return c;
            }
            return "";
        }

        // llama.cpp 安装目录：优先用户配置 DSH_LLAMA_EXE_DIR，否则自动探测
        public string LlamaExeDir()
        {
            try
            {
                string cfg = new ConfigManager(Paths.UserEnv).Get("DSH_LLAMA_EXE_DIR", "");
                if (!string.IsNullOrEmpty(cfg) && Directory.Exists(cfg) && File.Exists(Path.Combine(cfg, "llama-server.exe")))
                    return cfg;
            }
            catch { }
            return DetectLlamaDir();
        }

        // 模型扫描目录：优先用户配置 DSH_LLAMA_DIR；不存在时自动探测常见模型目录
        public string DetectModelDir()
        {
            try
            {
                var cfg = new ConfigManager(Paths.UserEnv);
                string d = cfg.Get("DSH_LLAMA_DIR", "");
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
                string[] candidates = {
                    @"D:\Ollama\Models", @"D:\models", @"D:\llm-models", @"D:\ai-models",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "models")
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(c) && Directory.GetFiles(c, "*.gguf", SearchOption.AllDirectories).Length > 0) return c;
                }
                return d;
            }
            catch { return ""; }
        }

        public List<string> FindGgufModels(string dir)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                try
                {
                    foreach (var f in Directory.GetFiles(cur, "*.gguf", SearchOption.TopDirectoryOnly))
                        list.Add(f);
                    foreach (var sub in Directory.GetDirectories(cur))
                        stack.Push(sub);
                }
                catch { } // 跳过无权限/无法访问的子目录，避免整体扫描失败
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // 详细 GGUF 扫描：带文件名与大小
        public class GgufFile
        {
            public string Path;
            public string Name;
            public long SizeBytes;
        }

        public List<GgufFile> FindGgufModelsDetailed(string dir)
        {
            var list = new List<GgufFile>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                try
                {
                    foreach (var f in Directory.GetFiles(cur, "*.gguf", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            list.Add(new GgufFile { Path = f, Name = fi.Name, SizeBytes = fi.Length });
                        }
                        catch { }
                    }
                    foreach (var sub in Directory.GetDirectories(cur))
                        stack.Push(sub);
                }
                catch { } // 跳过无权限/无法访问的子目录，避免整体扫描失败
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public bool LlamaRunning() { return Util.IsPortListening(LlamaPort); }

        public bool StartLlama(string dir, string model, string ctx, string gpu, string extraArgs)
        {
            if (LlamaRunning()) { Emit(string.Format("llama-server 已在运行（端口 {0}）。", LlamaPort)); return true; }
            string exe = Path.Combine(dir, "llama-server.exe");
            if (!File.Exists(exe)) { Emit("未找到 llama-server.exe：" + exe); return false; }
            if (!File.Exists(model)) { Emit("模型文件不存在：" + model); return false; }
            try
            {
                var sb = new StringBuilder();
                sb.Append("\"-m\" \"").Append(model).Append("\"");
                sb.Append(" --port ").Append(LlamaPort);
                sb.Append(" -c ").Append(string.IsNullOrEmpty(ctx) ? "4096" : ctx);
                sb.Append(" -ngl ").Append(string.IsNullOrEmpty(gpu) ? "99" : gpu);
                sb.Append(" --host 127.0.0.1");
                if (!string.IsNullOrEmpty(extraArgs)) sb.Append(" ").Append(extraArgs);

                var psi = new ProcessStartInfo(exe, sb.ToString());
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                _llamaProc = Process.Start(psi);
                _llamaProc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Emit("[llama.cpp] " + e.Data); };
                _llamaProc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Emit("[llama.cpp] " + e.Data); };
                _llamaProc.BeginOutputReadLine();
                _llamaProc.BeginErrorReadLine();
                Emit(string.Format("llama-server 已启动（端口 {0}），等待就绪 ...", LlamaPort));
                return true;
            }
            catch (Exception ex) { Emit("启动 llama-server 失败: " + ex.Message); return false; }
        }

        public void StopLlama()
        {
            if (_llamaProc != null)
            {
                try { Util.KillTreeForce(_llamaProc.Id); } catch { }
                _llamaProc = null;
            }
            else
            {
                // 同上：不再按进程名全杀
                Emit("llama-server 不是由本启动器启动的，未做处理。");
                return;
            }
            Emit("llama-server 已停止。");
        }

        public void StopAll()
        {
            StopOllama();
            StopLlama();
        }
    }

    // =====================================================================
    //  日志管理：追加到 logs\app.log（带轮转），并定期清理旧日志控制存储
    // =====================================================================
    public static class LogManager
    {
        private static readonly object _lock = new object();
        private const long MaxSize = 1024 * 1024;       // 单文件上限 1MB
        private static int _lastCleanedDay = -1;

        public static string LogFile { get { return Path.Combine(Paths.LogsDir, "app.log"); } }

        public static void Write(string line)
        {
            try
            {
                lock (_lock)
                {
                    string file = LogFile;
                    string dir = Path.GetDirectoryName(file);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string full = "[" + ts + "] " + line + Environment.NewLine;
                    File.AppendAllText(file, full, Encoding.UTF8);
                    // 轮转：超过上限则改名 app.log.1
                    var fi = new FileInfo(file);
                    if (fi.Length > MaxSize)
                    {
                        try { File.Copy(file, file + ".1", true); File.WriteAllText(file, "", Encoding.UTF8); }
                        catch { }
                    }
                    CleanupIfDue();
                }
            }
            catch { }
        }

        // 每天最多清理一次：删除 LogsDir 下 7 天前的 .log* 文件
        private static void CleanupIfDue()
        {
            int today = DateTime.Now.DayOfYear;
            if (_lastCleanedDay == today) return;
            _lastCleanedDay = today;
            try
            {
                if (!Directory.Exists(Paths.LogsDir)) return;
                var cutoff = DateTime.Now.AddDays(-7);
                foreach (var f in Directory.GetFiles(Paths.LogsDir, "*.log*"))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.LastWriteTime < cutoff) fi.Delete();
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    // =====================================================================
    //  主题持久化：config\theme.json（原样保存前端传来的 JSON）
    // =====================================================================
    public class ThemeStore
    {
        private string _file;
        public ThemeStore(string file) { _file = file; }

        public string Load()
        {
            try { if (File.Exists(_file)) return File.ReadAllText(_file, Encoding.UTF8); }
            catch { }
            return "";
        }

        public void Save(string json)
        {
            try
            {
                string dir = Path.GetDirectoryName(_file);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_file, json ?? "", new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // =====================================================================
    //  插件管理：扫描/安装/卸载 @deepseek-ai/dsh-* 插件
    // =====================================================================
    public class PluginInfo
    {
        public string Name;
        public string Version;
        public string Dir;
    }

    public class PluginService
    {
        public event Action<string> LogLine;
        private void Emit(string line) { var h = LogLine; if (h != null) h("[plugin] " + line); }

        public List<PluginInfo> ListInstalled()
        {
            var list = new List<PluginInfo>();
            try
            {
                string scopeDir = Path.Combine(Paths.GlobalDir, "node_modules", "@deepseek-ai");
                if (!Directory.Exists(scopeDir)) return list;
                foreach (var d in Directory.GetDirectories(scopeDir))
                {
                    string name = Path.GetFileName(d);
                    if (!name.StartsWith("dsh-")) continue;
                    var info = new PluginInfo { Name = "@deepseek-ai/" + name, Dir = d, Version = "-" };
                    string pkg = Path.Combine(d, "package.json");
                    if (File.Exists(pkg))
                    {
                        try
                        {
                            string txt = File.ReadAllText(pkg, Encoding.UTF8);
                            var m = System.Text.RegularExpressions.Regex.Match(txt, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                            if (m.Success) info.Version = m.Groups[1].Value;
                        }
                        catch { }
                    }
                    list.Add(info);
                }
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            return list;
        }

        // 通过便携 npm 安装插件包
        public bool Install(string package)
        {
            try
            {
                string npmCli = Path.Combine(Paths.NodeDir, "node_modules", "npm", "bin", "npm-cli.js");
                if (!File.Exists(npmCli)) npmCli = FindNpmCli();
                if (!File.Exists(npmCli)) { Emit("未找到 npm-cli.js，无法安装插件。"); return false; }

                var psi = new ProcessStartInfo(Paths.NodeExe, "\"" + npmCli + "\" install -g --legacy-peer-deps --foreground-scripts " + package);
                psi.WorkingDirectory = Paths.Root;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.EnvironmentVariables["PATH"] = Paths.NodeDir + ";" + Paths.GlobalDir + ";" + Environment.GetEnvironmentVariable("PATH");
                psi.EnvironmentVariables["npm_config_cache"] = Path.Combine(Paths.Tools, "npm-cache");
                psi.EnvironmentVariables["npm_config_prefix"] = Paths.GlobalDir;
                string outp, err; int ec;
                bool exited = Util.RunCaptured(psi, 300000, out outp, out err, out ec);
                if (!string.IsNullOrWhiteSpace(outp)) Emit(outp.Trim());
                if (!string.IsNullOrWhiteSpace(err)) Emit(err.Trim());
                return exited && ec == 0;
            }
            catch (Exception ex) { Emit("安装插件失败: " + ex.Message); return false; }
        }

        private string FindNpmCli()
        {
            try
            {
                foreach (var d in Directory.GetDirectories(Paths.NodeDir, "node_modules", SearchOption.AllDirectories))
                {
                    string f = Path.Combine(d, "npm", "bin", "npm-cli.js");
                    if (File.Exists(f)) return f;
                }
            }
            catch { }
            return "";
        }

        public bool Uninstall(string package)
        {
            try
            {
                string npmCli = Path.Combine(Paths.NodeDir, "node_modules", "npm", "bin", "npm-cli.js");
                if (!File.Exists(npmCli)) npmCli = FindNpmCli();
                if (!File.Exists(npmCli)) { Emit("未找到 npm-cli.js，无法卸载插件。"); return false; }
                var psi = new ProcessStartInfo(Paths.NodeExe, "\"" + npmCli + "\" uninstall -g " + package);
                psi.WorkingDirectory = Paths.Root;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.EnvironmentVariables["PATH"] = Paths.NodeDir + ";" + Paths.GlobalDir + ";" + Environment.GetEnvironmentVariable("PATH");
                psi.EnvironmentVariables["npm_config_cache"] = Path.Combine(Paths.Tools, "npm-cache");
                psi.EnvironmentVariables["npm_config_prefix"] = Paths.GlobalDir;
                string outp, err; int ec;
                bool exited = Util.RunCaptured(psi, 300000, out outp, out err, out ec);
                if (!string.IsNullOrWhiteSpace(outp)) Emit(outp.Trim());
                if (!string.IsNullOrWhiteSpace(err)) Emit(err.Trim());
                return exited && ec == 0;
            }
            catch (Exception ex) { Emit("卸载插件失败: " + ex.Message); return false; }
        }
    }
}
