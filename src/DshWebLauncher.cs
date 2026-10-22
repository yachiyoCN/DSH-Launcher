// DSHWebLauncher — 托盘常驻的 dsh web 启动器（C# 5 / .NET Framework 4.x）
// 无黑窗、托盘菜单、开机自启、崩溃自动拉起、工作目录与日志可配置。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("DSH 启动器")]
[assembly: AssemblyDescription("DeepSeek Harness Web 托盘启动器")]
[assembly: AssemblyCompany("千夏Tesni")]
[assembly: AssemblyProduct("DSH 启动器")]
[assembly: AssemblyCopyright("Copyright © 2026 千夏Tesni")]
[assembly: AssemblyInformationalVersion("1.0.4")]
[assembly: AssemblyVersion("1.0.4.0")]
[assembly: AssemblyFileVersion("1.0.4.0")]
[assembly: ComVisible(false)]

namespace DshWebLauncher
{
    internal static class Settings
    {
        public static string NodePath = "";
        public static string DshBin = "";
        public static string Profile = "web";
        public static int Port = 3080;
        public static string WorkDir = "";
        public static bool AutoRestart = true;

        public static string AppDir
        {
            get
            {
                string overrideDir = Environment.GetEnvironmentVariable("DSHLAUNCHER_HOME");
                if (!string.IsNullOrEmpty(overrideDir))
                {
                    Directory.CreateDirectory(overrideDir);
                    return overrideDir;
                }
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHWebLauncher");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string LogDir
        {
            get
            {
                string p = Path.Combine(AppDir, "logs");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string IniPath { get { return Path.Combine(AppDir, "settings.ini"); } }

        public static void Load()
        {
            if (File.Exists(IniPath))
            {
                try
                {
                    foreach (string raw in File.ReadAllLines(IniPath, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        if (k == "nodepath") NodePath = v;
                        else if (k == "dshbin") DshBin = v;
                        else if (k == "profile") Profile = v;
                        else if (k == "port") { int tmp; if (int.TryParse(v, out tmp)) Port = tmp; }
                        else if (k == "workdir") WorkDir = v;
                        else if (k == "autorestart") AutoRestart = v != "0";
                    }
                }
                catch { }
            }
            if (Port <= 0) Port = 3080;
            if (string.IsNullOrEmpty(WorkDir)) WorkDir = DefaultWorkDir();
            ResolveRuntime(false);
            Save();
        }

        public static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# DSH Web Launcher 配置（改完重启托盘程序生效）");
                sb.AppendLine("NodePath=" + NodePath);
                sb.AppendLine("DshBin=" + DshBin);
                sb.AppendLine("Profile=" + Profile);
                sb.AppendLine("Port=" + Port);
                sb.AppendLine("WorkDir=" + WorkDir);
                sb.AppendLine("AutoRestart=" + (AutoRestart ? "1" : "0"));
                File.WriteAllText(IniPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>运行时是否已经找到可执行的 Node 和 DSH。</summary>
        public static bool RuntimeReady
        {
            get { return IsUsableFile(NodePath) && IsUsableFile(DshBin); }
        }

        /// <summary>用于日志和状态栏的可读摘要。</summary>
        public static string RuntimeSummary()
        {
            return "Node=" + DisplayPath(NodePath) + "；DSH=" + DisplayPath(DshBin);
        }

        /// <summary>
        /// 自动识别 Node 与 DSH。force=false 时只修复失效路径，force=true 时强制重新探测。
        /// 环境变量 DSH_NODE / DSH_BIN 优先，其次 npm 全局目录、where.exe 和 PATH。
        /// </summary>
        public static bool ResolveRuntime(bool force)
        {
            bool changed = false;

            if (force || !IsUsableFile(NodePath))
            {
                string found = DetectNodePath();
                if (!string.IsNullOrEmpty(found) && !string.Equals(found, NodePath, StringComparison.OrdinalIgnoreCase))
                {
                    NodePath = found;
                    changed = true;
                }
            }

            if (force || !IsUsableFile(DshBin))
            {
                string found = DetectDshBin();
                if (!string.IsNullOrEmpty(found) && !string.Equals(found, DshBin, StringComparison.OrdinalIgnoreCase))
                {
                    DshBin = found;
                    changed = true;
                }
            }

            if (changed) Save();

            // 找到 DSH 后顺手把 Node 目录和 npm 全局 bin 加到用户 PATH。
            if (RuntimeReady) AddRuntimeToUserPath();
            return RuntimeReady;
        }

        /// <summary>查找当前机器可用的 npm.cmd。</summary>
        public static string FindNpmCommand()
        {
            string fromWhere = FirstWhere("npm.cmd");
            if (fromWhere != null) return fromWhere;

            string nodeDir = Path.GetDirectoryName(NodePath);
            string nearNode = Combine(nodeDir, "npm.cmd");
            if (IsUsableFile(nearNode)) return nearNode;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return FirstExisting(
                Combine(programFiles, @"nodejs\npm.cmd"),
                Combine(programFilesX86, @"nodejs\npm.cmd"));
        }

        /// <summary>npm 全局安装前缀，例如 C:\Users\你\AppData\Roaming\npm。</summary>
        public static string GetNpmPrefix()
        {
            string marker = @"\node_modules\@deepseek-ai\dsh\";
            if (!string.IsNullOrEmpty(DshBin))
            {
                int idx = DshBin.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx > 0) return DshBin.Substring(0, idx).TrimEnd('\\');
            }

            string root = FindNpmRoot();
            if (!string.IsNullOrEmpty(root))
            {
                string parent = Path.GetDirectoryName(root.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(parent)) return parent;
            }

            string npm = FindNpmCommand();
            if (!string.IsNullOrEmpty(npm)) return Path.GetDirectoryName(npm);

            return Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        }

        /// <summary>把 Node 目录与 npm 全局 bin 自动加入当前用户 PATH。</summary>
        public static bool AddRuntimeToUserPath()
        {
            string nodeDir = Path.GetDirectoryName(NodePath);
            string npmPrefix = GetNpmPrefix();
            return AddToUserPath(new string[] { nodeDir, npmPrefix });
        }

        private static bool AddToUserPath(string[] dirs)
        {
            try
            {
                string userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
                if (userPath == null) userPath = "";

                StringBuilder userBuilder = new StringBuilder(userPath);
                bool changed = false;
                foreach (string raw in dirs)
                {
                    string dir = NormalizePathEntry(raw);
                    if (dir.Length == 0 || PathListContains(userPath, dir)) continue;
                    if (userBuilder.Length > 0 && userBuilder[userBuilder.Length - 1] != ';') userBuilder.Append(';');
                    userBuilder.Append(dir);
                    changed = true;
                }

                if (!changed) return false;
                Environment.SetEnvironmentVariable("Path", userBuilder.ToString(), EnvironmentVariableTarget.User);

                // 当前进程也要更新，否则后续查找 npm/dsh 仍可能用旧 PATH。
                string processPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process);
                if (processPath == null) processPath = "";
                StringBuilder processBuilder = new StringBuilder(processPath);
                foreach (string raw in dirs)
                {
                    string dir = NormalizePathEntry(raw);
                    if (dir.Length == 0 || PathListContains(processPath, dir)) continue;
                    if (processBuilder.Length > 0 && processBuilder[processBuilder.Length - 1] != ';') processBuilder.Append(';');
                    processBuilder.Append(dir);
                }
                Environment.SetEnvironmentVariable("Path", processBuilder.ToString(), EnvironmentVariableTarget.Process);
                BroadcastEnvironmentChange();
                return true;
            }
            catch { return false; }
        }

        private static string NormalizePathEntry(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                return expanded.Length > 3 ? expanded.TrimEnd('\\') : expanded;
            }
            catch { return ""; }
        }

        private static bool PathListContains(string pathList, string dir)
        {
            if (string.IsNullOrEmpty(pathList) || string.IsNullOrEmpty(dir)) return false;
            foreach (string raw in pathList.Split(';'))
            {
                string entry = NormalizePathEntry(raw);
                if (entry.Length > 0 && string.Equals(entry, dir, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void BroadcastEnvironmentChange()
        {
            try
            {
                UIntPtr result;
                SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 1000, out result);
            }
            catch { }
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
            uint flags, uint timeout, out UIntPtr result);

        private static string DetectNodePath()
        {
            string fromEnv = FirstExisting(
                Environment.GetEnvironmentVariable("DSH_NODE"),
                Environment.GetEnvironmentVariable("DSHLAUNCHER_NODE"));
            if (fromEnv != null) return fromEnv;

            string fromWhere = FirstWhere("node.exe");
            if (fromWhere != null) return fromWhere;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");

            string[] candidates = new string[] {
                Combine(programFiles, @"nodejs\node.exe"),
                Combine(programFilesX86, @"nodejs\node.exe"),
                Combine(localAppData, @"Programs\nodejs\node.exe"),
                Combine(nvmSymlink, "node.exe"),
                Combine(userProfile, @"scoop\apps\nodejs\current\node.exe"),
                Combine(userProfile, @"AppData\Local\Programs\nodejs\node.exe")
            };
            return FirstExisting(candidates);
        }

        private static string DetectDshBin()
        {
            string fromEnv = FirstExisting(
                Environment.GetEnvironmentVariable("DSH_BIN"),
                Environment.GetEnvironmentVariable("DSHLAUNCHER_DSH_BIN"));
            if (fromEnv != null) return fromEnv;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");

            string[] prefixes = new string[] {
                Combine(appData, "npm"),
                Combine(localAppData, "npm"),
                Combine(programFiles, "nodejs"),
                Combine(programFilesX86, "nodejs"),
                nvmSymlink,
                Combine(userProfile, @"scoop\apps\nodejs\current"),
                Combine(userProfile, @"AppData\Roaming\npm")
            };
            string found = FindDshInPrefixes(prefixes);
            if (found != null) return found;

            string npmRoot = FindNpmRoot();
            if (!string.IsNullOrEmpty(npmRoot))
            {
                found = FindDshInPrefix(npmRoot);
                if (found != null) return found;
            }

            string shims = RunCommand("where.exe", "dsh.cmd", 5000);
            foreach (string line in SplitLines(shims))
            {
                string shim = line.Trim();
                if (shim.Length == 0) continue;
                string prefix = Path.GetDirectoryName(shim);
                found = FindDshInPrefix(prefix);
                if (found != null) return found;
            }

            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string raw in pathEnv.Split(';'))
                {
                    string dir = raw.Trim().Trim('"');
                    if (dir.Length == 0) continue;
                    dir = Environment.ExpandEnvironmentVariables(dir);
                    found = FindDshInPathEntry(dir);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private static string FindNpmRoot()
        {
            string output = RunCommand("cmd.exe", "/d /c npm root -g", 8000);
            string root = FirstExistingLine(output);
            if (root != null) return root;

            string npmCmd = FirstWhere("npm.cmd");
            if (npmCmd != null)
            {
                output = RunCommand("cmd.exe", "/d /s /c \"\"" + npmCmd + "\" root -g\"", 8000);
                root = FirstExistingLine(output);
                if (root != null) return root;
            }
            return null;
        }

        private static string FindDshInPrefixes(string[] prefixes)
        {
            foreach (string prefix in prefixes)
            {
                string found = FindDshInPrefix(prefix);
                if (found != null) return found;
            }
            return null;
        }

        private static string FindDshInPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return null;
            string[] candidates = new string[] {
                Path.Combine(prefix, @"node_modules\@deepseek-ai\dsh\lib\bin.js"),
                Path.Combine(prefix, @"@deepseek-ai\dsh\lib\bin.js")
            };
            return FirstExisting(candidates);
        }

        private static string FindDshInPathEntry(string dir)
        {
            string found = FindDshInPrefix(dir);
            if (found != null) return found;

            // PATH 里常见的是 ...\npm 或 ...\npm\node_modules\.bin；父目录再试一次。
            string parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(parent)) return FindDshInPrefix(parent);
            return null;
        }

        private static string FirstWhere(string exe)
        {
            string output = RunCommand("where.exe", exe, 5000);
            foreach (string line in SplitLines(output))
            {
                string path = line.Trim();
                if (IsUsableFile(path)) return path;
            }
            return null;
        }

        private static string FirstExistingLine(string text)
        {
            foreach (string line in SplitLines(text))
            {
                string path = line.Trim();
                if (Directory.Exists(path)) return path;
            }
            return null;
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (string path in paths)
            {
                if (!IsUsableFile(path)) continue;
                return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            }
            return null;
        }

        private static bool IsUsableFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                return expanded.Length > 0 && File.Exists(expanded);
            }
            catch { return false; }
        }

        private static string Combine(string dir, string relative)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, relative);
        }

        private static string DisplayPath(string path)
        {
            return string.IsNullOrEmpty(path) ? "(未找到)" : path;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            return text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string RunCommand(string fileName, string arguments, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                    }
                    return stdout;
                }
            }
            catch { return ""; }
        }

        private static string DefaultWorkDir()
        {
            string guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"deepseek-harness\default-workspace");
            if (Directory.Exists(guess)) return guess;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>dsh 的 app 名 web 本身即选择 profile，不能再叠加 --profile；--no-open 避免每次拉起都弹浏览器。</summary>
        public static string CommandArgs()
        {
            return " web --port " + Port + " --no-open";
        }

        public static string ResolvedWorkDir()
        {
            if (!string.IsNullOrEmpty(WorkDir) && Directory.Exists(WorkDir)) return WorkDir;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    /// <summary>通过 npm 安装/修复 DSH，并在成功后把 Node/npm 全局目录加入用户 PATH。</summary>
    internal static class DshSetup
    {
        private const string PackageName = "@deepseek-ai/dsh";
        private const string AllowScripts = "--allow-scripts=@deepseek-ai/dsh-subprocess-local,koffi,node-pty,@google/genai,protobufjs";

        public static bool InstallDsh(out string logPath, out string error)
        {
            logPath = Path.Combine(Settings.LogDir, "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            error = null;

            string npm = Settings.FindNpmCommand();
            if (string.IsNullOrEmpty(npm))
            {
                error = "找不到 npm.cmd。请先安装 Node.js（含 npm），再重试。";
                Append(logPath, error);
                return false;
            }
            if (!File.Exists(Settings.NodePath))
            {
                error = "找不到 node.exe。请先安装 Node.js。";
                Append(logPath, error);
                return false;
            }

            Append(logPath, "# npm=" + npm);
            Append(logPath, "# node=" + Settings.NodePath);
            Append(logPath, "# 开始安装 " + PackageName + " ...");

            StringBuilder output = new StringBuilder();
            int code = RunNpm(npm, "install -g " + PackageName + " --no-audit --no-fund", logPath, output);
            string text = output.ToString();
            if (code != 0 || text.IndexOf("allow-scripts", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Append(logPath, "# 检测到 npm 安装脚本限制，使用 allow-scripts 重试 ...");
                output.Length = 0;
                code = RunNpm(npm, "install -g " + PackageName + " " + AllowScripts + " --no-audit --no-fund", logPath, output);
            }

            if (code != 0)
            {
                error = "npm install 失败（退出码 " + code + "），详见日志：" + logPath;
                Append(logPath, "# " + error);
                return false;
            }

            if (!Settings.ResolveRuntime(true))
            {
                error = "安装完成，但仍未找到 DSH。请查看日志：" + logPath;
                Append(logPath, "# " + error);
                return false;
            }

            Settings.AddRuntimeToUserPath();
            Append(logPath, "# 已识别: " + Settings.RuntimeSummary());
            Append(logPath, "# 已加入 PATH: " + Settings.GetNpmPrefix());
            return true;
        }

        private static int RunNpm(string npm, string args, string logPath, StringBuilder output)
        {
            string commandLine = "/d /s /c \"\"" + npm + "\" " + args + "\"";
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", commandLine);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Settings.ResolvedWorkDir();

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Append(logPath, e.Data, output); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Append(logPath, e.Data, output); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(600000))
                {
                    try { p.Kill(); } catch { }
                    Append(logPath, "# npm 超时（10 分钟）");
                    return -1;
                }
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        private static void Append(string path, string line)
        {
            Append(path, line, null);
        }

        private static void Append(string path, string line, StringBuilder output)
        {
            try
            {
                if (output != null)
                {
                    lock (output) { output.AppendLine(line); }
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal sealed class Launcher : IDisposable
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "DSHWebLauncher";

        private readonly Mutex _mutex;
        private readonly NotifyIcon _tray;
        private readonly MenuItem _miAutostart;
        private readonly MenuItem _miStatus;
        private Process _proc;
        private bool _stopping;
        private int _restarts;
        private volatile bool _installing;
        private readonly object _lock = new object();
        private string _logPath;

        public Launcher()
        {
            bool created;
            _mutex = new Mutex(true, "Global\\DSHWebLauncher.SingleInstance", out created);
            if (!created) { _mutex = null; return; }

            _miStatus = new MenuItem("状态：启动中…");
            _miStatus.Enabled = false;
            _miAutostart = new MenuItem("开机自启", ToggleAutostart);
            _miAutostart.Checked = IsAutostartEnabled();

            MenuItem open = new MenuItem("打开 Web UI", delegate { OpenWebUi(); });
            MenuItem restart = new MenuItem("重启服务", delegate { Restart(); });
            MenuItem stop = new MenuItem("停止服务", delegate { StopService(true); });
            MenuItem install = new MenuItem("安装 / 修复 DSH", delegate { InstallOrRepair(); });
            MenuItem logs = new MenuItem("打开日志目录", delegate { OpenLogs(); });
            MenuItem about = new MenuItem("关于", delegate { ShowAbout(); });
            MenuItem quit = new MenuItem("退出", delegate { Quit(); });
            MenuItem[] items = new MenuItem[] {
                _miStatus, new MenuItem("-"), open, restart, stop, install,
                new MenuItem("-"), _miAutostart, logs, new MenuItem("-"), about, quit
            };

            _tray = new NotifyIcon();
            _tray.Icon = BuildIcon();
            _tray.Text = "DSH Web Launcher";
            _tray.ContextMenu = new ContextMenu(items);
            _tray.DoubleClick += delegate { OpenWebUi(); };
            _tray.Visible = true;

            AppDomain.CurrentDomain.ProcessExit += delegate { Shutdown(); };
        }

        public void Start()
        {
            if (_mutex == null) return;
            if (StartService())
            {
                Balloon("已启动", "dsh web 正在后台运行（端口 " + Settings.Port + "）");
            }
        }

        private bool StartService()
        {
            lock (_lock)
            {
                if (_proc != null && !_proc.HasExited) return true;
                if (IsPortOpen(Settings.Port))
                {
                    UpdateStatus("已在运行（外部实例，端口 " + Settings.Port + "）");
                    return true;
                }

                _logPath = Path.Combine(Settings.LogDir, "web-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

                // 旧版配置里可能残留开发机的写死路径；每次启动前都重新校验并自动修复。
                if (!Settings.ResolveRuntime(false))
                {
                    string detail = "找不到 Node.js 或 DSH。\n" + Settings.RuntimeSummary()
                        + "\n\n右键托盘图标 → “安装 / 修复 DSH” 可自动安装。\n"
                        + "也可以手动安装后重启启动器，或在 settings.ini 中填写正确路径。";
                    AppendLog("# config error: " + detail.Replace("\n", " | "));
                    UpdateStatus("配置错误：找不到 Node/DSH");
                    Balloon("无法启动", detail);
                    return false;
                }

                string args = "\"" + Settings.DshBin + "\"" + Settings.CommandArgs();

                ProcessStartInfo psi = new ProcessStartInfo(Settings.NodePath, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = Settings.ResolvedWorkDir();
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                Process p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { AppendLog(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { AppendLog(e.Data); };
                p.Exited += delegate { OnExited(); };

                _proc = p;
                _stopping = false;
                try
                {
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    UpdateStatus("运行中 PID " + p.Id + " · 端口 " + Settings.Port);
                    AppendLog("# config: " + Settings.RuntimeSummary());
                    AppendLog("# started: " + Settings.NodePath + " " + args);
                    AppendLog("# workdir: " + psi.WorkingDirectory);
                    return true;
                }
                catch (Exception ex)
                {
                    _proc = null;
                    AppendLog("# start failed: " + ex.Message);
                    UpdateStatus("启动失败");
                    Balloon("启动失败", ex.Message);
                    return false;
                }
            }
        }

        private void OnExited()
        {
            lock (_lock)
            {
                if (_stopping) return;
                UpdateStatus("服务已退出");
                int code = -1;
                try { code = _proc == null ? -1 : _proc.ExitCode; } catch { }
                AppendLog("# process exited, code=" + code);
                if (Settings.AutoRestart && _restarts < 3)
                {
                    _restarts++;
                    AppendLog("# auto-restart attempt " + _restarts);
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Thread.Sleep(5000);
                        StartService();
                    });
                }
                else if (Settings.AutoRestart)
                {
                    Balloon("反复退出", "已连续重启 3 次，停止自动拉起。请查看日志。");
                }
            }
        }

        private void Restart()
        {
            StopService(false);
            _restarts = 0;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(1200);
                StartService();
            });
        }

        private void InstallOrRepair()
        {
            if (_installing)
            {
                Balloon("正在安装", "DSH 安装任务还在进行，请稍候。");
                return;
            }

            Settings.ResolveRuntime(false);
            string npm = Settings.FindNpmCommand();
            if (string.IsNullOrEmpty(npm))
            {
                DialogResult answer = MessageBox.Show(
                    "没有找到 Node.js / npm。\n\n请先安装 Node.js LTS，安装时保持默认的 npm 组件。\n是否现在打开 Node.js 下载页？",
                    "缺少 Node.js",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes)
                {
                    try { Process.Start("https://nodejs.org/zh-cn/download"); } catch { }
                }
                return;
            }

            DialogResult confirm = MessageBox.Show(
                "将通过 npm 安装或修复 DSH：\n\n"
                + "npm install -g " + "@deepseek-ai/dsh\n\n"
                + "安装完成后会自动识别 DSH，并把 Node 目录和 npm 全局目录加入当前用户 PATH。\n"
                + "需要联网，可能耗时几分钟。继续吗？",
                "安装 / 修复 DSH",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            _installing = true;
            UpdateStatus("正在安装 DSH…");
            Balloon("正在安装", "正在通过 npm 安装 DSH，请稍候…");
            StopService(false);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string logPath = null;
                string error = null;
                bool ok = false;
                try { ok = DshSetup.InstallDsh(out logPath, out error); }
                catch (Exception ex) { error = ex.Message; }

                _installing = false;
                if (ok)
                {
                    UpdateStatus("DSH 已安装");
                    Balloon("安装完成", "DSH 已安装并加入 PATH，正在启动服务…");
                    StartService();
                }
                else
                {
                    UpdateStatus("安装失败");
                    string detail = error ?? "未知错误";
                    if (!string.IsNullOrEmpty(logPath)) detail += "\n日志：" + logPath;
                    Balloon("安装失败", detail);
                }
            });
        }

        private void StopService(bool manual)
        {
            lock (_lock)
            {
                _stopping = true;
                Process current = _proc;
                _proc = null;
                if (current != null && !current.HasExited)
                {
                    try
                    {
                        ProcessStartInfo kpsi = new ProcessStartInfo("taskkill", "/PID " + current.Id + " /T /F");
                        kpsi.CreateNoWindow = true;
                        kpsi.UseShellExecute = false;
                        Process k = Process.Start(kpsi);
                        k.WaitForExit(6000);
                    }
                    catch
                    {
                        try { current.Kill(); } catch { }
                    }
                }
                UpdateStatus(manual ? "已停止" : "正在重启…");
            }
        }

        private void OpenWebUi()
        {
            try { Process.Start("http://127.0.0.1:" + Settings.Port + "/"); }
            catch (Exception ex) { Balloon("打开失败", ex.Message); }
        }

        private void OpenLogs()
        {
            try { Process.Start("explorer.exe", "\"" + Settings.LogDir + "\""); } catch { }
        }

        private static void ShowAbout()
        {
            try
            {
                using (AboutForm f = new AboutForm())
                {
                    f.ShowDialog();
                }
            }
            catch { }
        }

        public static bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(RunValue) != null;
                }
            }
            catch { return false; }
        }

        private void ToggleAutostart(object sender, EventArgs e)
        {
            _miAutostart.Checked = !_miAutostart.Checked;
            SetAutostart(_miAutostart.Checked);
        }

        public static void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch { }
        }

        private void Balloon(string title, string text)
        {
            try { _tray.BalloonTipTitle = title; _tray.BalloonTipText = text; _tray.ShowBalloonTip(3000); } catch { }
        }

        private void UpdateStatus(string text)
        {
            try
            {
                _miStatus.Text = "状态：" + text;
                _tray.Text = Truncate("DSH Web — " + text, 63);
            }
            catch { }
        }

        private void AppendLog(string line)
        {
            if (line == null) return;
            try
            {
                string entry = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine;
                File.AppendAllText(_logPath ?? Path.Combine(Settings.LogDir, "launcher.log"), entry, Encoding.UTF8);
            }
            catch { }
        }

        private void Quit()
        {
            _stopping = true;
            StopService(false);
            Shutdown();
            Application.Exit();
        }

        private void Shutdown()
        {
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { if (_mutex != null) _mutex.ReleaseMutex(); } catch { }
        }

        public void Dispose() { Shutdown(); }

        private static bool IsPortOpen(int port)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(400)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        private static string Truncate(string s, int max) { return s.Length <= max ? s : s.Substring(0, max); }

        /// <summary>托盘图标：优先用嵌入的 whale.ico（7 个尺寸），失败则回退到运行时绘制的方块图标。</summary>
        private static Icon BuildIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("whale.ico"))
                {
                    if (s != null)
                    {
                        Size target = SystemInformation.SmallIconSize;
                        return new Icon(s, target);
                    }
                }
            }
            catch { }
            return BuildFallbackIcon();
        }

        private static Icon BuildFallbackIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(22, 26, 33)))
                using (GraphicsPath gp = RoundRect(new Rectangle(1, 1, 30, 30), 7))
                {
                    g.FillPath(bg, gp);
                }
                using (SolidBrush fg = new SolidBrush(Color.FromArgb(94, 234, 212)))
                using (Font f = new Font("Consolas", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("D", f, fg, new RectangleF(0, 0, 32, 32), sf);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>关于窗口：版本、适用端（Web 版，非 Desktop 端）、作者与仓库。</summary>
    internal sealed class AboutForm : Form
    {
        private Bitmap _logo;

        /// <summary>从嵌入资源取鲸鱼图标；失败再退回 exe 的 Win32 图标。</summary>
        private static Bitmap LoadLogo()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("whale.ico"))
                {
                    if (s != null) return new Icon(s, new Size(64, 64)).ToBitmap();
                }
            }
            catch { }
            try
            {
                Icon assoc = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (assoc != null) return assoc.ToBitmap();
            }
            catch { }
            return null;
        }

        /// <summary>直接画在窗体上，避免 PictureBox 在 DrawToBitmap 下不出的问题。</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_logo != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(_logo, new Rectangle(20, 14, 64, 64));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _logo != null) { try { _logo.Dispose(); } catch { } }
            base.Dispose(disposing);
        }

        public AboutForm()
        {
            Text = "关于 DSH 启动器";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 440);
            BackColor = Color.FromArgb(24, 27, 33);
            ForeColor = Color.FromArgb(226, 232, 240);
            Font = new Font("Microsoft YaHei UI", 9f);

            int y = 16;

            _logo = LoadLogo();
            DoubleBuffered = true;

            Label title = MakeLabel("DSH 启动器", 16f, FontStyle.Bold, Color.FromArgb(94, 234, 212));
            title.Location = new Point(96, y - 4);
            title.AutoSize = true;
            Controls.Add(title);

            Label ver = MakeLabel("v" + Program.VersionText + "   ·   Windows 托盘启动工具", 9f, FontStyle.Regular, Color.FromArgb(148, 163, 184));
            ver.Location = new Point(98, y + 26);
            ver.AutoSize = true;
            Controls.Add(ver);

            y += 82;

            Controls.Add(SectionTitle("它做什么", ref y));
            Controls.Add(BodyText("把 dsh web 变成托盘常驻的后台服务：双击一次，服务静默运行；\n托盘图标右键即可打开界面、重启、停止、安装/修复 DSH、开机自启、看日志。\n启动器会自动识别 Node.js 与 DSH，并把 npm 全局目录加入用户 PATH。", ref y));

            y += 4;
            Controls.Add(SectionTitle("适用端", ref y));
            Controls.Add(OkText(
                "适用于 Web 版 —— 以 dsh web 启动，界面在浏览器中访问\n"
                + "http://127.0.0.1:" + Settings.Port + "（端口可在配置中修改）", ref y));
            Controls.Add(WarnText(
                "不适用于 DeepSeek Harness Desktop 桌面客户端。\n"
                + "桌面端为 Electron 应用，自带托盘、开机自启与窗口管理，\n"
                + "无需本工具；其 desktop profile 由客户端独占，CLI 也无法接管。", ref y));

            y += 4;
            Controls.Add(SectionTitle("作者与仓库", ref y));
            Controls.Add(BodyText("作者：千夏Tesni      邮箱：yachi8000p@gmail.com", ref y));

            LinkLabel repo = new LinkLabel();
            repo.Text = "github.com/yachiyoCN/DSH-Launcher";
            repo.LinkColor = Color.FromArgb(94, 234, 212);
            repo.ActiveLinkColor = Color.White;
            repo.LinkBehavior = LinkBehavior.HoverUnderline;
            repo.Location = new Point(24, y);
            repo.AutoSize = true;
            repo.LinkClicked += delegate
            {
                try { Process.Start("https://github.com/yachiyoCN/DSH-Launcher"); } catch { }
            };
            Controls.Add(repo);

            Button ok = new Button();
            ok.Text = "确定";
            ok.Size = new Size(88, 30);
            ok.Location = new Point(ClientSize.Width - 108, ClientSize.Height - 44);
            ok.BackColor = Color.FromArgb(38, 44, 54);
            ok.ForeColor = Color.Gainsboro;
            ok.FlatStyle = FlatStyle.Flat;
            ok.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = ok;
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Microsoft YaHei UI", size, style);
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            return l;
        }

        private static Label SectionTitle(string text, ref int y)
        {
            Label l = MakeLabel(text, 9.5f, FontStyle.Bold, Color.FromArgb(94, 234, 212));
            l.Location = new Point(20, y);
            l.AutoSize = true;
            y += 24;
            return l;
        }

        private static Label BodyText(string text, ref int y)
        {
            Label l = MakeLabel(text, 9f, FontStyle.Regular, Color.FromArgb(203, 213, 225));
            l.Location = new Point(24, y);
            l.AutoSize = true;
            y += l.PreferredHeight + 6;
            return l;
        }

        private static Label OkText(string text, ref int y)
        {
            Label l = MakeLabel(text, 9f, FontStyle.Regular, Color.FromArgb(134, 239, 172));
            l.Location = new Point(20, y);
            l.AutoSize = true;
            y += l.PreferredHeight + 6;
            return l;
        }

        private static Label WarnText(string text, ref int y)
        {
            Label l = MakeLabel(text, 9f, FontStyle.Regular, Color.FromArgb(250, 204, 21));
            l.Location = new Point(20, y);
            l.AutoSize = true;
            y += l.PreferredHeight + 6;
            return l;
        }
    }

    internal static class Program
    {
        /// <summary>程序版本，与 AssemblyVersion 同步。</summary>
        public const string VersionText = "1.0.4";

        [STAThread]
        private static int Main(string[] args)
        {
            // GUI 子系统程序没有控制台时 Console.OutputEncoding 是空操作，
            // 必须直接包装标准输出流才能保证重定向到文件时中文不乱码。
            try
            {
                StreamWriter utf8Out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                utf8Out.AutoFlush = true;
                Console.SetOut(utf8Out);
            }
            catch { }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Settings.Load();

            bool selftest = false;
            bool doEnableAuto = false;
            bool doDisableAuto = false;
            bool doAutoStatus = false;
            bool doStatus = false;
            bool doDetect = false;
            bool doInstallDsh = false;
            bool doAbout = false;
            string renderAbout = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--port") { int tmp; if (i + 1 < args.Length && int.TryParse(args[++i], out tmp)) Settings.Port = tmp; }
                else if (a == "--profile") { if (i + 1 < args.Length) Settings.Profile = args[++i]; }
                else if (a == "--workdir") { if (i + 1 < args.Length) Settings.WorkDir = args[++i]; }
                else if (a == "--selftest") selftest = true;
                else if (a == "--enable-autostart") doEnableAuto = true;
                else if (a == "--disable-autostart") doDisableAuto = true;
                else if (a == "--autostart-status") doAutoStatus = true;
                else if (a == "--status") doStatus = true;
                else if (a == "--detect") doDetect = true;
                else if (a == "--install-dsh") doInstallDsh = true;
                else if (a == "--about") doAbout = true;
                else if (a == "--render-about") { if (i + 1 < args.Length) renderAbout = args[++i]; }
            }

            if (Settings.Port <= 0) Settings.Port = 3080;

            if (doDetect)
            {
                bool ok = Settings.ResolveRuntime(true);
                Settings.Save();
                Console.WriteLine("node=" + Settings.NodePath);
                Console.WriteLine("bin=" + Settings.DshBin);
                Console.WriteLine("runtime=" + (ok ? "ok" : "missing"));
                return ok ? 0 : 1;
            }

            if (doInstallDsh)
            {
                string logPath;
                string error;
                bool ok = DshSetup.InstallDsh(out logPath, out error);
                Console.WriteLine("install=" + (ok ? "ok" : "failed"));
                if (!string.IsNullOrEmpty(logPath)) Console.WriteLine("log=" + logPath);
                if (!string.IsNullOrEmpty(error)) Console.WriteLine("error=" + error);
                return ok ? 0 : 1;
            }

            Settings.Save();

            if (doEnableAuto) { Launcher.SetAutostart(true); Console.WriteLine("autostart=" + (Launcher.IsAutostartEnabled() ? "enabled" : "failed")); return 0; }
            if (doDisableAuto) { Launcher.SetAutostart(false); Console.WriteLine("autostart=" + (Launcher.IsAutostartEnabled() ? "failed" : "disabled")); return 0; }
            if (doAutoStatus) { Console.WriteLine("autostart=" + (Launcher.IsAutostartEnabled() ? "enabled" : "disabled")); return 0; }
            if (doStatus)
            {
                Console.WriteLine("port=" + Settings.Port);
                Console.WriteLine("workdir=" + Settings.ResolvedWorkDir());
                Console.WriteLine("node=" + Settings.NodePath);
                Console.WriteLine("bin=" + Settings.DshBin);
                Console.WriteLine("runtime=" + (Settings.RuntimeReady ? "ok" : "missing"));
                return 0;
            }
            if (doAbout)
            {
                Console.WriteLine(AboutText());
                return 0;
            }
            if (!string.IsNullOrEmpty(renderAbout))
            {
                return RenderAbout(renderAbout);
            }
            if (selftest) return SelfTest();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (Launcher launcher = new Launcher())
            {
                launcher.Start();
                Application.Run();
                return 0;
            }
        }

        /// <summary>把「关于」窗口渲染成 PNG（用于文档截图与视觉回归）。</summary>
        private static int RenderAbout(string path)
        {
            try
            {
                using (AboutForm f = new AboutForm())
                {
                    // DrawToBitmap 只能可靠地抓到"已显示"的控件，所以先把窗口挪到屏幕外显示出来
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-4000, -4000);
                    f.Show();
                    Application.DoEvents();
                    Thread.Sleep(300);
                    Application.DoEvents();
                    using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    f.Hide();
                }
                Console.WriteLine("rendered=" + path);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR " + ex.Message);
                return 1;
            }
        }

        /// <summary>关于信息（控制台版，与托盘「关于」窗口同源）。</summary>
        public static string AboutText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DSH 启动器 v" + VersionText);
            sb.AppendLine("把 dsh web 变成 Windows 托盘常驻的后台服务");
            sb.AppendLine();
            sb.AppendLine("适用端");
            sb.AppendLine("  [是] Web 版：以 dsh web 启动，界面在浏览器中访问");
            sb.AppendLine("       http://127.0.0.1:" + Settings.Port + "（端口可改）");
            sb.AppendLine("  [否] DeepSeek Harness Desktop 桌面客户端：Electron 应用自带托盘");
            sb.AppendLine("       与开机自启，desktop profile 由客户端独占，CLI 无法接管");
            sb.AppendLine();
            sb.AppendLine("环境");
            sb.AppendLine("  自动识别 Node.js / DSH，并把 Node 目录和 npm 全局 bin 加入用户 PATH");
            sb.AppendLine("  托盘菜单「安装 / 修复 DSH」可调用 npm 安装或修复 DSH");
            sb.AppendLine();
            sb.AppendLine("作者  千夏Tesni");
            sb.AppendLine("邮箱  yachi8000p@gmail.com");
            sb.AppendLine("仓库  https://github.com/yachiyoCN/DSH-Launcher");
            sb.AppendLine();
            sb.Append("本工具不包含 DSH 本体，仅从外部调用 node 启动 dsh web。");
            return sb.ToString();
        }

        /// <summary>同步自检：拉起 dsh web，等端口就绪，报告结果后清理退出。</summary>
        private static int SelfTest()
        {
            Settings.ResolveRuntime(false);
            Console.WriteLine("config: node=" + Settings.NodePath);
            Console.WriteLine("        bin=" + Settings.DshBin);
            Console.WriteLine("        port=" + Settings.Port);
            Console.WriteLine("        workdir=" + Settings.ResolvedWorkDir());

            if (!Settings.RuntimeReady)
            {
                Console.WriteLine("runtime=missing");
                Console.WriteLine("ERROR 找不到 Node.js 或 DSH；请先安装，或在 settings.ini 中填写正确路径。");
                return 1;
            }
            Console.WriteLine("runtime=ok");

            string logPath = Path.Combine(Settings.LogDir, "selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            Process p = null;
            int exit = 1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    Settings.NodePath,
                    "\"" + Settings.DshBin + "\"" + Settings.CommandArgs());
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = Settings.ResolvedWorkDir();
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) AppendLine(logPath, e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) AppendLine(logPath, e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                Console.WriteLine("spawned pid=" + p.Id);

                bool open = false;
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    if (p.HasExited) { Console.WriteLine("process exited early, code=" + p.ExitCode); break; }
                    if (PortOpen(Settings.Port)) { open = true; break; }
                    Thread.Sleep(700);
                }
                Console.WriteLine("port_open=" + (open ? "1" : "0"));
                exit = open ? 0 : 1;
            }
            catch (Exception ex) { Console.WriteLine("ERROR " + ex.Message); }
            finally
            {
                if (p != null && !p.HasExited)
                {
                    try
                    {
                        ProcessStartInfo kpsi = new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F");
                        kpsi.CreateNoWindow = true;
                        kpsi.UseShellExecute = false;
                        Process k = Process.Start(kpsi);
                        k.WaitForExit(6000);
                    }
                    catch { }
                }
            }
            Console.WriteLine("log=" + logPath);
            try
            {
                string[] lines = File.ReadAllLines(logPath);
                int from = Math.Max(0, lines.Length - 12);
                for (int i = from; i < lines.Length; i++) Console.WriteLine("  | " + lines[i]);
            }
            catch { }
            return exit;
        }

        private static void AppendLine(string path, string line)
        {
            try { File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        private static bool PortOpen(int port)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(400)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
