// DeepSeek Harness 管理器 (DeepSeek-Harness-Manager)
// 编译: csc /nologo /target:winexe /optimize+ /win32icon:DeepSeek-Harness.ico
//       /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
//       /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:Microsoft.Win32... (no)
//       Manager.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("DeepSeek Harness 管理器")]
[assembly: AssemblyDescription("DeepSeek Harness 本地服务管理器：启停/重启/日志/多实例/诊断")]
[assembly: AssemblyProduct("DeepSeek Harness Manager")]
[assembly: AssemblyCompany("DeepSeek Harness")]
[assembly: AssemblyCopyright("Copyright © 2026 DeepSeek Harness")]
[assembly: AssemblyVersion("1.2.2.0")]
[assembly: AssemblyFileVersion("1.2.2.0")]

namespace DshManager
{
    // ───────────────────────────────────────────────────────────── 程序入口
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { SetProcessDPIAware(); } catch { } }

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            { LogCrash(e.ExceptionObject as Exception); };
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            { LogCrash(e.Exception); };
            try
            {
                Run(args);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                throw;
            }
        }

        static void Run(string[] args)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { Ui.S = Math.Max(1f, g.DpiX / 96f); }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool minimized = args.Any(a => a == "--minimized");

            if (args.Any(a => a == "--cli"))
            {
                Cli.Run(args);
                return;
            }


            bool createdNew;
            Mutex mutex = new Mutex(true, "DeepSeekHarnessManager.SingleInstance", out createdNew);
            if (!createdNew) return; // 已有一个实例

            Settings.Load();
            CleanupOldLogs(); // 清理 30 天前的旧日志，避免 logs 目录无限增长
            Application.Run(new AppContext(minimized));
        }

        // 删除 logs 目录下超过 30 天的日志文件
        static void CleanupOldLogs()
        {
            try
            {
                string dir = Settings.LogsDir;
                if (!Directory.Exists(dir)) return;
                DateTime cutoff = DateTime.Now.AddDays(-30);
                foreach (string f in Directory.GetFiles(dir))
                {
                    try
                    {
                        if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }


        static void LogCrash(Exception ex)
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "logs");
                Directory.CreateDirectory(dir);
                string f = Path.Combine(dir, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                File.WriteAllText(f, ex == null ? "unknown exception" : ex.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }

    // ───────────────────────────────────────────────────────────── 统一度量
    static class Ui
    {
        public static float S = 1f;
        public static int P(float v) { return (int)Math.Round(v * S); }
    }

    // ───────────────────────────────────────────────────────────── 轻量动画器（UI 线程，16ms/帧）
    static class Anim
    {
        class Run { public float t; public float dur; public Action<float> tick; public Action done; }
        static Timer timer;
        static List<Run> runs = new List<Run>();

        public static void Start(float durMs, Action<float> tick, Action done)
        {
            if (timer == null)
            {
                timer = new Timer();
                timer.Interval = 16;
                timer.Tick += delegate { Tick(); };
            }
            runs.Add(new Run { t = 0f, dur = durMs, tick = tick, done = done });
            if (!timer.Enabled) timer.Start();
        }

        static void Tick()
        {
            for (int i = runs.Count - 1; i >= 0; i--)
            {
                Run r = runs[i];
                r.t += 16f;
                float p = Math.Min(1f, r.t / r.dur);
                try { if (r.tick != null) r.tick(p); } catch { }
                if (p >= 1f)
                {
                    try { if (r.done != null) r.done(); } catch { }
                    runs.RemoveAt(i);
                }
            }
            if (runs.Count == 0 && timer != null) timer.Stop();
        }
    }

    // ───────────────────────────────────────────────────────────── 颜色插值
    static class ColorX
    {
        public static Color Lerp(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    // ───────────────────────────────────────────────────────────── 主题
    class Theme
    {
        public string Key;
        public Color Page, Surface, SurfaceAlt, Sidebar, Border, BorderStrong, Text, TextMuted, TextFaint;
        public Color Accent, AccentHover, AccentSoft, Ok, Warn, Err;
        public Color LogBack, LogText, LogErr, LogWarn;
        public bool IsDark;

        public static Theme Current = Light;

        public static readonly Theme Light = new Theme
        {
            Key = "light", IsDark = false,
            Page = Color.FromArgb(246, 247, 251),
            Surface = Color.FromArgb(214, 255, 255, 255),
            SurfaceAlt = Color.FromArgb(243, 244, 248),
            Sidebar = Color.FromArgb(239, 241, 246),
            Border = Color.FromArgb(24, 15, 23, 42),
            BorderStrong = Color.FromArgb(48, 15, 23, 42),
            Text = Color.FromArgb(24, 28, 42),
            TextMuted = Color.FromArgb(132, 140, 156),
            TextFaint = Color.FromArgb(168, 174, 188),
            Accent = Color.FromArgb(77, 107, 254),
            AccentHover = Color.FromArgb(59, 90, 246),
            AccentSoft = Color.FromArgb(30, 77, 107, 254),
            Ok = Color.FromArgb(34, 197, 94),
            Warn = Color.FromArgb(245, 158, 11),
            Err = Color.FromArgb(239, 68, 68),
            LogBack = Color.FromArgb(250, 250, 252),
            LogText = Color.FromArgb(66, 72, 88),
            LogErr = Color.FromArgb(214, 62, 62),
            LogWarn = Color.FromArgb(196, 128, 20),
        };

        public static readonly Theme Dark = new Theme
        {
            Key = "dark", IsDark = true,
            Page = Color.FromArgb(20, 22, 28),
            Surface = Color.FromArgb(218, 30, 33, 42),
            SurfaceAlt = Color.FromArgb(34, 37, 46),
            Sidebar = Color.FromArgb(24, 27, 34),
            Border = Color.FromArgb(42, 255, 255, 255),
            BorderStrong = Color.FromArgb(64, 255, 255, 255),
            Text = Color.FromArgb(237, 239, 244),
            TextMuted = Color.FromArgb(154, 161, 176),
            TextFaint = Color.FromArgb(110, 117, 132),
            Accent = Color.FromArgb(107, 133, 255),
            AccentHover = Color.FromArgb(126, 150, 255),
            AccentSoft = Color.FromArgb(48, 107, 133, 255),
            Ok = Color.FromArgb(61, 214, 140),
            Warn = Color.FromArgb(245, 184, 76),
            Err = Color.FromArgb(255, 107, 107),
            LogBack = Color.FromArgb(17, 18, 23),
            LogText = Color.FromArgb(196, 202, 216),
            LogErr = Color.FromArgb(255, 129, 129),
            LogWarn = Color.FromArgb(240, 197, 116),
        };

        public static void Apply(string key)
        {
            if (key == "dark") Current = Dark;
            else if (key == "system") Current = SystemDark() ? Dark : Light;
            else Current = Light;
        }

        public static bool SystemDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v != null && (int)v == 0;
                }
            }
            catch { return false; }
        }
    }

    // ───────────────────────────────────────────────────────────── 玻璃/圆角/DPI 互操作
    static class Native
    {

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // 设置标准窗口标题栏的深色模式（DWMWA_USE_IMMERSIVE_DARK_MODE）
        public static void DwmSetAttribute(IntPtr hwnd, int attr, int value)
        {
            try
            {
                DwmSetWindowAttribute(hwnd, attr, ref value, 4);
            }
            catch { }
        }

        // 强制刷新窗口框架（非客户区/标题栏），让 DWMWA 属性立即生效
        public static void RefreshFrame(IntPtr hwnd)
        {
            try
            {
                const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch { }
        }

        // 强制整窗（含标题栏与所有子控件）立即重绘
        public static void RedrawAll(IntPtr hwnd)
        {
            try
            {
                const uint RDW_INVALIDATE = 0x1, RDW_UPDATENOW = 0x100, RDW_ALLCHILDREN = 0x80, RDW_FRAME = 0x400;
                RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_FRAME);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);


        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        public static extern int SetPreferredAppMode(int mode); // 0=Default 1=AllowDark 2=ForceDark 3=ForceLight

        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        public static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);


        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int TableClass, int Reserved);

        [StructLayout(LayoutKind.Sequential)]
        struct MibTcpRowOwnerPid { public uint state, localAddr, localPort, remoteAddr, remotePort, owningPid; }

        // 返回监听指定端口的进程 PID；无则 0
        public static int GetPidByPort(int port)
        {
            const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5;
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (size <= 0) return 0;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return 0;
                int num = Marshal.ReadInt32(buf);
                int stride = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
                for (int i = 0; i < num; i++)
                {
                    IntPtr row = new IntPtr(buf.ToInt64() + 4 + (long)i * stride);
                    MibTcpRowOwnerPid r = (MibTcpRowOwnerPid)Marshal.PtrToStructure(row, typeof(MibTcpRowOwnerPid));
                    ushort p = (ushort)((r.localPort >> 8) | ((r.localPort & 0xFF) << 8));
                    if (p == (ushort)port && r.state == 2) return (int)r.owningPid; // 2 = LISTEN
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return 0;
        }
    }

    // ───────────────────────────────────────────────────────────── 配置模型
    class InstanceConfig
    {
        public string Name = "";
        public string Host = "127.0.0.1";
        public int Port = 3080;
        public bool AutoOpenBrowser = true;
        public bool Watchdog = false;
        public bool ManualStopped = false;

        public string Url { get { return "http://" + Host + ":" + Port; } }

        public string Slug
        {
            get
            {
                string s = Regex.Replace(Name, "[^a-zA-Z0-9]", "-");
                return s.ToLowerInvariant() + "-" + Port;
            }
        }
    }

    class SettingsData
    {
        public string Theme = "light";
        public bool Glass = false; // 默认关闭：部分 Win10 上亚克力渲染为黑色，实色底色更稳定
        public bool Autostart = false;
        public bool CloseExits = false;
        public string NodePath = ""; // 用户手动指定的 node.exe（可选，适配非标准安装）
        public string DshPath = "";  // 用户手动指定的 dsh 入口脚本（可选，适配源码/自建安装）
        public string AutoNodePath = ""; // 从运行实例自动发现并持久化的 node.exe（停止后重启仍可用）
        public string AutoDshPath = "";  // 从运行实例自动发现并持久化的 dsh 入口
        public List<InstanceConfig> Instances = new List<InstanceConfig>();
    }

    static class Settings
    {
        public static SettingsData Data = new SettingsData();
        static readonly string FilePath = Path.Combine(AppDir, "config.json");
        public static string AppDir
        {
            get
            {
                string d = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return string.IsNullOrEmpty(d) ? "." : d;
            }
        }
        public static string LogsDir { get { return Path.Combine(AppDir, "logs"); } }

        static readonly JavaScriptSerializer Js = new JavaScriptSerializer();

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    Data = Js.Deserialize<SettingsData>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (Data.Instances == null) Data.Instances = new List<InstanceConfig>();
                    if (Data.Instances.Count == 0)
                        Data.Instances.Add(DefaultInstance());
                    foreach (InstanceConfig c in Data.Instances)
                    {
                        if (c.Name == null) c.Name = "";
                        if (c.Host == null || c.Host.Length == 0) c.Host = "127.0.0.1";
                        if (c.Port <= 0 || c.Port > 65535) c.Port = 3080;
                    }
                }
                else
                {
                    Data = new SettingsData();
                    Data.Instances.Add(DefaultInstance());
                }
            }
            catch { Data = new SettingsData(); Data.Instances.Add(DefaultInstance()); }
            Theme.Apply(Data.Theme);
        }

        static InstanceConfig DefaultInstance()
        {
            return new InstanceConfig { Name = "本地实例", Host = "127.0.0.1", Port = 3080, AutoOpenBrowser = true, Watchdog = true };
        }

        public static void Save()
        {
            try
            {
                lock (typeof(Settings))
                {
                    File.WriteAllText(FilePath, Js.Serialize(Data), Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ───────────────────────────────────────────────────────────── 服务控制
    enum SvcState { Unknown, Stopped, Starting, Running, Occupied, Error }

    class InstanceRuntime
    {
        public InstanceConfig Cfg;
        public SvcState State = SvcState.Unknown;
        public int Pid;
        public DateTime StartedAt;
        public DateTime LastOkStart; // 最近一次成功启动的时间（看门狗据此判断"崩溃恢复"）
        public int FailCount;        // 连续启动失败次数（看门狗据此停止骚扰）
        public string DiscNode = ""; // 运行中自动发现的 node.exe（本次会话缓存）
        public string DiscDsh = "";  // 运行中自动发现的 dsh 入口
        public bool DiscTried;       // 是否已尝试过发现（避免每轮轮询重复 WMI 查询）
        public long MemMb;
        public bool Busy;
        public string LastError = "";
        public Process Proc;
        public StreamWriter SwOut, SwErr;
    }

    static class DshService
    {
        public static string NodeExe = "";
        public static string BinJs = "";
        public static string NodeVersion = "";
        public static string DshVersion = "";
        public static string ResolveError = "";

        public static string FindNodeExe()
        {
            // 1. 用户手动指定的路径
            try
            {
                if (Settings.Data.NodePath.Length > 0 && File.Exists(Settings.Data.NodePath)) return Settings.Data.NodePath;
            }
            catch { }
            // 2. 随包分发的便携 Node（runtime\node\node.exe），对方无需安装 Node.js
            try
            {
                string bundled = Path.Combine(Settings.AppDir, "runtime", "node", "node.exe");
                if (File.Exists(bundled)) return bundled;
            }
            catch { }
            // 3. 系统 PATH
            try
            {
                var c = Environment.GetEnvironmentVariable("PATH");
                if (c != null)
                {
                    foreach (string dir in c.Split(';'))
                    {
                        if (dir.Length == 0) continue;
                        string p = Path.Combine(dir.Trim('"'), "node.exe");
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return "";
        }

        public static string FindBinJs()
        {
            // 1. 用户手动指定的路径（适配源码仓库/自建安装等非标准方式）
            try
            {
                if (Settings.Data.DshPath.Length > 0 && File.Exists(Settings.Data.DshPath)) return Settings.Data.DshPath;
            }
            catch { }
            // 2. PATH 上的 dsh 命令 → npm 安装布局
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in path.Split(';'))
                {
                    if (dir.Length == 0) continue;
                    string d = dir.Trim('"');
                    if (!File.Exists(Path.Combine(d, "dsh.cmd")) && !File.Exists(Path.Combine(d, "dsh"))) continue;
                    string bin = Path.GetFullPath(Path.Combine(d, "..", "@deepseek-ai", "dsh", "lib", "bin.js"));
                    if (File.Exists(bin)) return bin;
                }
            }
            catch { }
            // 3. npm 全局安装目录（npm root -g 探测）——常驻安装，清理 npm 缓存不受影响，无需用户配 PATH
            try
            {
                string node = FindNodeExe();
                if (node.Length > 0)
                {
                    string npm = Path.Combine(Path.GetDirectoryName(node), "npm.cmd");
                    if (File.Exists(npm))
                    {
                        string globalRoot = RunCapture(npm, "root -g").Trim();
                        if (globalRoot.Length > 0)
                        {
                            string bin = Path.Combine(globalRoot, "@deepseek-ai", "dsh", "lib", "bin.js");
                            if (File.Exists(bin)) return bin;
                        }
                    }
                }
            }
            catch { }
            // 4. npx 缓存（旧版安装布局，向后兼容）
            try
            {
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache", "_npx");
                if (Directory.Exists(local))
                {
                    foreach (string dir in Directory.GetDirectories(local).OrderByDescending(x => Directory.GetLastWriteTime(x)))
                    {
                        string bin = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                        if (File.Exists(bin)) return bin;
                    }
                }
            }
            catch { }
            return "";
        }

        // 读取指定进程的命令行（WMI）
        public static string GetProcessCommandLine(int pid)
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                {
                    foreach (var o in searcher.Get())
                    {
                        object v = o["CommandLine"];
                        if (v != null) return v.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        // 从命令行解析出 node.exe 与 dsh 入口脚本。
        // 典型形态：node.exe "<...>\@deepseek-ai\dsh\lib\bin.js" web --host ... --port ...
        //           或：node.exe "<仓库>\packages\dsh\dist\bin.js" web ...
        public static void ParseLaunchCommand(string cmd, out string node, out string dsh)
        {
            node = "";
            dsh = "";
            if (string.IsNullOrEmpty(cmd)) return;
            List<string> tokens = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inQ = false;
            for (int i = 0; i < cmd.Length; i++)
            {
                char ch = cmd[i];
                if (ch == '"') { inQ = !inQ; if (!inQ && cur.Length > 0) { tokens.Add(cur.ToString()); cur.Length = 0; } }
                else if (ch == ' ' && !inQ) { if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Length = 0; } }
                else cur.Append(ch);
            }
            if (cur.Length > 0) tokens.Add(cur.ToString());

            // 找 node.exe
            foreach (string t in tokens)
            {
                string tt = t.Trim();
                if (tt.Length > 0 && tt.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(tt))
                {
                    node = tt;
                    break;
                }
            }
            // 找 dsh 入口：node 之后第一个存在的 .js/.mjs/.cjs 文件；否则任何含 bin.js 或名为 dsh 的脚本
            int startIdx = 0;
            for (int i = 0; i < tokens.Count; i++) { if (tokens[i] == node) { startIdx = i + 1; break; } }
            for (int i = startIdx; i < tokens.Count; i++)
            {
                string t = tokens[i].Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("-")) continue; // 跳过参数
                if (t.EndsWith(".js") || t.EndsWith(".mjs") || t.EndsWith(".cjs"))
                {
                    if (File.Exists(t)) { dsh = t; break; }
                }
            }
            if (dsh.Length == 0)
            {
                foreach (string t in tokens)
                {
                    string tt = t.Trim();
                    if (tt.Length > 0 && (tt.EndsWith(".js") || tt.EndsWith(".mjs") || tt.EndsWith(".cjs")) &&
                        File.Exists(tt) && (tt.IndexOf("bin.js", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            tt.IndexOf("dsh", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        dsh = tt;
                        break;
                    }
                }
            }
        }

        // 从"正在运行的实例"反向发现 node 与 dsh 路径（适配任意安装方式：源码仓库/绿色包等）
        // 发现结果同时持久化到配置，供停止后重启 / 管理器重启后使用。
        public static void DiscoverFromRunning(InstanceRuntime rt)
        {
            try
            {
                int pid = rt.Pid != 0 ? rt.Pid : Native.GetPidByPort(rt.Cfg.Port);
                if (pid == 0) return;
                string cmd = GetProcessCommandLine(pid);
                string node, dsh;
                ParseLaunchCommand(cmd, out node, out dsh);
                rt.DiscTried = true;
                if (rt.DiscNode.Length == 0 && node.Length > 0) rt.DiscNode = node;
                if (rt.DiscDsh.Length == 0 && dsh.Length > 0) rt.DiscDsh = dsh;
                if (NodeExe.Length == 0 && node.Length > 0) NodeExe = node;
                if (BinJs.Length == 0 && dsh.Length > 0) BinJs = dsh;
                // 持久化（供停止后重启/下次启动使用）
                bool changed = false;
                if (node.Length > 0 && Settings.Data.AutoNodePath != node) { Settings.Data.AutoNodePath = node; changed = true; }
                if (dsh.Length > 0 && Settings.Data.AutoDshPath != dsh) { Settings.Data.AutoDshPath = dsh; changed = true; }
                if (changed) Settings.Save();
                if (NodeExe.Length > 0 && BinJs.Length > 0)
                {
                    try { NodeVersion = RunCapture(NodeExe, "--version").Trim(); } catch { }
                    try { DshVersion = RunCapture(NodeExe, "\"" + BinJs + "\" --version").Trim(); } catch { }
                }
            }
            catch { }
        }

        public static bool Resolve() { return Resolve(null); }

        public static bool Resolve(InstanceRuntime rt)
        {
            // 优先级（node 与 dsh 尽量成对匹配，避免"发现到仓库 dsh 却用自带 node"的错配）：
            // 1) 用户手动指定  2) 运行实例发现（已知能工作的组合）  3) 持久化的自动发现  4) 标准查找(自带node/PATH/npx缓存)  5) 实时发现
            string node = "";
            string dsh = "";
            // 1) 用户手动指定
            if (Settings.Data.NodePath.Length > 0 && File.Exists(Settings.Data.NodePath)) node = Settings.Data.NodePath;
            if (Settings.Data.DshPath.Length > 0 && File.Exists(Settings.Data.DshPath)) dsh = Settings.Data.DshPath;
            // 2) 本次会话运行实例发现（成对）
            if (rt != null)
            {
                if (node.Length == 0 && rt.DiscNode.Length > 0 && File.Exists(rt.DiscNode)) node = rt.DiscNode;
                if (dsh.Length == 0 && rt.DiscDsh.Length > 0 && File.Exists(rt.DiscDsh)) dsh = rt.DiscDsh;
            }
            // 3) 之前持久化的自动发现（管理器重启后仍可用）
            if (node.Length == 0 && Settings.Data.AutoNodePath.Length > 0 && File.Exists(Settings.Data.AutoNodePath)) node = Settings.Data.AutoNodePath;
            if (dsh.Length == 0 && Settings.Data.AutoDshPath.Length > 0 && File.Exists(Settings.Data.AutoDshPath)) dsh = Settings.Data.AutoDshPath;
            // 4) 标准查找：自带便携 Node / PATH / npm 缓存
            if (node.Length == 0) node = FindNodeExe();
            if (dsh.Length == 0) dsh = FindBinJs();
            // 5) 实时反向发现（实例正在运行时）
            if ((node.Length == 0 || dsh.Length == 0) && rt != null)
            {
                DiscoverFromRunning(rt);
                if (node.Length == 0 && rt.DiscNode.Length > 0 && File.Exists(rt.DiscNode)) node = rt.DiscNode;
                if (dsh.Length == 0 && rt.DiscDsh.Length > 0 && File.Exists(rt.DiscDsh)) dsh = rt.DiscDsh;
            }
            NodeExe = node;
            BinJs = dsh;
            if (NodeExe.Length == 0 && BinJs.Length == 0)
            {
                ResolveError = "未找到 node.exe 和 dsh。请确认程序自带 runtime\\node 目录完整（便携版已内置 Node.js）；" +
                               "或在「诊断」页点击「一键安装 dsh」（需联网一次）。";
                return false;
            }
            if (NodeExe.Length == 0)
            {
                ResolveError = "未找到 node.exe。请确认程序自带 runtime\\node 目录完整（便携版已内置 Node.js），" +
                               "或安装 Node.js 并加入系统 PATH。";
                return false;
            }
            if (BinJs.Length == 0)
            {
                ResolveError = "已找到 node（" + NodeExe + "），但未找到 dsh（@deepseek-ai/dsh）。" +
                               "请在「诊断」页点击「一键安装 dsh」（需联网一次）。";
                return false;
            }
            try
            {
                NodeVersion = RunCapture(NodeExe, "--version").Trim();
                DshVersion = RunCapture(NodeExe, "\"" + BinJs + "\" --version").Trim();
            }
            catch { }
            ResolveError = "";
            return true;
        }

        public static string RunCapture(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = file;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8; // npx/npm 输出 UTF-8，避免中文乱码
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    return (o + e).Trim();
                }
            }
            catch { return ""; }
        }

        // 流式运行：stdout/stderr 逐行回调（后台线程触发），进程结束后回调 onExit(timedOut)。
        // 用于安装等长耗时命令：让用户实时看到输出，而不是长时间静默。
        // timeoutMs > 0 时启用整体超时：超时后强制 Kill 进程并回调 onExit(true)，避免永久卡住。
        public static void RunStream(string file, string args, Action<string> onStdout, Action<string> onStderr, Action<bool> onExit, int timeoutMs = 0)
        {
            bool exited = false; // 防重入：正常退出 / 超时 / 启动失败 三路径只回调一次 onExit
            System.Threading.Timer killTimer = null;
            Process p = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = file;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8; // npx/npm 输出 UTF-8，避免中文乱码
                psi.StandardErrorEncoding = Encoding.UTF8;
                p = Process.Start(psi);
                if (timeoutMs > 0)
                {
                    killTimer = new System.Threading.Timer(delegate
                    {
                        if (exited) return;
                        exited = true;
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(2000); } catch { }
                        try { p.Dispose(); } catch { }
                        try { if (killTimer != null) killTimer.Dispose(); } catch { }
                        if (onExit != null) onExit(true);
                    }, null, timeoutMs, System.Threading.Timeout.Infinite);
                }
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null && onStdout != null) onStdout(e.Data);
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null && onStderr != null) onStderr(e.Data);
                };
                p.EnableRaisingEvents = true;
                p.Exited += delegate
                {
                    if (exited) return;
                    exited = true;
                    // 稍等片刻让最后的输出行排空，再收尾
                    try { p.WaitForExit(800); } catch { }
                    try { if (killTimer != null) killTimer.Dispose(); } catch { }
                    try { p.Dispose(); } catch { } // 显式释放进程句柄
                    if (onExit != null) onExit(false);
                };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch
            {
                if (!exited)
                {
                    exited = true;
                    try { if (killTimer != null) killTimer.Dispose(); } catch { }
                    if (onExit != null) onExit(false);
                }
            }
        }

        // 探测 URL 是否为 DSH 实例（页面含 __DSH_BOOT__ 标记）
        public static bool Probe(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 2500;
                req.ReadWriteTimeout = 2500;
                req.UserAgent = "DSH-Manager/1.0";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode >= 400) return false;
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string body = sr.ReadToEnd();
                        return body.IndexOf("__DSH_BOOT__", StringComparison.Ordinal) >= 0 ||
                               body.IndexOf("@deepseek-ai", StringComparison.Ordinal) >= 0;
                    }
                }
            }
            catch { return false; }
        }

        // DSH 0.1.0-rc 系列出于安全限制拒绝 --host 0.0.0.0（防远程代码执行），不支持局域网绑定：
        //   dsh-web-app 的 startup.js 硬性报错"intentionally not supported yet for safety"，
        //   且 webserver 配置 schema 只允许 "127.0.0.1" | "0.0.0.0"。
        // 版本未知时保守按"不支持"。DSH 未来版本若放开 0.0.0.0，此判断需相应更新。
        public static bool SupportsLan()
        {
            string v = DshVersion;
            if (v.Length == 0) return false;
            return !v.StartsWith("0.1.0-rc", StringComparison.OrdinalIgnoreCase);
        }

        public static SvcState Detect(InstanceRuntime rt)
        {
            try
            {
                if (rt.Proc != null && rt.Proc.HasExited)
                {
                    rt.Proc = null;
                    if (rt.SwOut != null) { try { rt.SwOut.Dispose(); } catch { } rt.SwOut = null; }
                    if (rt.SwErr != null) { try { rt.SwErr.Dispose(); } catch { } rt.SwErr = null; }
                }
                if (Probe(rt.Cfg.Url))
                {
                    if (rt.Pid == 0) rt.Pid = Native.GetPidByPort(rt.Cfg.Port);
                    return SvcState.Running;
                }
                if (rt.Proc != null) return SvcState.Starting;
                int pid = Native.GetPidByPort(rt.Cfg.Port);
                if (pid != 0)
                {
                    rt.Pid = pid;
                    return SvcState.Occupied;
                }
                return SvcState.Stopped;
            }
            catch { return SvcState.Error; }
        }

        public static bool Start(InstanceRuntime rt, Action<string> log)
        {
            rt.LastError = "";
            try
            {
                if (!Resolve(rt))
                {
                    rt.LastError = ResolveError;
                    return false;
                }
                if (!Directory.Exists(Settings.LogsDir)) Directory.CreateDirectory(Settings.LogsDir);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string outFile = Path.Combine(Settings.LogsDir, rt.Cfg.Slug + "-" + stamp + ".out.log");
                string errFile = Path.Combine(Settings.LogsDir, rt.Cfg.Slug + "-" + stamp + ".err.log");

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = NodeExe;
                psi.Arguments = "\"" + BinJs + "\" web --host " + rt.Cfg.Host + " --port " + rt.Cfg.Port;
                psi.WorkingDirectory = Settings.AppDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                Process p = Process.Start(psi);
                rt.Proc = p;
                rt.Pid = p.Id;
                rt.StartedAt = DateTime.Now;

                FileStream fo = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                rt.SwOut = new StreamWriter(fo, new UTF8Encoding(false));
                rt.SwOut.AutoFlush = true;
                FileStream fe = new FileStream(errFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                rt.SwErr = new StreamWriter(fe, new UTF8Encoding(false));
                rt.SwErr.AutoFlush = true;

                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null && rt.SwOut != null) { lock (rt.SwOut) rt.SwOut.WriteLine(e.Data); }
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null && rt.SwErr != null) { lock (rt.SwErr) rt.SwErr.WriteLine(e.Data); }
                };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (log != null) log("已启动进程 PID " + p.Id + "：" + rt.Cfg.Url);
                return true;
            }
            catch (Exception ex)
            {
                rt.LastError = ex.Message;
                return false;
            }
        }

        public static void Stop(InstanceRuntime rt, bool manual)
        {
            try
            {
                int pid = 0;
                if (rt.Proc != null && !rt.Proc.HasExited) pid = rt.Proc.Id;
                else pid = Native.GetPidByPort(rt.Cfg.Port);
                if (pid != 0)
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo();
                        psi.FileName = "taskkill.exe";
                        psi.Arguments = "/PID " + pid + " /T /F";
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        using (Process tp = Process.Start(psi)) { tp.WaitForExit(5000); }
                    }
                    catch { }
                }
                if (rt.Proc != null)
                {
                    try { rt.Proc.Kill(); } catch { }
                    try { rt.Proc.WaitForExit(2000); } catch { }
                    rt.Proc = null;
                }
                if (rt.SwOut != null) { try { rt.SwOut.Dispose(); } catch { } rt.SwOut = null; }
                if (rt.SwErr != null) { try { rt.SwErr.Dispose(); } catch { } rt.SwErr = null; }
                rt.Pid = 0;
                if (manual) rt.Cfg.ManualStopped = true;
            }
            catch { }
        }
    }

    // ───────────────────────────────────────────────────────────── 更新检测
    // 检测 dsh（npm registry）与管理器自身（GitHub Releases）是否有新版本；
    // 版本比较用数字段 semver（支持 0.1.0-rc.7 / 1.1.1.0，rc.9 vs rc.10 不会比错）。
    static class UpdateService
    {
        public static string DshLatest = "";        // npm latest 标签版本（默认升级轨，稳定/推荐版，""=未查到）
        public static string DshNext = "";          // npm next 标签版本（可选预览轨，含破坏性变更，仅当高于 latest 时非空）
        public static string ManagerLatest = "";    // GitHub 最新管理器版本，无 v 前缀
        public static string ManagerLatestUrl = ""; // 便携包 zip 下载地址
        public static string ManagerChecksumsUrl = ""; // checksums.txt 资产地址（可能为空）
        public static bool Checked;                 // 本次会话是否已自动检查过

        // ── HTTP 工具 ──
        public static string HttpGet(string url)
        {
            try
            {
                // .NET Framework 4.8 默认 TLS 协商可能过低，npm registry / GitHub API 均要求
                // TLS 1.2+，不显式启用会报"未能创建 SSL/TLS 安全通道"，导致检查失败。
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.UserAgent = "DSH-Manager/" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        // ── 版本比较：a < b 返回 -1，相等 0，a > b 返回 1 ──
        // 规则：先比主版本数字段；主版本相同则 正式版 > 预发布(rc)；同为预发布比 rc 序号。
        public static int CompareVersions(string a, string b)
        {
            if (a == b) return 0;
            string coreA = a, coreB = b, preA = "", preB = "";
            int da = a.IndexOf('-'), db = b.IndexOf('-');
            if (da >= 0) { coreA = a.Substring(0, da); preA = a.Substring(da + 1); }
            if (db >= 0) { coreB = b.Substring(0, db); preB = b.Substring(db + 1); }
            int c = CompareCore(coreA, coreB);
            if (c != 0) return c;
            bool hasA = preA.Length > 0, hasB = preB.Length > 0;
            if (hasA != hasB) return hasA ? -1 : 1; // 正式版(无预发布)更大
            if (!hasA) return 0;
            return LastNum(preA).CompareTo(LastNum(preB));
        }

        static int CompareCore(string a, string b)
        {
            string[] sa = a.Split('.'), sb = b.Split('.');
            int n = Math.Max(sa.Length, sb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = 0, y = 0;
                if (i < sa.Length) int.TryParse(sa[i], out x);
                if (i < sb.Length) int.TryParse(sb[i], out y);
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        static int LastNum(string pre)
        {
            string[] parts = pre.Split('.');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                int n; if (int.TryParse(parts[i], out n)) return n;
            }
            return 0;
        }

        // ── 查 dsh 版本（npm registry，公开接口）──
        // 双轨检测：@deepseek-ai/dsh 只发布 0.1.0-rc.x 预发布版本，官方把推荐版挂在 latest 标签、
        // 预览版挂在 next 标签（实测 rc.8=next、rc.7=latest）。默认升级只跟随 latest（稳定轨），
        // next（预览轨）作为"可选更新"，由用户自行决定是否冒险升级。
        // 因此读取完整 packument：DshLatest=latest 标签版本（缺失时取全部版本最高者），
        // DshNext=next 标签版本（仅当确实高于 latest 时才视为可选预览版）。
        // 返回 DshLatest，供既有调用兼容。
        public static string CheckDsh()
        {
            string body = HttpGet("https://registry.npmjs.org/@deepseek-ai/dsh");
            if (body.Length == 0) { DshLatest = ""; DshNext = ""; return ""; }
            try
            {
                // JavaScriptSerializer 非线程安全，检测可能在后台线程并发调用，每次新建
                Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>;
                if (d == null) { DshLatest = ""; DshNext = ""; return ""; }
                string latest = "", next = "";
                object tagsObj;
                if (d.TryGetValue("dist-tags", out tagsObj))
                {
                    Dictionary<string, object> tags = tagsObj as Dictionary<string, object>;
                    if (tags != null)
                    {
                        object v;
                        if (tags.TryGetValue("latest", out v) && v != null) latest = v.ToString().Trim();
                        if (tags.TryGetValue("next", out v) && v != null) next = v.ToString().Trim();
                    }
                }
                // 兜底：latest 标签缺失/为空时，取全部已发布版本中语义版本最高者
                if (latest.Length == 0)
                {
                    object verObj;
                    if (d.TryGetValue("versions", out verObj))
                    {
                        Dictionary<string, object> versions = verObj as Dictionary<string, object>;
                        if (versions != null)
                            foreach (string v in versions.Keys)
                                if (latest.Length == 0 || CompareVersions(v, latest) > 0) latest = v;
                    }
                }
                // next 只有确实高于 latest 才作为"可选预览版"；否则视为没有预览更新
                if (next.Length > 0 && latest.Length > 0 && CompareVersions(next, latest) <= 0) next = "";
                DshLatest = latest;
                DshNext = next;
                return latest;
            }
            catch { DshLatest = ""; DshNext = ""; return ""; }
        }

        // ── 查管理器最新版本（GitHub Releases，公开接口）──
        // 返回最新版本号（无 v 前缀）；同时填充便携包下载地址/大小/校验文件地址
        public static string CheckManager()
        {
            string body = HttpGet("https://api.github.com/repos/wuxingyuyouxing/DeepSeek-Harness-Manager/releases/latest");
            if (body.Length == 0) return "";
            try
            {
                // 同上：每次新建，避免并发线程共享同一 JavaScriptSerializer
                Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("tag_name")) return "";
                string tag = d["tag_name"].ToString();
                object[] assets = d["assets"] as object[];
                if (assets != null)
                {
                    foreach (object o in assets)
                    {
                        Dictionary<string, object> ad = o as Dictionary<string, object>;
                        if (ad == null || !ad.ContainsKey("name")) continue;
                        string name = ad["name"].ToString();
                        if (name == "checksums.txt")
                            ManagerChecksumsUrl = ad.ContainsKey("browser_download_url") ? ad["browser_download_url"].ToString() : "";
                        else if (name.IndexOf("Portable", StringComparison.OrdinalIgnoreCase) >= 0 && name.EndsWith(".zip"))
                        {
                            ManagerLatestUrl = ad.ContainsKey("browser_download_url") ? ad["browser_download_url"].ToString() : "";
                        }
                    }
                }
                return tag.TrimStart('v');
            }
            catch { return ""; }
        }

        // ── 一次性后台检查 dsh + 管理器，结果写入静态字段 ──
        public static void CheckAllAsync(Action done)
        {
            Task.Run(delegate
            {
                DshLatest = CheckDsh();
                ManagerLatest = CheckManager();
                Checked = true;
                if (done != null) done();
            });
        }
    }

    // ───────────────────────────────────────────────────────────── CLI 自测模式
    static class Cli
    {
        public static void Run(string[] args)
        {
            Settings.Load(); // CLI 模式也需要加载配置（含持久化的自动发现路径）
            int port = 3080;
            string host = "127.0.0.1";
            string cmd = "status";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length) int.TryParse(args[i + 1], out port);
                else if (args[i] == "--host" && i + 1 < args.Length) host = args[i + 1];
                else if (args[i] == "--cli") { }
                else if (args[i] == "status" || args[i] == "start" || args[i] == "stop" || args[i] == "restart" || args[i] == "doctor") cmd = args[i];
            }

            DshService.Resolve();

            if (cmd == "doctor")
            {
                DshService.Resolve();
                // 标准查找失败时，尝试从指定端口正在运行的实例反向发现（适配源码仓库/自建安装）
                if (DshService.NodeExe.Length == 0 || DshService.BinJs.Length == 0)
                {
                    InstanceRuntime drt = new InstanceRuntime();
                    drt.Cfg = new InstanceConfig { Name = "discover", Host = host, Port = port };
                    DshService.DiscoverFromRunning(drt);
                }
                Console.WriteLine("node:   " + (DshService.NodeExe.Length > 0 ? DshService.NodeExe + " (" + DshService.NodeVersion + ")" : "未找到"));
                Console.WriteLine("dsh:    " + (DshService.BinJs.Length > 0 ? DshService.BinJs + " (" + DshService.DshVersion + ")" : "未找到"));
                Console.WriteLine("DSH_HOME: " + (Environment.GetEnvironmentVariable("DSH_HOME") ?? "(未设置)"));
                Console.WriteLine("appDir: " + Settings.AppDir);
                Console.WriteLine("logsDir:" + Settings.LogsDir);
                Environment.ExitCode = (DshService.NodeExe.Length > 0 && DshService.BinJs.Length > 0) ? 0 : 1;
                return;
            }

            InstanceConfig cfg = new InstanceConfig { Name = "cli", Host = host, Port = port, AutoOpenBrowser = false, Watchdog = false };
            InstanceRuntime rt = new InstanceRuntime();
            rt.Cfg = cfg;

            if (cmd == "status")
            {
                SvcState st = DshService.Detect(rt);
                Console.WriteLine("state=" + st + " pid=" + rt.Pid);
                Environment.ExitCode = (st == SvcState.Running) ? 0 : 1;
                return;
            }
            if (cmd == "start")
            {
                bool ok = DshService.Start(rt, delegate(string s) { Console.WriteLine(s); });
                if (!ok) { Console.WriteLine("FAIL: " + rt.LastError); Environment.ExitCode = 1; return; }
                DateTime deadline = DateTime.Now.AddSeconds(60);
                SvcState st = SvcState.Starting;
                while (DateTime.Now < deadline)
                {
                    Thread.Sleep(800);
                    st = DshService.Detect(rt);
                    if (st == SvcState.Running) break;
                    if (st == SvcState.Occupied || st == SvcState.Error) break;
                }
                Console.WriteLine("state=" + st + " pid=" + rt.Pid);
                Environment.ExitCode = (st == SvcState.Running) ? 0 : 1;
                return;
            }
            if (cmd == "stop")
            {
                DshService.Stop(rt, false);
                Thread.Sleep(800);
                SvcState st = DshService.Detect(rt);
                Console.WriteLine("state=" + st);
                Environment.ExitCode = (st == SvcState.Stopped) ? 0 : 1;
                return;
            }
            if (cmd == "restart")
            {
                DshService.Stop(rt, false);
                Thread.Sleep(800);
                bool ok = DshService.Start(rt, delegate(string s) { Console.WriteLine(s); });
                DateTime deadline = DateTime.Now.AddSeconds(60);
                SvcState st = SvcState.Starting;
                while (DateTime.Now < deadline)
                {
                    Thread.Sleep(800);
                    st = DshService.Detect(rt);
                    if (st == SvcState.Running) break;
                }
                Console.WriteLine("state=" + st + " pid=" + rt.Pid);
                Environment.ExitCode = (st == SvcState.Running) ? 0 : 1;
            }
        }
    }

    // ───────────────────────────────────────────────────────────── 绘制工具
    static class Draw
    {
        public static GraphicsPath Rounded(RectangleF r, float rad)
        {
            GraphicsPath gp = new GraphicsPath();
            if (rad <= 0.5f)
            {
                gp.AddRectangle(r);
                return gp;
            }
            float d = rad * 2f;
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        public static void FillRound(Graphics g, Brush b, RectangleF r, float rad)
        {
            using (GraphicsPath gp = Rounded(r, rad)) g.FillPath(b, gp);
        }

        public static void StrokeRound(Graphics g, Pen p, RectangleF r, float rad)
        {
            using (GraphicsPath gp = Rounded(r, rad)) g.DrawPath(p, gp);
        }

        public static void Text(Graphics g, string s, Font f, Color c, RectangleF r, ContentAlignment align)
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = (align == ContentAlignment.MiddleLeft || align == ContentAlignment.TopLeft || align == ContentAlignment.BottomLeft) ? StringAlignment.Near
                         : (align == ContentAlignment.MiddleRight || align == ContentAlignment.TopRight || align == ContentAlignment.BottomRight) ? StringAlignment.Far
                         : StringAlignment.Center;
            sf.LineAlignment = (align == ContentAlignment.TopLeft || align == ContentAlignment.TopCenter || align == ContentAlignment.TopRight) ? StringAlignment.Near
                             : (align == ContentAlignment.BottomLeft || align == ContentAlignment.BottomCenter || align == ContentAlignment.BottomRight) ? StringAlignment.Far
                             : StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap; // 禁止换行：超出即裁剪，杜绝文字溢出/重叠
            using (SolidBrush b = new SolidBrush(c))
            {
                g.SetClip(r);
                g.DrawString(s, f, b, r, sf);
                g.ResetClip();
            }
            sf.Dispose();
        }
    }

    // ───────────────────────────────────────────────────────────── 基础控件
    class BaseControl : Control
    {
        public BaseControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }

        // 每个自绘控件都要擦掉自己的背景（用主题底色），否则控件移动/重绘时
        // 旧位置的文字会残留成"残影"，表现为文字重叠、文字出现在别的位置。
        // 背景色跟随父级：侧栏内的子控件用 Sidebar 色，避免露出白色框。
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Theme T = Theme.Current;
            Color fill = T.Page;
            if (Parent != null && Parent.Dock == DockStyle.Left) fill = T.Sidebar;
            using (SolidBrush b = new SolidBrush(fill))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }
    }

    class PillButton : BaseControl
    {
        public enum Variant { Primary, Ghost, Danger, GhostDanger }
        public Variant Kind = Variant.Primary;
        public string Glyph = "";
        public string Label = "";

        float hoverP, downP;

        public PillButton()
        {
            Cursor = Cursors.Hand;
            Height = (int)Ui.P(34);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            Anim.Start(90, delegate(float p) { hoverP = p; Invalidate(); }, null);
            base.OnMouseEnter(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            Anim.Start(90, delegate(float p) { hoverP = 1f - p; Invalidate(); }, null);
            base.OnMouseLeave(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Anim.Start(70, delegate(float p) { downP = p; Invalidate(); }, null);
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            Anim.Start(90, delegate(float p) { downP = 1f - p; Invalidate(); }, null);
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme T = Theme.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            RectangleF rc = new RectangleF(1, 1, Width - 2, Height - 2);
            float rad = (float)Math.Min(10, Height / 2) * Ui.S;

            Color fill = Color.Transparent, text = T.Text, border = T.Border;
            switch (Kind)
            {
                case Variant.Primary:
                    fill = ColorX.Lerp(T.Accent, T.AccentHover, Math.Max(hoverP, downP));
                    text = Color.White; border = Color.Empty; break;
                case Variant.Ghost:
                    fill = Color.FromArgb((int)(ColorX.Lerp(Color.FromArgb(0, 0, 0, 0), T.SurfaceAlt, Math.Max(hoverP, downP)).A), T.SurfaceAlt.R, T.SurfaceAlt.G, T.SurfaceAlt.B);
                    text = T.Text; border = T.BorderStrong; break;
                case Variant.Danger:
                    fill = ColorX.Lerp(T.Err, Color.FromArgb(220, 66, 66), Math.Max(hoverP, downP));
                    text = Color.White; border = Color.Empty; break;
                case Variant.GhostDanger:
                    fill = Color.FromArgb((int)(ColorX.Lerp(Color.FromArgb(0, 0, 0, 0), T.SurfaceAlt, hoverP).A), T.SurfaceAlt.R, T.SurfaceAlt.G, T.SurfaceAlt.B);
                    text = T.Err; border = T.BorderStrong; break;
            }
            // 禁用态：弱化文字与填充，让用户明确感知按钮不可点
            if (!Enabled)
            {
                fill = Color.Transparent;
                text = T.TextFaint;
                border = T.Border;
            }

            if (fill != Color.Transparent) Draw.FillRound(g, new SolidBrush(fill), rc, rad);
            if (border != Color.Empty) Draw.StrokeRound(g, new Pen(border), rc, rad);

            // 图标用 MDL2 字体、文字用正文字体分开绘制，并按内容自适应居中 + 裁剪，避免溢出重叠
            Font glyphFont = new Font("Segoe MDL2 Assets", 9.5f, FontStyle.Regular);
            Font labelFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            using (SolidBrush tb = new SolidBrush(text))
            {
                float gap = Ui.P(6);
                float gw = Glyph.Length > 0 ? g.MeasureString(Glyph, glyphFont).Width : 0f;
                float lw = g.MeasureString(Label, labelFont).Width;
                float total = gw + (gw > 0 ? gap : 0) + lw;
                float x0 = rc.X + (rc.Width - total) / 2f;
                float th = Math.Max(glyphFont.GetHeight(g), labelFont.GetHeight(g));
                float y0 = rc.Y + (rc.Height - th) / 2f;

                using (GraphicsPath clip = Draw.Rounded(rc, rad)) g.SetClip(clip);
                if (Glyph.Length > 0)
                {
                    g.DrawString(Glyph, glyphFont, tb, x0, y0);
                    x0 += gw + gap;
                }
                g.DrawString(Label, labelFont, tb, x0, y0);
                g.ResetClip();
            }
            glyphFont.Dispose();
            labelFont.Dispose();
        }
    }

    class SwitchControl : BaseControl
    {
        bool _checked;
        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked != value)
                {
                    _checked = value;
                    AnimateTo(_checked ? 1f : 0f);
                }
            }
        }
        public event EventHandler Changed;
        public string Label = "";
        public string Hint = "";

        float knobP; // 0=关, 1=开（动画插值）

        public SwitchControl()
        {
            Cursor = Cursors.Hand;
            Height = (int)Ui.P(30);
        }

        void AnimateTo(float target)
        {
            Anim.Start(150, delegate(float p)
            {
                knobP = target * p + (1f - p) * knobP; // 从当前位置插值到目标
                Invalidate();
            }, null);
        }

        protected override void OnClick(EventArgs e)
        {
            _checked = !_checked;
            AnimateTo(_checked ? 1f : 0f);
            if (Changed != null) Changed(this, EventArgs.Empty);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme T = Theme.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            float trackH = Ui.P(22), trackW = Ui.P(40);
            float y = (Height - trackH) / 2f;
            RectangleF track = new RectangleF(Ui.P(4), y, trackW, trackH);
            Color tc = ColorX.Lerp(T.BorderStrong, T.Accent, knobP);
            Draw.FillRound(g, new SolidBrush(tc), track, trackH / 2f);

            float knob = trackH - Ui.P(6);
            float kx = track.X + Ui.P(3) + (track.Width - knob - Ui.P(6)) * knobP;
            g.FillEllipse(new SolidBrush(Color.White), kx, track.Y + Ui.P(3), knob, knob);

            if (Label.Length > 0)
            {
                Font f = new Font("Segoe UI", 9.5f, FontStyle.Regular);
                Draw.Text(g, Label, f, T.Text, new RectangleF(track.Right + Ui.P(10), 0, Width - track.Right - Ui.P(16), Height), ContentAlignment.MiddleLeft);
                f.Dispose();
            }
        }
    }

    class StatusPill : BaseControl
    {
        SvcState _state = SvcState.Unknown;
        public SvcState State
        {
            get { return _state; }
            set
            {
                if (_state != value)
                {
                    _state = value;
                    TriggerPulse();
                    Invalidate();
                }
            }
        }
        public string Label = "";
        float pulseP = 1f; // 0→1 脉冲进度

        public StatusPill()
        {
            Height = (int)Ui.P(26);
        }

        // 状态变化时触发脉冲光晕
        public void TriggerPulse()
        {
            pulseP = 0f;
            Anim.Start(650, delegate(float p) { pulseP = p; Invalidate(); }, null);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme T = Theme.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Color dot = T.TextFaint, bg = T.SurfaceAlt, fg = T.TextMuted;
            switch (State)
            {
                case SvcState.Running: dot = T.Ok; bg = Blend(T.Ok, 26); fg = T.Ok; break;
                case SvcState.Starting: dot = T.Warn; bg = Blend(T.Warn, 26); fg = T.Warn; break;
                case SvcState.Occupied: dot = T.Err; bg = Blend(T.Err, 24); fg = T.Err; break;
                case SvcState.Error: dot = T.Err; bg = Blend(T.Err, 24); fg = T.Err; break;
                case SvcState.Stopped: dot = T.TextFaint; bg = T.SurfaceAlt; fg = T.TextMuted; break;
            }

            Font f = new Font("Segoe UI Semibold", 9f, FontStyle.Regular);
            SizeF szf = g.MeasureString(Label, f);
            float w = Math.Max(Width, Ui.P(14) + szf.Width + Ui.P(24));
            RectangleF rc = new RectangleF(0, (Height - Ui.P(24)) / 2f, w, Ui.P(24));
            Draw.FillRound(g, new SolidBrush(bg), rc, Ui.P(12));

            // 状态变化脉冲：扩散的透明圆环
            if (pulseP < 1f)
            {
                float cx = rc.X + Ui.P(10.5f), cy = rc.Y + Ui.P(12);
                float r = Ui.P(4) + pulseP * Ui.P(9);
                using (Pen ring = new Pen(Color.FromArgb((int)((1f - pulseP) * 170), dot.R, dot.G, dot.B), Ui.P(2)))
                    g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
            }

            g.FillEllipse(new SolidBrush(dot), rc.X + Ui.P(8), rc.Y + Ui.P(10), Ui.P(5), Ui.P(5));
            Draw.Text(g, Label, f, fg, new RectangleF(rc.X + Ui.P(18), rc.Y, rc.Width - Ui.P(18), rc.Height), ContentAlignment.MiddleLeft);
            f.Dispose();
        }

        // 按文字调整宽度（在设置 Label 后调用一次）
        public void FitWidth()
        {
            try
            {
                using (Font f = new Font("Segoe UI Semibold", 9f, FontStyle.Regular))
                using (Graphics g = CreateGraphics())
                {
                    SizeF szf = g.MeasureString(Label, f);
                    Width = (int)(Ui.P(14) + szf.Width + Ui.P(24));
                }
            }
            catch { }
        }

        static Color Blend(Color c, int alpha)
        {
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }
    }

    class StatCard : BaseControl
    {
        public string Title = "";
        public string Value = "";
        public string Sub = "";
        public bool SubWrap;        // 描述允许换行（最多两行）
        public Color ValueColor = Color.Empty;

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme T = Theme.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            RectangleF rc = new RectangleF(0, 0, Width - 1, Height - 1);
            Draw.FillRound(g, new SolidBrush(T.Surface), rc, Ui.P(12));
            Draw.StrokeRound(g, new Pen(T.Border), rc, Ui.P(12));

            Font ft = new Font("Segoe UI", 9f, FontStyle.Regular);
            Draw.Text(g, Title, ft, T.TextMuted, new RectangleF(Ui.P(14), Ui.P(12), Width - Ui.P(28), Ui.P(18)), ContentAlignment.MiddleLeft);
            Font fv = new Font("Segoe UI Semibold", 15f, FontStyle.Regular);
            Draw.Text(g, Value, fv, ValueColor == Color.Empty ? T.Text : ValueColor,
                new RectangleF(Ui.P(14), Ui.P(30), Width - Ui.P(28), Ui.P(26)), ContentAlignment.MiddleLeft);
            Font fs = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            if (SubWrap)
            {
                using (SolidBrush b = new SolidBrush(T.TextFaint))
                {
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Near;
                    sf.LineAlignment = StringAlignment.Near;
                    g.DrawString(Sub, fs, b, new RectangleF(Ui.P(14), Ui.P(58), Width - Ui.P(28), Ui.P(34)), sf);
                    sf.Dispose();
                }
            }
            else
            {
                Draw.Text(g, Sub, fs, T.TextFaint, new RectangleF(Ui.P(14), Ui.P(56), Width - Ui.P(28), Ui.P(16)), ContentAlignment.MiddleLeft);
            }
            ft.Dispose(); fv.Dispose(); fs.Dispose();
        }
    }

    // 自绘文本控件：绘制时实时读取当前主题色，天然适配浅色/深色切换
    class SectionLabel : BaseControl
    {
        public string Caption = "";
        public float FontSize = 9f;
        public bool Faint;          // 弱化文字（TextFaint）
        public bool UseTextColor;   // 用正文色（Text）
        public bool Semibold = true;
        public Color CustomColor = Color.Empty; // 非空时优先使用（如"发现更新"的警告色）

        public SectionLabel()
        {
            Height = (int)Ui.P(22);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme T = Theme.Current;
            Color c = CustomColor != Color.Empty ? CustomColor : (UseTextColor ? T.Text : (Faint ? T.TextFaint : T.TextMuted));
            Font f = new Font(Semibold ? "Segoe UI Semibold" : "Segoe UI", FontSize, FontStyle.Regular);
            Draw.Text(e.Graphics, Caption, f, c, ClientRectangle, ContentAlignment.MiddleLeft);
            f.Dispose();
        }
    }

    // 标签页淡入图层：按透明度绘制页面快照
    class FadeOverlay : BaseControl
    {
        Bitmap bmp;
        float alpha;

        public FadeOverlay(Bitmap b) { bmp = b; }

        public void SetAlpha(float a) { alpha = a; }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (bmp == null || alpha <= 0.02f) return;
            using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
            {
                System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix();
                cm.Matrix33 = alpha;
                ia.SetColorMatrix(cm);
                e.Graphics.DrawImage(bmp, ClientRectangle, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
            }
        }
    }

    class SidebarItem : BaseControl
    {
        public string Title = "";
        public string Sub = "";
        public SvcState State = SvcState.Unknown;
        public bool Selected;
        bool hover;

        public event EventHandler ItemClick;

        public SidebarItem()
        {
            Height = (int)Ui.P(58);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (ItemClick != null) ItemClick(this, EventArgs.Empty);
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme T = Theme.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            RectangleF rc = new RectangleF(Ui.P(4), 1, Width - Ui.P(8), Height - 2);
            if (Selected) Draw.FillRound(g, new SolidBrush(T.AccentSoft), rc, Ui.P(10));
            else if (hover) Draw.FillRound(g, new SolidBrush(T.SurfaceAlt), rc, Ui.P(10));

            Color dot = T.TextFaint;
            switch (State)
            {
                case SvcState.Running: dot = T.Ok; break;
                case SvcState.Starting: dot = T.Warn; break;
                case SvcState.Occupied: dot = T.Err; break;
                case SvcState.Error: dot = T.Err; break;
            }
            g.FillEllipse(new SolidBrush(dot), rc.X + Ui.P(12), rc.Y + (Height - Ui.P(8)) / 2f, Ui.P(8), Ui.P(8));

            Font fn = new Font("Segoe UI Semibold", 9.5f, FontStyle.Regular);
            Draw.Text(g, Title, fn, Selected ? T.Accent : T.Text,
                new RectangleF(rc.X + Ui.P(28), rc.Y + Ui.P(8), rc.Width - Ui.P(34), Ui.P(20)), ContentAlignment.MiddleLeft);
            Font fs = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            Draw.Text(g, Sub, fs, T.TextMuted,
                new RectangleF(rc.X + Ui.P(28), rc.Y + Ui.P(28), rc.Width - Ui.P(34), Ui.P(18)), ContentAlignment.MiddleLeft);
            fn.Dispose(); fs.Dispose();
        }
    }

    // 自绘日志控件：完全主题化（无 RichTextBox 在深色下的灰色怪癖），支持滚轮与自动滚动
    class LogView : BaseControl
    {
        class Line { public string Text; public int Level; } // 0=普通 1=警告 2=错误
        List<Line> lines = new List<Line>();
        int scrollTop;
        bool autoScroll = true;
        Font mono = new Font("Consolas", 9f);

        public bool AutoScroll
        {
            get { return autoScroll; }
            set { autoScroll = value; if (value) ScrollToEnd(); Invalidate(); }
        }

        public LogView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Color.Black; // 防闪烁，实际底色在 OnPaint 中按主题绘制
        }

        public void AppendLine(string text, int level)
        {
            if (lines.Count > 3000) lines.RemoveAt(0);
            lines.Add(new Line { Text = text, Level = level });
            if (autoScroll) ScrollToEnd();
            Invalidate();
        }

        public void ClearAll()
        {
            lines.Clear();
            scrollTop = 0;
            Invalidate();
        }

        int LineHeight { get { return mono.Height; } }

        void ScrollToEnd()
        {
            int visible = Math.Max(1, Height / LineHeight);
            scrollTop = Math.Max(0, lines.Count - visible);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, lines.Count - Math.Max(1, Height / LineHeight));
            scrollTop -= Math.Sign(e.Delta) * 3;
            if (scrollTop < 0) scrollTop = 0;
            if (scrollTop > max) scrollTop = max;
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme T = Theme.Current;
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush b = new SolidBrush(T.LogBack)) g.FillRectangle(b, ClientRectangle);

            int lh = LineHeight;
            int visible = Math.Max(1, Height / lh);
            int n = Math.Min(lines.Count, scrollTop + visible + 1);
            int y = 2;
            for (int i = scrollTop; i < n; i++)
            {
                Line ln = lines[i];
                Color c = ln.Level == 2 ? T.LogErr : (ln.Level == 1 ? T.LogWarn : T.LogText);
                using (SolidBrush tb = new SolidBrush(c))
                    g.DrawString(ln.Text, mono, tb, 6f, y);
                y += lh;
            }
        }
    }

    // ───────────────────────────────────────────────────────────── 实例编辑对话框（标准对话框）
    class InstanceEditorDialog : Form
    {
        TextBox tbName, tbHost, tbPort;
        SwitchControl swOpen, swWatch;
        public InstanceConfig Result;

        public InstanceEditorDialog(InstanceConfig edit)
        {
            Text = edit == null ? "添加实例" : "编辑实例";
            FormBorderStyle = FormBorderStyle.FixedDialog; // 标准对话框：原生边框/标题栏/关闭按钮
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size((int)Ui.P(380), (int)Ui.P(300));
            BackColor = Theme.Current.Page;
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding((int)Ui.P(20));
            Controls.Add(body);

            int y = Ui.P(10);
            body.Controls.Add(MkLabel("名称"));
            tbName = MkTextBox(edit != null ? edit.Name : "新实例");
            tbName.Location = new Point(Ui.P(20), y += Ui.P(22));
            tbName.Size = new Size(ClientSize.Width - Ui.P(40), Ui.P(28));
            body.Controls.Add(tbName);

            body.Controls.Add(MkLabel("绑定地址"));
            tbHost = MkTextBox(edit != null ? edit.Host : "127.0.0.1");
            tbHost.Location = new Point(Ui.P(20), y += Ui.P(46));
            tbHost.Size = new Size((int)((ClientSize.Width - Ui.P(40)) * 0.55), Ui.P(28));
            body.Controls.Add(tbHost);

            body.Controls.Add(MkLabel("端口"));
            tbPort = MkTextBox(edit != null ? edit.Port.ToString() : "3080");
            tbPort.Location = new Point((int)(Ui.P(20) + (ClientSize.Width - Ui.P(40)) * 0.6), y += Ui.P(22));
            tbPort.Size = new Size((int)((ClientSize.Width - Ui.P(40)) * 0.38), Ui.P(28));
            body.Controls.Add(tbPort);

            y += Ui.P(40);
            swOpen = new SwitchControl();
            swOpen.Label = "启动成功后自动打开浏览器";
            swOpen.Location = new Point(Ui.P(16), y);
            swOpen.Size = new Size(ClientSize.Width - Ui.P(32), Ui.P(30));
            swOpen.Checked = edit == null || edit.AutoOpenBrowser;
            body.Controls.Add(swOpen);

            swWatch = new SwitchControl();
            swWatch.Label = "看门狗：意外退出自动重启";
            swWatch.Location = new Point(Ui.P(16), y += Ui.P(36));
            swWatch.Size = new Size(ClientSize.Width - Ui.P(32), Ui.P(30));
            swWatch.Checked = edit != null && edit.Watchdog;
            body.Controls.Add(swWatch);

            PillButton ok = new PillButton();
            ok.Kind = PillButton.Variant.Primary;
            ok.Label = "保存";
            ok.Location = new Point(ClientSize.Width - Ui.P(150), ClientSize.Height - Ui.P(56));
            ok.Size = new Size(Ui.P(64), Ui.P(32));
            ok.Click += delegate
            {
                int port;
                if (!int.TryParse(tbPort.Text.Trim(), out port) || port < 1 || port > 65535)
                {
                    MessageBox.Show(this, "端口必须是 1-65535 的数字。", "输入有误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (tbName.Text.Trim().Length == 0) tbName.Text = "实例";
                Result = new InstanceConfig();
                Result.Name = tbName.Text.Trim();
                Result.Host = tbHost.Text.Trim();
                if (Result.Host.Length == 0) Result.Host = "127.0.0.1";
                Result.Port = port;
                Result.AutoOpenBrowser = swOpen.Checked;
                Result.Watchdog = swWatch.Checked;
                Result.ManualStopped = edit != null && edit.ManualStopped;
                DialogResult = DialogResult.OK;
                Close();
            };
            body.Controls.Add(ok);

            PillButton cancel = new PillButton();
            cancel.Kind = PillButton.Variant.Ghost;
            cancel.Label = "取消";
            cancel.Location = new Point(ClientSize.Width - Ui.P(80), ClientSize.Height - Ui.P(56));
            cancel.Size = new Size(Ui.P(64), Ui.P(32));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            body.Controls.Add(cancel);
        }

        Label MkLabel(string s)
        {
            Label l = new Label();
            l.Text = s;
            l.BackColor = Color.Transparent; // Label 默认灰底，必须透明（深色主题下尤其明显）
            l.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            l.ForeColor = Theme.Current.TextMuted;
            return l;
        }

        TextBox MkTextBox(string text)
        {
            TextBox t = new TextBox();
            t.Font = new Font("Segoe UI", 10f);
            t.Text = text;
            t.BackColor = Theme.Current.SurfaceAlt;
            t.ForeColor = Theme.Current.Text;
            t.BorderStyle = BorderStyle.FixedSingle;
            return t;
        }
    }

    // ───────────────────────────────────────────────────────────── 主窗体
    class MainForm : Form
    {
        List<InstanceRuntime> runtimes = new List<InstanceRuntime>();
        InstanceRuntime selected;
        Timer pollTimer, logTimer, watchTimer;
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        bool reallyExit;
        bool firstHide = true;
        bool themeAnimating;
        bool installingDsh; // 防止「一键安装 dsh」重复点击

        // 关于页
        SectionLabel lblAboutDshVer, lblAboutDshStatus, lblAboutMgrVer, lblAboutMgrStatus;
        PillButton btnDshCheck, btnDshUpgrade, btnDshNext, btnMgrCheck, btnMgrUpdate;
        Label lblEnvInfo;
        bool aboutBusy; // 更新检查/下载进行中

        // 布局
        int SideW = 226;
        int TabH = 44;
        Panel contentPanel;
        List<SidebarItem> sideItems = new List<SidebarItem>();
        Dictionary<string, Control> tabPages = new Dictionary<string, Control>();
        string activeTab = "overview";
        Control tabBar;

        // 动态元素引用
        StatusPill headPill;
        PillButton btnStart, btnStop, btnRestart, btnBrowser;
        StatCard stState, stPort, stProc, stLan, stEnv;
        LogView logView;
        SwitchControl swAutoOpen, swWatch, swAutostart, swCloseExits;
        SectionLabel lblTheme;
        RichTextBox diagBox;
        TextBox tbNodePath, tbDshPath;

        public MainForm(bool minimized)
        {
            Text = "DeepSeek Harness 管理器";
            FormBorderStyle = FormBorderStyle.Sizable; // 标准窗口：原生标题栏/按钮/动画
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size((int)Ui.P(920), (int)Ui.P(640));
            MinimumSize = new Size((int)Ui.P(860), (int)Ui.P(560));
            BackColor = Theme.Current.Page;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } // 标题栏/任务栏鲸鱼图标

            foreach (InstanceConfig c in Settings.Data.Instances)
            {
                InstanceRuntime rt = new InstanceRuntime();
                rt.Cfg = c;
                runtimes.Add(rt);
            }
            if (runtimes.Count > 0) selected = runtimes[0];

            BuildTray();
            BuildLayout();
            ApplyChrome();

            pollTimer = new Timer();
            pollTimer.Interval = 2000;
            pollTimer.Tick += delegate { PollStates(); };
            pollTimer.Start();

            logTimer = new Timer();
            logTimer.Interval = 1200;
            logTimer.Tick += delegate { RefreshLogs(); };
            logTimer.Start();

            watchTimer = new Timer();
            watchTimer.Interval = 4000;
            watchTimer.Tick += delegate { Watchdog(); };
            watchTimer.Start();

            if (minimized) { Hide(); }
            else { Show(); }

            AutoCheckUpdates(); // 启动后静默检查一次 dsh/管理器更新（异步，失败静默）
        }

        // ── 托盘 ──
        void BuildTray()
        {
            tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            tray.Text = "DeepSeek Harness 管理器";
            tray.Visible = true;

            trayMenu = new ContextMenuStrip();
            ToolStripMenuItem mShow = new ToolStripMenuItem("打开主界面");
            mShow.Click += delegate { ShowWindow(); };
            ToolStripMenuItem mStart = new ToolStripMenuItem("启动服务");
            mStart.Click += delegate { if (selected != null) StartAsync(selected); };
            ToolStripMenuItem mStop = new ToolStripMenuItem("停止服务");
            mStop.Click += delegate { if (selected != null) StopAsync(selected); };
            ToolStripMenuItem mExit = new ToolStripMenuItem("退出");
            mExit.Click += delegate { ReallyExit(); };
            trayMenu.Items.Add(mShow);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(mStart);
            trayMenu.Items.Add(mStop);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(mExit);
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowWindow(); };
        }

        void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void ReallyExit()
        {
            reallyExit = true;
            try { tray.Visible = false; } catch { }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit)
            {
                if (Settings.Data.CloseExits) { ReallyExit(); return; }
                e.Cancel = true;
                Hide();
                if (firstHide)
                {
                    firstHide = false;
                    try
                    {
                        tray.ShowBalloonTip(2500, "DeepSeek Harness 管理器",
                            "服务仍在后台运行。右键托盘图标可启停服务，双击可打开主界面。",
                            ToolTipIcon.Info);
                    }
                    catch { }
                }
                return;
            }
            try { tray.Visible = false; } catch { }
            base.OnFormClosing(e);
        }

        // ── 玻璃 ──
        // 标准窗口：标题栏是否跟随深色模式。
        // Win10 上运行时改 DWMWA 属性不立即生效。这里设属性后用 1px 尺寸往返强制重算非客户区，
        // 窗口始终可见（无隐藏/重显闪烁）。
        void ApplyChrome()
        {
            try
            {
                bool dark = Theme.Current.IsDark;
                int darkInt = dark ? 1 : 0;
                Native.SetPreferredAppMode(dark ? 2 : 0);
                Native.AllowDarkModeForWindow(Handle, dark);
                Native.DwmSetAttribute(Handle, 19, darkInt);
                Native.DwmSetAttribute(Handle, 20, darkInt);
                Native.RefreshFrame(Handle);
                ForceFrameRefresh();
                Native.RedrawAll(Handle);
            }
            catch { }
        }

        // 强制重算非客户区：1px 高度往返，窗口保持可见（几乎无感知）
        void ForceFrameRefresh()
        {
            try
            {
                if (WindowState != FormWindowState.Normal) return;
                IntPtr h = Handle;
                const uint SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
                Native.SetWindowPos(h, IntPtr.Zero, 0, 0, Width, Height + 1, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                Native.SetWindowPos(h, IntPtr.Zero, 0, 0, Width, Height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                Native.RefreshFrame(h);
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyChrome();
        }

        // ── 布局 ──
        void BuildLayout()
        {
            SuspendLayout();

            tabBar = new BaseControl();
            tabBar.Dock = DockStyle.Top;   // 标签栏占顶部条带
            tabBar.Height = (int)Ui.P(TabH); // 必须随 DPI 缩放，否则高 DPI（如 200% 缩放）下按钮被下方页面遮住

            contentPanel = new Panel();
            contentPanel.Dock = DockStyle.Fill;
            contentPanel.BackColor = Color.Transparent; // Panel 支持透明，让玻璃底色透出

            // 添加顺序即 Dock 优先级：后添加的先停靠。
            // contentPanel(Fill) 最先添加 → 最后停靠 → 占据剩余空间；side(Left)、bar(Top) 依次优先。
            Controls.Add(contentPanel);
            contentPanel.Controls.Add(BuildOverview());
            contentPanel.Controls.Add(BuildLogs());
            contentPanel.Controls.Add(BuildSettings());
            contentPanel.Controls.Add(BuildDiag());
            contentPanel.Controls.Add(BuildAbout());
            contentPanel.Controls.Add(tabBar);

            // 标签栏
            BuildTabBar();

            // 侧栏
            BuildSidebar();

            ResumeLayout(false);
            RefreshSidebar();
        }

        void BuildSidebar()
        {
            BaseControl side = new BaseControl();
            side.Dock = DockStyle.Left;
            side.Width = (int)Ui.P(SideW);
            side.Paint += delegate(object s, PaintEventArgs e)
            {
                Theme T = Theme.Current;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                // 独立的侧栏背景色，与内容区拉开层次
                using (SolidBrush b = new SolidBrush(T.Sidebar))
                    e.Graphics.FillRectangle(b, ClientRectangle);
                // 右侧内阴影渐变，强化分区
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new Rectangle(Width - (int)Ui.P(10), 0, (int)Ui.P(10), Height),
                    Color.FromArgb(28, 15, 23, 42), Color.FromArgb(0, 15, 23, 42), LinearGradientMode.Horizontal))
                    e.Graphics.FillRectangle(lg, Width - (int)Ui.P(10), 0, (int)Ui.P(10), Height);
                // 右缘 1px 分界线
                using (Pen p = new Pen(T.BorderStrong))
                    e.Graphics.DrawLine(p, Width - 1, 0, Width - 1, Height);
                Font f = new Font("Segoe UI Semibold", 9f, FontStyle.Regular);
                Draw.Text(e.Graphics, "实 例", f, T.TextMuted, new RectangleF(Ui.P(20), Ui.P(14), Ui.P(120), Ui.P(20)), ContentAlignment.MiddleLeft);
                f.Dispose();
            };
            // 实例行 + 添加按钮 作为 side 的子控件（重建一次，之后仅随尺寸变化重新摆放）
            Controls.Add(side);
            RebuildSidebarChildren(side);
            side.Resize += delegate { LayoutSidebarChildren(side); };
        }

        void RebuildSidebarChildren(Control side)
        {
            side.Controls.Clear();
            sideItems.Clear();

            int y = Ui.P(44);
            foreach (InstanceRuntime rt in runtimes)
            {
                SidebarItem it = new SidebarItem();
                it.Title = rt.Cfg.Name;
                it.Sub = rt.Cfg.Url;
                it.Selected = (rt == selected);
                it.Location = new Point(Ui.P(8), y);
                it.Size = new Size(side.Width - (int)Ui.P(16), (int)Ui.P(58));
                it.ItemClick += delegate { SelectInstance(rt); };
                it.MouseUp += delegate(object s, MouseEventArgs e)
                {
                    if (e.Button == MouseButtons.Right) ShowInstanceMenu(rt, (Control)s, e.Location);
                };
                side.Controls.Add(it);
                sideItems.Add(it);
                y += Ui.P(62);
            }

            PillButton add = new PillButton();
            add.Kind = PillButton.Variant.Ghost;
            add.Glyph = "\uE710";
            add.Label = "添加实例";
            add.Location = new Point(Ui.P(14), y + Ui.P(6));
            add.Size = new Size(side.Width - (int)Ui.P(28), (int)Ui.P(34));
            add.Click += delegate { AddInstance(); };
            side.Controls.Add(add);

            SectionLabel ver = new SectionLabel();
            ver.Caption = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3); // 显示完整版本(如 v1.1.1)，随 AssemblyVersion 自动更新
            ver.Faint = true;
            ver.Semibold = false;
            ver.FontSize = 8f;
            ver.Location = new Point(Ui.P(20), side.Height - Ui.P(26));
            ver.Size = new Size(side.Width - (int)Ui.P(40), (int)Ui.P(18));
            side.Controls.Add(ver);
        }

        // 仅按当前尺寸重新摆放（不重建控件，避免 Resize 期间清空子控件）
        void LayoutSidebarChildren(Control side)
        {
            int y = Ui.P(44);
            foreach (SidebarItem it in sideItems)
            {
                it.Location = new Point(Ui.P(8), y);
                it.Size = new Size(side.Width - (int)Ui.P(16), (int)Ui.P(58));
                y += Ui.P(62);
            }
            foreach (Control c in side.Controls)
            {
                if (c is PillButton) { c.Location = new Point(Ui.P(14), y + Ui.P(6)); c.Size = new Size(side.Width - (int)Ui.P(28), (int)Ui.P(34)); }
                else if (c is SectionLabel) { c.Location = new Point(Ui.P(20), side.Height - Ui.P(26)); }
            }
        }

        void ShowInstanceMenu(InstanceRuntime rt, Control host, Point at)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem edit = new ToolStripMenuItem("编辑实例");
            edit.Click += delegate { EditInstance(rt); };
            ToolStripMenuItem del = new ToolStripMenuItem("删除实例");
            del.Click += delegate { DeleteInstance(rt); };
            m.Items.Add(edit);
            m.Items.Add(del);
            m.Show(host, at);
        }

        void SelectInstance(InstanceRuntime rt)
        {
            selected = rt;
            RefreshSidebar();
            RefreshOverview();
            RefreshSettings();
            logView.ClearAll();
        }

        void RefreshSidebar()
        {
            foreach (SidebarItem it in sideItems)
            {
                InstanceRuntime rt = FindRt(it);
                if (rt != null) { it.Selected = (rt == selected); it.State = rt.State; it.Invalidate(); }
            }
            RefreshHeadPill();
        }

        InstanceRuntime FindRt(SidebarItem it)
        {
            int i = sideItems.IndexOf(it);
            if (i >= 0 && i < runtimes.Count) return runtimes[i];
            return null;
        }

        void AddInstance()
        {
            InstanceEditorDialog d = new InstanceEditorDialog(null);
            if (d.ShowDialog(this) == DialogResult.OK && d.Result != null)
            {
                InstanceRuntime rt = new InstanceRuntime();
                rt.Cfg = d.Result;
                runtimes.Add(rt);
                Settings.Data.Instances.Add(rt.Cfg);
                Settings.Save();
                SelectInstance(rt);
                RebuildSidebarControl();
            }
        }

        void EditInstance(InstanceRuntime rt)
        {
            InstanceEditorDialog d = new InstanceEditorDialog(rt.Cfg);
            if (d.ShowDialog(this) == DialogResult.OK && d.Result != null)
            {
                rt.Cfg.Name = d.Result.Name;
                rt.Cfg.Host = d.Result.Host;
                rt.Cfg.Port = d.Result.Port;
                rt.Cfg.AutoOpenBrowser = d.Result.AutoOpenBrowser;
                rt.Cfg.Watchdog = d.Result.Watchdog;
                Settings.Save();
                SelectInstance(rt);
                RebuildSidebarControl();
            }
        }

        void DeleteInstance(InstanceRuntime rt)
        {
            if (runtimes.Count <= 1)
            {
                MessageBox.Show(this, "至少保留一个实例。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult dr = MessageBox.Show(this, "删除实例「" + rt.Cfg.Name + "」？不会停止其正在运行的服务。", "确认删除",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;
            runtimes.Remove(rt);
            Settings.Data.Instances.Remove(rt.Cfg);
            Settings.Save();
            selected = runtimes[0];
            RebuildSidebarControl();
            SelectInstance(selected);
        }

        void RebuildSidebarControl()
        {
            foreach (Control c in Controls)
            {
                if (c is BaseControl && c.Dock == DockStyle.Left) { RebuildSidebarChildren(c); break; }
            }
            RefreshSidebar();
        }

        // ── 标签栏 ──
        void BuildTabBar()
        {
            tabBar.Paint += delegate(object s, PaintEventArgs e)
            {
                Theme T = Theme.Current;
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                string[] tabs = { "概览", "日志", "设置", "诊断", "关于" };
                float x = Ui.P(18);
                float w = Ui.P(72);
                for (int i = 0; i < tabs.Length; i++)
                {
                    bool act = (activeTab == (i == 0 ? "overview" : i == 1 ? "logs" : i == 2 ? "settings" : i == 3 ? "diag" : "about"));
                    Font f = new Font("Segoe UI", 9.5f, act ? FontStyle.Bold : FontStyle.Regular);
                    Color c = act ? T.Accent : T.TextMuted;
                    RectangleF rc = new RectangleF(x, Ui.P(6), w, Ui.P(32));
                    if (act)
                    {
                        Draw.FillRound(g, new SolidBrush(T.AccentSoft), rc, Ui.P(8));
                    }
                    Draw.Text(g, tabs[i], f, c, rc, ContentAlignment.MiddleCenter);
                    f.Dispose();
                    x += w + Ui.P(6);
                }
            };
            tabBar.MouseClick += delegate(object s, MouseEventArgs e)
            {
                float x = Ui.P(18);
                float w = Ui.P(72);
                string[] tabs = { "overview", "logs", "settings", "diag", "about" };
                for (int i = 0; i < tabs.Length; i++)
                {
                    RectangleF rc = new RectangleF(x, Ui.P(6), w, Ui.P(32));
                    if (rc.Contains(e.Location)) { SwitchTab(tabs[i]); return; }
                    x += w + Ui.P(6);
                }
            };
        }

        void SwitchTab(string name)
        {
            if (activeTab == name) return;
            activeTab = name;
            Control page = null;
            foreach (KeyValuePair<string, Control> kv in tabPages)
            {
                kv.Value.Visible = (kv.Key == name);
                if (kv.Key == name) page = kv.Value;
            }
            tabBar.Invalidate();
            if (name == "diag") RunDiag();
            if (name == "logs") RefreshLogs();
            if (name == "settings") RefreshSettings();
            if (name == "about") RefreshAbout();
            FadeInPage(page);
        }

        // 标签页淡入：截取页面快照并以透明度渐显（~170ms）
        void FadeInPage(Control page)
        {
            if (page == null || page.Width <= 0 || page.Height <= 0) return;
            try
            {
                Bitmap snap = new Bitmap(page.Width, page.Height);
                page.DrawToBitmap(snap, new Rectangle(0, 0, page.Width, page.Height));
                page.Visible = false;
                FadeOverlay overlay = new FadeOverlay(snap);
                overlay.Dock = DockStyle.Fill;
                contentPanel.Controls.Add(overlay);
                overlay.BringToFront();
                Anim.Start(170, delegate(float p)
                {
                    overlay.SetAlpha(p);
                    overlay.Invalidate();
                }, delegate
                {
                    contentPanel.Controls.Remove(overlay);
                    overlay.Dispose();
                    snap.Dispose();
                    page.Visible = true;
                    page.Invalidate();
                });
            }
            catch { }
        }

        // ── 概览页 ──
        Control BuildOverview()
        {
            BaseControl page = new BaseControl();
            page.Dock = DockStyle.Fill;
            tabPages["overview"] = page;

            // 实例名（自绘，避免与下方按钮重叠）
            page.Paint += delegate(object s, PaintEventArgs e)
            {
                Theme T = Theme.Current;
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                if (selected == null) return;
                Font f = new Font("Segoe UI Semibold", 12f, FontStyle.Regular);
                Draw.Text(g, selected.Cfg.Name, f, T.Text, new RectangleF(Ui.P(20), Ui.P(10), Ui.P(180), Ui.P(28)), ContentAlignment.MiddleLeft);
                f.Dispose();
            };

            // 按钮（右侧对齐，随页面宽度重排）
            btnStart = new PillButton();
            btnStart.Glyph = "\uE768";
            btnStart.Label = "启动";
            btnStart.Size = new Size((int)Ui.P(84), (int)Ui.P(32));
            btnStart.Click += delegate { if (selected != null) StartAsync(selected); };
            page.Controls.Add(btnStart);

            btnStop = new PillButton();
            btnStop.Kind = PillButton.Variant.Ghost;
            btnStop.Glyph = "\uE71A";
            btnStop.Label = "停止";
            btnStop.Size = new Size((int)Ui.P(84), (int)Ui.P(32));
            btnStop.Click += delegate { if (selected != null) StopAsync(selected); };
            page.Controls.Add(btnStop);

            btnRestart = new PillButton();
            btnRestart.Kind = PillButton.Variant.Ghost;
            btnRestart.Glyph = "\uE72C";
            btnRestart.Label = "重启";
            btnRestart.Size = new Size((int)Ui.P(84), (int)Ui.P(32));
            btnRestart.Click += delegate { if (selected != null) RestartAsync(selected); };
            page.Controls.Add(btnRestart);

            btnBrowser = new PillButton();
            btnBrowser.Kind = PillButton.Variant.Ghost;
            btnBrowser.Glyph = "\uE774";
            btnBrowser.Label = "打开浏览器";
            btnBrowser.Size = new Size((int)Ui.P(112), (int)Ui.P(32));
            btnBrowser.Click += delegate
            {
                if (selected == null) return;
                InstanceRuntime rt = selected;
                if (rt.State != SvcState.Running && rt.State != SvcState.Starting)
                {
                    DialogResult dr = MessageBox.Show(this,
                        "服务当前未运行，浏览器会显示“无法连接”。仍要打开吗？",
                        "打开浏览器", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr != DialogResult.Yes) return;
                }
                // 即时反馈 + 后台启动浏览器（不阻塞 UI）
                btnBrowser.Label = "打开中…";
                btnBrowser.Enabled = false;
                btnBrowser.Invalidate();
                Task.Run(delegate
                {
                    try { Process.Start(rt.Cfg.Url); } catch { }
                    SafeInvoke(delegate
                    {
                        btnBrowser.Label = "打开浏览器";
                        btnBrowser.Enabled = true;
                        btnBrowser.Invalidate();
                    });
                });
            };
            page.Controls.Add(btnBrowser);

            headPill = new StatusPill();
            headPill.Width = (int)Ui.P(120);
            page.Controls.Add(headPill);

            // 统计卡片
            stState = new StatCard();
            stPort = new StatCard();
            stProc = new StatCard();
            StatCard[] cards = { stState, stPort, stProc };
            for (int i = 0; i < cards.Length; i++)
            {
                cards[i].Size = new Size((int)Ui.P(220), (int)Ui.P(80));
                page.Controls.Add(cards[i]);
            }
            stState.Title = "服务状态";
            stState.Value = "—";
            stState.Sub = "上次探测";
            stPort.Title = "监听地址";
            stPort.Value = "—";
            stPort.Sub = "绑定";
            stProc.Title = "进程";
            stProc.Value = "—";
            stProc.Sub = "内存占用";

            stLan = new StatCard();
            stLan.Size = new Size((int)Ui.P(454), (int)Ui.P(96));
            stLan.Title = "局域网访问";
            stLan.Value = "—";
            stLan.Sub = "提示：跨设备访问需 --host 0.0.0.0 --trusted-host <设备IP>";
            page.Controls.Add(stLan);

            stEnv = new StatCard();
            stEnv.Size = new Size((int)Ui.P(454), (int)Ui.P(96));
            stEnv.Title = "环境信息";
            stEnv.Value = "—";
            stEnv.Sub = "";
            page.Controls.Add(stEnv);

            // 诊断信息小字
            SectionLabel note = new SectionLabel();
            note.Caption = "DeepSeek Harness · 本地服务管理器";
            note.Faint = true;
            note.Semibold = false;
            note.FontSize = 8.5f;
            note.Location = new Point((int)Ui.P(22), (int)Ui.P(286));
            note.Size = new Size((int)Ui.P(400), (int)Ui.P(18));
            page.Controls.Add(note);

            // 响应式布局：按钮与卡片随页面宽度重排，任何窗口大小都不溢出
            LayoutOverview(page);
            page.Resize += delegate { LayoutOverview(page); };

            return page;
        }

        void LayoutOverview(Control page)
        {
            int W = page.Width;
            int M = (int)Ui.P(20);
            int G = (int)Ui.P(14);
            int by = (int)Ui.P(10);

            // 按钮行：右侧对齐（最小 X 让出左侧标题区）
            int minX = M + (int)Ui.P(188);
            int bx = W - M - btnBrowser.Width;
            btnBrowser.Location = new Point(bx, by);
            bx -= (int)Ui.P(8) + btnRestart.Width;
            btnRestart.Location = new Point(bx, by);
            bx -= (int)Ui.P(8) + btnStop.Width;
            btnStop.Location = new Point(bx, by);
            bx -= (int)Ui.P(8) + btnStart.Width;
            btnStart.Location = new Point(bx < minX ? minX : bx, by);

            headPill.Location = new Point(M, (int)Ui.P(48));

            // 三张统计卡：平分可用宽度
            int cw = (W - M * 2 - G * 2) / 3;
            if (cw < (int)Ui.P(150)) cw = (int)Ui.P(150);
            stState.Location = new Point(M, (int)Ui.P(84));
            stPort.Location = new Point(M + cw + G, (int)Ui.P(84));
            stProc.Location = new Point(M + 2 * (cw + G), (int)Ui.P(84));
            stState.Size = new Size(cw, (int)Ui.P(80));
            stPort.Size = new Size(cw, (int)Ui.P(80));
            stProc.Size = new Size(cw, (int)Ui.P(80));

            // 两张宽卡：各占一半
            int ww = (W - M * 2 - G) / 2;
            if (ww < (int)Ui.P(180)) ww = (int)Ui.P(180);
            stLan.Location = new Point(M, (int)Ui.P(178));
            stEnv.Location = new Point(M + ww + G, (int)Ui.P(178));
            stLan.Size = new Size(ww, (int)Ui.P(100));
            stEnv.Size = new Size(ww, (int)Ui.P(100));
            stLan.SubWrap = true;
            stEnv.SubWrap = true;
        }

        // ── 日志页 ──
        Control BuildLogs()
        {
            BaseControl page = new BaseControl();
            page.Dock = DockStyle.Fill;
            tabPages["logs"] = page;

            logView = new LogView();
            logView.Dock = DockStyle.Fill;
            page.Controls.Add(logView);

            BaseControl bar = new BaseControl();
            bar.Dock = DockStyle.Top;
            bar.Height = (int)Ui.P(46);
            page.Controls.Add(bar);

            SwitchControl swAuto = new SwitchControl();
            swAuto.Label = "自动滚动";
            swAuto.Location = new Point((int)Ui.P(14), (int)Ui.P(8));
            swAuto.Size = new Size((int)Ui.P(140), (int)Ui.P(30));
            swAuto.Checked = true;
            swAuto.Changed += delegate { logView.AutoScroll = swAuto.Checked; };
            bar.Controls.Add(swAuto);

            PillButton btnClear = new PillButton();
            btnClear.Kind = PillButton.Variant.Ghost;
            btnClear.Label = "清空显示";
            btnClear.Location = new Point((int)Ui.P(170), (int)Ui.P(7));
            btnClear.Size = new Size((int)Ui.P(84), (int)Ui.P(32));
            btnClear.Click += delegate { logView.ClearAll(); };
            bar.Controls.Add(btnClear);

            PillButton btnOpen = new PillButton();
            btnOpen.Kind = PillButton.Variant.Ghost;
            btnOpen.Glyph = "\uE8B7";
            btnOpen.Label = "日志文件夹";
            btnOpen.Location = new Point((int)Ui.P(262), (int)Ui.P(7));
            btnOpen.Size = new Size((int)Ui.P(104), (int)Ui.P(32));
            btnOpen.Click += delegate
            {
                try
                {
                    if (!Directory.Exists(Settings.LogsDir)) Directory.CreateDirectory(Settings.LogsDir);
                    Process.Start("explorer.exe", "\"" + Settings.LogsDir + "\"");
                }
                catch { }
            };
            bar.Controls.Add(btnOpen);

            return page;
        }

        // ── 设置页 ──
        Control BuildSettings()
        {
            BaseControl page = new BaseControl();
            page.Dock = DockStyle.Fill;
            tabPages["settings"] = page;

            // 当前实例卡片
            SectionLabel lInst = new SectionLabel();
            lInst.Caption = "当前实例";
            lInst.Location = new Point((int)Ui.P(20), (int)Ui.P(18));
            lInst.Size = new Size((int)Ui.P(200), (int)Ui.P(20));
            page.Controls.Add(lInst);

            swAutoOpen = new SwitchControl();
            swAutoOpen.Label = "启动成功后自动打开浏览器";
            swAutoOpen.Location = new Point((int)Ui.P(20), (int)Ui.P(46));
            swAutoOpen.Size = new Size((int)Ui.P(320), (int)Ui.P(30));
            swAutoOpen.Changed += delegate { if (selected != null) { selected.Cfg.AutoOpenBrowser = swAutoOpen.Checked; Settings.Save(); } };
            page.Controls.Add(swAutoOpen);

            swWatch = new SwitchControl();
            swWatch.Label = "看门狗：意外退出自动重启";
            swWatch.Location = new Point((int)Ui.P(20), (int)Ui.P(82));
            swWatch.Size = new Size((int)Ui.P(320), (int)Ui.P(30));
            swWatch.Changed += delegate { if (selected != null) { selected.Cfg.Watchdog = swWatch.Checked; Settings.Save(); } };
            page.Controls.Add(swWatch);

            // 通用卡片
            SectionLabel lGen = new SectionLabel();
            lGen.Caption = "通用";
            lGen.Location = new Point((int)Ui.P(20), (int)Ui.P(140));
            lGen.Size = new Size((int)Ui.P(200), (int)Ui.P(20));
            page.Controls.Add(lGen);

            swAutostart = new SwitchControl();
            swAutostart.Label = "开机自启动（登录后最小化到托盘）";
            swAutostart.Location = new Point((int)Ui.P(20), (int)Ui.P(168));
            swAutostart.Size = new Size((int)Ui.P(340), (int)Ui.P(30));
            swAutostart.Changed += delegate { ApplyAutostart(swAutostart.Checked); };
            page.Controls.Add(swAutostart);

            swCloseExits = new SwitchControl();
            swCloseExits.Label = "关闭窗口时直接退出（否则最小化到托盘）";
            swCloseExits.Location = new Point((int)Ui.P(20), (int)Ui.P(204));
            swCloseExits.Size = new Size((int)Ui.P(340), (int)Ui.P(30));
            swCloseExits.Changed += delegate { Settings.Data.CloseExits = swCloseExits.Checked; Settings.Save(); };
            page.Controls.Add(swCloseExits);

            // 主题：分节标题 → 当前主题文字 → 切换按钮（上下排列，互不遮挡）
            SectionLabel lTheme = new SectionLabel();
            lTheme.Caption = "主题";
            lTheme.Location = new Point((int)Ui.P(20), (int)Ui.P(240));
            lTheme.Size = new Size((int)Ui.P(200), (int)Ui.P(20));
            page.Controls.Add(lTheme);

            lblTheme = new SectionLabel();
            lblTheme.UseTextColor = true;
            lblTheme.Semibold = false;
            lblTheme.FontSize = 9.5f;
            lblTheme.Location = new Point((int)Ui.P(20), (int)Ui.P(268));
            lblTheme.Size = new Size((int)Ui.P(220), (int)Ui.P(22));
            page.Controls.Add(lblTheme);

            PillButton btnTheme = new PillButton();
            btnTheme.Kind = PillButton.Variant.Ghost;
            btnTheme.Label = "切换主题";
            btnTheme.Location = new Point((int)Ui.P(20), (int)Ui.P(296));
            btnTheme.Size = new Size((int)Ui.P(92), (int)Ui.P(32));
            btnTheme.Click += delegate
            {
                if (themeAnimating) return;
                themeAnimating = true;

                // 平滑过渡：整体淡暗(90ms) → 在底部瞬间切换主题 → 平滑恢复(150ms)。
                // 不做内容快照混合，避免浅色↔深色交叉产生灰色泥浆感。
                Anim.Start(90, delegate(float p) { Opacity = 1f - 0.8f * p; }, delegate
                {
                    try
                    {
                        // 循环：浅色 → 深色 → 跟随系统 → 浅色
                        string k = Settings.Data.Theme;
                        if (k == "light") Settings.Data.Theme = "dark";
                        else if (k == "dark") Settings.Data.Theme = "system";
                        else Settings.Data.Theme = "light";
                        Theme.Apply(Settings.Data.Theme);
                        Settings.Save();
                        ApplyChrome();
                        ApplyTheme();
                        UpdateSelectedUi(); // 让状态卡/胶囊按新主题刷新颜色
                        RefreshSettings();
                        Native.RedrawAll(Handle); // 强制整窗立即重绘，标题栏/状态区同步变色
                    }
                    finally
                    {
                        Anim.Start(150, delegate(float p) { Opacity = 0.2f + 0.8f * p; },
                            delegate { Opacity = 1f; themeAnimating = false; });
                    }
                });
            };
            page.Controls.Add(btnTheme);

            return page;
        }

        void ApplyAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("DeepSeekHarnessManager", "\"" + Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue("DeepSeekHarnessManager", false);
                }
                Settings.Data.Autostart = on;
                Settings.Save();
            }
            catch { }
        }

        void ApplyTheme()
        {
            BackColor = Theme.Current.Page;
            ApplyThemeRec(Controls);
            Invalidate(true);
        }

        void ApplyThemeRec(Control.ControlCollection coll)
        {
            foreach (Control c in coll)
            {
                if (c is Label)
                {
                    c.BackColor = Color.Transparent; // Label 默认是系统灰底，一律透明以透出页面底色
                    string tag = (c.Tag as string) ?? "muted";
                    if (tag == "faint") c.ForeColor = Theme.Current.TextFaint;
                    else if (tag == "text") c.ForeColor = Theme.Current.Text;
                    else if (tag == "accent") c.ForeColor = Theme.Current.Accent;
                    else c.ForeColor = Theme.Current.TextMuted;
                }
                else if (c is RichTextBox)
                {
                    c.BackColor = Theme.Current.LogBack;
                    c.ForeColor = Theme.Current.LogText;
                }
                else if (c is TextBox)
                {
                    // Tag="page" 的输入框与页面底色完全一致（诊断页路径框，避免灰色底纹）
                    c.BackColor = (c.Tag as string) == "page" ? Theme.Current.Page : Theme.Current.SurfaceAlt;
                    c.ForeColor = Theme.Current.Text;
                }
                c.Invalidate(true);
                if (c.Controls.Count > 0) ApplyThemeRec(c.Controls);
            }
        }

        // ── 诊断页 ──
        Control BuildDiag()
        {
            BaseControl page = new BaseControl();
            page.Dock = DockStyle.Fill;
            tabPages["diag"] = page;

            diagBox = new RichTextBox();
            diagBox.ReadOnly = true;
            diagBox.BorderStyle = BorderStyle.None;
            diagBox.BackColor = Theme.Current.LogBack;
            diagBox.ForeColor = Theme.Current.LogText;
            diagBox.Font = new Font("Consolas", 9.5f);
            diagBox.Dock = DockStyle.Fill;
            diagBox.Padding = new Padding((int)Ui.P(8));
            page.Controls.Add(diagBox);

            BaseControl bar = new BaseControl();
            bar.Dock = DockStyle.Top;
            bar.Height = (int)Ui.P(46);
            page.Controls.Add(bar);

            PillButton btnRe = new PillButton();
            btnRe.Kind = PillButton.Variant.Ghost;
            btnRe.Glyph = "\uE72C";
            btnRe.Label = "重新检测";
            btnRe.Location = new Point((int)Ui.P(14), (int)Ui.P(7));
            btnRe.Size = new Size((int)Ui.P(96), (int)Ui.P(32));
            btnRe.Click += delegate { RunDiag(); };
            bar.Controls.Add(btnRe);

            PillButton btnCopy = new PillButton();
            btnCopy.Kind = PillButton.Variant.Ghost;
            btnCopy.Glyph = "\uE8C8";
            btnCopy.Label = "复制启动命令";
            btnCopy.Location = new Point((int)Ui.P(118), (int)Ui.P(7));
            btnCopy.Size = new Size((int)Ui.P(116), (int)Ui.P(32));
            btnCopy.Click += delegate
            {
                if (selected == null) return;
                string cmd = "dsh web --host " + selected.Cfg.Host + " --port " + selected.Cfg.Port;
                try { Clipboard.SetText(cmd); } catch { }
            };
            bar.Controls.Add(btnCopy);

            // 一键安装 dsh（用自带/本机 Node 的 npx，需联网一次）
            PillButton btnInstall = new PillButton();
            btnInstall.Kind = PillButton.Variant.Primary;
            btnInstall.Glyph = "\uE895";
            btnInstall.Label = "一键安装 dsh";
            btnInstall.Location = new Point((int)Ui.P(242), (int)Ui.P(7));
            btnInstall.Size = new Size((int)Ui.P(116), (int)Ui.P(32));
            btnInstall.Click += delegate { InstallDsh(btnInstall, "一键安装 dsh", UpdateService.DshLatest, null); };
            bar.Controls.Add(btnInstall);

            // 手动指定 node/dsh 路径（适配源码仓库/自建安装等非标准方式）
            BaseControl pathBar = new BaseControl();
            pathBar.Dock = DockStyle.Bottom;
            pathBar.Height = (int)Ui.P(92);
            page.Controls.Add(pathBar);

            Label lNode = new Label();
            lNode.Text = "node.exe（可选）";
            // Label 默认 BackColor 是系统灰(SystemColors.Control)，深色页面上就是灰底块，
            // 必须设透明以透出父级页面底色
            lNode.BackColor = Color.Transparent;
            lNode.ForeColor = Theme.Current.TextMuted;
            lNode.Font = new Font("Segoe UI", 8.5f);
            lNode.Location = new Point((int)Ui.P(14), (int)Ui.P(8));
            lNode.AutoSize = true;
            pathBar.Controls.Add(lNode);

            tbNodePath = new TextBox();
            tbNodePath.ReadOnly = true;
            // ReadOnly 输入框默认背景是系统灰，必须显式用主题色。
            // 这里用与页面完全一致的 Page 色（Tag="page"），深色/浅色下都与页面融为一体，
            // 只留细边框区分，不再有任何灰色底纹观感。
            tbNodePath.Tag = "page";
            tbNodePath.BackColor = Theme.Current.Page;
            tbNodePath.ForeColor = Theme.Current.Text;
            tbNodePath.BorderStyle = BorderStyle.FixedSingle;
            tbNodePath.Text = Settings.Data.NodePath;
            tbNodePath.Location = new Point((int)Ui.P(14), (int)Ui.P(26));
            tbNodePath.Size = new Size((int)Ui.P(430), (int)Ui.P(22));
            pathBar.Controls.Add(tbNodePath);

            PillButton btnNodeBrowse = new PillButton();
            btnNodeBrowse.Kind = PillButton.Variant.Ghost;
            btnNodeBrowse.Label = "选择";
            btnNodeBrowse.Location = new Point((int)Ui.P(452), (int)Ui.P(24));
            btnNodeBrowse.Size = new Size((int)Ui.P(56), (int)Ui.P(26));
            btnNodeBrowse.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Title = "选择 node.exe";
                    d.Filter = "node.exe|node.exe";
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        Settings.Data.NodePath = d.FileName;
                        Settings.Save();
                        tbNodePath.Text = d.FileName;
                        RunDiag();
                    }
                }
            };
            pathBar.Controls.Add(btnNodeBrowse);

            Label lDsh = new Label();
            lDsh.Text = "dsh 入口（可选）";
            // 同上：Label 默认灰底，必须透明
            lDsh.BackColor = Color.Transparent;
            lDsh.ForeColor = Theme.Current.TextMuted;
            lDsh.Font = new Font("Segoe UI", 8.5f);
            lDsh.Location = new Point((int)Ui.P(14), (int)Ui.P(50));
            lDsh.AutoSize = true;
            pathBar.Controls.Add(lDsh);

            tbDshPath = new TextBox();
            tbDshPath.ReadOnly = true;
            // 同上：用页面色（Tag="page"），与页面完全一致，无灰色底纹
            tbDshPath.Tag = "page";
            tbDshPath.BackColor = Theme.Current.Page;
            tbDshPath.ForeColor = Theme.Current.Text;
            tbDshPath.BorderStyle = BorderStyle.FixedSingle;
            tbDshPath.Text = Settings.Data.DshPath;
            tbDshPath.Location = new Point((int)Ui.P(14), (int)Ui.P(68));
            tbDshPath.Size = new Size((int)Ui.P(430), (int)Ui.P(22));
            pathBar.Controls.Add(tbDshPath);

            PillButton btnDshBrowse = new PillButton();
            btnDshBrowse.Kind = PillButton.Variant.Ghost;
            btnDshBrowse.Label = "选择";
            btnDshBrowse.Location = new Point((int)Ui.P(452), (int)Ui.P(66));
            btnDshBrowse.Size = new Size((int)Ui.P(56), (int)Ui.P(26));
            btnDshBrowse.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Title = "选择 dsh 入口脚本（bin.js）";
                    d.Filter = "JS 文件|*.js;*.mjs;*.cjs|所有文件|*.*";
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        Settings.Data.DshPath = d.FileName;
                        Settings.Save();
                        tbDshPath.Text = d.FileName;
                        RunDiag();
                    }
                }
            };
            pathBar.Controls.Add(btnDshBrowse);

            return page;
        }

        // ── 关于页：版本信息 / 更新检查 / 运行环境 / 链接 ──
        // 布局说明：控件直接放在页面上（页面底色），卡片区域只画圆角边框、不填充 Surface，
        // 因此所有文字/按钮背景都与页面一致，任何主题下都不会出现灰色底纹。
        Control BuildAbout()
        {
            BaseControl page = new BaseControl();
            page.Dock = DockStyle.Fill;
            tabPages["about"] = page;

            // 标题区：鲸鱼图标 + 名称 + 副标题
            PictureBox logo = new PictureBox();
            try { logo.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch { }
            logo.SizeMode = PictureBoxSizeMode.StretchImage;
            logo.BackColor = Color.Transparent; // PictureBox 默认灰底(SystemColors.Control)，必须透明
            logo.Size = new Size((int)Ui.P(40), (int)Ui.P(40));
            logo.Location = new Point((int)Ui.P(20), (int)Ui.P(16));
            page.Controls.Add(logo);

            SectionLabel title = new SectionLabel();
            title.Caption = "DeepSeek Harness 管理器";
            title.UseTextColor = true;
            title.Semibold = true;
            title.FontSize = 15f;
            title.Location = new Point((int)Ui.P(70), (int)Ui.P(18));
            title.Size = new Size((int)Ui.P(320), (int)Ui.P(28));
            page.Controls.Add(title);

            SectionLabel subtitle = new SectionLabel();
            subtitle.Caption = "管理 dsh web 本地服务 · 版本 v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            subtitle.Faint = true;
            subtitle.Semibold = false;
            subtitle.FontSize = 9f;
            subtitle.Location = new Point((int)Ui.P(70), (int)Ui.P(44));
            subtitle.Size = new Size((int)Ui.P(420), (int)Ui.P(20));
            page.Controls.Add(subtitle);

            // ── 更新检查区 ──
            SectionLabel lUpd = new SectionLabel();
            lUpd.Caption = "更新检查";
            lUpd.Location = new Point((int)Ui.P(34), (int)Ui.P(88));
            lUpd.Size = new Size((int)Ui.P(200), (int)Ui.P(20));
            page.Controls.Add(lUpd);

            SectionLabel lDsh = new SectionLabel();
            lDsh.Caption = "dsh 版本";
            lDsh.Semibold = false;
            lDsh.FontSize = 9.5f; // 与右侧版本值同字号同控件，保证水平齐平
            lDsh.Location = new Point((int)Ui.P(34), (int)Ui.P(116));
            lDsh.Size = new Size((int)Ui.P(64), (int)Ui.P(22)); // 宽度按文字收窄，绝不给右侧版本值重叠
            page.Controls.Add(lDsh);

            lblAboutDshVer = new SectionLabel();
            lblAboutDshVer.UseTextColor = true;
            lblAboutDshVer.Semibold = false;
            lblAboutDshVer.FontSize = 9.5f;
            lblAboutDshVer.Location = new Point((int)Ui.P(110), (int)Ui.P(116));
            lblAboutDshVer.Size = new Size((int)Ui.P(200), (int)Ui.P(22));
            page.Controls.Add(lblAboutDshVer);

            btnDshUpgrade = new PillButton();
            btnDshUpgrade.Kind = PillButton.Variant.Primary;
            btnDshUpgrade.Label = "一键升级 dsh";
            btnDshUpgrade.Size = new Size((int)Ui.P(116), (int)Ui.P(30));
            btnDshUpgrade.Click += delegate { UpgradeDsh(btnDshUpgrade, UpdateService.DshLatest, "一键升级 dsh"); };
            page.Controls.Add(btnDshUpgrade);

            btnDshCheck = new PillButton();
            btnDshCheck.Kind = PillButton.Variant.Ghost;
            btnDshCheck.Label = "检查更新";
            btnDshCheck.Size = new Size((int)Ui.P(88), (int)Ui.P(30));
            btnDshCheck.Click += delegate { CheckUpdates(); };
            page.Controls.Add(btnDshCheck);

            // 「升级到预览版」：next 轨可选升级（含破坏性变更，需用户确认），默认隐藏
            btnDshNext = new PillButton();
            btnDshNext.Kind = PillButton.Variant.Ghost;
            btnDshNext.Label = "升级到预览版";
            btnDshNext.Size = new Size((int)Ui.P(116), (int)Ui.P(30));
            btnDshNext.Visible = false;
            btnDshNext.Click += delegate { UpgradeDsh(btnDshNext, UpdateService.DshNext, "升级到预览版"); };
            page.Controls.Add(btnDshNext);

            lblAboutDshStatus = new SectionLabel();
            lblAboutDshStatus.Faint = true;
            lblAboutDshStatus.Semibold = false;
            lblAboutDshStatus.FontSize = 8.5f;
            lblAboutDshStatus.Location = new Point((int)Ui.P(34), (int)Ui.P(146));
            lblAboutDshStatus.Size = new Size((int)Ui.P(560), (int)Ui.P(20));
            page.Controls.Add(lblAboutDshStatus);

            SectionLabel lMgr = new SectionLabel();
            lMgr.Caption = "管理器版本";
            lMgr.Semibold = false;
            lMgr.FontSize = 9.5f; // 与右侧版本值同字号同控件，保证水平齐平
            lMgr.Location = new Point((int)Ui.P(34), (int)Ui.P(176));
            lMgr.Size = new Size((int)Ui.P(72), (int)Ui.P(22)); // 宽度按文字收窄(5字≈66px)，绝不给右侧版本值重叠
            page.Controls.Add(lMgr);

            lblAboutMgrVer = new SectionLabel();
            lblAboutMgrVer.UseTextColor = true;
            lblAboutMgrVer.Semibold = false;
            lblAboutMgrVer.FontSize = 9.5f;
            lblAboutMgrVer.Location = new Point((int)Ui.P(110), (int)Ui.P(176));
            lblAboutMgrVer.Size = new Size((int)Ui.P(200), (int)Ui.P(22));
            page.Controls.Add(lblAboutMgrVer);

            btnMgrUpdate = new PillButton();
            btnMgrUpdate.Kind = PillButton.Variant.Primary;
            btnMgrUpdate.Label = "下载并更新";
            btnMgrUpdate.Size = new Size((int)Ui.P(116), (int)Ui.P(30));
            btnMgrUpdate.Click += delegate { UpdateManager(btnMgrUpdate); };
            page.Controls.Add(btnMgrUpdate);

            btnMgrCheck = new PillButton();
            btnMgrCheck.Kind = PillButton.Variant.Ghost;
            btnMgrCheck.Label = "检查更新";
            btnMgrCheck.Size = new Size((int)Ui.P(88), (int)Ui.P(30));
            btnMgrCheck.Click += delegate { CheckUpdates(); };
            page.Controls.Add(btnMgrCheck);

            lblAboutMgrStatus = new SectionLabel();
            lblAboutMgrStatus.Faint = true;
            lblAboutMgrStatus.Semibold = false;
            lblAboutMgrStatus.FontSize = 8.5f;
            lblAboutMgrStatus.Location = new Point((int)Ui.P(34), (int)Ui.P(206));
            lblAboutMgrStatus.Size = new Size((int)Ui.P(560), (int)Ui.P(20));
            page.Controls.Add(lblAboutMgrStatus);

            // ── 运行环境区 ──
            SectionLabel lEnv = new SectionLabel();
            lEnv.Caption = "运行环境";
            lEnv.Location = new Point((int)Ui.P(34), (int)Ui.P(246));
            lEnv.Size = new Size((int)Ui.P(200), (int)Ui.P(20));
            page.Controls.Add(lEnv);

            lblEnvInfo = new Label();
            lblEnvInfo.BackColor = Color.Transparent;
            lblEnvInfo.ForeColor = Theme.Current.TextMuted;
            lblEnvInfo.Font = new Font("Consolas", 9f);
            lblEnvInfo.Location = new Point((int)Ui.P(34), (int)Ui.P(272));
            lblEnvInfo.Size = new Size((int)Ui.P(560), (int)Ui.P(64));
            page.Controls.Add(lblEnvInfo);

            PillButton btnLogOpen = new PillButton();
            btnLogOpen.Kind = PillButton.Variant.Ghost;
            btnLogOpen.Label = "打开日志目录";
            btnLogOpen.Size = new Size((int)Ui.P(110), (int)Ui.P(28));
            btnLogOpen.Click += delegate
            {
                try
                {
                    if (!Directory.Exists(Settings.LogsDir)) Directory.CreateDirectory(Settings.LogsDir);
                    Process.Start("explorer.exe", "\"" + Settings.LogsDir + "\"");
                }
                catch { }
            };
            page.Controls.Add(btnLogOpen);

            // ── 链接行 ──
            int ly = (int)Ui.P(362);
            PillButton btnDoc = LinkButton(page, "DeepSeek Harness 文档", "https://www.npmjs.com/package/@deepseek-ai/dsh", ly);
            PillButton btnRepo = LinkButton(page, "GitHub 仓库", "https://github.com/wuxingyuyouxing/DeepSeek-Harness-Manager", ly);
            PillButton btnRel = LinkButton(page, "更新记录", "https://github.com/wuxingyuyouxing/DeepSeek-Harness-Manager/releases", ly);
            btnDoc.Location = new Point((int)Ui.P(20), ly);
            btnRepo.Location = new Point((int)Ui.P(208), ly); // 按钮宽176 + 12px 间距
            btnRel.Location = new Point((int)Ui.P(396), ly);

            // 版权
            SectionLabel copy = new SectionLabel();
            copy.Caption = "Copyright © 2026 DeepSeek Harness · MIT License";
            copy.Faint = true;
            copy.Semibold = false;
            copy.FontSize = 8.5f;
            copy.Location = new Point((int)Ui.P(20), ly + (int)Ui.P(52));
            copy.Size = new Size((int)Ui.P(400), (int)Ui.P(18));
            page.Controls.Add(copy);

            SectionLabel tm = new SectionLabel();
            tm.Caption = "鲸鱼 Logo 为 DeepSeek 商标，仅用于标识本工具管理的目标产品";
            tm.Faint = true;
            tm.Semibold = false;
            tm.FontSize = 8f;
            tm.Location = new Point((int)Ui.P(20), ly + (int)Ui.P(70));
            tm.Size = new Size((int)Ui.P(500), (int)Ui.P(18));
            page.Controls.Add(tm);

            // 分组圆角边框（只描边不填充：控件背景与页面一致，无底纹）
            page.Paint += delegate(object s, PaintEventArgs e)
            {
                Theme T = Theme.Current;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF rc1 = new RectangleF((int)Ui.P(14), (int)Ui.P(78), page.Width - (int)Ui.P(28), (int)Ui.P(150));
                using (Pen p = new Pen(T.Border)) Draw.StrokeRound(e.Graphics, p, rc1, Ui.P(10));
                RectangleF rc2 = new RectangleF((int)Ui.P(14), (int)Ui.P(236), page.Width - (int)Ui.P(28), (int)Ui.P(112));
                using (Pen p = new Pen(T.Border)) Draw.StrokeRound(e.Graphics, p, rc2, Ui.P(10));
            };

            // 按钮右对齐 + 动态宽度跟随
            page.Resize += delegate
            {
                int bx = page.Width - (int)Ui.P(28) - btnDshCheck.Width;
                btnDshCheck.Location = new Point(bx, (int)Ui.P(114));
                btnDshUpgrade.Location = new Point(bx - (int)Ui.P(8) - btnDshUpgrade.Width, (int)Ui.P(114));
                btnDshNext.Location = new Point(btnDshUpgrade.Left - (int)Ui.P(8) - btnDshNext.Width, (int)Ui.P(114));
                int bx2 = page.Width - (int)Ui.P(28) - btnMgrCheck.Width;
                btnMgrCheck.Location = new Point(bx2, (int)Ui.P(174));
                btnMgrUpdate.Location = new Point(bx2 - (int)Ui.P(8) - btnMgrUpdate.Width, (int)Ui.P(174));
                lblAboutDshStatus.Width = page.Width - (int)Ui.P(68);
                lblAboutMgrStatus.Width = page.Width - (int)Ui.P(68);
                lblEnvInfo.Width = page.Width - (int)Ui.P(198); // 收窄，给右侧按钮留位
                btnLogOpen.Location = new Point(page.Width - (int)Ui.P(28) - (int)Ui.P(20) - btnLogOpen.Width, (int)Ui.P(300));
            };

            RefreshAbout();
            return page;
        }

        PillButton LinkButton(Control page, string label, string url, int y)
        {
            PillButton b = new PillButton();
            b.Kind = PillButton.Variant.Ghost;
            b.Label = label;
            b.Size = new Size((int)Ui.P(176), (int)Ui.P(30)); // 加宽：长文案（如"DeepSeek Harness 文档"）不被裁剪
            b.Click += delegate { try { Process.Start(url); } catch { } };
            page.Controls.Add(b);
            return b;
        }

        // 刷新关于页：版本显示 / 检查状态 / 环境信息
        void RefreshAbout()
        {
            if (lblAboutDshVer == null) return;
            Theme T = Theme.Current;
            string localDsh = DshService.DshVersion.Length > 0 ? DshService.DshVersion : "未知";
            lblAboutDshVer.Caption = localDsh;
            lblAboutDshVer.Invalidate();
            // 双轨：latest（稳定轨）默认提示；next（预览轨）作为可选更新，明确标注风险
            bool latestUp = UpdateService.DshLatest.Length > 0 && localDsh != "未知" &&
                UpdateService.CompareVersions(UpdateService.DshLatest, localDsh) > 0;
            bool nextUp = UpdateService.DshNext.Length > 0 && localDsh != "未知" &&
                UpdateService.CompareVersions(UpdateService.DshNext, localDsh) > 0;
            if (UpdateService.DshLatest.Length > 0)
            {
                if (latestUp)
                {
                    lblAboutDshStatus.Caption = "发现新版本 " + UpdateService.DshLatest + "（当前 " + localDsh + "）→ 点击「一键升级 dsh」";
                    lblAboutDshStatus.CustomColor = T.Warn;
                }
                else if (nextUp)
                {
                    lblAboutDshStatus.Caption = "已是最新稳定版 " + localDsh + " · 可选预览版 " + UpdateService.DshNext + "（含破坏性变更）";
                    lblAboutDshStatus.CustomColor = T.Warn;
                }
                else
                {
                    lblAboutDshStatus.Caption = "已是最新版本 " + localDsh;
                    lblAboutDshStatus.CustomColor = Color.Empty;
                }
            }
            else
            {
                lblAboutDshStatus.Caption = UpdateService.Checked ? "检查失败（可能离线）" : "尚未检查";
                lblAboutDshStatus.CustomColor = Color.Empty;
            }
            lblAboutDshStatus.Invalidate();

            string cur = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            lblAboutMgrVer.Caption = "v" + cur;
            lblAboutMgrVer.Invalidate();
            if (UpdateService.ManagerLatest.Length > 0)
            {
                if (UpdateService.CompareVersions(UpdateService.ManagerLatest, cur) > 0)
                {
                    lblAboutMgrStatus.Caption = "发现新版本 v" + UpdateService.ManagerLatest + " → 点击「下载并更新」";
                    lblAboutMgrStatus.CustomColor = T.Warn;
                    btnMgrUpdate.Enabled = true;
                }
                else
                {
                    lblAboutMgrStatus.Caption = "已是最新版本";
                    lblAboutMgrStatus.CustomColor = Color.Empty;
                    btnMgrUpdate.Enabled = false;
                }
            }
            else
            {
                lblAboutMgrStatus.Caption = UpdateService.Checked ? "检查失败（可能离线）" : "尚未检查";
                lblAboutMgrStatus.CustomColor = Color.Empty;
                btnMgrUpdate.Enabled = false;
            }
            lblAboutMgrStatus.Invalidate();

            // 运行环境
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            bool homeExplicit = !string.IsNullOrEmpty(home);
            if (!homeExplicit) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            lblEnvInfo.Text = "node " + (DshService.NodeVersion.Length > 0 ? DshService.NodeVersion : "未知")
                + "    dsh " + (localDsh != "未知" ? localDsh : "未知")
                + "    DSH_HOME: " + home + (homeExplicit ? "" : "（默认）") + "\n"
                + "应用目录: " + Settings.AppDir + "\n"
                + "日志目录: " + Settings.LogsDir;

            // 一键升级可用性：dsh 可升级（npm 全局安装 / npx 缓存安装）且确实发现新版才可点；
            // 已是最新 / 检查失败 / 版本未知 → 禁用（避免白跑一次安装）
            bool canUpgrade = DshUpgradeable() && latestUp;
            btnDshUpgrade.Enabled = canUpgrade;
            btnDshUpgrade.Invalidate();
            // 「升级到预览版」：仅当存在高于本地的 next 预览版时可点
            bool canNext = DshUpgradeable() && nextUp;
            btnDshNext.Visible = canNext;
            btnDshNext.Enabled = canNext;
            btnDshNext.Invalidate();
        }

        // dsh 是否可一键升级：按当前生效入口判断——npm 全局安装 或 npx 缓存安装均可
        // （全局升级用 npm install -g，升级后管理器自动改指到全局路径）；
        // 源码仓库/自定义等非标准路径则引导手动更新，不做一键升级
        static bool DshUpgradeable()
        {
            string bin = DshService.BinJs;
            if (bin.Length == 0) bin = Settings.Data.AutoDshPath;
            if (bin.Length == 0) return true; // 尚无路径：允许安装/升级
            bool npxCache = bin.IndexOf("_npx", StringComparison.OrdinalIgnoreCase) >= 0;
            bool npmGlobal = bin.IndexOf("\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js", StringComparison.OrdinalIgnoreCase) >= 0;
            return npxCache || npmGlobal;
        }

        // 手动检查 dsh + 管理器更新
        void CheckUpdates()
        {
            if (aboutBusy) return;
            aboutBusy = true;
            if (lblAboutDshStatus != null) { lblAboutDshStatus.Caption = "正在检查…"; lblAboutDshStatus.CustomColor = Color.Empty; lblAboutDshStatus.Invalidate(); }
            if (lblAboutMgrStatus != null) { lblAboutMgrStatus.Caption = "正在检查…"; lblAboutMgrStatus.CustomColor = Color.Empty; lblAboutMgrStatus.Invalidate(); }
            UpdateService.CheckAllAsync(delegate
            {
                SafeInvoke(delegate { aboutBusy = false; RefreshAbout(); });
            });
        }

        // 启动时静默检查一次：有更新弹托盘气泡
        void AutoCheckUpdates()
        {
            UpdateService.CheckAllAsync(delegate
            {
                SafeInvoke(delegate
                {
                    RefreshAbout();
                    string msg = "";
                    string localDsh = DshService.DshVersion;
                    if (UpdateService.DshLatest.Length > 0 && localDsh.Length > 0 &&
                        UpdateService.CompareVersions(UpdateService.DshLatest, localDsh) > 0)
                        msg = "dsh 新版本 " + UpdateService.DshLatest + " 可用";
                    string cur = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                    if (UpdateService.ManagerLatest.Length > 0 && UpdateService.CompareVersions(UpdateService.ManagerLatest, cur) > 0)
                        msg = (msg.Length > 0 ? msg + "；" : "") + "管理器新版本 v" + UpdateService.ManagerLatest + " 可用";
                    if (msg.Length > 0)
                    {
                        try { tray.ShowBalloonTip(5000, "发现更新", msg + "\n到「关于」页查看详情。", ToolTipIcon.Info); } catch { }
                    }
                });
            });
        }

        // 一键升级 dsh（targetVersion：目标版本；trackLabel：按钮/弹窗文案，如"一键升级 dsh"/"升级到预览版"）
        // 安全原则：若 dsh 正在运行，先停止（绝不覆盖运行中的文件），升级完成后自动重启。
        void UpgradeDsh(PillButton btn, string targetVersion, string trackLabel)
        {
            if (!DshUpgradeable())
            {
                MessageBox.Show(this, "当前 dsh 来自手动指定/自定义路径，无法一键升级。\n请到「诊断」页查看 dsh 入口路径并自行更新。", trackLabel, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // 兜底：目标版本不高于本地时不白跑安装
            string localDsh = DshService.DshVersion;
            if (targetVersion.Length > 0 && localDsh.Length > 0 &&
                UpdateService.CompareVersions(targetVersion, localDsh) <= 0)
            {
                MessageBox.Show(this, "dsh 已是最新版本（" + localDsh + "），无需升级。", trackLabel, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // 升级会替换 dsh 程序文件：正在运行的实例必须先停止，完成后自动重启
            List<InstanceRuntime> toRestart = new List<InstanceRuntime>();
            foreach (InstanceRuntime rt in runtimes)
            {
                if (rt.State == SvcState.Running || rt.State == SvcState.Starting)
                {
                    if (MessageBox.Show(this,
                        "检测到 dsh 服务正在运行（PID " + rt.Pid + "）。\n升级将替换 dsh 程序文件，为避免破坏正在运行的服务，会先停止服务，升级完成后自动重启。\n\n继续吗？",
                        trackLabel, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return;
                    toRestart.Add(rt);
                    break;
                }
            }
            // 停止前打上 Busy + ManualStopped 标记：阻止看门狗在安装期间把服务自动拉起
            // （否则会覆盖运行中的文件，造成损坏）；安装完成后由 InstallDsh 清除并自动重启
            foreach (InstanceRuntime rt in toRestart)
            {
                rt.Busy = true;
                rt.Cfg.ManualStopped = true;
                DshService.Stop(rt, false);
            }
            UpdateSelectedUi();
            InstallDsh(btn, trackLabel, targetVersion, toRestart);
        }

        // 管理器半自动自更新：下载便携包 → 校验 → 解压 → update.bat 替换 → 自动重启
        void UpdateManager(PillButton btn)
        {
            if (aboutBusy) return;
            if (UpdateService.ManagerLatestUrl.Length == 0)
            {
                CheckUpdates();
                MessageBox.Show(this, "正在检查更新，请稍后在「关于」页查看结果。", "更新管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string cur = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            if (UpdateService.ManagerLatest.Length == 0 || UpdateService.CompareVersions(UpdateService.ManagerLatest, cur) <= 0)
            {
                MessageBox.Show(this, "当前已是最新版本。", "更新管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // 安装版：不自动替换（避免破坏安装器/卸载入口），引导走 Setup 升级
            if (IsInstalledVersion())
            {
                MessageBox.Show(this, "检测到当前为安装版安装。为避免破坏安装器与卸载功能，请下载 Setup 安装包重新运行安装向导完成升级（配置与日志会保留）。\n\n即将打开下载页…",
                    "更新管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenUrl("https://github.com/wuxingyuyouxing/DeepSeek-Harness-Manager/releases/latest");
                return;
            }
            if (MessageBox.Show(this, "发现新版本 v" + UpdateService.ManagerLatest + "。\n将下载便携包（约 36MB）并替换当前程序，完成后自动重启管理器。\n\n继续吗？",
                "更新管理器", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            aboutBusy = true;
            btn.Label = "下载中 0%";
            btn.Enabled = false;
            btn.Invalidate();

            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            string tmpDir = Path.Combine(exeDir, ".update-tmp");
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            try { Directory.CreateDirectory(tmpDir); } catch { }
            string zipPath = Path.Combine(tmpDir, "manager.zip");
            string newExe = Path.Combine(tmpDir, "DeepSeek-Harness-Manager.exe");

            WebClient wc = new WebClient();
            wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
            {
                SafeInvoke(delegate { btn.Label = "下载中 " + e.ProgressPercentage + "%"; btn.Invalidate(); });
            };
            wc.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e)
            {
                if (e.Error != null)
                {
                    try { wc.Dispose(); } catch { }
                    SafeInvoke(delegate { FinishUpdateUi(btn, false, "下载失败：" + e.Error.Message); });
                    return;
                }
                // 校验(可能联网) + 解压放后台线程，避免卡 UI
                Task.Run(delegate
                {
                    string err = VerifyAndExtract(zipPath, newExe, tmpDir);
                    SafeInvoke(delegate
                    {
                        try { wc.Dispose(); } catch { }
                        if (err.Length > 0) { FinishUpdateUi(btn, false, err); return; }
                        // 生成 update.bat 并启动，然后退出主程序（bat 等进程退出后替换并重启）
                        WriteUpdateBat(tmpDir, newExe);
                        btn.Label = "更新就绪，即将重启…";
                        btn.Invalidate();
                        try
                        {
                            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + Path.Combine(tmpDir, "update.bat") + "\"")
                            { UseShellExecute = false, CreateNoWindow = true });
                        }
                        catch { }
                        ReallyExit();
                    });
                });
            };
            try { wc.DownloadFileAsync(new Uri(UpdateService.ManagerLatestUrl), zipPath); }
            catch (Exception ex) { FinishUpdateUi(btn, false, "无法开始下载：" + ex.Message); }
        }

        // 下载/更新结束后的按钮与状态复位；err 为空表示成功进入重启
        void FinishUpdateUi(PillButton btn, bool ok, string err)
        {
            aboutBusy = false;
            btn.Enabled = true;
            btn.Label = "下载并更新";
            btn.Invalidate();
            if (err.Length > 0)
            {
                if (lblAboutMgrStatus != null) { lblAboutMgrStatus.Caption = err; lblAboutMgrStatus.Invalidate(); }
                MessageBox.Show(this, err, "更新管理器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 校验下载的 zip（有 checksums.txt 时比对 SHA-256）并解压出主程序
        static string VerifyAndExtract(string zipPath, string newExe, string tmpDir)
        {
            if (UpdateService.ManagerChecksumsUrl.Length > 0)
            {
                string cs = UpdateService.HttpGet(UpdateService.ManagerChecksumsUrl);
                string hash = Sha256(zipPath);
                if (cs.Length > 0 && hash.Length > 0 && cs.IndexOf(hash, StringComparison.OrdinalIgnoreCase) < 0)
                    return "下载文件校验失败（SHA-256 不匹配），已取消更新。";
            }
            try
            {
                using (FileStream fs = File.OpenRead(zipPath))
                using (System.IO.Compression.ZipArchive za = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
                {
                    System.IO.Compression.ZipArchiveEntry entry = null;
                    foreach (System.IO.Compression.ZipArchiveEntry en in za.Entries)
                        if (en.FullName.Equals("DeepSeek-Harness-Manager.exe", StringComparison.OrdinalIgnoreCase)) { entry = en; break; }
                    if (entry == null) return "便携包中未找到主程序，已取消更新。";
                    using (Stream es = entry.Open())
                    using (FileStream ofs = new FileStream(newExe, FileMode.Create, FileAccess.Write))
                        es.CopyTo(ofs);
                }
            }
            catch (Exception ex) { return "解压失败：" + ex.Message; }
            return "";
        }

        // 生成 update.bat：等主进程退出 → copy 覆盖 exe → 启动新 exe → 清理临时目录。
        // 不先 del：copy 失败（杀软锁定/磁盘满）时旧 exe 仍在，可继续运行，绝不"静默卸载"。
        static void WriteUpdateBat(string tmpDir, string newExe)
        {
            string exe = Application.ExecutablePath;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine(":loop");
            sb.AppendLine("tasklist /fi \"imagename eq DeepSeek-Harness-Manager.exe\" 2>nul | find /i \"DeepSeek-Harness-Manager.exe\" >nul");
            sb.AppendLine("if %errorlevel%==0 (");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto loop");
            sb.AppendLine(")");
            sb.AppendLine("copy /y \"" + newExe + "\" \"" + exe + "\" >nul");
            sb.AppendLine("if %errorlevel%==0 (");
            sb.AppendLine("  start \"\" \"" + exe + "\"");
            sb.AppendLine(") else (");
            sb.AppendLine("  echo update copy failed at %date% %time% > \"" + Path.Combine(tmpDir, "update-error.log") + "\"");
            sb.AppendLine("  start \"\" \"" + exe + "\"");
            sb.AppendLine(")");
            sb.AppendLine("rd /s /q \"" + tmpDir + "\"");
            // bat 需与系统代码页一致才能正确解析中文路径
            File.WriteAllText(Path.Combine(tmpDir, "update.bat"), sb.ToString(), Encoding.Default);
        }

        static string Sha256(string file)
        {
            try
            {
                using (FileStream fs = File.OpenRead(file))
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch { return ""; }
        }

        // 当前是否安装版安装（注册表有安装记录且 exe 位于安装目录）
        static bool IsInstalledVersion()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarnessManager"))
                {
                    if (k == null) return false;
                    string loc = (string)k.GetValue("InstallLocation") ?? "";
                    return loc.Length > 0 && Application.ExecutablePath.StartsWith(loc, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(url); } catch { }
        }

        // 通过 npm 全局安装/升级 dsh CLI（后台执行，输出实时流式显示在诊断框，按钮显示已用秒数）
        // 全局安装常驻在 npm 全局目录（npm root -g），用户清理 npm 缓存不会把 dsh 清掉。
        // targetVersion：目标版本号（"" 表示装 npm latest 标签）；toRestart：升级前已停止、完成后需自动重启的实例
        void InstallDsh(PillButton btn, string doneLabel, string targetVersion, List<InstanceRuntime> toRestart)
        {
            if (DshService.NodeExe.Length == 0) DshService.Resolve();
            if (DshService.NodeExe.Length == 0)
            {
                MessageBox.Show(this, "未找到 node.exe，无法安装 dsh。", "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (installingDsh) return;
            installingDsh = true;
            btn.Enabled = false;
            btn.Label = "安装中 0s";
            btn.Invalidate();
            // 显式指定目标版本：@deepseek-ai/dsh 只发 rc 预发布版，npm 的 latest 标签停在较早的 rc，
            // 不指定版本会装 latest（如 rc.7），导致"升级完成但版本没变"。
            string pkg = "@deepseek-ai/dsh";
            if (targetVersion.Length > 0) pkg += "@" + targetVersion;
            AppendDiag("正在安装 " + pkg + "（需要联网，首次约 1-2 分钟）…", Theme.Current.LogWarn);
            AppendDiag("安装进度会实时显示在下方，请耐心等待。", Theme.Current.LogText);

            // 按钮上显示已用秒数，让用户明确知道"正在安装、没有卡死"
            DateTime start = DateTime.Now;
            Timer ticker = new Timer();
            ticker.Interval = 1000;
            ticker.Tick += delegate
            {
                int sec = (int)(DateTime.Now - start).TotalSeconds;
                btn.Label = "安装中 " + sec + "s";
                btn.Invalidate();
            };
            ticker.Start();

            string npm = Path.Combine(Path.GetDirectoryName(DshService.NodeExe), "npm.cmd");
            // npm 全局安装：常驻在 npm 全局目录（npm root -g），清理 npm 缓存不影响 dsh。
            // --prefer-offline 尽量复用本地缓存；--no-audit/--no-fund 减少无谓网络请求，降低卡死概率。
            // 全新安装整棵依赖树（几百个包）在网络不稳时仍可能较慢，故超时放宽到 30 分钟。
            string installCmd = "/c \"" + npm + "\" install -g " + pkg + " --prefer-offline --no-audit --no-fund";
            // cmd /c 包装确保 .cmd 可执行；stdout/stderr 逐行追加到诊断框（实时进度）
            // 整体超时 30 分钟：网络卡死时自动终止安装进程并复位界面，避免永久"安装中"
            DshService.RunStream("cmd.exe", installCmd,
                delegate(string line)
                {
                    string ln = line.Trim();
                    if (ln.Length == 0) return;
                    SafeInvoke(delegate { AppendDiag("  " + ln, Theme.Current.LogText); });
                },
                delegate(string line)
                {
                    string ln = line.Trim();
                    if (ln.Length == 0) return;
                    SafeInvoke(delegate { AppendDiag("  " + ln, Theme.Current.LogWarn); });
                },
                delegate(bool timedOut)
                {
                    SafeInvoke(delegate
                    {
                        try { ticker.Stop(); ticker.Dispose(); } catch { }
                        btn.Enabled = true;
                        btn.Label = doneLabel;
                        btn.Invalidate();
                        installingDsh = false;
                        if (timedOut)
                        {
                            AppendDiag("安装超时（30 分钟），已终止安装进程。请检查网络后重试。", Theme.Current.LogErr);
                            MessageBox.Show(this, "安装超时（30 分钟），已终止安装进程。\n请检查网络后重试。", "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            // 恢复升级前停止的实例（安装未完成，dsh 未被改动，可安全按原版本重启）
                            foreach (InstanceRuntime rt in toRestart)
                            {
                                if (rt == null) continue;
                                rt.Busy = false;
                                rt.Cfg.ManualStopped = false;
                                StartAsync(rt);
                            }
                            return;
                        }
                        // 全局安装后：通过 npm root -g 定位全局 node_modules，把 dsh 路径指向全局安装并持久化
                        // （Resolve 的持久化路径优先，若不清掉/更新旧路径会继续用旧版本）
                        string globalRoot = "";
                        try { globalRoot = DshService.RunCapture(npm, "root -g").Trim(); } catch { }
                        if (globalRoot.Length > 0)
                        {
                            string bin = Path.Combine(globalRoot, "@deepseek-ai", "dsh", "lib", "bin.js");
                            if (File.Exists(bin))
                            {
                                Settings.Data.AutoDshPath = bin;
                                Settings.Data.AutoNodePath = DshService.NodeExe;
                                Settings.Save();
                            }
                        }
                        DshService.Resolve(); // 重新解析并刷新版本
                        RunDiag();
                        if (DshService.BinJs.Length > 0)
                        {
                            MessageBox.Show(this, "dsh 安装成功：" + DshService.BinJs + "\n版本：" + DshService.DshVersion, "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            // 升级前停止的实例自动重启（先清除 Busy/ManualStopped，允许看门狗与启动流程接管）
                            foreach (InstanceRuntime rt in toRestart)
                            {
                                if (rt == null) continue;
                                rt.Busy = false;
                                rt.Cfg.ManualStopped = false;
                                StartAsync(rt);
                            }
                        }
                        else
                            MessageBox.Show(this, "安装可能未成功，请检查网络后重试。", "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                },
                1800000); // 30 分钟整体超时
        }

        void RunDiag()
        {
            if (diagBox == null) return;
            diagBox.Clear();
            // 标准查找失败时，尝试从运行中的实例反向发现（适配源码仓库等安装方式）
            if (DshService.NodeExe.Length == 0 || DshService.BinJs.Length == 0)
            {
                if (selected != null) DshService.DiscoverFromRunning(selected);
            }
            bool ok = DshService.NodeExe.Length > 0 && DshService.BinJs.Length > 0;
            string dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrEmpty(dshHome)) dshHome = "(未设置，默认 ~/.dsh)";
            AppendDiag("环境自检", Theme.Current.Accent);
            AppendDiag("  node  : " + (DshService.NodeExe.Length > 0 ? DshService.NodeExe + "  (" + DshService.NodeVersion + ")" : "未找到 ✗"), ok ? Theme.Current.LogText : Theme.Current.LogErr);
            AppendDiag("  dsh   : " + (DshService.BinJs.Length > 0 ? DshService.BinJs + "  (" + DshService.DshVersion + ")" : "未找到 ✗"), ok ? Theme.Current.LogText : Theme.Current.LogErr);
            AppendDiag("  DSH_HOME: " + dshHome, Theme.Current.LogText);
            AppendDiag("  应用目录: " + Settings.AppDir, Theme.Current.LogText);
            AppendDiag("  日志目录: " + Settings.LogsDir, Theme.Current.LogText);
            AppendDiag("", Theme.Current.LogText);
            if (!ok)
            {
                AppendDiag("提示：可在下方手动指定 node.exe / dsh 入口路径，或点击「一键安装 dsh」（需联网）。", Theme.Current.LogErr);
            }
            if (selected != null)
            {
                AppendDiag("启动命令", Theme.Current.Accent);
                AppendDiag("  dsh web --host " + selected.Cfg.Host + " --port " + selected.Cfg.Port, Theme.Current.LogText);
                if (DshService.SupportsLan())
                {
                    AppendDiag("  局域网访问：dsh web --host 0.0.0.0 --port " + selected.Cfg.Port + " --trusted-host <本机IP>", Theme.Current.LogText);
                }
                else
                {
                    AppendDiag("  局域网访问：暂不可用", Theme.Current.LogErr);
                    AppendDiag("    DSH " + (DshService.DshVersion.Length > 0 ? DshService.DshVersion : "当前版本") + " 出于安全限制（防远程代码执行）拒绝 --host 0.0.0.0，", Theme.Current.LogText);
                    AppendDiag("    服务只能监听 127.0.0.1，其他设备无法访问。", Theme.Current.LogText);
                    AppendDiag("    请关注 DSH 后续版本是否放开 0.0.0.0 绑定；", Theme.Current.LogText);
                    AppendDiag("    如需局域网使用，可用反向代理将 127.0.0.1:" + selected.Cfg.Port + " 暴露到局域网。", Theme.Current.LogText);
                }
            }
        }

        void AppendDiag(string line, Color c)
        {
            if (diagBox == null) return;
            diagBox.SelectionStart = diagBox.TextLength;
            diagBox.SelectionColor = c;
            diagBox.AppendText(line + "\n");
            diagBox.SelectionStart = diagBox.TextLength;
            diagBox.ScrollToCaret();
        }

        // ── 状态轮询 ──
        void PollStates()
        {
            foreach (InstanceRuntime rt in runtimes)
            {
                if (rt.Busy) continue;
                try
                {
                    Task.Run(delegate
                    {
                        SvcState st = DshService.Detect(rt);
                        long mem = 0;
                        DateTime started = DateTime.MinValue;
                        if (st == SvcState.Running && rt.Pid != 0)
                        {
                            // 运行中：反向发现 node/dsh 并持久化（停止后重启仍可用，适配源码/自建安装）
                            if (!rt.DiscTried)
                            {
                                DshService.DiscoverFromRunning(rt);
                            }
                            try
                            {
                                using (Process p = Process.GetProcessById(rt.Pid))
                                {
                                    mem = p.WorkingSet64 / (1024 * 1024);
                                    started = p.StartTime;
                                }
                            }
                            catch { }
                        }
                        SafeInvoke(delegate
                        {
                            rt.State = st;
                            if (st == SvcState.Running)
                            {
                                rt.MemMb = mem;
                                if (started != DateTime.MinValue) rt.StartedAt = started;
                            }
                            if (st == SvcState.Stopped) rt.MemMb = 0;
                            UpdateSelectedUi();
                        });
                    });
                }
                catch { }
            }
        }

        void SafeInvoke(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { }
        }

        void UpdateSelectedUi()
        {
            if (selected == null) return;
            RefreshSidebar();
            RefreshOverview();
        }

        void RefreshOverview()
        {
            if (selected == null) return;
            Theme T = Theme.Current;
            SvcState st = selected.State;

            string stText = "未知";
            switch (st)
            {
                case SvcState.Running: stText = "运行中"; break;
                case SvcState.Stopped: stText = "已停止"; break;
                case SvcState.Starting: stText = "启动中"; break;
                case SvcState.Occupied: stText = "端口被占用"; break;
                case SvcState.Error: stText = "错误"; break;
            }
            if (headPill != null)
            {
                headPill.State = st;
                headPill.Label = stText + (st == SvcState.Running && selected.Pid != 0 ? " · PID " + selected.Pid : "");
                headPill.FitWidth();
                headPill.Invalidate();
            }

            if (stState != null)
            {
                stState.Value = stText;
                stState.Sub = selected.Busy ? "操作进行中…" : "每 2 秒自动探测";
                stState.ValueColor = st == SvcState.Running ? T.Ok : (st == SvcState.Occupied || st == SvcState.Error ? T.Err : (st == SvcState.Starting ? T.Warn : Color.Empty));
                stState.Invalidate();
            }
            if (stPort != null)
            {
                stPort.Value = selected.Cfg.Host + ":" + selected.Cfg.Port;
                stPort.Sub = "DSH Web 服务";
                stPort.Invalidate();
            }
            if (stProc != null)
            {
                if (st == SvcState.Running && selected.Pid != 0)
                {
                    stProc.Value = "PID " + selected.Pid;
                    stProc.Sub = "内存 " + selected.MemMb + " MB · " + (selected.StartedAt != DateTime.MinValue ? selected.StartedAt.ToString("HH:mm:ss") : "");
                }
                else if (st == SvcState.Occupied && selected.Pid != 0)
                {
                    stProc.Value = "PID " + selected.Pid;
                    stProc.Sub = "由其他进程监听";
                }
                else
                {
                    stProc.Value = "—";
                    stProc.Sub = "无运行进程";
                }
                stProc.Invalidate();
            }
            if (stLan != null)
            {
                if (!DshService.SupportsLan())
                {
                    // DSH 0.1.0-rc 系列安全限制：拒绝 --host 0.0.0.0，局域网绑定不可用
                    stLan.Value = "仅本机可访问";
                    stLan.Sub = "DSH " + (DshService.DshVersion.Length > 0 ? DshService.DshVersion : "当前版本")
                        + " 出于安全限制暂不支持局域网绑定（--host 0.0.0.0 被拒绝），详见「诊断」页";
                    stLan.Invalidate();
                }
                else
                {
                    List<string> ips = new List<string>();
                    try
                    {
                        IPAddress[] adds = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
                        foreach (IPAddress a in adds)
                        {
                            if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)) ips.Add(a.ToString());
                        }
                    }
                    catch { }
                    if (ips.Count > 0)
                    {
                        stLan.Value = "http://" + ips[0] + ":" + selected.Cfg.Port;
                        stLan.Sub = "局域网地址（如有多张网卡见诊断页）· 跨设备需 --trusted-host";
                    }
                    else
                    {
                        stLan.Value = "仅本机可访问";
                        stLan.Sub = "未检测到局域网地址";
                    }
                    stLan.Invalidate();
                }
            }
            if (stEnv != null)
            {
                // 懒加载：首次展示环境信息时解析 node/dsh 版本（约 100-200ms，仅一次）
                if (DshService.NodeExe.Length == 0) DshService.Resolve();
                string home = Environment.GetEnvironmentVariable("DSH_HOME");
                bool homeExplicit = !string.IsNullOrEmpty(home);
                if (!homeExplicit)
                {
                    home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                }
                string url = Environment.GetEnvironmentVariable("DSH_WEB_URL");
                stEnv.Value = home;
                string env = homeExplicit ? "DSH_HOME: 已显式设置" : "DSH_HOME 未显式设置，使用默认目录";
                if (!string.IsNullOrEmpty(url)) env += "  ·  DSH_WEB_URL: " + url;
                if (DshService.NodeExe.Length > 0 && DshService.NodeVersion.Length > 0) env += (env.Length > 0 ? "  ·  " : "") + "node " + DshService.NodeVersion;
                if (DshService.BinJs.Length > 0 && DshService.DshVersion.Length > 0) env += (env.Length > 0 ? "  ·  " : "") + "dsh " + DshService.DshVersion;
                stEnv.Sub = env;
                stEnv.Invalidate();
            }

            bool busy = selected.Busy;
            btnStart.Enabled = !busy && (st != SvcState.Running && st != SvcState.Starting);
            btnStop.Enabled = !busy && (st == SvcState.Running || st == SvcState.Occupied || st == SvcState.Starting);
            btnRestart.Enabled = !busy && st != SvcState.Starting;
            btnBrowser.Enabled = true;
        }

        void RefreshHeadPill()
        {
            if (headPill == null || selected == null) return;
            headPill.State = selected.State;
            string stText = "未知";
            switch (selected.State)
            {
                case SvcState.Running: stText = "运行中"; break;
                case SvcState.Stopped: stText = "已停止"; break;
                case SvcState.Starting: stText = "启动中"; break;
                case SvcState.Occupied: stText = "端口被占用"; break;
                case SvcState.Error: stText = "错误"; break;
            }
            headPill.Label = stText;
            headPill.FitWidth();
            headPill.Invalidate();
        }

        void RefreshSettings()
        {
            if (selected == null) return;
            swAutoOpen.Checked = selected.Cfg.AutoOpenBrowser;
            swWatch.Checked = selected.Cfg.Watchdog;
            swAutostart.Checked = Settings.Data.Autostart;
            swCloseExits.Checked = Settings.Data.CloseExits;
            if (lblTheme != null)
            {
                if (Settings.Data.Theme == "dark") lblTheme.Caption = "深色";
                else if (Settings.Data.Theme == "system") lblTheme.Caption = "跟随系统（" + (Theme.SystemDark() ? "深色" : "浅色") + "）";
                else lblTheme.Caption = "浅色";
                lblTheme.Invalidate();
            }
        }

        // ── 日志 ──
        Dictionary<string, long> logPos = new Dictionary<string, long>();
        Dictionary<string, string> logFile = new Dictionary<string, string>();

        void RefreshLogs()
        {
            if (selected == null || logView == null || !logView.Visible) return;
            string slug = selected.Cfg.Slug;
            string[] files = GetLogFiles(slug);
            foreach (string f in files)
            {
                string key = slug + "|" + Path.GetFileName(f);
                long pos = 0;
                logPos.TryGetValue(key, out pos);
                string prev = "";
                logFile.TryGetValue(key, out prev);
                if (prev != f) { pos = 0; }
                try
                {
                    using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Length < pos) pos = 0;
                        fs.Seek(pos, SeekOrigin.Begin);
                        byte[] buf = new byte[fs.Length - pos];
                        int read = fs.Read(buf, 0, buf.Length);
                        if (read > 0)
                        {
                            string text = Encoding.UTF8.GetString(buf, 0, read);
                            text = Regex.Replace(text, "\x1b\\[[0-9;]*m", "");
                            string[] lines = text.Split('\n');
                            foreach (string ln in lines)
                            {
                                if (ln.Length == 0) continue;
                                string low = ln.ToLowerInvariant();
                                int level = 0;
                                if (low.Contains("error") || low.Contains("fail") || low.Contains("exception")) level = 2;
                                else if (low.Contains("warn")) level = 1;
                                logView.AppendLine(ln.TrimEnd('\r'), level);
                            }
                        }
                        logPos[key] = pos + read;
                        logFile[key] = f;
                    }
                }
                catch { }
            }
        }

        string[] GetLogFiles(string slug)
        {
            List<string> res = new List<string>();
            try
            {
                if (!Directory.Exists(Settings.LogsDir)) return res.ToArray();
                string pat = slug + "-*.out.log";
                res.AddRange(Directory.GetFiles(Settings.LogsDir, pat).OrderBy(x => Directory.GetLastWriteTime(x)));
                if (res.Count == 0)
                    res.AddRange(Directory.GetFiles(Settings.LogsDir, "dsh-web-*.out.log").OrderBy(x => Directory.GetLastWriteTime(x)));
            }
            catch { }
            return res.ToArray();
        }

        // ── 看门狗 ──
        // 只做"崩溃恢复"：仅当服务最近成功运行过又意外退出时才自动重启。
        // 从未成功启动（如环境缺失、手动启动失败）时绝不自动重试，避免弹窗轰炸。
        void Watchdog()
        {
            foreach (InstanceRuntime rt in runtimes)
            {
                if (!rt.Cfg.Watchdog) continue;
                if (rt.Cfg.ManualStopped) continue;
                if (rt.Busy) continue;
                if (rt.State != SvcState.Stopped) continue;
                if (rt.FailCount >= 3) continue; // 连续失败多次，停止自动重试
                if (rt.LastOkStart == default(DateTime)) continue; // 从未成功启动过
                if ((DateTime.Now - rt.LastOkStart).TotalMinutes > 10) continue; // 停止已久，视为手动停止
                StartAsync(rt, true); // 静默重启：失败不弹窗
            }
        }

        // ── 启停 ──
        void StartAsync(InstanceRuntime rt) { StartAsync(rt, false); }

        void StartAsync(InstanceRuntime rt, bool silent)
        {
            if (rt.Busy) return;
            rt.Busy = true;
            rt.Cfg.ManualStopped = false;
            UpdateSelectedUi();
            Task.Run(delegate
            {
                bool ok = DshService.Start(rt, delegate(string s) { });
                DateTime deadline = DateTime.Now.AddSeconds(90);
                SvcState st = SvcState.Starting;
                if (ok)
                {
                    while (DateTime.Now < deadline)
                    {
                        Thread.Sleep(800);
                        st = DshService.Detect(rt);
                        if (st == SvcState.Running) break;
                        if (st == SvcState.Occupied || st == SvcState.Error) break;
                    }
                }
                else st = SvcState.Error;

                SvcState final = st;
                bool okStart = ok;
                SafeInvoke(delegate
                {
                    rt.Busy = false;
                    rt.State = final;
                    if (final == SvcState.Running)
                    {
                        rt.LastOkStart = DateTime.Now; // 记录成功启动时间（看门狗依据）
                        rt.FailCount = 0;
                    }
                    UpdateSelectedUi();
                    if (!okStart)
                    {
                        rt.FailCount++;
                        if (!silent)
                        {
                            // 手动启动失败：抑制看门狗自动重试，直到用户再次手动操作
                            rt.Cfg.ManualStopped = true;
                            MessageBox.Show(this, "启动失败：" + rt.LastError +
                                "\n\n可到「诊断」页查看 node/dsh 状态，或点击「一键安装 dsh」。",
                                "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        // 静默失败（看门狗触发）：只计数，不打扰
                    }
                    else if (final == SvcState.Running)
                    {
                        // 启动成功：明确告知访问地址，避免用户不知道要在浏览器中使用
                        if (rt.Cfg.AutoOpenBrowser)
                        {
                            try { Process.Start(rt.Cfg.Url); } catch { }
                            if (!silent)
                            {
                                try { tray.ShowBalloonTip(4000, "DeepSeek Harness", "服务已启动：" + rt.Cfg.Url + "\n已在浏览器中打开。", ToolTipIcon.Info); } catch { }
                            }
                        }
                        else if (!silent)
                        {
                            try { tray.ShowBalloonTip(4000, "DeepSeek Harness", "服务已启动：" + rt.Cfg.Url + "\n在「概览」页点击「打开浏览器」即可使用。", ToolTipIcon.Info); } catch { }
                        }
                    }
                    else if (final == SvcState.Occupied)
                        MessageBox.Show(this, "端口 " + rt.Cfg.Port + " 已被其他进程占用。", "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
        }

        void StopAsync(InstanceRuntime rt)
        {
            if (rt.Busy) return;
            rt.Busy = true;
            UpdateSelectedUi();
            Task.Run(delegate
            {
                DshService.Stop(rt, true);
                Thread.Sleep(600);
                SvcState st = DshService.Detect(rt);
                SafeInvoke(delegate
                {
                    rt.Busy = false;
                    rt.State = st;
                    UpdateSelectedUi();
                });
            });
        }

        void RestartAsync(InstanceRuntime rt)
        {
            if (rt.Busy) return;
            rt.Busy = true;
            UpdateSelectedUi();
            Task.Run(delegate
            {
                DshService.Stop(rt, false);
                Thread.Sleep(600);
                bool ok = DshService.Start(rt, delegate(string s) { });
                DateTime deadline = DateTime.Now.AddSeconds(90);
                SvcState st = SvcState.Starting;
                if (ok)
                {
                    while (DateTime.Now < deadline)
                    {
                        Thread.Sleep(800);
                        st = DshService.Detect(rt);
                        if (st == SvcState.Running) break;
                    }
                }
                else st = SvcState.Error;
                SvcState final = st;
                SafeInvoke(delegate
                {
                    rt.Busy = false;
                    rt.State = final;
                    UpdateSelectedUi();
                });
            });
        }

        // ── 窗口绘制 ──
        protected override void OnPaint(PaintEventArgs e)
        {
            // 用实色主题底色铺满整个窗口（OnPaint 中绘制，最可靠）：
            // 保证浅色/深色主题明确可见，不受亚克力在部分系统上渲染异常的影响。
            using (SolidBrush b = new SolidBrush(Theme.Current.Page))
                e.Graphics.FillRectangle(b, ClientRectangle);
            base.OnPaint(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate(true);
        }
    }

    // ───────────────────────────────────────────────────────────── 应用上下文（托盘生命周期）
    class AppContext : ApplicationContext
    {
        MainForm form;

        public AppContext(bool minimized)
        {
            form = new MainForm(minimized);
            form.FormClosed += delegate { ExitThread(); };
        }
    }
}
