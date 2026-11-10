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
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CadSignature
{
    /// <summary>
    /// 「人员设置」对话框（5.9.0）：逐行编辑 7 个岗位的姓名与签名笔迹文件。
    /// 纯界面操作（不碰图纸），保存时写入核心 DLL 旁边的「人员名单.txt」。
    /// 由 Commands.RunEditPeople() 以模态方式弹出；保存后下一次「一键签名」就按新名单执行。
    ///
    /// 5.10.0 起可制作签名文件：「选取…」回传 CaptureName，由命令上下文去图上框选签名笔迹、
    /// 存成 姓名.dwg，之后重新弹出本对话框。
    /// 5.11.0 起每行有「>」：打开签名文件夹挑现成的 .dwg；「签名文件」框空着时用水印显示默认规则。
    /// 5.12.0 起「选取…」改为**全局一个**（底部「制作签名文件」：填姓名 → 点选取），不再每行一个。
    /// 5.13.0：「>」挑中的文件名自动去掉 .dwg 填进「姓名」栏（少一步手打）；「选文件」列表头去掉，只留按钮。
    /// 5.13.2：对话框底部显示「名单保存到：<完整路径>」—— 保存到底存哪、有没有存上，用户自己就能核对；
    /// 保存失败时错误框给出真实路径与原因，且不关闭对话框（改动保留，可重试）。
    /// 5.23.0：新做的笔迹加密进隐藏素材库；「明文转加密库」把历史遗留的明文收进库。
    /// 5.25.2：「>」改弹自家的 <see cref="SignaturePicker"/>，直接列加密库里的笔迹。
    /// 5.26.0：**删掉「打开签名文件夹」「打开名单文件夹」「设置默认文件夹」「明文转加密库」四个按钮**
    /// —— 素材已经全在加密库里，翻文件夹既没必要也违背"不要明文"的初衷；这几个动作现在分别由
    /// 「导出人员设置」（带走全部设置）、<see cref="SignaturePicker"/> 里的「从文件添加…」（加密收进库）
    /// 和底部那行自动定位的路径信息覆盖。新增「导出人员设置」：把名单 + 笔迹打成一个加密包。
    /// </summary>
    internal sealed class PeopleEditor : Form
    {
        private readonly Dictionary<string, PersonEntry> _people;
        private readonly string _directory;
        private readonly string _settingsDirectory;
        private readonly string _packageDirectory;
        private readonly Dictionary<string, TextBox> _nameBoxes = new Dictionary<string, TextBox>();
        private readonly Dictionary<string, TextBox> _fileBoxes = new Dictionary<string, TextBox>();
        private readonly Button _ok = new Button();
        private readonly Button _cancel = new Button();
        private readonly Label _filesInfo = new Label();

        /// <summary>用户是否点了「保存」并成功写入。</summary>
        public bool Saved { get; private set; }

        /// <summary>保存成功时写入的配置文件完整路径（供命令行提示用）。</summary>
        public string SavedPath { get; private set; }

        /// <summary>点了「选取…」时 = 要制作的姓名（存成 姓名.dwg）；关闭/取消 = null。</summary>
        public string CaptureName { get; private set; }

        /// <param name="directory">签名 .dwg 所在文件夹（Signatures 或「签名文件夹.txt」里设置过的目录）。</param>
        /// <param name="settingsDirectory">设置文件的存放目录（核心 DLL 同目录，存「人员名单.txt」）。</param>
        /// <param name="packageDirectory">「人员设置.cspkg」配置包的目录 —— DLL 的**物理**所在文件夹
        /// （绿色版 = 用户放 DLL 的那个文件夹；装机版 = Contents\Windows），见 <see cref="CoreEntry.PackageDirectory"/>。</param>
        public PeopleEditor(Dictionary<string, PersonEntry> people, string directory, string settingsDirectory, string packageDirectory)
        {
            _people = people;
            _directory = directory;
            _settingsDirectory = settingsDirectory;
            _packageDirectory = packageDirectory;
            Saved = false;
            CaptureName = null;

            Text = "人员设置 · A1 图框签字";
            Font = UiFont();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;
            Padding = new Padding(4);

            Label hint = new Label();
            hint.AutoSize = true;
            hint.MaximumSize = new Size(600, 0);
            hint.Margin = new Padding(0, 0, 0, 8);
            hint.ForeColor = Color.FromArgb(90, 90, 90);
            hint.Text = "每个岗位填一个姓名，保存后立即生效（下一次「一键签名」按新名单执行）。\n「签名文件」留空 = 用素材库/签名文件夹里的「姓名.dwg」（框内灰字就是当前默认值）。\n「>」＝ 挑一份笔迹：加密素材库里的和文件夹里的明文 .dwg 都会列出来（库里那些文件夹里看不到，只能在这里选）。\n「选取…」＝ 回图上框选笔迹，加密存进隐藏素材库。新做的签名：文件夹看不到、搜索搜不到、拷走也打不开。\n配置迁移统一在出图中心的「设置 → 配置管理」中操作。";

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.ColumnCount = 4;
            grid.RowCount = PeopleConfig.Roles.Length + 1;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            grid.Margin = new Padding(0, 0, 0, 4);

            grid.Controls.Add(HeaderLabel("岗位"), 0, 0);
            grid.Controls.Add(HeaderLabel("姓名"), 1, 0);
            grid.Controls.Add(HeaderLabel("签名文件（可留空）"), 2, 0);
            // 第 3 列（选文件按钮）不设表头，只留按钮本身

            for (int i = 0; i < PeopleConfig.Roles.Length; i++)
            {
                string role = PeopleConfig.Roles[i];
                PersonEntry entry;
                if (!_people.TryGetValue(role, out entry) || entry == null) entry = new PersonEntry();

                Label roleLabel = new Label();
                roleLabel.Text = role;
                roleLabel.Width = 88;
                roleLabel.Height = 26;
                roleLabel.TextAlign = ContentAlignment.MiddleLeft;
                roleLabel.Margin = new Padding(0, 3, 6, 0);

                TextBox nameBox = new TextBox();
                nameBox.Width = 120;
                nameBox.Margin = new Padding(0, 0, 8, 0);
                nameBox.Text = entry.Name ?? "";

                TextBox fileBox = new TextBox();
                fileBox.Width = 140;
                fileBox.Margin = new Padding(0, 0, 4, 0);
                fileBox.Text = entry.SignatureFile ?? "";

                // 「签名文件」框空着时显示默认规则水印，随姓名实时变（只提示，不写进配置）
                EventHandler refreshCue = delegate { SetCue(fileBox, CueText(nameBox.Text)); };
                nameBox.TextChanged += refreshCue;
                fileBox.TextChanged += delegate { if (fileBox.Text.Length > 0) SetCue(fileBox, ""); };
                refreshCue(null, EventArgs.Empty);   // 初次打开先画一次

                Button browse = new Button();
                browse.Text = ">";
                browse.Width = 26;
                browse.Height = 25;
                browse.Margin = new Padding(0, 0, 4, 0);
                browse.FlatStyle = FlatStyle.System;
                browse.UseVisualStyleBackColor = true;
                _tips.SetToolTip(browse, "挑一份笔迹：加密素材库里的和文件夹里的明文 .dwg 都会列出来（库里那些在文件夹里看不到，只能在这里选）；选中后文件名自动填到「姓名」栏，不写任何明文文件。");
                TextBox fileBoxRef = fileBox;
                TextBox nameBoxRef = nameBox;
                browse.Click += delegate { BrowseSignatureFile(nameBoxRef, fileBoxRef); };

                _nameBoxes[role] = nameBox;
                _fileBoxes[role] = fileBox;
                grid.Controls.Add(roleLabel, 0, i + 1);
                grid.Controls.Add(nameBox, 1, i + 1);
                grid.Controls.Add(fileBox, 2, i + 1);
                grid.Controls.Add(browse, 3, i + 1);
            }

            // 5.12.0：制作签名文件 —— 全局一个「选取」，旁边填要制作签名的姓名
            Label makeLabel = new Label();
            makeLabel.Text = "制作签名文件：";
            makeLabel.Width = 96;
            makeLabel.Height = 26;
            makeLabel.TextAlign = ContentAlignment.MiddleLeft;
            makeLabel.Margin = new Padding(0, 3, 6, 0);

            TextBox makeName = new TextBox();
            makeName.Width = 120;
            makeName.Margin = new Padding(0, 0, 8, 0);
            SetCue(makeName, "填姓名（加密保存进素材库）");

            Button pick = new Button();
            pick.Text = "选取…";
            pick.Width = 76;
            pick.Height = 26;
            pick.Margin = new Padding(0);
            pick.FlatStyle = FlatStyle.System;
            pick.UseVisualStyleBackColor = true;
            _tips.SetToolTip(pick, "回到当前图纸：框选签名笔迹 → 可指定插入点（默认外包框中心）→ 加密存进隐藏素材库（文件夹与搜索都看不到）。「一键签名」按姓名自动取用。");
            TextBox makeNameRef = makeName;
            pick.Click += delegate
            {
                string wanted = makeNameRef.Text.Trim();
                if (wanted.Length == 0)
                {
                    MessageBox.Show(this, "先填要制作签名的姓名——签名文件会按 姓名.dwg 命名保存。", "人员设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                CollectToPeople();
                CaptureName = wanted;
                Close();
            };

            FlowLayoutPanel makeRow = new FlowLayoutPanel();
            makeRow.FlowDirection = FlowDirection.LeftToRight;
            makeRow.WrapContents = false;
            makeRow.AutoSize = true;
            makeRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            makeRow.Margin = new Padding(0, 2, 0, 8);
            makeRow.Controls.Add(makeLabel);
            makeRow.Controls.Add(makeName);
            makeRow.Controls.Add(pick);

            MakeButton(_ok, "保存");
            _ok.Click += OnSave;
            MakeButton(_cancel, "取消");
            _cancel.Click += delegate { Close(); };

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.Margin = new Padding(0);
            buttons.Controls.Add(_ok);
            buttons.Controls.Add(_cancel);

            _filesInfo.AutoSize = true;
            _filesInfo.MaximumSize = new Size(600, 0);
            _filesInfo.ForeColor = Color.Gray;
            _filesInfo.Margin = new Padding(0, 8, 0, 0);
            UpdateFilesInfo();

            FlowLayoutPanel root = new FlowLayoutPanel();
            root.FlowDirection = FlowDirection.TopDown;
            root.WrapContents = false;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.BackColor = Color.White;
            root.Padding = new Padding(16, 14, 16, 12);
            root.Controls.Add(hint);
            root.Controls.Add(grid);
            root.Controls.Add(makeRow);
            root.Controls.Add(buttons);
            root.Controls.Add(_filesInfo);

            Controls.Add(root);
            AcceptButton = _ok;
            CancelButton = _cancel;
        }

        /// <summary>「导出人员设置」（5.26.0）：把当前面板里的名单 + 名单中每个人的签名笔迹
        /// 打成一个加密包（<see cref="SettingsPackage"/>）。
        ///
        /// 默认存到 DLL 所在目录、默认名 `人员设置.cspkg` —— 直接点「保存」就落在"该在的地方"。
        /// 导出前先把面板内容**保存到本地名单**：导出的是"我看到的这一份"，不然关掉面板不保存
        /// 就会本地一份、包里一份，对不上。
        ///
        /// 全程不解出明文 dwg 落盘：笔迹用包自己的新密钥重新封装（见 SettingsPackage.Export）。</summary>
        private void OnExportProfile(object sender, EventArgs e)
        {
            CollectToPeople();

            string savedWarning = null;
            try
            {
                PeopleConfig.Save(_settingsDirectory, _people);
            }
            catch (System.Exception ex)
            {
                savedWarning = "（提醒：本地名单这次没保存成功 —— " + ex.Message + "；导出的是面板里当前这份内容）";
            }

            string target;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出人员设置";
                dialog.Filter = "人员设置包 (*.cspkg)|*.cspkg|所有文件 (*.*)|*.*";
                dialog.FileName = SettingsPackage.PackageFileName;
                dialog.OverwritePrompt = true;
                try { if (Directory.Exists(_packageDirectory)) dialog.InitialDirectory = _packageDirectory; }
                catch (System.Exception) { }
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                target = dialog.FileName;
            }

            PackageExport result;
            try
            {
                result = SettingsPackage.Export(target, _people, _directory);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this,
                    "导出失败。\n\n目标位置：" + target + "\n原因：" + ex.GetType().Name + " — " + ex.Message,
                    "导出人员设置", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            UpdateFilesInfo();

            StringBuilder summary = new StringBuilder();
            summary.Append("人员设置已导出：\n\n").Append(result.Path).Append("\n\n");
            summary.Append("岗位 ").Append(result.Names.Count).Append(" 个：");
            summary.Append(result.Names.Count == 0 ? "（都是空的）" : string.Join("、", result.Names.ToArray())).Append("\n");
            summary.Append("笔迹 ").Append(result.Included.Count).Append(" 份：");
            summary.Append(result.Included.Count == 0 ? "（没有素材可打包）" : string.Join("、", result.Included.ToArray()));
            if (result.Missing.Count > 0)
                summary.Append("\n       未找到素材、未打包 ").Append(result.Missing.Count).Append(" 份：")
                       .Append(string.Join("、", result.Missing.ToArray()));
            summary.Append("\n\n");
            summary.Append("把它放在 DLL 所在文件夹（默认就存到那里）——换台电脑或发给同事后，\n")
                   .Append("插件一加载就会自动按它填好姓名并接上笔迹；\n")
                   .Append("对方本机已经有自己的名单时**不会**被覆盖（本机名单优先），\n")
                   .Append("想让它生效就先删掉 DLL 旁的「人员名单.txt」再打开一次「人员设置」。\n\n");
            summary.Append("说明：包内自带密钥（和加密素材库的 key.bin 同一个道理 —— 密钥随包走，\n")
                   .Append("才能做到「放上去就自动生效」、不用输口令）。所以它防的是\n")
                   .Append("被随手翻到、被 Windows 搜索搜到、被别的软件打开，不是口令加密。");
            if (savedWarning != null) summary.Append("\n\n").Append(savedWarning);

            MessageBox.Show(this, summary.ToString(), "导出人员设置", MessageBoxButtons.OK,
                result.Missing.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        /// <summary>「签名文件」框的水印文字：姓名填了 → 显示默认规则；姓名空 → 提示整行不签。</summary>
        private static string CueText(string name)
        {
            string trimmed = name == null ? "" : name.Trim();
            return trimmed.Length == 0 ? "姓名留空 = 该行不签名" : "默认 " + trimmed + ".dwg（可留空）";
        }

        /// <summary>EM_SETCUEBANNER：在文本框里画灰色提示文字（只提示，不改变框里的真实内容）。</summary>
        private static void SetCue(TextBox box, string text)
        {
            try
            {
                SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text ?? "");
            }
            catch (System.Exception)
            {
                // 水印失败无碍功能（个别系统主题下不支持），静默忽略
            }
        }

        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>「>」：弹「选择签名笔迹」对话框（5.25.2 起改成 <see cref="SignaturePicker"/>）。
        ///
        /// 以前这里直接弹 Windows 文件对话框（筛选 `*.dwg`）并指向签名文件夹 —— 但 5.23.0 之后
        /// 新做的签名全都以 **GUID 文件名 + AES 加密**躺在隐藏的 `.db\` 里（扩展名 `.csig`），
        /// 文件对话框当然看不见，**刚做好的笔迹挑不到**，看着像"没存上"。
        /// 现在由 SignaturePicker 直接向素材库要名单（只读索引、**不解密**），
        /// 加密库与明文文件夹里的笔迹一视同仁地列出来。
        ///
        /// 选中后把**文件名填到「签名文件」栏**、去掉扩展名填到「姓名」栏（5.13.0 起的行为）。
        /// **全程不写出任何明文 dwg** —— 原来"选到文件夹外面的文件就明文复制一份进来"的做法
        /// 已去掉（它正好和"不要明文"相悖），改由对话框里的「从文件添加…」加密收进库。</summary>
        private void BrowseSignatureFile(TextBox nameBox, TextBox fileBox)
        {
            using (SignaturePicker picker = new SignaturePicker(_directory))
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                string fileName = picker.SelectedFileName;
                if (string.IsNullOrEmpty(fileName)) return;
                fileBox.Text = fileName;   // 配置里只存文件名（相对签名文件夹）
                nameBox.Text = Path.GetFileNameWithoutExtension(fileName);   // 姓名栏自动跟随文件名
            }
        }

        /// <summary>把当前各行的输入收进内存名单（不落盘）。「选取…」跳去制图前、
        /// 以及「导出人员设置」打包前调用。</summary>
        private void CollectToPeople()
        {
            foreach (string role in PeopleConfig.Roles)
            {
                _people[role] = new PersonEntry
                {
                    Name = _nameBoxes[role].Text.Trim(),
                    SignatureFile = _fileBoxes[role].Text.Trim()
                };
            }
        }

        /// <summary>保存：逐行校验后写入配置文件。姓名为空但填了笔迹文件视为矛盾，拦下来。
        /// 5.13.1 起写到 <see cref="_settingsDirectory"/>（核心 DLL 同目录）—— 此前误写到签名
        /// 文件夹，与读取端（也读核心 DLL 同目录）不一致，导致保存后重进又回默认名单。
        ///
        /// 5.13.2 起：抛异常时**不关对话框**（用户改的内容还在，可直接重试），错误框里给出
        /// 真实写入路径，方便判断是权限还是路径问题。PeopleConfig.Save 内部已含回读校验。</summary>
        private void OnSave(object sender, EventArgs e)
        {
            Dictionary<string, PersonEntry> updated = new Dictionary<string, PersonEntry>();
            foreach (string role in PeopleConfig.Roles)
            {
                string name = _nameBoxes[role].Text.Trim();
                string file = _fileBoxes[role].Text.Trim();
                if (name.Length == 0 && file.Length > 0)
                {
                    MessageBox.Show(this, "岗位「" + role + "」姓名为空但填了签名文件。\n请补上姓名，或把签名文件一栏也清空。",
                        "人员设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                updated[role] = new PersonEntry { Name = name, SignatureFile = file };
            }

            string path = PeopleConfig.ConfigPath(_settingsDirectory);
            try
            {
                PeopleConfig.Save(_settingsDirectory, updated);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this,
                    "保存失败，名单没有写进去。\n\n"
                    + "目标位置：" + path + "\n"
                    + "原因：" + ex.GetType().Name + " — " + ex.Message + "\n\n"
                    + "如果是「拒绝访问」，说明该文件夹只读或被安全软件拦截。",
                    "人员设置", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;     // 不关对话框，改过的内容还在，可以重试
            }

            foreach (string role in PeopleConfig.Roles) _people[role] = updated[role];
            SavedPath = path;
            Saved = true;
            Close();
        }

        /// <summary>刷新底部的保存位置 / 签名文件夹 / 现有笔迹 / 配置包状态四行信息。</summary>
        private void UpdateFilesInfo()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("名单保存到：" + PeopleConfig.ConfigPath(_settingsDirectory));
            text.AppendLine("当前签名文件夹：" + _directory);
            text.AppendLine("现有笔迹：" + ExistingFiles());
            text.Append(SettingsPackage.Describe(_packageDirectory, _settingsDirectory));
            _filesInfo.Text = text.ToString();
        }

        /// <summary>列出签名素材：加密库里的（GUID 文件名，看不见）+ 文件夹里残留的明文 .dwg。</summary>
        private string ExistingFiles()
        {
            try
            {
                List<string> names = new List<string>();
                // 干净安装（绿色版出厂状态）时签名文件夹还不存在：直接说"还没有素材"，
                // 别落到下面的 catch 里报成"读取失败"，那也是误导。
                if (!Directory.Exists(_directory)) return "（还没有签名素材）";
                foreach (string file in Directory.GetFiles(_directory, "*.dwg")) names.Add(Path.GetFileName(file));
                foreach (string name in SignatureVault.Names(_directory)) if (!names.Contains(name)) names.Add(name);
                return names.Count == 0 ? "（还没有签名素材）" : string.Join("、", names.ToArray());
            }
            catch (System.Exception)
            {
                return "（读取失败）";
            }
        }

        private static Label HeaderLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Width = 160;
            label.Height = 22;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = Color.FromArgb(60, 60, 60);
            label.Margin = new Padding(0, 0, 8, 2);
            return label;
        }

        private static void MakeButton(Button button, string text)
        {
            button.Text = text;
            button.Width = 110;
            button.Height = 30;
            button.Margin = new Padding(0, 4, 8, 0);
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
        }

        private readonly ToolTip _tips = new ToolTip();

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
