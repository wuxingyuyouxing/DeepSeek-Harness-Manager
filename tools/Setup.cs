// DeepSeek Harness Manager 安装器
// 用法: Setup.exe             -> 安装向导（payload.zip 作为嵌入资源）
//       Setup.exe --uninstall -> 卸载（由注册表卸载入口调用）
//       Setup.exe --silent <目录> -> 静默安装（自动化/测试）
// 编译: csc /target:winexe /win32icon:DeepSeek-Harness.ico /resource:payload.zip,payload.zip Setup.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("DeepSeek Harness 管理器 安装程序")]
[assembly: AssemblyDescription("DeepSeek Harness 管理器一键安装/升级/卸载")]
[assembly: AssemblyProduct("DeepSeek Harness Manager")]
[assembly: AssemblyCompany("DeepSeek Harness")]
[assembly: AssemblyCopyright("Copyright © 2026 DeepSeek Harness")]
[assembly: AssemblyVersion("1.2.6.0")]
[assembly: AssemblyFileVersion("1.2.6.0")]

namespace SetupApp
{
    static class Program
    {
        const string AppName = "DeepSeek Harness 管理器";
        const string AppExe = "DeepSeek-Harness-Manager.exe";
        const string AppIcon = "DeepSeek-Harness.ico";
        const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarnessManager";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool uninstall = args != null && args.Length > 0 && args[0] == "--uninstall";
            if (uninstall)
            {
                if (MessageBox.Show("确定要卸载 DeepSeek Harness 管理器吗？\n（不会删除已运行的 dsh 服务数据）", "卸载",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    Uninstall();
                    MessageBox.Show("已卸载。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            // 静默安装：Setup.exe --silent <目录>（供自动化/测试）
            if (args != null && args.Length >= 2 && args[0] == "--silent")
            {
                try
                {
                    new InstallForm().InstallTo(args[1]);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("安装失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            Application.Run(new InstallForm());
        }

        static string ReadPreviousInstallDir()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (k != null) return (string)k.GetValue("InstallLocation") ?? "";
                }
            }
            catch { }
            return "";
        }

        static void Uninstall()
        {
            try
            {
                // 读取安装目录
                string dir = "";
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (k != null) dir = (string)k.GetValue("InstallLocation") ?? "";
                }
                // 删除快捷方式
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DeepSeek Harness");
                DeleteFile(Path.Combine(desktop, AppName + ".lnk"));
                DeleteFile(Path.Combine(startMenu, AppName + ".lnk"));
                DeleteFile(Path.Combine(startMenu, "卸载.lnk"));
                try { if (Directory.Exists(startMenu)) Directory.Delete(startMenu, true); } catch { }
                // 删除安装目录：先校验确实是本程序安装目录（含主程序、非盘符根/系统目录），
                // 防止注册表 InstallLocation 被改写后误删系统目录；删除失败（如 dsh 服务占用文件）要如实反馈
                if (dir.Length > 0)
                {
                    bool valid = Directory.Exists(dir)
                        && File.Exists(Path.Combine(dir, AppExe))
                        && !IsDriveRoot(dir);
                    if (valid)
                    {
                        try { Directory.Delete(dir, true); }
                        catch (Exception ex)
                        {
                            MessageBox.Show("安装目录删除失败：" + ex.Message + "\n请先停止正在运行的 dsh 服务（或管理器），然后重试卸载。",
                                "卸载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return; // 保留注册表入口，便于重试
                        }
                    }
                }
                // 删除注册表
                try { Registry.CurrentUser.DeleteSubKeyTree(RegKey, false); } catch { }
            }
            catch { }
        }

        // 判断路径是否为盘符根（C:\ 等），卸载防护用
        static bool IsDriveRoot(string dir)
        {
            try
            {
                string r = Path.GetPathRoot(dir ?? "");
                if (string.IsNullOrEmpty(r)) return false;
                return string.Equals(r.TrimEnd('\\', '/'), dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static void DeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        class InstallForm : Form
        {
            TextBox tbPath;
            Button btnBrowse, btnInstall, btnExit;
            ProgressBar bar;
            Label lblStatus, lblHeader, lblSub;
            bool installing;
            bool done;   // 安装是否已成功完成（完成后点击走 Finish）
            string installDir;

            public InstallForm()
            {
                Text = "安装 " + AppName;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(480, 300);
                BackColor = Color.FromArgb(246, 247, 251);
                Font = new Font("Segoe UI", 9f);
                try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

                lblHeader = new Label();
                lblHeader.Text = AppName;
                lblHeader.Font = new Font("Segoe UI Semibold", 14f);
                lblHeader.Location = new Point(24, 22);
                lblHeader.AutoSize = true;
                Controls.Add(lblHeader);

                lblSub = new Label();
                lblSub.Text = "一键管理 DeepSeek Harness（dsh web）服务\n安装将自带到便携 Node.js，无需单独安装";
                lblSub.ForeColor = Color.FromArgb(120, 128, 144);
                lblSub.Location = new Point(24, 56);
                lblSub.AutoSize = true;
                Controls.Add(lblSub);

                Label lDir = new Label();
                lDir.Text = "安装目录：";
                lDir.Location = new Point(24, 118);
                lDir.AutoSize = true;
                Controls.Add(lDir);

                tbPath = new TextBox();
                // 自动匹配上次安装目录（升级安装）：从注册表读取，找不到才用默认
                string prev = ReadPreviousInstallDir();
                string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DeepSeek Harness Manager");
                tbPath.Text = (prev.Length > 0 && Directory.Exists(prev)) ? prev : defaultDir;
                tbPath.Location = new Point(24, 142);
                tbPath.Size = new Size(320, 24);
                Controls.Add(tbPath);

                btnBrowse = new Button();
                btnBrowse.Text = "浏览…";
                btnBrowse.Location = new Point(352, 140);
                btnBrowse.Size = new Size(76, 26);
                btnBrowse.Click += delegate
                {
                    using (FolderBrowserDialog d = new FolderBrowserDialog())
                    {
                        d.Description = "选择安装目录";
                        if (d.ShowDialog(this) == DialogResult.OK) tbPath.Text = d.SelectedPath;
                    }
                };
                Controls.Add(btnBrowse);

                bar = new ProgressBar();
                bar.Location = new Point(24, 196);
                bar.Size = new Size(404, 18);
                bar.Visible = false;
                Controls.Add(bar);

                lblStatus = new Label();
                lblStatus.ForeColor = Color.FromArgb(120, 128, 144);
                lblStatus.Location = new Point(24, 220);
                lblStatus.AutoSize = true;
                Controls.Add(lblStatus);

                btnInstall = new Button();
                btnInstall.Text = "安装";
                btnInstall.BackColor = Color.FromArgb(77, 107, 254);
                btnInstall.ForeColor = Color.White;
                btnInstall.FlatStyle = FlatStyle.Flat;
                btnInstall.FlatAppearance.BorderSize = 0;
                btnInstall.Location = new Point(288, 256);
                btnInstall.Size = new Size(92, 30);
                // 单一 Click 处理器：安装完成后进入 Finish（不再用匿名委托 -= / += 切换，避免移除的是新实例）
                btnInstall.Click += delegate { if (done) Finish(); else BeginInstall(); };
                Controls.Add(btnInstall);

                // 检测到已有安装时提示"升级"，并明确保留配置/日志
                if (prev.Length > 0 && Directory.Exists(prev))
                {
                    lblSub.Text = "检测到已有安装，将覆盖升级到：" + prev + "\n（配置与日志会保留，服务数据不受影响）";
                    Text = "升级 " + AppName;
                    btnInstall.Text = "升级";
                }

                btnExit = new Button();
                btnExit.Text = "退出";
                btnExit.Location = new Point(388, 256);
                btnExit.Size = new Size(64, 30);
                btnExit.Click += delegate { Close(); };
                Controls.Add(btnExit);
            }

            void BeginInstall()
            {
                if (installing) return;
                installDir = tbPath.Text.Trim();
                if (installDir.Length == 0) installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DeepSeek Harness Manager");
                // 程序正在运行时禁止覆盖（exe 被占用会导致安装失败）
                Process[] running = Process.GetProcessesByName("DeepSeek-Harness-Manager");
                if (running.Length > 0)
                {
                    MessageBox.Show(this,
                        "检测到 DeepSeek Harness 管理器正在运行。\n请先退出程序（右键托盘图标 → 退出），再继续安装。",
                        "请先关闭程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                installing = true;
                btnInstall.Enabled = false;
                btnBrowse.Enabled = false;
                bar.Visible = true;
                bar.Value = 0;
                lblStatus.Text = "正在解压…";
                Application.DoEvents();
                try
                {
                    InstallWorker();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "安装失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    installing = false;
                    btnInstall.Enabled = true;
                    btnBrowse.Enabled = true;
                    bar.Visible = false;
                    return;
                }
                lblStatus.Text = "安装完成！";
                btnInstall.Text = "完成";
                btnInstall.Enabled = true;
                done = true; // 后续点击走 Finish()
            }

            // 供静默安装调用
            public void InstallTo(string dir)
            {
                installDir = dir;
                InstallWorker();
            }

            void InstallWorker()
            {
                // 读取嵌入的 payload.zip
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                {
                    if (s == null) throw new Exception("未找到内置安装包（payload.zip）。");
                    Directory.CreateDirectory(installDir);
                    using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read))
                    {
                        List<ZipArchiveEntry> entries = new List<ZipArchiveEntry>(zip.Entries);
                        int i = 0;
                        foreach (ZipArchiveEntry e in entries)
                        {
                            string target = Path.Combine(installDir, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                            if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) continue;
                            // zip-slip 防护：目标必须位于安装目录之下（拒绝 .. 段/绝对路径/UNC 条目），
                            // payload 虽为构建时自嵌，仍按不信任输入处理
                            string fullInstall = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            if (!Path.GetFullPath(target).StartsWith(fullInstall, StringComparison.OrdinalIgnoreCase))
                                throw new Exception("安装包包含非法路径条目：" + e.FullName);
                            string d = Path.GetDirectoryName(target);
                            if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
                            using (Stream es = e.Open())
                            using (FileStream fs = new FileStream(target, FileMode.Create))
                            {
                                es.CopyTo(fs);
                            }
                            i++;
                            if (i % 8 == 0)
                            {
                                bar.Value = (int)((double)i / entries.Count * 100);
                                Application.DoEvents();
                            }
                        }
                    }
                }
                bar.Value = 100;
                Application.DoEvents();

                // 快捷方式
                string exePath = Path.Combine(installDir, AppExe);
                string icoPath = Path.Combine(installDir, AppIcon);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DeepSeek Harness");
                try
                {
                    Directory.CreateDirectory(startMenu);
                    var ws = new object();
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic sh = Activator.CreateInstance(t);
                    MakeShortcut(sh, Path.Combine(desktop, AppName + ".lnk"), exePath, installDir, icoPath);
                    MakeShortcut(sh, Path.Combine(startMenu, AppName + ".lnk"), exePath, installDir, icoPath);
                    MakeShortcut(sh, Path.Combine(startMenu, "卸载.lnk"), Assembly.GetExecutingAssembly().Location, installDir, icoPath, "--uninstall");
                }
                catch { }

                // 注册表卸载信息
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayIcon", exePath);
                    k.SetValue("InstallLocation", installDir);
                    k.SetValue("UninstallString", "\"" + Assembly.GetExecutingAssembly().Location + "\" --uninstall");
                    k.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
                    k.SetValue("Publisher", "DeepSeek Harness");
                }
            }

            void MakeShortcut(dynamic sh, string lnkPath, string target, string workDir, string icon, string args = "")
            {
                dynamic lnk = sh.CreateShortcut(lnkPath);
                lnk.TargetPath = target;
                lnk.WorkingDirectory = workDir;
                lnk.IconLocation = icon + ",0";
                if (args.Length > 0) lnk.Arguments = args;
                lnk.Save();
            }

            void Finish()
            {
                bool run = true;
                try
                {
                    run = MessageBox.Show(this, "安装完成！\n是否立即运行 " + AppName + "？", "完成",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                }
                catch { }
                if (run)
                {
                    try { Process.Start(Path.Combine(installDir, AppExe)); } catch { }
                }
                Close();
            }
        }
    }
}
