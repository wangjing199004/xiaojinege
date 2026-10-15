using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
#else
using ZwSoft.ZwCAD.EditorInput;
#endif

namespace ZwcadBatchPlot;

/// <summary>仅观察插件操作和取点，不修改坐标、系统变量或消息处理结果。</summary>
internal sealed class CadPickTrace : IDisposable, System.Windows.Forms.IMessageFilter
{
    private readonly Editor _editor;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastSample = -300;
    private int _events;
    private int _samples;
    private bool _disposed;
    private bool _monitorAttached;
    private bool _filterAttached;
    private bool _reportedError;

    internal CadPickTrace(Editor editor)
    {
        _editor = editor;
        try
        {
            editor.PointMonitor += OnPointMonitor;
            _monitorAttached = true;
            System.Windows.Forms.Application.AddMessageFilter(this);
            _filterAttached = true;
            CadPickDiagnostics.Log("trace", "ATTACH point monitor + input filter");
            CadPickDiagnostics.Log("geometry", "document=" + editor.Document.Name
                + " UCS=" + editor.CurrentUserCoordinateSystem);
            using var view = editor.GetCurrentView();
            CadPickDiagnostics.Log("view", $"center={view.CenterPoint} width={view.Width} height={view.Height} target={view.Target} direction={view.ViewDirection} twist={view.ViewTwist}");
        }
        catch (System.Exception ex)
        {
            CadPickDiagnostics.Log("trace-error", ex.ToString());
        }
    }

    private void OnPointMonitor(object sender, PointMonitorEventArgs e)
    {
        try
        {
            _events++;
            if (_clock.ElapsedMilliseconds - _lastSample < 250) return;
            _lastSample = _clock.ElapsedMilliseconds;
            _samples++;
            var c = e.Context;
            CadPickDiagnostics.Log("move", $"#{CadPickDiagnostics.PickSeq} event={_events} rawWcs={c.RawPoint} computedWcs={c.ComputedPoint} history={c.History}");
            CadPickDiagnostics.LogPoint("move", "rawWcs", c.RawPoint.X, c.RawPoint.Y, c.RawPoint.Z);
            CadPickDiagnostics.LogPoint("move", "computedWcs", c.ComputedPoint.X, c.ComputedPoint.Y, c.ComputedPoint.Z);
            var paths = c.GetPickedEntities();
            for (var i = 0; i < Math.Min(paths.Length, 4); i++)
                CadPickDiagnostics.Log("hit", "path=" + i + " objects="
                    + string.Join(",", Array.ConvertAll(paths[i].GetObjectIds(), x => x.ToString())));
        }
        catch (System.Exception ex)
        {
            if (_reportedError) return;
            _reportedError = true;
            CadPickDiagnostics.Log("monitor-error", ex.ToString());
        }
    }

    public bool PreFilterMessage(ref System.Windows.Forms.Message m)
    {
        try
        {
            // 仅在 CAD 取点作用域记录点击与控制键，不记录全局输入或普通文字。
            var id = m.Msg;
            if (id == 0x201 || id == 0x202 || id == 0x204 || id == 0x205 || id == 0x20A
                || ((id == 0x100 || id == 0x101) && IsControlKey(m.WParam.ToInt32())))
                CadPickDiagnostics.Log("input", $"#{CadPickDiagnostics.PickSeq} msg=0x{id:X} hwnd=0x{m.HWnd.ToInt64():X} wparam=0x{m.WParam.ToInt64():X} lparam=0x{m.LParam.ToInt64():X} modifiers={System.Windows.Forms.Control.ModifierKeys}");
        }
        catch { /* 观察失败不能影响 CAD 消息。 */ }
        return false;
    }

    private static bool IsControlKey(int key) => key == 27 || key == 13 || key == 32
        || key == 16 || key == 17 || key == 18 || (key >= 112 && key <= 123);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_monitorAttached) _editor.PointMonitor -= OnPointMonitor; }
        catch (System.Exception ex) { CadPickDiagnostics.Log("detach-error", ex.ToString()); }
        finally
        {
            if (_filterAttached) System.Windows.Forms.Application.RemoveMessageFilter(this);
            CadPickDiagnostics.Log("trace", $"DETACH elapsedMs={_clock.ElapsedMilliseconds} pointEvents={_events} samples={_samples}");
        }
    }

    internal static void AttachWindow(Window window)
    {
        var id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(window).ToString("X8");
        void Write(string action) => CadPickDiagnostics.Log("ui", $"window={id} {window.GetType().Name} {action}");
        window.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, e) =>
        {
            var button = e.OriginalSource as ButtonBase ?? e.Source as ButtonBase;
            Write("CLICK_COMPLETED " + button?.Name + " content=" + Convert.ToString(button?.Content, CultureInfo.InvariantCulture));
        }), true);
        window.IsVisibleChanged += (_, _) => Write("VISIBLE=" + window.IsVisible);
        window.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((sender, e) =>
        {
            if (e.OriginalSource is ComboBox combo)
                Write("COMBO " + combo.Name + " index=" + combo.SelectedIndex + " value=" + combo.SelectedItem);
            else if (e.OriginalSource is TabControl tabs)
                Write("TAB index=" + tabs.SelectedIndex);
        }), true);
        window.Activated += (_, _) => Write("ACTIVATED");
        window.Deactivated += (_, _) => Write("DEACTIVATED");
        window.Closing += (_, e) => Write("CLOSING cancel=" + e.Cancel);
        window.Closed += (_, _) => Write("CLOSED");
        window.PreviewKeyDown += (_, e) => Write("KEY=" + e.Key);
        Write("CREATED");
    }
}
