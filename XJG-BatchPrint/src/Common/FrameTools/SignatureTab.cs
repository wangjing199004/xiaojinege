#nullable disable
#if AUTOCAD
#if ACAD_CORE
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
using CadDatabase = Autodesk.AutoCAD.DatabaseServices.Database;
using CadRuntime = Autodesk.AutoCAD.Runtime;
#else
using AcApp = ZwSoft.ZwCAD.ApplicationServices.Application;
using CadDatabase = ZwSoft.ZwCAD.DatabaseServices.Database;
using CadRuntime = ZwSoft.ZwCAD.Runtime;
#endif
using System;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Collections.Generic;
using CadSignature;

namespace TitleAttributeBatch.Core
{
    internal static class SignatureRuntime
    {
        internal static string DataDirectory
        {
            get
            {
                return Path.Combine(ZwcadBatchPlot.PluginRuntimePaths.BaseDirectory, "SignatureData");
            }
        }
        internal static string Run(Func<string> action)
        {
            string folder = DataDirectory;
            Directory.CreateDirectory(folder);
            string session = GreenStore.Unlock(folder);
            WriteNotice();
            CoreEntry.Initialize(session);
            CoreEntry.PackageBaseDirectory = Path.GetDirectoryName(folder);
            try { PrepareSession(folder, session); return action(); }
            finally { GreenStore.Seal(folder); WriteNotice(); }
        }
        internal static void PrepareSession(string folder, string session)
        {
            Directory.CreateDirectory(session);
            string calibration = Path.Combine(session, "图框定位.txt");
            string seedCalibration = Path.Combine(folder, "Seed", "图框定位.txt");
            if (!File.Exists(calibration) && File.Exists(seedCalibration)) File.Copy(seedCalibration, calibration);
            string signatures = Path.Combine(session, "Signatures");
            string vault = Path.Combine(signatures, ".db");
            string seedVault = Path.Combine(folder, "Seed", "Signatures", ".db");
            if (!Directory.Exists(vault) && Directory.Exists(seedVault))
            {
                Directory.CreateDirectory(vault);
                foreach (string source in Directory.GetFiles(seedVault)) File.Copy(source, Path.Combine(vault, Path.GetFileName(source)), false);
            }
            Directory.CreateDirectory(signatures);
            // Keep migrated absolute paths from writing back into the original Tk project.
            SignatureSettings.SaveCustomDirectory(session, signatures);
        }
        private static void WriteNotice()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc != null && !string.IsNullOrEmpty(GreenStore.LastNotice)) doc.Editor.WriteMessage("\n" + GreenStore.LastNotice);
        }
    }
    internal sealed class SignatureTab : UserControl
    {
        private readonly AttributePanel host;
        private readonly CheckBox clearAll = new CheckBox { Text = "当前图纸清除全部签字（不勾选则框选清除）", AutoSize = true };
        private readonly DataGridView files = new DataGridView();
        private readonly List<Button> buttons = new List<Button>();
        private readonly Label state = new Label { AutoSize = true, Text = "点击“刷新签字状态”查看当前图纸的签字区域。" };
        private readonly TextBox result = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        internal SignatureTab(AttributePanel host)
        {
            this.host = host;
            Dock = DockStyle.Fill;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var setup = ButtonRow(false);
            AddAction(setup, "人员设置", PanelAction.EditPeople);
            AddAction(setup, "图幅标定", PanelAction.FrameCalib);
            var methods = ButtonRow(false);
            AddAction(methods, "单张签字", PanelAction.ManualSign);
            AddAction(methods, "图框签字", PanelAction.FrameSign);
            top.Controls.Add(CreateGroup("预设置", setup), 0, 0);
            top.Controls.Add(CreateGroup("签字方式", methods), 1, 0);
            root.Controls.Add(top, 0, 0);

            var other = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            var actions = ButtonRow(false);
            AddAction(actions, "显示 / 隐藏签字", PanelAction.ToggleHidden);
            AddAction(actions, "清除签字", PanelAction.Clear);
            var refresh = CreateButton("刷新签字状态");
            refresh.Click += delegate { host.RunInCommandContext(delegate { state.Text = SignatureRuntime.Run(Commands.DockedStatus); }); };
            actions.Controls.Add(refresh);
            other.Controls.Add(actions, 0, 0);
            other.Controls.Add(clearAll, 0, 1);
            state.Margin = new Padding(3, 6, 3, 3);
            other.Controls.Add(state, 0, 2);
            root.Controls.Add(CreateGroup("其他", other), 0, 1);

            var multiple = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            multiple.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            multiple.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            multiple.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            var fileButtons = ButtonRow(true);
            AddFileButton(fileButtons, "添加 DWG 文件", AddFiles);
            AddFileButton(fileButtons, "添加已打开图纸", AddOpenFiles);
            AddFileButton(fileButtons, "移除选中文件", delegate {
                if (host.IsRunning) return;
                foreach (DataGridViewRow row in files.SelectedRows) files.Rows.Remove(row);
            });
            AddFileButton(fileButtons, "清空文件列表", delegate { if (!host.IsRunning) files.Rows.Clear(); });
            AddFileButton(fileButtons, "一键签名", delegate { RunFiles(false); });
            AddFileButton(fileButtons, "一键删除签名", delegate { RunFiles(true); });
            // Measure every button once and use the smallest shared size that fits all labels.
            Size buttonSize = Size.Empty;
            foreach (Button button in buttons)
            {
                Size preferred = button.GetPreferredSize(Size.Empty);
                buttonSize.Width = Math.Max(buttonSize.Width, preferred.Width);
                buttonSize.Height = Math.Max(buttonSize.Height, preferred.Height);
            }
            foreach (Button button in buttons) { button.AutoSize = false; button.Size = buttonSize; }
            MinimumSize = new Size((buttonSize.Width + 6) * 4 + 42, 0);
            ZwcadBatchPlot.UiLayout.StyleFrameToolsGrid(files, Font); files.ReadOnly = true;
            files.AllowUserToAddRows = false; files.AllowUserToDeleteRows = false;
            files.RowHeadersVisible = false; files.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            files.Columns.Add(new DataGridViewTextBoxColumn { Name = "FileName", HeaderText = "文件名", Width = 140, SortMode = DataGridViewColumnSortMode.NotSortable });
            files.Columns.Add(new DataGridViewTextBoxColumn { Name = "FilePath", HeaderText = "文件路径", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            files.Columns.Add(new DataGridViewTextBoxColumn { Name = "Result", HeaderText = "处理结果", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            multiple.Controls.Add(fileButtons, 0, 0);
            multiple.Controls.Add(files, 0, 1);
            result.Text = "多图列表支持已打开和未打开的 DWG：已打开图纸修改内存并标记待保存，未打开文件直接保存，不生成备份。\r\n签名处理模型及所有布局中块名含“图框”的图框。";
            multiple.Controls.Add(result, 0, 2);
            var multipleGroup = CreateGroup("多图签名", multiple);
            multipleGroup.AutoSize = false;
            root.Controls.Add(multipleGroup, 0, 2);
            root.SizeChanged += delegate { state.MaximumSize = new Size(Math.Max(200, root.ClientSize.Width - 44), 0); };
            Controls.Add(root);
        }
        private static FlowLayoutPanel ButtonRow(bool wrap)
        { return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = wrap, Margin = new Padding(0) }; }
        private GroupBox CreateGroup(string title, Control content)
        {
            var group = new GroupBox { Text = title, Font = Font, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(7), Margin = new Padding(3, 3, 3, 5) };
            group.Controls.Add(content);
            return group;
        }
        private void AddAction(Control parent, string text, PanelAction action)
        {
            var button = CreateButton(text);
            button.Click += delegate {
                bool all = clearAll.Checked;
                host.RunInCommandContext(delegate {
                    result.Text = "正在执行：" + text + "。需要选择图元时，请到 CAD 绘图区操作。";
                    try
                    {
                        result.Text = SignatureRuntime.Run(delegate { return Commands.ExecuteDocked(action, all); });
                        state.Text = Commands.DockedStatus();
                    }
                    catch (System.Exception ex) { result.Text = "操作未完成：" + ex.Message; throw; }
                });
            };
            parent.Controls.Add(button);
        }
        private Button CreateButton(string text)
        {
            var button = new Button { Text = text, Font = Font, AutoSize = true, Padding = new Padding(4, 2, 4, 2), Margin = new Padding(3), Anchor = AnchorStyles.Top | AnchorStyles.Left };
            buttons.Add(button);
            return button;
        }
        private void AddFileButton(Control parent, string text, Action action)
        {
            var button = CreateButton(text);
            button.Click += delegate { action(); };
            parent.Controls.Add(button);
        }
        private List<string> FilePaths()
        {
            var paths = new List<string>();
            foreach (DataGridViewRow row in files.Rows) paths.Add(Convert.ToString(row.Cells["FilePath"].Value));
            return paths;
        }
        private void AddFiles()
        {
            if (host.IsRunning) return;
            using (var dialog = new System.Windows.Forms.OpenFileDialog { Title = "添加签名操作的 DWG 文件", Filter = "CAD 图纸 (*.dwg)|*.dwg", Multiselect = true, CheckFileExists = true })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                var existing = new HashSet<string>(FilePaths(), StringComparer.OrdinalIgnoreCase);
                foreach (string file in dialog.FileNames)
                {
                    string path = Path.GetFullPath(file);
                    OpenDrawingBatch.Remember(path);
                    if (existing.Add(path)) files.Rows.Add(Path.GetFileName(path), path, "待处理");
                }
            }
            result.Text = "已添加 " + files.Rows.Count + " 个文件。已打开的图纸将更新内存并保持待保存；未打开文件直接保存。签名及删除签名均不生成备份。";
        }
        private void AddOpenFiles()
        {
            if (host.IsRunning) return;
            var existing = new HashSet<string>(FilePaths(), StringComparer.OrdinalIgnoreCase);
            foreach (string path in OpenDrawingBatch.OpenPaths())
                if (existing.Add(path)) files.Rows.Add(Path.GetFileName(path), path, "已打开，待处理");
            result.Text = "已添加当前打开的图纸。点击“一键签名”或“一键删除签名”即可处理，完成后请保存已打开的图纸。";
        }
        private void FileStatus(string path, string message)
        {
            foreach (DataGridViewRow row in files.Rows)
                if (string.Equals(Convert.ToString(row.Cells["FilePath"].Value), path, StringComparison.OrdinalIgnoreCase))
                {
                    row.Cells["Result"].Value = message;
                    row.Cells["Result"].ToolTipText = message;
                    files.FirstDisplayedScrollingRowIndex = row.Index;
                    break;
                }
            files.Refresh();
        }
        private void RunFiles(bool remove)
        {
            host.RunInCommandContext(delegate {
                if (files.Rows.Count == 0) { host.Notify("请先在签名页添加 DWG 文件。"); return; }
                if (remove) ProcessFiles(true);
                else SignatureRuntime.Run(delegate {
                    Commands.ValidateOfflineSigning();
                    ProcessFiles(false);
                    return "批量签名完成";
                });
            });
        }
        private void ProcessFiles(bool remove)
        {
            string operation = remove ? "删除签名" : "签名";
            int saved = 0, updated = 0, skipped = 0, failed = 0;
            foreach (string path in FilePaths())
            {
                FileStatus(path, "正在" + operation + "……");
                try
                {
                    var document = OpenDrawingBatch.Find(path);
                    string message = document != null
                        ? OpenDrawingBatch.Run(document, delegate(CadDatabase db) {
                            int changed;
                            string detail = Commands.ProcessOffline(db, remove, out changed);
                            return new AttributeUpdateResult { Changed = changed > 0, Detail = detail };
                        })
                        : OfflineSignatureUpdater.Update(path, remove);
                    FileStatus(path, message);
                    if (message.StartsWith("已保存")) saved++; else if (message.StartsWith("已更新")) updated++; else skipped++;
                }
                catch (System.Exception ex) { FileStatus(path, "失败：" + ex.Message); failed++; }
            }
            result.Text = string.Format("批量{0}完成：未打开文件已保存 {1} 个；已打开图纸已更新 {2} 个（待保存）；跳过/未修改 {3} 个；失败 {4} 个。不生成备份。", operation, saved, updated, skipped, failed);
        }
    }
}
