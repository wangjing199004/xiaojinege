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
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CadSignature
{
    /// <summary>「选择签名笔迹」对话框（5.25.2 新增）。
    ///
    /// **为什么需要它**：5.23.0 起新做的签名全部以「GUID 文件名 + AES 加密」存进隐藏的
    /// `Signatures\.db\`，扩展名是 `.csig`。而「>」原来弹的是 Windows 文件对话框
    /// （筛选 `*.dwg`）——于是**刚做好的笔迹在挑文件时根本看不到**，看起来像"没存上"，
    /// 其实它好好躺在加密库里（对话框底部那行「现有笔迹」是问库要的名单，所以那边看得见）。
    ///
    /// **做法**：不再让用户去文件系统里翻，而是**直接向素材库要名单**
    /// （<see cref="SignatureVault.Names"/>，只读那本加密索引，**一个字节都不解密**），
    /// 连同签名文件夹里残留的明文 `.dwg` 一起列出来给用户挑。选中后只回传**文件名**
    /// （如 `张三.dwg`）——`InsertSignature` 是「库优先、明文回落」，所以照常能用。
    ///
    /// **不产生明文**：整个过程不解密、不写出 dwg；「从文件添加…」也是把外部 dwg
    /// **加密收进库**（原来那套"明文复制进签名文件夹"的做法已去掉，它正好和"不要明文"相悖）。
    ///
    /// 纯 WinForms，不碰图纸，可脱离 CAD 复用界面逻辑。</summary>
    internal sealed class SignaturePicker : Form
    {
        private readonly string _directory;
        private readonly ListView _list = new ListView();
        private readonly Label _hint = new Label();
        private readonly Label _info = new Label();
        private readonly Button _ok = new Button();
        private readonly Button _cancel = new Button();
        private readonly Button _add = new Button();
        private readonly Button _refresh = new Button();
        private readonly Button _delete = new Button();
        private readonly ToolTip _tips = new ToolTip();

        /// <summary>确定时回传的文件名（如 `张三.dwg`）；取消 = null。</summary>
        public string SelectedFileName { get; private set; }

        /// <param name="signatureDirectory">签名文件夹（加密库就在它的 `.db` 子目录里）。</param>
        public SignaturePicker(string signatureDirectory)
        {
            _directory = signatureDirectory ?? "";
            SelectedFileName = null;

            Text = "选择签名笔迹";
            Font = UiFont();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(540, 408);
            BackColor = Color.White;
            Padding = new Padding(14, 12, 14, 12);

            _hint.Dock = DockStyle.Top;
            _hint.Height = 46;
            _hint.ForeColor = Color.FromArgb(90, 90, 90);
            _hint.Text = "下面列出「一键签名」能找到的全部笔迹。加密素材库里的笔迹在文件夹里看不到，只能在这里挑；\r\n"
                       + "选中它不会写出任何明文文件 —— 配置里只记下名字，签名时临时解开、用完即删。";

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.Columns.Add("名称", 250);
            _list.Columns.Add("来源", 240);
            _list.DoubleClick += delegate { Accept(); };
            _list.SelectedIndexChanged += delegate { UpdateInfo(); };

            _info.Dock = DockStyle.Bottom;
            _info.Height = 24;
            _info.ForeColor = Color.Gray;
            _info.TextAlign = ContentAlignment.MiddleLeft;

            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.FlowDirection = FlowDirection.LeftToRight;
            bottom.WrapContents = false;
            bottom.Height = 40;
            bottom.Padding = new Padding(0, 5, 0, 0);

            MakeButton(_add, "从文件添加…", 112);
            _tips.SetToolTip(_add, "挑一个外部的 .dwg 笔迹，加密存进隐藏素材库（不复制明文副本，原文件不动），存完立刻出现在上面的列表里。");
            _add.Click += OnAddFromFile;

            MakeButton(_refresh, "刷新", 68);
            _tips.SetToolTip(_refresh, "重新读一遍素材库与签名文件夹（在别处改了笔迹时用）。");
            _refresh.Click += delegate { Reload(); };

            MakeButton(_delete, "删除笔迹", 88);
            _tips.SetToolTip(_delete, "删除所选笔迹的库内副本；关联的人员需要重新选择笔迹，图纸中已插入的签名不受影响。");
            _delete.Click += OnDeleteSelected;

            MakeButton(_ok, "确定", 78);
            _ok.Click += delegate { Accept(); };
            MakeButton(_cancel, "取消", 78);
            _cancel.Click += delegate { Close(); };

            bottom.Controls.Add(_add);
            bottom.Controls.Add(_refresh);
            bottom.Controls.Add(_delete);
            bottom.Controls.Add(_ok);
            bottom.Controls.Add(_cancel);

            // Dock 的占位顺序 = 逆着 Controls 集合来：先加的最内层，后加的最外层。
            // 所以这里先 Fill，再两个 Bottom，最后 Top。
            Controls.Add(_list);
            Controls.Add(_info);
            Controls.Add(bottom);
            Controls.Add(_hint);

            AcceptButton = _ok;
            CancelButton = _cancel;

            Reload();
        }

        /// <summary>重新列一遍：加密库里的（只读索引，不解密）+ 签名文件夹里残留的明文 .dwg。
        /// 同名的只显示一次（库里的优先 —— 重新制作过的签名在库里，比明文新）。</summary>
        private void Reload()
        {
            string previous = SelectedFileName;
            if (_list.SelectedItems.Count > 0) previous = (string)_list.SelectedItems[0].Tag;

            List<string> vault = new List<string>();
            List<string> plain = new List<string>();
            try { vault.AddRange(SignatureVault.Names(_directory)); }
            catch (System.Exception) { }
            try
            {
                if (Directory.Exists(_directory))
                    foreach (string file in Directory.GetFiles(_directory, "*.dwg")) plain.Add(Path.GetFileName(file));
            }
            catch (System.Exception) { }

            _list.BeginUpdate();
            _list.Items.Clear();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in vault)
            {
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                AddRow(name, "加密素材库（文件夹里看不到）");
            }
            foreach (string name in plain)
            {
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                AddRow(name, "明文文件（在签名文件夹里）");
            }
            _list.EndUpdate();

            if (previous != null) Select(previous);
            if (_list.Items.Count == 0) _list.Items.Add(new ListViewItem(new string[] { "（还没有笔迹）", "用上面的「从文件添加…」，或先点「选取…」在图上做一份" }));
            UpdateInfo();
        }

        private void AddRow(string fileName, string source)
        {
            ListViewItem item = new ListViewItem(new string[] { fileName, source });
            item.Tag = fileName;
            _list.Items.Add(item);
        }

        /// <summary>按文件名选中一行（找不到就选中第一行）。</summary>
        private void Select(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return;
            foreach (ListViewItem item in _list.Items)
            {
                string name = item.Tag as string;
                if (name != null && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    item.Selected = true;
                    item.Focused = true;
                    item.EnsureVisible();
                    return;
                }
            }
        }

        private void UpdateInfo()
        {
            _delete.Enabled = _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is string;
            int vault = 0, plain = 0;
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag == null) continue;
                if (((string)item.SubItems[1].Text).StartsWith("加密", StringComparison.Ordinal)) vault++;
                else plain++;
            }
            _info.Text = "共 " + (vault + plain) + " 份笔迹：加密素材库 " + vault + " 份，明文文件 " + plain + " 份";
        }

        private void OnDeleteSelected(object sender, EventArgs e)
        {
            if (_list.SelectedItems.Count == 0 || !(_list.SelectedItems[0].Tag is string)) return;
            string name = (string)_list.SelectedItems[0].Tag;
            if (MessageBox.Show(this, "删除笔迹“" + name + "”？\n\n将删除素材库及签名文件夹内的同名副本，关联人员需重新选择笔迹。不会删除当初从外部导入的原文件，也不会修改图纸中已有签名。", "删除签名笔迹", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                SignatureVault.Delete(_directory, name);
                if (string.Equals(SelectedFileName, name, StringComparison.OrdinalIgnoreCase)) SelectedFileName = null;
                Reload();
            }
            catch (System.Exception ex) { MessageBox.Show(this, "删除失败：" + ex.Message, "删除签名笔迹", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void Accept()
        {
            if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag == null)
            {
                MessageBox.Show(this, "先在上面选中一份笔迹。", "选择签名笔迹", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SelectedFileName = (string)_list.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>「从文件添加…」：挑外部 dwg → **加密收进素材库**（不复制明文）。
        /// 如果挑的本来就是签名文件夹里的明文，直接列出来用它，不再多存一份密文。</summary>
        private void OnAddFromFile(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "挑一个 .dwg 笔迹，加密存进素材库";
                dialog.Filter = "签名笔迹 (*.dwg)|*.dwg|所有文件 (*.*)|*.*";
                try { if (Directory.Exists(_directory)) dialog.InitialDirectory = _directory; }
                catch (System.Exception) { }
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string selected = dialog.FileName;
                string name = Path.GetFileName(selected);

                bool inside;
                try
                {
                    inside = string.Equals(Path.GetFullPath(selected),
                                           Path.GetFullPath(Path.Combine(_directory, name)),
                                           StringComparison.OrdinalIgnoreCase);
                }
                catch (System.Exception)
                {
                    inside = false;
                }
                if (inside)
                {
                    // 本来就是签名文件夹里的明文，直接列出来用
                    Reload();
                    Select(name);
                    return;
                }

                DialogResult answer = MessageBox.Show(this,
                    "把这个笔迹加密存进隐藏素材库？\n\n" + selected + "\n\n"
                    + "存进去之后：文件夹里看不到、Windows 搜索搜不到、拷走也打不开；\n"
                    + "原文件保持原样，不会多出一份明文副本。",
                    "从文件添加", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;

                try
                {
                    SignatureVault.Save(_directory, name, File.ReadAllBytes(selected));
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show(this, "加密存入失败：" + ex.Message, "从文件添加", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                SelectedFileName = name;
                Reload();
                Select(name);
            }
        }

        private static void MakeButton(Button button, string text, int width)
        {
            button.Text = text;
            button.Width = width;
            button.Height = 28;
            button.Margin = new Padding(0, 0, 8, 0);
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
        }

        /// <summary>选一个系统里存在的中文字体，避免在缺字体的机器上变成方块。</summary>
        private static Font UiFont()
        {
            foreach (string name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun" })
            {
                try
                {
                    using (FontFamily family = new FontFamily(name))
                    {
                        return new Font(family, 9F, FontStyle.Regular, GraphicsUnit.Point);
                    }
                }
                catch (ArgumentException)
                {
                }
            }
            return SystemFonts.MessageBoxFont;
        }
    }
}
