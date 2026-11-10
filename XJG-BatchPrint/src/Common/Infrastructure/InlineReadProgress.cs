using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace ZwcadBatchPlot;
// CAD reads stay on their owning thread. Only render-priority work is pumped here,
// so progress updates cannot dispatch another scan, mouse pick or edit command.
internal sealed class InlineReadProgress : IDisposable
{
    [ThreadStatic] private static InlineReadProgress? _current;
    private readonly InlineReadProgress? _previous;
    private readonly FrameworkElement _panel;
    private readonly ProgressBar _bar;
    private readonly TextBlock _text;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _last = -1000;
    private string _detail = "";
    private InlineReadProgress(FrameworkElement panel, ProgressBar bar, TextBlock text)
    { _panel = panel; _bar = bar; _text = text; _previous = _current; _current = this; }
    internal static InlineReadProgress Start(FrameworkElement panel, ProgressBar bar, TextBlock text)
    {
        var session = new InlineReadProgress(panel, bar, text);
        panel.Visibility = Visibility.Visible;
        session.Update("准备读取图纸…", 0, 0);
        return session;
    }
    internal static void Report(string detail, int current = 0, int total = 0)
        => _current?.Update(detail, current, total);
    private void Update(string detail, int current, int total)
    {
        if (_clock.ElapsedMilliseconds - _last < 100 && !(total > 0 && current >= total)) return;
        _last = _clock.ElapsedMilliseconds; _detail = detail;
        _text.Text = detail;
        _bar.IsIndeterminate = total <= 0;
        _bar.Value = total > 0 ? Math.Max(0, Math.Min(100, current * 100.0 / total)) : 0;
        _panel.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
    }
    public void Dispose()
    {
        _current = _previous;
        if (_previous == null) _panel.Visibility = Visibility.Collapsed;
    }
}
