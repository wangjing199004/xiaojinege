using System;
using System.Threading;

namespace ZwcadBatchPlot;

/**
 * @file BatchPlotHostProgress.cs
 * @description 批打窗体自行显示进度/停止时，抑制 CAD PlotProgressDialog，避免盖住批打界面。
 */
internal static class BatchPlotHostProgress
{
    private static int _suppressEngineDialog;

    /** SuppressEnginePlotDialog：批打窗已接管进度时为 true。 */
    internal static bool SuppressEnginePlotDialog => Volatile.Read(ref _suppressEngineDialog) > 0;

    /** Begin：进入批打窗进度模式（可嵌套）。 */
    internal static void Begin() => Interlocked.Increment(ref _suppressEngineDialog);

    /** End：退出批打窗进度模式。 */
    internal static void End() => Interlocked.Decrement(ref _suppressEngineDialog);
}
