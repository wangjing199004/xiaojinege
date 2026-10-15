using System;
using System.Windows.Threading;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/**
 * @file PendingPlotPreview.cs
 * @description 非模态批打窗把预览请求交给内部命令执行时的暂存区。
 *
 * AutoCAD PlotEngine 交互预览必须在命令/文档上下文中启动；按钮事件处于应用上下文，
 * 直接 Preview 会出现「假启动」（画面出来但滚轮仍归主编辑器）。
 *
 * 外部图不能在文档命令里 Open。Start 先在应用上下文打开并激活源 DWG，
 * 再对该文档 SendStringToExecute；预览结束后在 Idle 里归还/关闭。
 */
internal static class PendingPlotPreview
{
    private static readonly object Gate = new();
    private static Request? _pending;

    /**
     * @description 一次待执行的打印预览请求。
     */
    internal sealed class Request
    {
        public PlotJob Job { get; set; } = null!;
        public string DeviceName { get; set; } = "";
        public string StyleSheet { get; set; } = "";
        public Document Document { get; set; } = null!;
        public Action? OnFinally { get; set; }
        public Action<Exception>? OnError { get; set; }
    }

    /**
     * Start：应用上下文准备源文档，再投递文档上下文预览命令。
     * dispatcher 用于预览命令结束后再关本次打开的 DWG。
     */
    public static void Start(Request request, Dispatcher dispatcher)
    {
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;
        var openedByUs = false;
        Document? previewDoc = null;
        try
        {
            previewDoc = PlotterService.PrepareJobDocument(request.Job, request.Document, out openedByUs);
            request.Document = previewDoc;
            var userFinally = request.OnFinally;
            var docToRelease = previewDoc;
            request.OnFinally = () =>
            {
                try
                {
                    userFinally?.Invoke();
                }
                finally
                {
                    dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        PlotterService.ReleaseTemporaryDocument(oldActive, docToRelease, openedByUs);
                    }));
                }
            };
            Queue(request);
            previewDoc.SendStringToExecute("_ZBP_INTERNAL_PREVIEW ", true, false, false);
        }
        catch
        {
            Take();
            if (previewDoc != null)
            {
                PlotterService.ReleaseTemporaryDocument(oldActive, previewDoc, openedByUs);
            }

            throw;
        }
    }

    /** Queue：覆盖写入下一次内部预览命令要消费的请求。 */
    public static void Queue(Request request)
    {
        lock (Gate)
        {
            _pending = request;
        }
    }

    /** Take：取出并清空待执行请求；无请求时返回 null。 */
    public static Request? Take()
    {
        lock (Gate)
        {
            var request = _pending;
            _pending = null;
            return request;
        }
    }
}
