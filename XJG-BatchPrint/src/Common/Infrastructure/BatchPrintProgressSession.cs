using System;
using System.Windows;
using System.Windows.Threading;

namespace ZwcadBatchPlot;

/// <summary>
/// 批量打印进度会话：打开进度窗、更新进度、结束时关闭。
/// </summary>
internal sealed class BatchPrintProgressSession : IDisposable
{
    private readonly BatchPrintProgressWindow _window;
    private bool _disposed;

    private BatchPrintProgressSession(BatchPrintProgressWindow window)
    {
        _window = window;
    }

    /// <summary>打开进度窗并挂到批打主窗。</summary>
    public static BatchPrintProgressSession Start(Window owner, int total, Action onCancel)
    {
        var window = new BatchPrintProgressWindow(total, onCancel)
        {
            Owner = owner
        };

        if (owner.IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.Show();
        window.Activate();
        Pump(window);
        return new BatchPrintProgressSession(window);
    }

    /// <summary>报告当前已开始的张数与任务说明。</summary>
    public void Report(int completed, int total, string detail)
    {
        if (_disposed)
        {
            return;
        }

        _window.Report(completed, total, detail);
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.Activate();
        Pump(_window);
    }

    /// <summary>切换到合并等阶段说明。</summary>
    public void SetPhase(string title, string detail)
    {
        if (_disposed)
        {
            return;
        }

        _window.SetPhase(title, detail);
        _window.Activate();
        Pump(_window);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.ForceClose();
    }

    private static void Pump(DispatcherObject source)
    {
        source.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
    }
}