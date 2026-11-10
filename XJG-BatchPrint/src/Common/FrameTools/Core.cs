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
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
#else
using ZwSoft.ZwCAD.ApplicationServices;
#endif
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
#endif
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
#else
using ZwSoft.ZwCAD.EditorInput;
#endif
#if AUTOCAD
using Autodesk.AutoCAD.Windows;
#else
using ZwSoft.ZwCAD.Windows;
#endif
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.IO;
namespace TitleAttributeBatch.Core
{
    public static class Entry
    {
        private static AttributePanel panel;
        private static SignatureTab signaturePanel;
        private static Document sourceDocument;
        private static string sourceBlockName;
        private static bool sourceFromFile;
        internal static System.Windows.Window OwnerWindow;
        internal static bool IsBusy { get { return panel != null && panel.IsRunning; } }
        private static void EnsurePanels()
        {
            if (panel != null) return;
            panel = new AttributePanel();
            signaturePanel = new SignatureTab(panel);
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        public static Control CreateAttributeView() { EnsurePanels(); return panel; }
        public static Control CreateSignatureView() { EnsurePanels(); return signaturePanel; }
        public static void Show() { EnsurePanels(); }
        public static void Shutdown()
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            if (panel != null && !panel.IsDisposed) panel.Dispose();
            if (signaturePanel != null && !signaturePanel.IsDisposed) signaturePanel.Dispose();
            panel = null; signaturePanel = null;
            sourceDocument = null; sourceBlockName = null; sourceFromFile = false; OwnerWindow = null;
        }
        private static void OnProcessExit(object sender, EventArgs args)
        { try { TempBackupStore.Session.Clear(); } catch { } }
        internal static void CheckClosedBackups() { }
        private static Document Current() { return AcApp.DocumentManager.MdiActiveDocument; }
        public static void Read()
        {
            Show();
            var doc = Current(); if (doc == null) return;
            doc.Window.Focus();
            var options = new PromptEntityOptions("\n请选择用于读取属性的图框块：");
            options.SetRejectMessage("\n请选择块参照。");
            options.AddAllowedClass(typeof(BlockReference), true);
            var result = doc.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return;
            var values = new List<Field>();
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int duplicates = 0;
            string name;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var block = (BlockReference)tr.GetObject(result.ObjectId, OpenMode.ForRead);
                var definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                if (definition.IsFromExternalReference) { panel.Status("外部参照无法作为属性样板。"); return; }
                name = EffectiveName(block, tr);
                foreach (ObjectId id in block.AttributeCollection)
                {
                    var attribute = tr.GetObject(id, OpenMode.ForRead) as AttributeReference;
                    if (attribute == null || attribute.IsConstant) continue;
                    if (!tags.Add(attribute.Tag)) { duplicates++; continue; }
                    values.Add(new Field { Tag = attribute.Tag, Value = attribute.IsMTextAttribute ? attribute.MTextAttribute.Contents : attribute.TextString });
                }
                tr.Commit();
            }
            if (values.Count == 0) { panel.Status("该图框没有可编辑属性，原有列表已保留。"); return; }
            sourceDocument = doc;
            sourceBlockName = name;
            sourceFromFile = false;
            panel.SetFields(values);
            panel.Status("已读取：" + name + "，共 " + values.Count + " 个字段。请编辑并勾选需要写入的字段。" + (duplicates > 0 ? "重复 TAG 只显示首项。" : ""));
        }
        public static void Apply()
        {
            Show();
            var doc = Current(); if (doc == null) return;
            if (!panel.HasFields || (!sourceFromFile && sourceDocument != doc)) { panel.Notify("请先读取样板属性，再进行批量写入。"); return; }
            var fields = panel.GetSelected();
            if (fields.Count == 0) { panel.Notify("请勾选左侧“写入”列，或点击“全选字段”，再批量写入。"); return; }
            panel.Status("请到图纸中选择目标图框，完成后按回车；按 Esc 取消。");
            doc.Window.Focus();
            var options = new PromptSelectionOptions { MessageForAdding = "\n请选择要批量修改的属性图框，完成后按回车：" };
            var result = doc.Editor.GetSelection(options, new SelectionFilter(new[] { new TypedValue(0, "INSERT") }));
            if (result.Status != PromptStatus.OK) { panel.Status("已取消目标选择，没有写入任何属性。"); return; }
            ApplyTargets(doc, fields, result.Value.GetObjectIds());
        }
        private static string EffectiveName(BlockReference block, Transaction tr)
        {
            ObjectId definitionId = block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord;
            return ZwcadBatchPlot.StandardFrameIdentity.GetCanonicalName((BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead), tr);
        }
        public static void ApplyAll()
        {
            Show();
            var doc = Current(); if (doc == null) return;
            if ((!sourceFromFile && sourceDocument != doc) || string.IsNullOrEmpty(sourceBlockName))
            { panel.Notify("请先读取样板属性，再应用当前全图。"); return; }
            var fields = panel.GetSelected();
            if (fields.Count == 0) { panel.Notify("请先勾选需要应用的字段，或点击“全选字段”。"); return; }
            var targets = new List<ObjectId>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId spaceId in table)
                {
                    var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    if (!space.IsLayout || space.IsFromExternalReference) continue;
                    foreach (ObjectId id in space)
                    {
                        var block = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (block == null || block.AttributeCollection.Count == 0) continue;
                        var definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                        if (definition.IsFromExternalReference) continue;
                        foreach (ObjectId attributeId in block.AttributeCollection)
                        {
                            var attribute = tr.GetObject(attributeId, OpenMode.ForRead) as AttributeReference;
                            if (attribute != null && !attribute.IsConstant && fields.ContainsKey(attribute.Tag))
                            { targets.Add(id); break; }
                        }
                    }
                }
                tr.Commit();
            }
            if (targets.Count == 0) { panel.Notify("当前图纸没有找到包含已勾选属性标记的属性块。"); return; }
            panel.Status("正在按属性标记应用当前全图：" + targets.Count + " 个属性块（模型及全部布局，不限制块名或尺寸）。");
            ApplyTargets(doc, fields, targets.ToArray());
        }
        private static void ApplyTargets(Document doc, Dictionary<string, string> fields, ObjectId[] targets)
        {
            int changedBlocks = 0, changedFields = 0, locked = 0, unchanged = 0, missing = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in targets)
                {
                    var block = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (block == null) continue;
                    var layer = (LayerTableRecord)tr.GetObject(block.LayerId, OpenMode.ForRead);
                    if (layer.IsLocked) { locked++; continue; }
                    var definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                    if (definition.IsFromExternalReference) { unchanged++; continue; }
                    int count = 0;
                    var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId aid in block.AttributeCollection)
                    {
                        var attribute = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                        string value;
                        if (attribute == null || attribute.IsConstant || !fields.TryGetValue(attribute.Tag, out value)) continue;
                        found.Add(attribute.Tag);
                        string current = attribute.IsMTextAttribute ? attribute.MTextAttribute.Contents : attribute.TextString;
                        if (current == value) continue;
                        var attributeLayer = (LayerTableRecord)tr.GetObject(attribute.LayerId, OpenMode.ForRead);
                        if (attributeLayer.IsLocked) throw new InvalidOperationException("属性所在图层已锁定：" + attributeLayer.Name + "。本批次已回滚，请解锁后重试。");
                        attribute.UpgradeOpen();
                        if (attribute.IsMTextAttribute)
                        {
                            using (var text = attribute.MTextAttribute) { text.Contents = value; attribute.MTextAttribute = text; }
                            attribute.UpdateMTextAttribute();
                        }
                        else attribute.TextString = value;
                        count++;
                    }
                    missing += fields.Count - found.Count;
                    if (count > 0) { changedBlocks++; changedFields += count; } else unchanged++;
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            string message = string.Format("完成：修改 {0} 个图框、{1} 个属性；锁定跳过 {2} 个；未变化 {3} 个；缺少字段 {4} 项。可输入 U 撤销本批次。", changedBlocks, changedFields, locked, unchanged, missing);
            panel?.Status(message);
            doc.Editor.WriteMessage("\n" + message);
        }
        public static void ApplyFiles()
        {
            if (string.IsNullOrEmpty(sourceBlockName)) { panel.Notify("请先读取样板图框。"); return; }
            var fields = panel.GetSelected();
            if (fields.Count == 0) { panel.Notify("请先勾选需要更新的属性字段。"); return; }
            var files = panel.FilePaths();
            if (files.Count == 0) { panel.Notify("请先添加需要更新的 DWG 文件。"); return; }
            int saved = 0, updated = 0, skipped = 0, failed = 0;
            foreach (string path in files)
            {
                panel.FileStatus(path, "处理中……");
                try
                {
                    var document = OpenDrawingBatch.Find(path);
                    var result = document != null
                        ? OpenDrawingBatch.Run(document, delegate(Database db) { return OfflineUpdater.UpdateDatabase(db, sourceBlockName, fields); })
                        : OfflineUpdater.Update(path, sourceBlockName, fields);
                    panel.FileStatus(path, result);
                    if (result.StartsWith("已保存")) saved++; else if (result.StartsWith("已更新")) updated++; else skipped++;
                }
                catch (System.Exception ex) { panel.FileStatus(path, "失败：" + ex.Message); failed++; }
            }
            panel.Status(string.Format("完成：未打开文件已保存 {0} 个；已打开图纸已更新 {1} 个（待保存）；跳过/未变化 {2} 个；失败 {3} 个。", saved, updated, skipped, failed));
        }
        internal static void ReadFirstFileSample()
        {
            if (panel.HasFields) return;
            var files = panel.FilePaths();
            if (files.Count == 0) return;
            string path = files[0];
            panel.Status("正在从第一个 DWG 统计属性图框并读取样板：" + Path.GetFileName(path));
            try
            {
                SampleData sample = null;
                var document = OpenDrawingBatch.Find(path);
                if (document != null) using (document.LockDocument()) sample = OfflineUpdater.ReadSample(document.Database);
                if (sample == null)
                {
                    using (var db = new Database(false, true))
                    {
                        db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, null);
                        db.CloseInput(true);
                        sample = OfflineUpdater.ReadSample(db);
                    }
                }
                sourceDocument = null;
                sourceFromFile = true;
                sourceBlockName = sample.BlockName;
                panel.SetFields(sample.Fields);
                panel.FileStatus(path, "待处理（已自动读取样板：" + sample.BlockName + "）");
                panel.Status(string.Format("已从 {0} 自动读取样板：{1}，出现 {2} 个，共 {3} 个字段。请编辑并勾选后更新。", Path.GetFileName(path), sample.BlockName, sample.Count, sample.Fields.Count));
            }
            catch (System.Exception ex)
            {
                panel.FileStatus(path, "样板读取失败：" + ex.Message);
                panel.Notify("第一个 DWG 的样板读取失败：" + ex.Message + " 可更换第一个文件后重新添加，或手动读取样板。");
            }
        }
    }
    internal sealed class Field { public string Tag; public string Value; }
    internal sealed class SampleData { public string BlockName; public int Count; public List<Field> Fields; }
    internal sealed class AttributePanel : UserControl
    {
        private readonly DataGridView grid = new DataGridView();
        private readonly Label status = new Label();
        private bool loadingFields;
        private readonly DataGridView fileGrid = new DataGridView();
        private readonly Button openBackups = new Button { Text = "打开文件备份", AutoSize = true, Enabled = false, Padding = new Padding(6, 3, 6, 3) };
        private readonly System.Windows.Forms.Timer backupTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        public AttributePanel()
        {
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            Dock = DockStyle.Fill;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 155));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            AddButton(buttons, "读取样板图框", delegate { RunInCommandContext(Entry.Read); });
            AddButton(buttons, "框选写入图框", delegate { grid.EndEdit(); RunInCommandContext(Entry.Apply); });
            AddButton(buttons, "应用当前全图", delegate { grid.EndEdit(); RunInCommandContext(Entry.ApplyAll); });
            var tools = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            AddButton(buttons, "全选字段", delegate { Mark(true); });
            AddButton(buttons, "取消全选", delegate { Mark(false); });
            buttons.Controls.Add(new Label { Text = "|", AutoSize = true, Margin = new Padding(8, 9, 8, 0) });
            var quickTools = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            buttons.Controls.Add(quickTools);
            AddButton(quickTools, "一键图名", delegate {
                RunInCommandContext(delegate { ZwcadBatchPlot.OneClickDrawingTitle.Run(AcApp.DocumentManager.MdiActiveDocument); });
            });
            AddButton(quickTools, "图框编号", delegate {
                RunInCommandContext(delegate {
                    Status(SignatureRuntime.Run(delegate { return CadSignature.Commands.ExecuteDocked(CadSignature.PanelAction.FrameNumber, false); }));
                });
            });
            tools.Controls.Add(new Label { Text = "按属性标记匹配；空值会清空目标属性", AutoSize = true, Margin = new Padding(6, 9, 0, 0) });
            ZwcadBatchPlot.UiLayout.StyleFrameToolsGrid(grid, Font);
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Apply", HeaderText = "写入", Width = 50 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tag", HeaderText = "属性标记", ReadOnly = true, Width = 160, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "属性内容（可编辑）", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) {
                if (!loadingFields && e.RowIndex >= 0 && e.ColumnIndex == 2) grid.Rows[e.RowIndex].Cells[0].Value = true;
            };
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            status.Dock = DockStyle.Fill; status.Padding = new Padding(0, 8, 0, 0);
            status.Text = "请先读取样板并勾选字段。“应用当前全图”按属性标记匹配当前图纸模型及所有布局中的属性块，不限制图框块名或尺寸。";
            layout.Controls.Add(buttons, 0, 0); layout.Controls.Add(tools, 0, 1); layout.Controls.Add(grid, 0, 2); layout.Controls.Add(status, 0, 3);
            layout.Controls.Remove(status);
            var fileButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            AddButton(fileButtons, "添加 DWG 文件", delegate { AddFiles(); });
            AddButton(fileButtons, "添加已打开图纸", delegate { AddOpenFiles(); });
            AddButton(fileButtons, "移除选中文件", delegate {
                if (running) return;
                foreach (DataGridViewRow row in fileGrid.SelectedRows) fileGrid.Rows.Remove(row);
            });
            AddButton(fileButtons, "清空文件列表", delegate { if (!running) fileGrid.Rows.Clear(); });
            AddButton(fileButtons, "一键更新文件列表", delegate { grid.EndEdit(); RunInCommandContext(Entry.ApplyFiles); });
            openBackups.Click += delegate {
                RefreshBackupButton();
                if (!openBackups.Enabled) return;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + TempBackupStore.Session.Root + "\"") { UseShellExecute = true }); }
                catch (System.Exception ex) { Notify("无法打开备份文件夹：" + ex.Message); }
            };
            fileButtons.Controls.Add(openBackups);
            ZwcadBatchPlot.UiLayout.StyleFrameToolsGrid(fileGrid, Font); fileGrid.ReadOnly = true;
            fileGrid.AllowUserToAddRows = false; fileGrid.AllowUserToDeleteRows = false;
            fileGrid.RowHeadersVisible = false; fileGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            fileGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FileName", HeaderText = "文件名", Width = 160, SortMode = DataGridViewColumnSortMode.NotSortable });
            fileGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FilePath", HeaderText = "文件路径", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45, SortMode = DataGridViewColumnSortMode.NotSortable });
            fileGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Result", HeaderText = "处理结果 / 备份位置", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 55, SortMode = DataGridViewColumnSortMode.NotSortable });
            layout.Controls.Add(fileButtons, 0, 3); layout.Controls.Add(fileGrid, 0, 4); layout.Controls.Add(status, 0, 5);
            Controls.Add(layout);
            backupTimer.Tick += delegate { Entry.CheckClosedBackups(); RefreshBackupButton(); };
            backupTimer.Start();
        }
        private void RefreshBackupButton()
        { try { openBackups.Enabled = !running && TempBackupStore.Session.HasBackups(); } catch { openBackups.Enabled = false; } }
        protected override void Dispose(bool disposing)
        { if (disposing) { backupTimer.Stop(); backupTimer.Dispose(); } base.Dispose(disposing); }
        private void AddFiles()
        {
            if (running) return;
            using (var dialog = new System.Windows.Forms.OpenFileDialog { Title = "添加待更新的 DWG（支持多选）", Filter = "CAD 图纸 (*.dwg)|*.dwg", Multiselect = true, CheckFileExists = true })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                var existing = new HashSet<string>(FilePaths(), StringComparer.OrdinalIgnoreCase);
                foreach (string file in dialog.FileNames)
                {
                    string path = Path.GetFullPath(file);
                    OpenDrawingBatch.Remember(path);
                    if (existing.Add(path)) fileGrid.Rows.Add(Path.GetFileName(path), path, "待处理");
                }
            }
            Status("文件已添加：已打开图纸更新内存、待保存；未打开文件保存到磁盘并保留最多 3 个临时备份。按样板块名匹配全部布局和模型。");
            if (!HasFields && fileGrid.Rows.Count > 0) RunInCommandContext(Entry.ReadFirstFileSample);
        }
        private void AddOpenFiles()
        {
            if (running) return;
            var existing = new HashSet<string>(FilePaths(), StringComparer.OrdinalIgnoreCase);
            foreach (string path in OpenDrawingBatch.OpenPaths())
                if (existing.Add(path)) fileGrid.Rows.Add(Path.GetFileName(path), path, "已打开，待处理");
            Status("已添加当前打开的图纸；一键更新将直接修改其内存内容，完成后请保存。");
            if (!HasFields && fileGrid.Rows.Count > 0) RunInCommandContext(Entry.ReadFirstFileSample);
        }
        internal List<string> FilePaths()
        {
            var result = new List<string>();
            foreach (DataGridViewRow row in fileGrid.Rows) result.Add(Convert.ToString(row.Cells["FilePath"].Value));
            return result;
        }
        internal void FileStatus(string path, string message)
        {
            foreach (DataGridViewRow row in fileGrid.Rows)
                if (string.Equals(Convert.ToString(row.Cells["FilePath"].Value), path, StringComparison.OrdinalIgnoreCase))
                { row.Cells["Result"].Value = message; row.Cells["Result"].ToolTipText = message; fileGrid.FirstDisplayedScrollingRowIndex = row.Index; break; }
            fileGrid.Refresh();
            RefreshBackupButton();
        }
        private static void AddButton(Control parent, string text, EventHandler click)
        { var button = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 3, 6, 3) }; button.Click += click; parent.Controls.Add(button); }
        private bool running;
        internal bool IsRunning { get { return running; } }
        internal bool HasFields { get { return grid.Rows.Count > 0; } }
        internal void RunInCommandContext(Action action)
        {
            if (running) { Status("正在执行操作，请先完成图纸中的选择或按 Esc 取消。"); return; }
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) { Notify("请先打开一张图纸。"); return; }
            if (!doc.Editor.IsQuiescent) { Notify("CAD 正在执行其他命令，请先按 Esc 结束后重试。"); return; }
            running = true;
            try
            {
                ZwcadBatchPlot.PendingLibraryInput.Queue(Entry.OwnerWindow, delegate {
                    try { action(); }
                    catch (System.Exception ex) { Status("操作未完成：" + ex.Message); doc.Editor.WriteMessage("\n图框工具：" + ex); }
                    finally { running = false; RefreshBackupButton(); }
                }, null, doc, "ZBP_PANEL_INPUT", onFinished: delegate { running = false; if (!IsDisposed) RefreshBackupButton(); });
            }
            catch (System.Exception ex) { running = false; Notify("无法开始操作：" + ex.Message); }
        }
        private void Mark(bool value) { grid.EndEdit(); foreach (DataGridViewRow row in grid.Rows) row.Cells[0].Value = value; }
        internal void SetFields(List<Field> fields)
        {
            loadingFields = true;
            try { grid.Rows.Clear(); foreach (var field in fields) grid.Rows.Add(false, field.Tag, field.Value); }
            finally { loadingFields = false; }
        }
        internal Dictionary<string, string> GetSelected()
        {
            grid.EndEdit();
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in grid.Rows)
                if (Convert.ToBoolean(row.Cells[0].Value)) fields[Convert.ToString(row.Cells[1].Value)] = Convert.ToString(row.Cells[2].Value);
            return fields;
        }
        internal void Status(string message) { status.Text = message; }
        internal void Notify(string message) { Status(message); MessageBox.Show(message, "属性图框批量修改", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}
