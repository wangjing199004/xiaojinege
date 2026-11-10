using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Win32;

internal static class UninstallWorkflow
{
    internal static bool HasRunningCad()
    {
        foreach (string name in new[] { "acad", "zwcad", "accoreconsole" })
        {
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Length != 0;
            foreach (var process in processes) process.Dispose();
            if (running) return true;
        }
        return false;
    }
    internal static void ValidateRoot(string root)
    {
        string marker = SetupEngine.SafePath(root, "drawing-center.install.xml");
        var document = XDocument.Load(marker);
        if ((string)document.Root.Attribute("product") != "DrawingCenter" || !SetupProgram.Platforms.Contains((string)document.Root.Attribute("platform"))) throw new IOException("未找到有效的出图中心 EXE 安装记录。");
    }
    internal static int Request(string root)
    {
        ValidateRoot(root);
        if (MessageBox.Show("卸载出图中心的程序文件？个人配置及签名数据会保留。\r\n\r\n请先保存图纸并关闭所有 CAD，关闭后将自动继续卸载。", "卸载出图中心", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
        if (HasRunningCad()) using (var wait = new WaitForCadForm()) if (wait.ShowDialog() != DialogResult.OK) return 0;
        // 在临时位置继续卸载，待本进程退出后可一并删除安装目录内的卸载EXE。
        string temporary = Path.Combine(Path.GetTempPath(), "DrawingCenter-Uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        string worker = Path.Combine(temporary, "uninstall-worker.exe");
        File.Copy(typeof(UninstallWorkflow).Assembly.Location, worker);
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
        Process.Start(new ProcessStartInfo(worker, "/uninstall-worker \"" + root + "\" " + Process.GetCurrentProcess().Id + " " + sid) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        return 0;
    }
    internal static int RunWorker(string[] args)
    {
        bool elevated = args[0] == "/uninstall-worker-elevated";
        if (args.Length != (elevated ? 3 : 4)) throw new IOException("卸载参数不正确。");
        string root = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
        ValidateRoot(root);
        if (!elevated)
        {
            int parentId; if (!int.TryParse(args[2], out parentId) || parentId <= 0 || parentId == Process.GetCurrentProcess().Id) throw new IOException("卸载进程参数不正确。");
            try { using (var parent = Process.GetProcessById(parentId)) if (!parent.WaitForExit(30000)) throw new IOException("卸载程序尚未释放，请稍后重试。"); }
            catch (ArgumentException) { } // 原启动进程已退出。
        }
        string sid = args[elevated ? 2 : 3];
        SetupProgram.EnsureCadClosed();
        try
        {
            SetupProgram.UserRegistry = Registry.Users.OpenSubKey(sid, true);
            if (SetupProgram.UserRegistry == null) throw new IOException("无法访问安装用户的注册表。");
            SetupEngine.Uninstall(root, true);
        }
        catch (UnauthorizedAccessException)
        {
            if (elevated) throw;
            using (var helper = Process.Start(new ProcessStartInfo(typeof(UninstallWorkflow).Assembly.Location, "/uninstall-worker-elevated \"" + root + "\" " + sid) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
            { helper.WaitForExit(); if (helper.ExitCode != 0) return helper.ExitCode; }
            return 0;
        }
        MessageBox.Show("出图中心已卸载。个人配置和签名数据已保留。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}

internal sealed class WaitForCadForm : Form
{
    private readonly Timer timer = new Timer { Interval = 700 };
    internal WaitForCadForm()
    {
        Text = "卸载出图中心 · 等待 CAD 关闭";
        Font = new Font("Microsoft YaHei UI", 10); ClientSize = new Size(500, 170);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        var label = new Label { Dock = DockStyle.Fill, Padding = new Padding(20), Text = "请保存图纸并关闭所有 CAD 窗口。\r\n关闭后会自动继续卸载，请保留此窗口。\r\n\r\n不会强制关闭 CAD，也不会删除你的图纸。" };
        var cancel = new Button { Text = "取消卸载", Dock = DockStyle.Bottom, Height = 36 };
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(label); Controls.Add(cancel);
        timer.Tick += delegate { Check(); };
        Shown += delegate { timer.Start(); Check(); };
    }
    private void Check() { if (!UninstallWorkflow.HasRunningCad()) { timer.Stop(); DialogResult = DialogResult.OK; Close(); } }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}
