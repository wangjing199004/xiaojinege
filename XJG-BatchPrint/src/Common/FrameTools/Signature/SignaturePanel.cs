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
using System.Drawing;
using System.Windows.Forms;

namespace CadSignature
{
    /// <summary>面板上按了哪个按钮；关闭窗口或按 ESC 时为 None。</summary>
    internal enum PanelAction
    {
        None,
        /// <summary>单张签字：框选签字栏 + 立即签名（5.19.0 起合并原「签字范围」与「一键签名」）。</summary>
        ManualSign,
        /// <summary>图框签字：框选批量，按图幅自动签名。</summary>
        FrameSign,
        /// <summary>图幅标定（图框签字右侧的小「>」按钮）。</summary>
        FrameCalib,
        /// <summary>图框编号：框选图框按顺序写编号进属性（5.22.0，原 TKBH / 图框编号2.lsp 移植）。</summary>
        FrameNumber,
        EditPeople,
        ToggleHidden,
        Clear
    }

    /// <summary>
    /// 签字面板（模态对话框，5.19.0 起 2 列 × 3 行；5.22.0 起图框编号入面板、关闭右移）：
    ///   第 1 行：单张签字 ｜ 图框签字 ｜ >(图幅标定，小方钮)
    ///   第 2 行：隐藏签字 ｜ 清除签字 ｜ [全部]复选框
    ///   第 3 行：人员设置 ｜ 图框编号 ｜ 关闭
    /// 按钮只记录动作并立即关闭窗口，真正的图纸操作由 <see cref="Commands.ShowSignaturePanel"/>
    /// 在命令上下文里执行（框选角点需要干净的绘图区输入）。
    /// 「单张签字」「图框签字」「图幅标定(>)」「图框编号」「人员设置」执行完会重新弹出面板，
    /// 「隐藏签字」「清除签字」执行完直接结束，让结果不被面板遮挡。
    /// 尺寸规则（5.19.1）：六个主按钮**等宽对齐**——宽度 = 最长按钮文字 + 左右各一个汉字宽；
    /// 两列之间留 12px 间距；第 3 列放小件：「>」小方钮、「全部」复选框、窄版「关闭」（5.22.0）。
    /// 「全部」（5.20.0）：不勾选时「清除签字」= 框选范围清除（框外不动）；勾选后 = 全图清除
    /// （两个签名图层上的所有内容 + 整个图形中的签名块定义，类似 PURGE），见 Commands.RunClearAll。
    /// </summary>
    internal sealed class SignaturePanel : Form
    {
        private const int ButtonHeight = 28;

        private readonly ToolTip _tips = new ToolTip();
        private readonly Label _slotInfo = new Label();
        private readonly Label _lastInfo = new Label();
        private readonly Button _manual = new Button();
        private readonly Button _frameSign = new Button();
        private readonly Button _frameCalib = new Button();
        private readonly Button _toggle = new Button();
        private readonly Button _clear = new Button();
        private readonly CheckBox _clearAll = new CheckBox();
        private readonly Button _people = new Button();
        private readonly Button _number = new Button();
        private readonly Button _close = new Button();

        /// <summary>用户按下的按钮。</summary>
        public PanelAction Action { get; private set; }

        /// <summary>「全部」复选框是否勾选（5.20.0）：勾选时「清除签字」走全图清除。
        /// 必须在窗体 Dispose 之前读走（ShowModalDialog 返回后、using 块内）。</summary>
        public bool ClearAllRequested
        {
            get { return _clearAll.Checked; }
        }

        /// <param name="slotStatus">顶部状态行文本，已含当前空间与各签字区域的定位点情况。</param>
        /// <param name="statusReady">状态是否就绪：就绪显示绿色，需要用户处理时显示橙色。</param>
        /// <param name="signatureVisible">签名图层当前是否处于打开状态。</param>
        /// <param name="lastMessage">上一次操作的结果，显示在面板上。</param>
        public SignaturePanel(string slotStatus, bool statusReady, bool signatureVisible, string lastMessage)
        {
            Action = PanelAction.None;

            // 标题带上核心版本号：改完代码热更新后，这里会直接变成新版本，一眼就能确认生效了
            Text = "签字面板 · 核心 " + CoreEntry.Version;
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

            _slotInfo.AutoSize = true;
            _slotInfo.Margin = new Padding(0, 0, 0, 2);
            _slotInfo.Text = slotStatus;
            _slotInfo.ForeColor = statusReady ? Color.FromArgb(0, 110, 60) : Color.FromArgb(190, 95, 0);

            _lastInfo.AutoSize = true;
            _lastInfo.Margin = new Padding(0, 0, 0, 10);
            _lastInfo.ForeColor = Color.FromArgb(90, 90, 90);
            _lastInfo.Text = string.IsNullOrEmpty(lastMessage) ? "上次操作：—" : "上次操作：" + lastMessage;

            MakeButton(_manual, "单张签字", "框选这一张图的签字栏（姓名列左上角 → 签名列右下角），随后直接签名并立即上屏；按空格/回车继续框选下一处，E 或 ESC 结束。7 个岗位全没填姓名时会先提示去「人员设置」。");
            MakeButton(_frameSign, "图框签字", "鼠标框选一个范围，范围内的图框一次全部签名：每个图框按内容高度自动识别图幅（A0/A1/A2/A3，加长图幅共用同类标定），套用「图幅标定」里对应图幅的签名位置。没标定的图幅会点名跳过。");
            MakeButton(_frameCalib, ">", "图幅标定：分别固定 A0/A1/A2/A3 四种图框的签名位置（每种图幅标一次，加长变体共用；改动点「确认」才生效，「取消」放弃）。");
            MakeButton(_toggle, signatureVisible ? "隐藏签字" : "显示签字", "切换签名的显示/隐藏：隐藏时该图层关闭，图上不可见也不会被打印。");
            MakeButton(_clear, "清除签字", "不勾「全部」：鼠标指定一个矩形范围（点第一个角点 → 点对角点），框内的签名与定位点全删，框外一律不动。\r\n勾选「全部」：不进框选，直接清全图——删掉两个签名图层上的所有内容，并清掉整个图形中的签名块定义（类似 PURGE）。");
            MakeButton(_people, "人员设置", "修改 7 个岗位各签谁、用哪份签名笔迹（.dwg）。保存后立即生效，下一次签名按新名单执行，不用重新编译也不用重启 AutoCAD。");
            MakeButton(_number, "图框编号", "给框选的图框按顺序统一编号（原 TKBH 命令）：固定写入『图号』属性，输入编号前缀、起始数字，选排序方向（1 横向 / 2 纵向 / 3 逆轴，空格取默认 1），再框选图框——按排序把编号逐个写进属性，10 以内自动补零（01、02…）。");
            MakeButton(_close, "关闭", "关闭面板；下次输入 TKQM 可再次打开。");

            // 「全部」复选框（5.20.0）：清除签字的全图模式开关，放在「清除签字」右侧
            _clearAll.Text = "全部";
            _clearAll.Font = Font;
            _clearAll.AutoSize = true;
            _clearAll.BackColor = Color.White;
            _clearAll.Margin = new Padding(8, 5, 2, 2);
            _clearAll.UseVisualStyleBackColor = true;
            _tips.SetToolTip(_clearAll, "勾选后点「清除签字」= 全图清除：删掉「CAD_签名」与「CAD_签名定位」两个图层上的所有内容（模型空间与所有布局都清），并清掉整个图形中的签名块定义（CAD_SIG_*，类似 PURGE）。不勾选 = 框选范围清除，框外不动。");

            _manual.Click += delegate { Finish(PanelAction.ManualSign); };
            _frameSign.Click += delegate { Finish(PanelAction.FrameSign); };
            _frameCalib.Click += delegate { Finish(PanelAction.FrameCalib); };
            _toggle.Click += delegate { Finish(PanelAction.ToggleHidden); };
            _clear.Click += delegate { Finish(PanelAction.Clear); };
            _people.Click += delegate { Finish(PanelAction.EditPeople); };
            _number.Click += delegate { Finish(PanelAction.FrameNumber); };
            _close.Click += delegate { Finish(PanelAction.None); };

            // 统一度量（5.19.1）：主按钮宽度 = 最长文字 + 左右各一个汉字宽（边框比文字多一个字符），
            // 六个主按钮等宽对齐；「>」缩成一个汉字宽的小方钮；「关闭」按自己的两字文字做窄版（5.22.0）。
            int charW = TextRenderer.MeasureText("字", Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (charW < 8) charW = 8;
            Button[] mains = { _manual, _frameSign, _toggle, _clear, _people, _number };
            int mainW = 0;
            foreach (Button b in mains)
            {
                mainW = Math.Max(mainW, TextRenderer.MeasureText(b.Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width);
            }
            mainW += charW * 2;
            foreach (Button b in mains)
            {
                b.Width = mainW;
                b.Height = ButtonHeight;
                b.Margin = new Padding(6, 2, 6, 2);
            }
            int smallW = charW + 14;
            _frameCalib.Width = smallW;
            _frameCalib.Height = ButtonHeight;
            _frameCalib.Margin = new Padding(8, 2, 2, 2);
            int closeW = TextRenderer.MeasureText(_close.Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + charW * 2;
            _close.Width = closeW;
            _close.Height = ButtonHeight;
            _close.Margin = new Padding(8, 2, 2, 2);

            // 顶部两行状态文字的换行宽度 = 按钮区总宽
            int textWidth = mainW * 2 + 60;
            _slotInfo.MaximumSize = new Size(textWidth, 0);
            _lastInfo.MaximumSize = new Size(textWidth, 0);

            // 3 列：主按钮 ｜ 主按钮 ｜ 小件列（第 1 行「>」，第 2 行「全部」，第 3 行窄版「关闭」）
            // 第 3 列宽度要同时装得下三者，取最宽
            int tailW = Math.Max(Math.Max(smallW + 10, _clearAll.PreferredSize.Width + 10), closeW + 12);
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.ColumnCount = 3;
            grid.RowCount = 3;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, mainW + 12));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, mainW + 12));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, tailW));
            grid.AutoSize = true;
            grid.BackColor = Color.White;
            grid.Margin = new Padding(0);
            grid.Controls.Add(_manual, 0, 0);
            grid.Controls.Add(_frameSign, 1, 0);
            grid.Controls.Add(_frameCalib, 2, 0);
            grid.Controls.Add(_toggle, 0, 1);
            grid.Controls.Add(_clear, 1, 1);
            grid.Controls.Add(_clearAll, 2, 1);
            grid.Controls.Add(_people, 0, 2);
            grid.Controls.Add(_number, 1, 2);
            grid.Controls.Add(_close, 2, 2);

            FlowLayoutPanel root = new FlowLayoutPanel();
            root.FlowDirection = FlowDirection.TopDown;
            root.WrapContents = false;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.BackColor = Color.White;
            root.Padding = new Padding(16, 14, 16, 12);

            root.Controls.Add(_slotInfo);
            root.Controls.Add(_lastInfo);
            root.Controls.Add(grid);

            Controls.Add(root);
            CancelButton = _close;
        }

        private void Finish(PanelAction action)
        {
            Action = action;
            Close();
        }

        private void MakeButton(Button button, string text, string tip)
        {
            button.Text = text;
            button.Height = ButtonHeight;
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
            _tips.SetToolTip(button, tip);
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
