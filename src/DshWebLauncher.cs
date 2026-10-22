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
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("DSH Web Launcher")]
[assembly: AssemblyDescription("Background tray launcher for dsh web")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace DshWebLauncher
{
    internal static class Settings
    {
        public static string NodePath = @"C:\Program Files\nodejs\node.exe";
        public static string DshBin = @"C:\Users\yachi\AppData\Roaming\npm\node_modules\@deepseek-ai\dsh\lib\bin.js";
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
            MenuItem logs = new MenuItem("打开日志目录", delegate { OpenLogs(); });
            MenuItem quit = new MenuItem("退出", delegate { Quit(); });
            MenuItem[] items = new MenuItem[] {
                _miStatus, new MenuItem("-"), open, restart, stop,
                new MenuItem("-"), _miAutostart, logs, new MenuItem("-"), quit
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
            StartService();
            Balloon("已启动", "dsh web 正在后台运行（端口 " + Settings.Port + "）");
        }

        private void StartService()
        {
            lock (_lock)
            {
                if (_proc != null && !_proc.HasExited) return;
                if (IsPortOpen(Settings.Port))
                {
                    UpdateStatus("已在运行（外部实例，端口 " + Settings.Port + "）");
                    return;
                }

                _logPath = Path.Combine(Settings.LogDir, "web-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
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
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                UpdateStatus("运行中 PID " + p.Id + " · 端口 " + Settings.Port);
                AppendLog("# started: " + Settings.NodePath + " " + args);
                AppendLog("# workdir: " + psi.WorkingDirectory);
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

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Settings.Load();

            bool selftest = false;
            bool doEnableAuto = false;
            bool doDisableAuto = false;
            bool doAutoStatus = false;
            bool doStatus = false;

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
            }

            if (Settings.Port <= 0) Settings.Port = 3080;
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
                return 0;
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

        /// <summary>同步自检：拉起 dsh web，等端口就绪，报告结果后清理退出。</summary>
        private static int SelfTest()
        {
            Console.WriteLine("config: node=" + Settings.NodePath);
            Console.WriteLine("        bin=" + Settings.DshBin);
            Console.WriteLine("        port=" + Settings.Port);
            Console.WriteLine("        workdir=" + Settings.ResolvedWorkDir());

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
