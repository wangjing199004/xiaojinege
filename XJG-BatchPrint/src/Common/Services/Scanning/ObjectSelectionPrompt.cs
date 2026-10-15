using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 对象选择提示的返回结果：要么是用户选中的实体，要么是右键菜单选择的扫描范围。
/// </summary>
internal sealed class ObjectScopeSelectionResult
{
    /// <summary>用户选中的实体 ObjectId 集合；走右键菜单或取消路径时为 null。</summary>
    public ObjectId[]? SelectedIds { get; set; }

    /// <summary>右键菜单选择的扫描范围；走对象选择或取消路径时为 null。</summary>
    public TitleBlockScanScope? Scope { get; set; }

    /// <summary>用户取消（ESC / 空选回车 / 关闭菜单未选）。</summary>
    public bool Cancelled => SelectedIds == null && Scope == null;
}

/// <summary>
/// CAD 对象选择提示：GetSelection + 类型过滤，并在选择期间拦截右键。
/// 仅用于对象扫描路径，不用于框选的 GetPoint/GetCorner。
/// </summary>
internal static class ObjectSelectionPrompt
{
    /// <summary>图框块扫描：选择阶段只允许 INSERT（块参照）。</summary>
    public static SelectionFilter TitleBlockFilter()
        => ScanCandidateFilter.TitleBlockSelectionFilter();

    /// <summary>
    /// 矩形框扫描：块参照与各类多段线；开启四线识别时额外允许 LINE。
    /// </summary>
    /// <param name="includeLines">是否把直线纳入可选类型。</param>
    public static SelectionFilter RectangleFrameFilter(bool includeLines)
        => ScanCandidateFilter.RectangleFrameSelectionFilter(includeLines);

    /// <summary>
    /// 提示用户点选或框选对象；未拾取时右键弹出扫描范围菜单，已拾取时右键确认选择。
    /// 取消、空选或关闭菜单未选时 <see cref="ObjectScopeSelectionResult.Cancelled"/> 为 true。
    /// </summary>
    public static ObjectScopeSelectionResult Prompt(Editor editor, string message, SelectionFilter? filter)
    {
        using var menu = new ScopeContextMenu(editor.Document.Database);
        while (true)
        {
            using var interceptor = new RightClickMenuInterceptor(menu);
            Application.AddMessageFilter(interceptor);
            PromptSelectionResult? selection = null;
            try
            {
                var options = new PromptSelectionOptions { MessageForAdding = message };
                selection = filter != null
                    ? editor.GetSelection(options, filter)
                    : editor.GetSelection(options);
            }
            catch
            {
                // 仍检查菜单是否已选定范围（ESC 注入后提示可能抛错）。
            }
            finally
            {
                Application.RemoveMessageFilter(interceptor);
            }

            if (menu.ChosenScope is { } scope)
            {
                return new ObjectScopeSelectionResult { Scope = scope };
            }

            if (selection is { Status: PromptStatus.OK, Value: not null })
            {
                var ids = selection.Value.GetObjectIds();
                if (ids != null && ids.Length > 0)
                {
                    return new ObjectScopeSelectionResult { SelectedIds = ids };
                }
            }

            // 右键确认时一个对象都没选到（拾取落空）：重新提示。
            if (interceptor.ConfirmRequested)
            {
                continue;
            }

            return new ObjectScopeSelectionResult();
        }
    }

    /// <summary>向系统输入队列注入按键（ESC 结束提示并放弃，ENTER 结束提示并确认选择）。</summary>
    private static class KeyInjection
    {
        private const uint KeyeventfKeyup = 0x0002;
        private const byte VkEscape = 0x1B;
        private const byte VkReturn = 0x0D;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        public static void PressEscape()
        {
            keybd_event(VkEscape, 0, 0, UIntPtr.Zero);
            keybd_event(VkEscape, 0, KeyeventfKeyup, UIntPtr.Zero);
        }

        public static void PressEnter()
        {
            keybd_event(VkReturn, 0, 0, UIntPtr.Zero);
            keybd_event(VkReturn, 0, KeyeventfKeyup, UIntPtr.Zero);
        }
    }

    /// <summary>
    /// 选择提示期间拦截 CAD 绘图区右键：
    /// 已拾取 → 右键注入回车确认；从未拾取 → 弹出扫描范围菜单。右键按下/抬起均吞掉。
    /// </summary>
    private sealed class RightClickMenuInterceptor : IMessageFilter, IDisposable
    {
        private readonly ScopeContextMenu _menu;
        private bool _rButtonDown;

        /// <summary>提示期间发生过左键拾取（点选或框选的第一个角点）。</summary>
        internal bool PickActivity { get; private set; }

        /// <summary>已通过右键请求“确认选择”（注入回车）。</summary>
        internal bool ConfirmRequested { get; private set; }

        public RightClickMenuInterceptor(ScopeContextMenu menu) => _menu = menu;

        public bool PreFilterMessage(ref Message m)
        {
            if (_menu.IsMenuVisible)
            {
                return false;
            }

            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_RBUTTONUP = 0x0205;
            switch (m.Msg)
            {
                case WM_LBUTTONDOWN:
                    PickActivity = true;
                    return false;
                case WM_RBUTTONDOWN:
                    _rButtonDown = true;
                    return true;
                case WM_RBUTTONUP when _rButtonDown:
                    _rButtonDown = false;
                    if (PickActivity)
                    {
                        ConfirmRequested = true;
                        KeyInjection.PressEnter();
                    }
                    else
                    {
                        _menu.ShowAtCursor();
                    }

                    return true;
                default:
                    return false;
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>右键弹出的扫描范围快捷菜单（按当前空间状态自适应菜单项）。</summary>
    private sealed class ScopeContextMenu : IDisposable
    {
        private readonly ContextMenuStrip _menu;

        /// <summary>用户在菜单中选择的扫描范围；未选或取消时为 null。</summary>
        public TitleBlockScanScope? ChosenScope { get; private set; }

        public bool IsMenuVisible => _menu.Visible;

        public ScopeContextMenu(Database db)
        {
            _menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                ShowCheckMargin = false,
                AutoClose = true,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            };

            if (db.TileMode)
            {
                AddItem("当前模型", TitleBlockScanScope.CurrentSpace);
                AddItem("所有布局", TitleBlockScanScope.PaperLayouts);
                AddItem("模型+布局", TitleBlockScanScope.AllSpaces);
            }
            else
            {
                AddItem("模型空间", TitleBlockScanScope.ModelSpace);
                AddItem("所有布局", TitleBlockScanScope.PaperLayouts);
                AddItem("模型+布局", TitleBlockScanScope.AllSpaces);
                var layoutName = "";
                try
                {
                    layoutName = LayoutManager.Current.CurrentLayout ?? "";
                }
                catch
                {
                    // 取不到当前布局名时仍提供“当前布局”项。
                }

                AddItem(
                    string.IsNullOrWhiteSpace(layoutName) ? "当前布局" : $"当前布局({layoutName})",
                    TitleBlockScanScope.CurrentSpace);
            }
        }

        private void AddItem(string text, TitleBlockScanScope scope)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (_, _) =>
            {
                ChosenScope = scope;
                KeyInjection.PressEscape();
                _menu.Close();
            };
            _menu.Items.Add(item);
        }

        public void ShowAtCursor() => _menu.Show(Cursor.Position);

        public void Dispose() => _menu.Dispose();
    }
}
