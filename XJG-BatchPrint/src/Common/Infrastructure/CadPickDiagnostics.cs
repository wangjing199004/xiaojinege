using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace ZwcadBatchPlot;

/// <summary>图框交互原始取证日志；状态仅作为证据，不在日志中预判根因。</summary>
internal static class CadPickDiagnostics
{
    private const int NameBufferLen = 256;
    private const uint GwOwner = 4;
    private const int MaxListedWindows = 24;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>全进程取点计数。重载 DLL 会重置，用于确认「第几次」出的问题。</summary>
    internal static int PickSeq { get; private set; }

    /// <summary>当前 DispatcherFrame 嵌套深度。取点时若 &gt; 0，说明取点仍被压在 WPF 帧内。</summary>
    internal static int FrameDepth { get; private set; }

    /// <summary>本会话历史最大嵌套深度（只增不减）。</summary>
    internal static int MaxFrameDepth { get; private set; }

    /// <summary><see cref="Process.MainWindowHandle"/> 最近一次抓到的窗口类名，验证它是否抓错。</summary>
    internal static string LastMainWindowClass { get; private set; } = "<none>";

    /// <summary>本次进程生命周期内是否出现过「残留的可见 WPF 顶层窗」（旧对话框未销毁）。</summary>
    internal static bool SawStaleWpfWindow { get; private set; }

    private static readonly object Gate = new();
    private static string _logPath = "";
    private static bool _headerWritten;


    internal static string LogPath
    {
        get
        {
            if (_logPath.Length == 0)
            {
                try
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "AcadBatchPlot", "Logs");
                    Directory.CreateDirectory(dir);
                    _logPath = Path.Combine(dir, "PickDiag_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
                }
                catch
                {
                    _logPath = "";
                }
            }

            return _logPath;
        }
    }

    /// <summary>压入一层 DispatcherFrame（内部已配平计数，异常路径也不会泄漏深度）。</summary>
    internal static void PushFrame(System.Windows.Threading.DispatcherFrame frame)
    {
        FrameDepth++;
        if (FrameDepth > MaxFrameDepth)
        {
            MaxFrameDepth = FrameDepth;
        }

        try
        {
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        finally
        {
            FrameDepth--;
        }
    }

    /// <summary>
    /// 取点前释放 WPF/Win32 鼠标捕获。
    /// 保留作为兜底：正常路径实测无残留，但 WPF Button 的 MouseDown 捕获在
    /// 「隐藏窗口 + 非模态帧」路径下理论上有残留可能。
    /// </summary>
    internal static void ReleaseMouseCapture(Window? window)
    {
        try
        {
            if (Mouse.Captured != null)
            {
                Log("capture", "before-release wpf=" + Describe(Mouse.Captured));
                Mouse.Capture(null);
            }

            if (window != null && window.IsMouseCaptured)
            {
                window.ReleaseMouseCapture();
            }
        }
        catch
        {
            // 捕获释放失败不阻断取点。
        }

        try
        {
            if (GetCapture() != IntPtr.Zero)
            {
                Log("capture", "win32-before-release hwnd=" + DescribeWindow(GetCapture()));
                ReleaseCapture();
            }
        }
        catch
        {
            // 同上。
        }
    }

    /// <summary>取点流程开始（窗体仍可见时）。</summary>
    internal static void LogPickBegin(string what)
    {
        PickSeq++;
        Log("pick", $"#{PickSeq} BEGIN {what}");
    }

    /// <summary>
    /// 真正的取点时刻：此时窗体已 Hide、已尝试把焦点交给 CAD。
    /// 这是最有价值的一次转储 —— 它反映 GetPoint 实际运行时谁在前台。
    /// </summary>
    internal static void LogPickReady(string what)
    {
        Log("pick", $"#{PickSeq} READY {what} (calling GetPoint now)");
        Dump("ready");
    }

    /// <summary>取点结束。</summary>
    internal static void LogPickEnd(string what, string status)
    {
        Log("pick", $"#{PickSeq} END {what} status={status}");
    }

    /// <summary>
    /// 记录 GetPoint/GetCorner 实际返回的坐标。
    /// 与光标物理位置对比可判断「点被挪到了别处」还是「捕捉没咬住但坐标正常」。
    /// </summary>
    internal static void LogPoint(string what, string label, double x, double y, double z)
    {
        Log("point", FormattableString.Invariant($"#{PickSeq} {what} {label}=({x:R}, {y:R}, {z:R}) cursorScreenPx={CursorText()}"));
    }

    /// <summary>记录一条自定义事件。key 用于后续 grep。</summary>
    internal static void Log(string key, string message)
    {
        var path = LogPath;
        if (path.Length == 0)
        {
            return;
        }

        try
        {
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} [{1}] {2}{3}",
                DateTime.Now,
                key,
                message,
                Environment.NewLine);
            lock (Gate)
            {
                if (!_headerWritten)
                {
                    var assembly = typeof(CadPickDiagnostics).Assembly;
                    File.AppendAllText(path, "=== pick-trace-v5 " + DateTimeOffset.Now.ToString("O")
                        + " ===" + Environment.NewLine + "assembly=" + assembly.Location
                        + " build=" + assembly.ManifestModule.ModuleVersionId
                        + " OS=" + Environment.OSVersion + " CLR=" + Environment.Version
                        + " bit64=" + Environment.Is64BitProcess + Environment.NewLine, Encoding.UTF8);
                    _headerWritten = true;
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 写日志失败绝不能影响框选。
        }
    }

    internal static IDisposable? BeginSnapOverride(int mode)
    {
        if (mode == 0) return null;
        var saved = Convert.ToInt32(GetCurrentVariable("OSMODE"), CultureInfo.InvariantCulture);
        var target = mode == 1 ? 33 : saved | 16384;
        SetOsmode(target);
        Log("snap-test", $"mode={mode} original={saved} temporary={target}");
        return new SnapRestore(saved);
    }

    private sealed class SnapRestore : IDisposable
    {
        private readonly int _saved;
        private bool _disposed;
        internal SnapRestore(int saved) { _saved = saved; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { SetOsmode(_saved); Log("snap-test", "RESTORED OSMODE=" + _saved); }
            catch (Exception ex) { Log("snap-restore-error", ex.ToString()); }
        }
    }

    private static void SetOsmode(int value)
    {
#if AUTOCAD
#if ACAD_CORE
        Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", value);
#else
        Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("OSMODE", value);
#endif
#else
        ZwSoft.ZwCAD.ApplicationServices.Application.SetSystemVariable("OSMODE", value);
#endif
    }

    /// <summary>转储取点时刻的关键状态 + 本进程顶层窗口清单。</summary>
    internal static void Dump(string tag)
    {
        var sb = new StringBuilder();
        sb.Append("    state(").Append(tag).Append("): ");
        sb.Append("frameDepth=").Append(FrameDepth);
        sb.Append(" maxFrameDepth=").Append(MaxFrameDepth);
        sb.Append(" pickSeq=").Append(PickSeq);
        sb.Append(" threadId=").Append(System.Threading.Thread.CurrentThread.ManagedThreadId);
        sb.AppendLine();

        try
        {
            sb.Append("      win32 capture=").Append(DescribeWindow(GetCapture()));
            sb.Append(" foreground=").Append(DescribeWindow(GetForegroundWindow()));
            sb.Append(" active=").Append(DescribeWindow(GetActiveWindow()));
            sb.AppendLine();
            sb.Append("      cursor=").Append(CursorText());
            sb.AppendLine();
        }
        catch
        {
            sb.AppendLine("      win32 probe failed");
        }

        try
        {
            sb.Append("      wpf captured=").Append(Mouse.Captured == null ? "<none>" : Describe(Mouse.Captured));
            sb.Append(" directlyOver=").Append(Describe(Mouse.DirectlyOver));
            sb.AppendLine();
        }
        catch
        {
            sb.AppendLine("      wpf probe failed");
        }

        AppendMainWindowProbe(sb);
        AppendTopLevelWindows(sb);
        AppendSystemVariables(sb);
        Log("state", sb.ToString().TrimEnd());
    }

    /// <summary>
    /// 验证 <c>Process.MainWindowHandle</c> 抓到的是不是 AutoCAD 主框架。
    /// 抓到 WPF 窗口（类名 HwndWrapper*）即为抓错 —— 这是本轮重点取证项。
    /// </summary>
    private static void AppendMainWindowProbe(StringBuilder sb)
    {
        sb.Append("      MainWindowHandle=");
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();
            var handle = process.MainWindowHandle;
            sb.Append(DescribeWindow(handle));
            LastMainWindowClass = ReadClassName(handle);
            if (IsWpfWindow(handle))
            {
                SawStaleWpfWindow = true;
                sb.Append("  [MainWindowHandle refers to WPF; compare actual foreground]");
            }
        }
        catch (Exception ex)
        {
            sb.Append("<err ").Append(ex.GetType().Name).Append('>');
        }

        sb.AppendLine();
    }

    /// <summary>列出本进程所有顶层窗口：句柄 / 类名 / 可见性 / 属主 / 标题。</summary>
    private static void AppendTopLevelWindows(StringBuilder sb)
    {
        sb.Append("      topLevelWindows:");
        try
        {
            var pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var count = 0;
            EnumWindows(
                (hWnd, _) =>
                {
                    try
                    {
                        GetWindowThreadProcessId(hWnd, out var owner);
                        if (owner != pid)
                        {
                            return true;
                        }

                        if (count >= MaxListedWindows)
                        {
                            return false;
                        }

                        count++;
                        var visible = IsWindowVisible(hWnd);
                        var ownerWnd = GetWindow(hWnd, GwOwner);
                        var className = ReadClassName(hWnd);
                        sb.AppendLine();
                        sb.Append("        [").Append(count).Append("] 0x")
                            .Append(hWnd.ToInt64().ToString("X"));
                        sb.Append(" class=").Append(className);
                        sb.Append(" visible=").Append(visible);
                        sb.Append(" owner=").Append(ownerWnd == IntPtr.Zero
                            ? "<none>"
                            : "0x" + ownerWnd.ToInt64().ToString("X"));
                        sb.Append(" title=").Append(ReadTitle(hWnd));
                        if (IsWpfWindow(hWnd) && visible)
                        {
                            SawStaleWpfWindow = true;
                            sb.Append("  <== 可见的 WPF 顶层窗");
                        }
                    }
                    catch
                    {
                        // 单个窗口枚举失败不影响整体输出。
                    }

                    return true;
                },
                IntPtr.Zero);
            sb.AppendLine();
        }
        catch (Exception ex)
        {
            sb.Append(" <enum failed ").Append(ex.GetType().Name).Append('>').AppendLine();
        }
    }

    private static void AppendSystemVariables(StringBuilder sb)
    {
        sb.Append("      sysvars:");
        foreach (var name in new[] { "ACADVER", "OSMODE", "3DOSMODE", "OSNAPCOORD", "OSNAPZ", "APERTURE", "PICKBOX", "AUTOSNAP", "DYNMODE", "DYNPICOORDS", "ORTHOMODE", "SNAPMODE", "SNAPUNIT", "SNAPBASE", "SNAPANG", "POLARMODE", "ELEVATION", "UCSFOLLOW", "UCSDETECT", "UCSORG", "UCSXDIR", "UCSYDIR", "VIEWCTR", "VIEWSIZE", "VIEWTWIST", "VIEWDIR", "TARGET", "SCREENSIZE", "CVPORT", "TILEMODE", "CTAB", "CMDACTIVE", "CMDNAMES" })
        {
            try
            {
                sb.Append(' ').Append(name).Append('=').Append(DescribeVar(GetCurrentVariable(name)));
            }
            catch (Exception ex)
            {
                sb.Append(' ').Append(name).Append("=<err ").Append(ex.GetType().Name).Append('>');
            }
        }

        sb.AppendLine();
    }

    private static string DescribeVar(object? value)
    {
        switch (value)
        {
            case null:
                return "<null>";
            case string text:
                return text;
            case IEnumerable list:
                var parts = new StringBuilder("[");
                var i = 0;
                foreach (var item in list)
                {
                    if (i++ > 0)
                    {
                        parts.Append(',');
                    }

                    parts.Append(item);
                }

                return parts.Append(']').ToString();
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>";
        }
    }

    private static object? GetCurrentVariable(string name)
    {
#if AUTOCAD
#if ACAD_CORE
        return Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable(name);
#else
        return Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable(name);
#endif
#else
        return ZwSoft.ZwCAD.ApplicationServices.Application.GetSystemVariable(name);
#endif
    }

    private static string CursorText()
    {
        try
        {
            return GetCursorPos(out var pt) ? pt.X + "," + pt.Y : "<err>";
        }
        catch
        {
            return "<err>";
        }
    }

    /// <summary>
    /// 是否是 WPF 顶层窗口。WPF 创建的 HWND 类名固定为 <c>HwndWrapper[...]</c>；
    /// AutoCAD 主框架类是 <c>AfxWnd...</c>。
    /// </summary>
    private static bool IsWpfWindow(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero
            && ReadClassName(hWnd).StartsWith("HwndWrapper", StringComparison.Ordinal);
    }

    private static string ReadClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return "<none>";
        }

        try
        {
            var sb = new StringBuilder(NameBufferLen);
            GetClassName(hWnd, sb, NameBufferLen);
            return sb.ToString();
        }
        catch
        {
            return "<err>";
        }
    }

    private static string ReadTitle(IntPtr hWnd)
    {
        try
        {
            var sb = new StringBuilder(NameBufferLen);
            GetWindowText(hWnd, sb, NameBufferLen);
            var text = sb.ToString();
            return text.Length == 0 ? "<empty>" : "\"" + text + "\"";
        }
        catch
        {
            return "<err>";
        }
    }

    private static string DescribeWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return "<none>";
        }

        return "0x" + hWnd.ToInt64().ToString("X") + "(" + ReadClassName(hWnd) + ")";
    }

    private static string Describe(IInputElement? element)
    {
        if (element == null)
        {
            return "<none>";
        }

        try
        {
            var type = element.GetType().Name;
            var name = (element as FrameworkElement)?.Name;
            return string.IsNullOrEmpty(name) ? type : type + "#" + name;
        }
        catch
        {
            return "<unknown>";
        }
    }
}
