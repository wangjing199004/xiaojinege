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
using System.Windows.Forms;

namespace CadSignature
{
    /// <summary>「图幅标定」对话框的关闭方式。</summary>
    internal enum FrameCalibResult
    {
        /// <summary>取消 / 关窗 / ESC：放弃本轮全部改动，不落盘。</summary>
        Cancel,
        /// <summary>确认：把本轮改动一次性写入「图框定位.txt」。</summary>
        Save,
        /// <summary>点了某个图幅的「标定…」，要回图上选图框点两下（由 Commands.RunFrameCalibrate 接手）。</summary>
        Calibrate
    }

    /// <summary>
    /// 图幅标定对话框（5.18.0 新增；5.18.1 加确认/取消 + 修窗口裁切）。
    ///
    /// 每行一个图幅：状态（未标定 / 已标定·内容高≈…，本轮改动带「待保存」标记）+
    /// 「标定…」（关窗回图上交互，结果只记账）+「清除」（也只记账）。
    /// 底部「确认 / 取消」：本轮所有改动（新标定 + 清除）**确认才写入「图框定位.txt」，
    /// 取消全部放弃**——撤销语义就来自这个"先记账、后落盘"。
    ///
    /// 「标定…」只把动作记下来并关窗，真正的选图框/点角点由 Commands 在命令上下文里
    /// 执行（需要干净的绘图区输入），做完后对话框重新弹出——与「人员设置」同一个套路。
    /// </summary>
    internal sealed class FrameCalibEditor : Form
    {
        private readonly ToolTip _tips = new ToolTip();
        private readonly Dictionary<string, Label> _status = new Dictionary<string, Label>();
        private readonly Dictionary<string, double[]> _calibrations;
        private readonly Dictionary<string, double[]> _pendingSave;
        private readonly HashSet<string> _pendingRemove;

        /// <summary>用户点的是「确认」「取消」还是某个图幅的「标定…」。</summary>
        public FrameCalibResult Result { get; private set; }

        /// <summary>要标定的图幅（A0/A1/A2/A3），<see cref="Result"/> == Calibrate 时有效。</summary>
        public string CalibSlot { get; private set; }

        /// <param name="calibrations">当前各图幅的标定（含本轮未保存的新标定；double[5] = 内容高 + 两角偏移）。</param>
        /// <param name="pendingSave">本轮已标定、待保存的图幅（键用于显示「待保存」标记；值 = 新标定数据）。</param>
        /// <param name="pendingRemove">本轮已清除、待保存的图幅。</param>
        public FrameCalibEditor(Dictionary<string, double[]> calibrations,
            Dictionary<string, double[]> pendingSave, HashSet<string> pendingRemove)
        {
            _calibrations = calibrations;
            _pendingSave = pendingSave;
            _pendingRemove = pendingRemove;
            Result = FrameCalibResult.Cancel;

            Text = "图幅标定 · " + CoreEntry.Version;
            Font = UiFont();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            // 5.18.1 修：窗口必须 AutoSize 跟着内容撑开，否则底部按钮行会被裁掉
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.ColumnCount = 4;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));    // 图幅
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));   // 状态
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));    // 标定
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));    // 清除
            grid.RowCount = FrameOffsetConfig.Slots.Length;
            grid.AutoSize = true;
            grid.Padding = new Padding(16, 14, 16, 2);
            grid.BackColor = Color.White;

            for (int index = 0; index < FrameOffsetConfig.Slots.Length; index++)
            {
                string slot = FrameOffsetConfig.Slots[index];

                Label name = new Label();
                name.Text = slot == "封面" ? "封面" : slot + " 图框";
                name.AutoSize = true;
                name.Margin = new Padding(0, 9, 0, 4);

                Label status = new Label();
                status.AutoSize = true;
                status.Margin = new Padding(4, 9, 4, 4);
                _status[slot] = status;
                UpdateStatus(slot);

                Button calibrate = MakeButton("标定…", slot == "封面" ? "选择封面块，再框选六岗位笔迹区（左上角 → 右下角），不含制图、不显示姓名。确认后生效。" : "回图上：先选一个 " + slot + " 图框，再点两下（姓名列左上角 → 签名列右下角）。结果先记账，点「确认」才生效。");
                string slotCopy = slot;      // 闭包里用的副本，避免事件里拿到循环变量
                calibrate.Click += delegate { Result = FrameCalibResult.Calibrate; CalibSlot = slotCopy; Close(); };

                Button clear = MakeButton("清除", "删除 " + slot + " 图幅的标定（先记账，点「确认」才生效；「取消」可放弃）。");
                clear.Click += delegate { ClearSlot(slotCopy); };

                grid.Controls.Add(name, 0, index);
                grid.Controls.Add(status, 1, index);
                grid.Controls.Add(calibrate, 2, index);
                grid.Controls.Add(clear, 3, index);
            }

            Label hint = new Label();
            hint.AutoSize = true;
            hint.MaximumSize = new Size(430, 0);
            hint.Margin = new Padding(16, 12, 16, 0);
            hint.ForeColor = Color.FromArgb(90, 90, 90);
            hint.Text = "锚点 = 每个图框自身内容的右下角（块内坐标），平移/缩放/旋转/动态拉伸都能对准；"
                + "「图框签字」框选时按内容高度自动区分图幅（±5%）——A1+0.25/A1+0.5 等加长图幅与 A1 高度相同，标定一次全类共用。"
                + "封面独立按标定块名识别：框选六岗位笔迹区，不含制图、不显示姓名，不参与 A0～A3 高度匹配。确认保存，取消放弃。";

            Button confirm = new Button();
            confirm.Text = "确认";
            confirm.Width = 96;
            confirm.Height = 30;
            confirm.Margin = new Padding(0, 0, 8, 0);
            confirm.FlatStyle = FlatStyle.System;
            confirm.UseVisualStyleBackColor = true;
            _tips.SetToolTip(confirm, "把本轮改动（新标定 + 清除）一次性写入「图框定位.txt」，随后生效。");
            confirm.Click += delegate { Result = FrameCalibResult.Save; Close(); };

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Width = 96;
            cancel.Height = 30;
            cancel.Margin = confirm.Margin;
            cancel.FlatStyle = FlatStyle.System;
            cancel.UseVisualStyleBackColor = true;
            _tips.SetToolTip(cancel, "放弃本轮全部改动（已落盘的旧标定不受影响）。");
            cancel.Click += delegate { Result = FrameCalibResult.Cancel; Close(); };

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.AutoSize = true;
            buttons.Margin = new Padding(16, 10, 16, 14);
            buttons.Controls.Add(confirm);
            buttons.Controls.Add(cancel);

            FlowLayoutPanel root = new FlowLayoutPanel();
            root.FlowDirection = FlowDirection.TopDown;
            root.WrapContents = false;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.BackColor = Color.White;
            root.Controls.Add(grid);
            root.Controls.Add(hint);
            root.Controls.Add(buttons);

            Controls.Add(root);
            AcceptButton = confirm;
            CancelButton = cancel;
        }

        /// <summary>刷新一行的状态显示（含「待保存」标记）。</summary>
        private void UpdateStatus(string slot)
        {
            double[] calibration = null;
            bool has = _calibrations != null && _calibrations.TryGetValue(slot, out calibration) && calibration != null;
            bool saving = _pendingSave.ContainsKey(slot);
            bool removing = _pendingRemove.Contains(slot) && !saving;

            Label status = _status[slot];
            if (removing)
            {
                status.Text = "未标定（待保存）";
                status.ForeColor = Color.FromArgb(190, 95, 0);
            }
            else if (has)
            {
                status.Text = "已标定 · 内容高 ≈ " + calibration[0].ToString("F0") + (saving ? "（待保存）" : "");
                status.ForeColor = Color.FromArgb(0, 110, 60);
            }
            else
            {
                status.Text = "未标定";
                status.ForeColor = Color.FromArgb(190, 95, 0);
            }
        }

        /// <summary>清除一个图幅的标定——只记账（待保存），不写文件；「取消」可放弃。</summary>
        private void ClearSlot(string slot)
        {
            double[] calibration = null;
            bool has = _calibrations != null && _calibrations.TryGetValue(slot, out calibration) && calibration != null;
            if (!has && !_pendingSave.ContainsKey(slot))
            {
                UpdateStatus(slot);        // 本来就未标定，不记账
                return;
            }
            _pendingSave.Remove(slot);
            _pendingRemove.Add(slot);
            UpdateStatus(slot);
        }

        private Button MakeButton(string text, string tip)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = 76;
            button.Height = 28;
            button.Margin = new Padding(2, 4, 2, 4);
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
            _tips.SetToolTip(button, tip);
            return button;
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
