using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Win32;

[assembly: AssemblyTitle("出图中心安装程序")]
[assembly: AssemblyVersion("32.0.0.0")]
[assembly: AssemblyFileVersion("32.0.0.0")]

internal static class SetupProgram
{
#if CLEAN_EDITION
    internal const string Edition = "纯净版";
#else
    internal const string Edition = "HP版";
#endif
    internal static RegistryKey UserRegistry = Registry.CurrentUser;
    internal static readonly string[] Platforms = { "AutoCAD2015-2024", "AutoCAD2025-2027", "ZWCAD2025" };
    internal static readonly string[] Loaders = { "AcadBatchPlot.Loader.dll", "AcadBatchPlot.Core.Loader.dll", "BatchPlotter.Loader.dll" };
    [STAThread]
    private static int Main(string[] args)
    {
#if UNINSTALL_ONLY
        if (args.Length == 0) args = new[] { "/uninstall" };
#endif
        try
        {
            if ((args.Length == 4 || args.Length == 5) && args[0] == "/install-elevated")
            {
                if (!Platforms.Contains(args[1]) || !string.Equals(Path.GetFileName(Path.GetFullPath(args[2]).TrimEnd('\\')), args[1], StringComparison.OrdinalIgnoreCase)) throw new IOException("安装路径与平台不匹配。");
                EnsureCadClosed(); UserRegistry = Registry.Users.OpenSubKey(args[3], true);
                if (UserRegistry == null) throw new IOException("无法访问原用户的注册表。");
                SetupEngine.SafePath(args[2], "permission-check"); Directory.CreateDirectory(args[2]);
                var acl = Directory.GetAccessControl(args[2]);
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(args[3]), System.Security.AccessControl.FileSystemRights.Modify, System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                Directory.SetAccessControl(args[2], acl); SetupEngine.InstallSelected(args[1], args[2], true, args.Length == 5 ? DecodeTargets(args[4]) : null); return 0;
            }
            if (args.Length == 2 && args[0] == "/verify")
            {
                if (Directory.Exists(args[1])) throw new IOException("验证目录必须不存在。");
                foreach (string platform in Platforms) SetupEngine.Extract(platform, Path.Combine(args[1], platform));
                File.WriteAllText(Path.Combine(args[1], "PASS.txt"), "PASS: all payload files extracted and SHA256 verified", Encoding.UTF8);
                return 0;
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && (args[0] == "/uninstall-worker" || args[0] == "/uninstall-worker-elevated")) return UninstallWorkflow.RunWorker(args);
            if (args.Length == 1 && args[0] == "/uninstall" || args.Length == 2 && args[0] == "/from-cad")
            {
                if (args[0] == "/from-cad") { int pid; if (!int.TryParse(args[1], out pid) || pid <= 0) throw new IOException("CAD进程参数不正确。"); }
                return UninstallWorkflow.Request(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            }
            Application.Run(new SetupForm()); return 0;
        }
        catch (Exception ex)
        {
            if (args.Length == 2 && args[0] == "/verify") { File.WriteAllText(args[1] + ".error.txt", ex.ToString()); return 1; }
            MessageBox.Show(ex.Message, "出图中心安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1;
        }
    }
    internal static string EncodeTargets(IEnumerable<string> paths) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", paths))); }
    internal static string[] DecodeTargets(string text)
    {
        if (text.Length > 100000) throw new IOException("CAD选择参数过长。");
        var paths = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0) throw new IOException("请至少选择一个CAD版本。");
        return paths;
    }
    internal static void EnsureCadClosed()
    {
        foreach (string name in new[] { "acad", "zwcad", "accoreconsole" })
            if (Process.GetProcessesByName(name).Length != 0) throw new InvalidOperationException("请关闭 CAD 后再安装或卸载，避免文件被占用。");
    }
}

internal sealed class SetupForm : Form
{
    private sealed class Choice
    {
        internal string Platform; internal CadStartupRegistration.Target Target;
        public override string ToString() { return Target.Name + "  [" + Platform + "]"; }
    }
    private readonly CheckedListBox versions = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly TextBox location = new TextBox { Dock = DockStyle.Fill };
    private readonly Button install = new Button { Text = "安装", AutoSize = true };
    private readonly Label state = new Label { AutoSize = true, MaximumSize = new Size(610, 0) };
    private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Visible = false, Style = ProgressBarStyle.Marquee };
    private readonly Button browse = new Button { Text = "浏览…", AutoSize = true };
    private readonly Button selectAll = new Button { Text = "全选", AutoSize = true };
    private readonly Button selectNone = new Button { Text = "取消全选", AutoSize = true };
    private bool busy;
    private bool installed;
    internal SetupForm()
    {
        Text = "出图中心 " + SetupProgram.Edition + " 安装程序 · v32";
        Font = new Font("Microsoft YaHei UI", 10); ClientSize = new Size(720, 590);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 3, RowCount = 9 };
        for (int row = 0; row < 9; row++) layout.RowStyles.Add(new RowStyle(row == 3 ? SizeType.Percent : SizeType.AutoSize, row == 3 ? 100 : 0));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label { Text = "出图中心 · " + SetupProgram.Edition, AutoSize = true, Font = new Font(Font.FontFamily, 17, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) };
        layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 3);
        string description = SetupProgram.Edition == "HP版" ? "包含全部功能、HP目录及内嵌标准图框。" : "保留通用功能；不包含HP目录、标准图框插入及其模板资源。";
        var info = new Label { Text = description + "\r\n默认勾选全部已安装的兼容CAD；取消勾选的版本本次不安装。", AutoSize = true, MaximumSize = new Size(670, 0), Margin = new Padding(0, 0, 0, 12) };
        layout.Controls.Add(info, 0, 1); layout.SetColumnSpan(info, 3);
        layout.Controls.Add(new Label { Text = "CAD 版本", AutoSize = true }, 0, 2);
        var selection = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        selection.Controls.Add(selectAll); selection.Controls.Add(selectNone); layout.Controls.Add(selection, 1, 2); layout.SetColumnSpan(selection, 2);
        layout.Controls.Add(versions, 0, 3); layout.SetColumnSpan(versions, 3);
        location.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DrawingCenter");
        layout.Controls.Add(new Label { Text = "安装位置", AutoSize = true }, 0, 4); layout.Controls.Add(location, 1, 4); layout.Controls.Add(browse, 2, 4);
        browse.Click += delegate { using (var dialog = new FolderBrowserDialog { Description = "选择安装父文件夹（自动建立对应平台子文件夹）", SelectedPath = location.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) location.Text = dialog.SelectedPath; };
        var note = new Label { Text = "同一平台共用一份程序文件；升级沿用该平台已有安装目录，已有配置保留。", AutoSize = true, MaximumSize = new Size(670, 0), Margin = new Padding(0, 8, 0, 6) };
        layout.Controls.Add(note, 0, 5); layout.SetColumnSpan(note, 3);
        layout.Controls.Add(state, 0, 6); layout.SetColumnSpan(state, 3);
        layout.Controls.Add(progress, 0, 7); layout.SetColumnSpan(progress, 3);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var close = new Button { Text = "关闭", AutoSize = true }; close.Click += delegate { Close(); }; footer.Controls.Add(close); footer.Controls.Add(install);
        layout.Controls.Add(footer, 0, 8); layout.SetColumnSpan(footer, 3);
        Controls.Add(layout); install.Click += Install;
        selectAll.Click += delegate { for (int i = 0; i < versions.Items.Count; i++) versions.SetItemChecked(i, true); };
        selectNone.Click += delegate { for (int i = 0; i < versions.Items.Count; i++) versions.SetItemChecked(i, false); };
        foreach (string platform in SetupProgram.Platforms)
            foreach (var target in CadStartupRegistration.Detect(platform, SetupProgram.UserRegistry, Registry.LocalMachine)) versions.Items.Add(new Choice { Platform = platform, Target = target }, true);
        versions.ItemCheck += delegate { BeginInvoke(new Action(UpdateSelection)); };
        UpdateSelection();
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
    }
    private void UpdateSelection()
    {
        if (busy || installed) return;
        int count = versions.CheckedItems.Count;
        install.Enabled = count > 0;
        state.Text = versions.Items.Count == 0 ? "未检测到兼容CAD，请确认已安装64位支持版本。" : "已选择 " + count + " / " + versions.Items.Count + " 个CAD版本，安装后启动所选CAD即可使用。";
    }
    private string InstallFolder(string platform)
    {
        using (var previous = SetupProgram.UserRegistry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DrawingCenter-" + platform))
        {
            string installed = previous == null ? "" : Convert.ToString(previous.GetValue("InstallLocation"));
            if (installed.Length > 0 && Directory.Exists(installed)) return installed;
        }
        return Path.Combine(Path.GetFullPath(location.Text.Trim()), platform);
    }
    private async void Install(object sender, EventArgs e)
    {
        if (busy || installed) return;
        var completed = new List<string>();
        try
        {
            var selected = versions.CheckedItems.Cast<Choice>().ToList();
            if (selected.Count == 0) throw new InvalidOperationException("请至少勾选一个CAD版本。");
            SetupProgram.EnsureCadClosed();
            var plans = selected.GroupBy(choice => choice.Platform).Select(group => new { Platform = group.Key, Root = InstallFolder(group.Key), Paths = group.Select(choice => choice.Target.Path).ToArray(), Names = string.Join("、", group.Select(choice => choice.Target.Name)) }).ToList();
            if (plans.Any(plan => plan.Root.StartsWith("\\", StringComparison.Ordinal))) throw new IOException("请选择本机磁盘目录。");
            busy = true; install.Enabled = versions.Enabled = location.Enabled = browse.Enabled = selectAll.Enabled = selectNone.Enabled = false; progress.Visible = true;
            foreach (var plan in plans)
            {
                state.Text = "正在安装：" + plan.Names;
                bool needsElevation = false;
                try { await Task.Run(delegate { SetupEngine.InstallSelected(plan.Platform, plan.Root, true, plan.Paths); }); }
                catch (UnauthorizedAccessException) { needsElevation = true; }
                if (needsElevation)
                {
                    state.Text = "安装目录需要管理员权限，正在申请Windows授权…";
                    string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                    string arguments = "/install-elevated " + plan.Platform + " \"" + plan.Root + "\" " + sid + " " + SetupProgram.EncodeTargets(plan.Paths);
                    using (var helper = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, arguments) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
                    { await Task.Run(delegate { helper.WaitForExit(); }); if (helper.ExitCode != 0) throw new IOException("管理员安装未完成：" + plan.Names); }
                }
                completed.Add(plan.Names);
            }
            state.Text = "安装完成：" + string.Join("、", completed) + "。请启动所选CAD使用出图中心。";
            CompleteInstallation();
        }
        catch (Exception ex) { state.Text = (completed.Count == 0 ? "安装未完成：" : "已完成：" + string.Join("、", completed) + "。其余安装未完成：") + ex.Message; MessageBox.Show(this, state.Text, "安装未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; progress.Visible = false; if (!installed) { versions.Enabled = location.Enabled = browse.Enabled = selectAll.Enabled = selectNone.Enabled = true; install.Enabled = versions.CheckedItems.Count > 0; } }
    }
    private void CompleteInstallation()
    {
        installed = true;
        install.Visible = browse.Visible = selectAll.Visible = selectNone.Visible = false;
        versions.Enabled = location.Enabled = false;
    }

}

internal static class SetupEngine
{
    private const string Marker = "drawing-center.install.xml";
    private static bool IsPersonal(string relative) { return relative.StartsWith("SignatureData/", StringComparison.OrdinalIgnoreCase) || relative.Equals("人员设置.cspkg", StringComparison.OrdinalIgnoreCase); }
    internal static string SafePath(string root, string relative)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (relative.Contains(":") || relative.Contains("\\") || relative.StartsWith("/")) throw new IOException("安装清单路径非法。");
        string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装清单路径超出目标目录。");
        string parent = Path.GetDirectoryName(result);
        while (parent != null && parent.Length >= root.Length - 1)
        {
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("安装目录不能包含目录链接。");
            parent = Path.GetDirectoryName(parent);
        }
        if (File.Exists(result) && (File.GetAttributes(result) & FileAttributes.ReparsePoint) != 0) throw new IOException("目标文件不能是链接。");
        return result;
    }
    private static string Hash(Stream source) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(source)).Replace("-", ""); }
    internal static void Extract(string platform, string root)
    {
        if (!SetupProgram.Platforms.Contains(platform)) throw new IOException("不支持的 CAD 平台。");
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
        {
            var entry = zip.GetEntry("payload.xml");
            XDocument manifest; using (var xml = entry.Open()) manifest = XDocument.Load(xml);
            foreach (var file in manifest.Root.Elements("file").Where(x => (string)x.Attribute("platform") == platform))
            {
                string relative = (string)file.Attribute("path"); string target = SafePath(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var item = zip.GetEntry(platform + "/" + relative); if (item == null) throw new IOException("安装文件缺失：" + relative);
                using (var source = item.Open()) using (var output = File.Create(target)) source.CopyTo(output);
                using (var check = File.OpenRead(target)) if (Hash(check) != (string)file.Attribute("sha256")) throw new IOException("安装文件校验失败：" + relative);
            }
        }
    }
    internal static void Install(string platform, string root, bool register) { InstallSelected(platform, root, register, null); }
    internal static void InstallSelected(string platform, string root, bool register, string[] targets)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        string stage = Path.Combine(root, ".setup-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(stage, "backup"); var changed = new List<string>();
        bool hadSignatures = Directory.Exists(Path.Combine(root, "SignatureData"));
        CadStartupRegistration startup = null;
        try
        {
            string payload = Path.Combine(stage, "payload"); Extract(platform, payload);
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("uninstall.exe"))
            using (var output = File.Create(Path.Combine(payload, "卸载出图中心.exe"))) resource.CopyTo(output);
            var files = Directory.GetFiles(payload, "*", SearchOption.AllDirectories).Select(p => p.Substring(payload.Length + 1).Replace('\\', '/')).ToList();
            var record = new XDocument(new XElement("installation", new XAttribute("product", "DrawingCenter"), new XAttribute("platform", platform), new XAttribute("edition", SetupProgram.Edition),
                files.Where(p => !IsPersonal(p)).Select(p => new XElement("file", new XAttribute("path", p)))));
            record.Save(Path.Combine(payload, Marker)); files.Add(Marker);
            // 在覆盖任何文件前检查整个更新集合，避免已加载的固定 Loader 导致部分升级。
            foreach (string relative in files)
            {
                string target = SafePath(root, relative);
                if ((hadSignatures && relative.StartsWith("SignatureData/", StringComparison.OrdinalIgnoreCase)) || (IsPersonal(relative) && File.Exists(target))) continue;
                if (File.Exists(target))
                {
                    if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) throw new IOException("目标文件为只读：" + relative);
                    using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
            }
            foreach (string relative in files)
            {
                string target = SafePath(root, relative);
                if ((hadSignatures && relative.StartsWith("SignatureData/", StringComparison.OrdinalIgnoreCase)) || (IsPersonal(relative) && File.Exists(target))) continue;
                if (File.Exists(target)) { string saved = SafePath(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)); File.Copy(target, saved); }
                changed.Add(relative); Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(SafePath(payload, relative), target, true);
            }
            if (register)
            {
                startup = CadStartupRegistration.ApplySelected(platform, root, SetupProgram.UserRegistry, Registry.LocalMachine, targets);
                Register(root, platform);
                startup.Commit();
            }
        }
        catch
        {
            if (startup != null) startup.Dispose();
            foreach (string relative in changed.AsEnumerable().Reverse())
            {
                string saved = SafePath(backup, relative), target = SafePath(root, relative);
                if (File.Exists(saved)) File.Copy(saved, target, true); else if (File.Exists(target)) File.Delete(target);
            }
            throw;
        }
        finally { if (startup != null) startup.Dispose(); if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    private static string UninstallKey(string platform) { return @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DrawingCenter-" + platform; }
    private static void Register(string root, string platform)
    {
        using (var key = SetupProgram.UserRegistry.CreateSubKey(UninstallKey(platform)))
        {
            key.SetValue("DisplayName", "出图中心 " + SetupProgram.Edition + "（" + platform + "）"); key.SetValue("DisplayVersion", "32.0");
            key.SetValue("InstallLocation", root); key.SetValue("UninstallString", "\"" + Path.Combine(root, "卸载出图中心.exe") + "\" /uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }
    internal static void Uninstall(string root, bool unregister)
    {
        var record = XDocument.Load(Path.Combine(root, Marker));
        if ((string)record.Root.Attribute("product") != "DrawingCenter") throw new IOException("安装标记不匹配。");
        string platform = (string)record.Root.Attribute("platform");
        if (!SetupProgram.Platforms.Contains(platform)) throw new IOException("安装平台不匹配。");
        var paths = record.Root.Elements("file").Select(x => (string)x.Attribute("path")).Where(p => !IsPersonal(p)).Select(p => SafePath(root, p)).ToList();
        foreach (string path in paths)
        {
            if (string.Equals(path, Assembly.GetExecutingAssembly().Location, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(path)) File.Delete(path);
        }
        // 只清理由本安装位置注册的自动加载项，保留其他位置和平台的注册。
        if (unregister)
        {
            CadStartupRegistration.Remove(root, SetupProgram.UserRegistry);
            using (var key = SetupProgram.UserRegistry.OpenSubKey(UninstallKey(platform)))
                if (key != null && string.Equals((string)key.GetValue("InstallLocation"), root, StringComparison.OrdinalIgnoreCase))
                    SetupProgram.UserRegistry.DeleteSubKeyTree(UninstallKey(platform), false);
            RemoveAutoload(@"Software\Autodesk\AutoCAD", root, 0);
            RemoveAutoload(@"Software\ZWSOFT\ZWCAD", root, 0);
        }
        File.Delete(Path.Combine(root, Marker));
    }
    private static void RemoveAutoload(string path, string root, int depth)
    {
        if (depth > 5) return;
        using (var parent = SetupProgram.UserRegistry.OpenSubKey(path))
        {
            if (parent == null) return;
            foreach (string child in parent.GetSubKeyNames())
            {
                string candidate = path + "\\" + child;
                if (child == "AcadBatchPlot" || child == "ZwcadBatchPlot")
                {
                    using (var app = SetupProgram.UserRegistry.OpenSubKey(candidate))
                    {
                        string loader = Convert.ToString(app.GetValue("LOADER"));
                        if (loader.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) SetupProgram.UserRegistry.DeleteSubKeyTree(candidate, false);
                    }
                }
                else RemoveAutoload(candidate, root, depth + 1);
            }
        }
    }
}
