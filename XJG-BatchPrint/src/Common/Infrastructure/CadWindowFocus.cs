using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace ZwcadBatchPlot;

/// <summary>
/// CAD 内嵌窗口隐藏后，Windows 可能把前台焦点交给其他进程。
/// WinForms 录入窗与 WPF 其它面板共用同一套 Hide/Restore 语义（与 main 分支一致）。
/// </summary>
internal static class CadWindowFocus
{
    private const int SwRestore = 9;
    private const uint GwOwner = 4;
    private const int NameBufferLen = 256;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    /// <summary>
    /// 把 CAD 主框架拉回前台。
    ///
    /// 这里**不能**直接用 <c>Process.MainWindowHandle</c>：它是「进程内第一个符合
    /// WinForms 主窗口判据的顶层窗」，而插件自己的 WPF 窗口（WPF 创建的 HWND，
    /// 类名 <c>HwndWrapper[...]</c>）同样可能命中。第一次运行时插件窗是唯一候选，
    /// 抓到的是 CAD；但对话框关闭后旧 HWND 仍留在进程里，第二次命令就可能抓到插件窗，
    /// 于是 <c>SetForegroundWindow</c> 把**插件窗**而不是 CAD 拉回前台 ——
    /// 绘图区拿不到输入，表现为「重新加载 DLL 后第一次能框选、第二次框选失效/乱飘」。
    ///
    /// 改为枚举本进程顶层窗口：跳过 WPF（<c>HwndWrapper</c>）与不可见窗口，
    /// 优先取 AutoCAD 主框架（类名以 <c>AfxWnd</c> 开头，MFC 应用），
    /// 取不到再退化到 MainWindowHandle。
    /// </summary>
    public static void ActivateCadWindow()
    {
        try
        {
            var windowHandle = ResolveCadMainWindow();
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }

            if (IsIconic(windowHandle))
            {
                ShowWindowAsync(windowHandle, SwRestore);
            }

            BringWindowToTop(windowHandle);
            SetForegroundWindow(windowHandle);
        }
        catch
        {
            // Core Console 没有主窗口；焦点恢复失败也不能阻断图框编辑和框选。
        }
    }

    /// <summary>
    /// 解析真正的 CAD 主窗口句柄：枚举本进程顶层窗口并跳过 WPF 插件窗。
    /// </summary>
    private static IntPtr ResolveCadMainWindow()
    {
        try
        {
            var pid = (uint)Process.GetCurrentProcess().Id;
            var anyVisible = IntPtr.Zero;

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

                        var className = ReadClassName(hWnd);

                        // 插件自己的 WPF 窗口：绝不能当成「CAD 主窗口」。
                        if (className.StartsWith("HwndWrapper", StringComparison.Ordinal))
                        {
                            return true;
                        }

                        if (!IsWindowVisible(hWnd))
                        {
                            return true;
                        }

                        // AutoCAD 主框架：MFC 应用，类名以 AfxWnd 开头。
                        if (className.StartsWith("AfxWnd", StringComparison.Ordinal))
                        {
                            // 优先选无属主的顶层窗（MDI 框架容器）。
                            if (GetWindow(hWnd, GwOwner) == IntPtr.Zero)
                            {
                                anyVisible = hWnd;
                                return false;
                            }

                            anyVisible = hWnd;
                            return true;
                        }

                        if (anyVisible == IntPtr.Zero)
                        {
                            anyVisible = hWnd;
                        }
                    }
                    catch
                    {
                        // 单个窗口枚举失败不影响整体。
                    }

                    return true;
                },
                IntPtr.Zero);

            if (anyVisible != IntPtr.Zero)
            {
                return anyVisible;
            }
        }
        catch
        {
            // 枚举失败则退化到 MainWindowHandle。
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return process.MainWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
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

    /// <summary>隐藏 WinForms 插件窗并立即把输入焦点交给 CAD。</summary>
    public static void HideForCadInput(Form form)
    {
        form.Hide();
        // 只抢一次前台 + 一次消息泵；原先二次 Activate 容易造成图面多闪一下。
        ActivateCadWindow();
        System.Windows.Forms.Application.DoEvents();
    }

    /// <summary>隐藏 WPF 插件窗并立即把输入焦点交给 CAD。</summary>
    public static void HideForCadInput(Window window)
    {
        window.Hide();
        ActivateCadWindow();
        System.Windows.Forms.Application.DoEvents();
    }

    /// <summary>CAD 取点结束后恢复 WinForms 窗体。</summary>
    public static void RestoreDialog(Form form)
    {
        form.Visible = true;
        form.BringToFront();
        form.Activate();
    }

    /// <summary>CAD 取点结束后恢复 WPF 窗体。</summary>
    public static void RestoreDialog(Window window)
    {
        window.Visibility = Visibility.Visible;
        window.BringToFrontHwnd();
        window.Activate();
    }

    private static void BringToFrontHwnd(this Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                BringWindowToTop(handle);
            }
        }
        catch
        {
            // 句柄尚未创建时忽略。
        }
    }
}
