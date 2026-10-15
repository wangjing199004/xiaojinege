using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

/**
 * @file PlotterService.MultiFile.cs（AutoCAD）
 * @description 非当前打开图的独立出图编排：打开 DWG、切活动文档、按布局 Regen。
 *
 * 主要功能：
 * - PrepareJobDocument / ReleaseTemporaryDocument：预览按钮在应用上下文打开/归还文档
 * - PlotExternalFileJobs：一组外部 DWG 的打印
 * - PreviewExternalFile：外部图预览（仅当文档命令里尚未激活源文件时作为兜底）
 * - PrepareExternalSpace：同一 SpaceName 只切布局并 Regen 一次
 *
 * 注意：不与当前打开图的 EnsureSpaceRegenerated / PlotDocumentJobs 共用实现。
 * 必须等外部图真正成为活动文档并生成显示列表后再出图，否则会出现图号对、页面全白。
 * 文档命令（_ZBP_INTERNAL_PREVIEW）里禁止 Open/切文档；预览须先在应用上下文 PrepareJobDocument。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    private const int ExternalDocumentReadyTimeoutMs = 10000;

    /**
     * PrepareJobDocument：确保任务源 DWG 已打开并成为活动文档。
     * 必须在应用上下文调用（非模态按钮）；文档命令里 Open 会失败。
     */
    public static Document PrepareJobDocument(PlotJob job, Document currentDocument, out bool openedByUs)
    {
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            openedByUs = false;
            if (CadApp.DocumentManager.MdiActiveDocument != currentDocument)
            {
                ActivateExternalDocument(currentDocument, currentDocument.Name);
            }

            return currentDocument;
        }

        var sourceFile = RequireExistingSourceFile(job.SourceFile);
        openedByUs = FindOpenDocument(sourceFile) == null;
        var doc = OpenExternalDocumentForPlot(sourceFile);
        ActivateExternalDocument(doc, sourceFile);
        TryResolveExternalXrefs(doc);
        return doc;
    }

    /**
     * ReleaseTemporaryDocument：预览/出图结束后切回原活动文档；仅关闭本次新打开的 DWG。
     * 须等文档命令结束后再调用，不能关正在执行命令的文档。
     */
    public static void ReleaseTemporaryDocument(Document? previousActive, Document document, bool openedByUs)
    {
        try
        {
            WaitForPlotIdle();
        }
        catch
        {
        }

        TryRestoreActiveDocument(previousActive);
        if (openedByUs && document != null && !document.IsDisposed && previousActive != document)
        {
            CloseWithoutSave(document);
        }

        var active = CadApp.DocumentManager.MdiActiveDocument;
        if (active != null && !active.IsDisposed)
        {
            try
            {
                HostApplicationServices.WorkingDatabase = active.Database;
            }
            catch
            {
            }
        }
    }

    /**
     * PlotExternalFileJobs：打开或定位外部 DWG，设为活动文档后按布局 Regen 再出图。
     * 本次打开的文档结束后关闭且不保存；本来就开着的文档不关。
     */
    private static void PlotExternalFileJobs(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        using var scope = AcquireExternalDocument(sourceFile);
        var doc = scope.Document;
        TryResolveExternalXrefs(doc);

        string? lastSpaceKey = null;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PrepareExternalSpace(doc, job, ref lastSpaceKey);
                beforeJob?.Invoke(job);
                // 进度窗 Activate 会抢走 CAD 焦点，出图前必须再次确认活动文档。
                ActivateExternalDocument(doc, sourceFile);
                using (doc.LockDocument())
                {
                    PlotDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, settings, doc);
                }

                results.Add(new PlotJobResult { Job = job });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }

    /**
     * PreviewExternalFile：打开外部 DWG、切活动文档、目标空间 Regen 一次后预览。
     * 本次打开的文档结束后关闭且不保存。
     * 若从文档命令进入且源文件尚未激活，Open 仍会失败；预览按钮应先走 PrepareJobDocument。
     */
    private static void PreviewExternalFile(PlotJob job, string deviceName, string styleSheet)
    {
        using var scope = AcquireExternalDocument(job.SourceFile);
        var doc = scope.Document;
        TryResolveExternalXrefs(doc);
        string? lastSpaceKey = null;
        PrepareExternalSpace(doc, job, ref lastSpaceKey);
        ActivateExternalDocument(doc, job.SourceFile);
        using (doc.LockDocument())
        {
            PreviewDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, doc);
        }
    }

    /** AcquireExternalDocument：打开并激活外部 DWG，Dispose 时归还活动文档并按需关闭。 */
    private static ExternalDocumentScope AcquireExternalDocument(string sourceFile)
    {
        sourceFile = RequireExistingSourceFile(sourceFile);
        var previousActive = CadApp.DocumentManager.MdiActiveDocument;
        var openedByUs = FindOpenDocument(sourceFile) == null;
        var doc = OpenExternalDocumentForPlot(sourceFile);
        ActivateExternalDocument(doc, sourceFile);
        return new ExternalDocumentScope(doc, previousActive, openedByUs);
    }

    /** RequireExistingSourceFile：校验外部任务路径存在并归一化为完整路径。 */
    private static string RequireExistingSourceFile(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            throw new InvalidOperationException("该图纸没有关联 DWG 路径，无法打开外部文件。");
        }

        var fullPath = Path.GetFullPath(sourceFile);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找不到外部图纸文件: " + fullPath, fullPath);
        }

        return fullPath;
    }

    /**
     * OpenExternalDocumentForPlot：打开外部 DWG，并等到文档管理器里能按路径找到它。
     * 禁止在异步打开未完成时回退到当前活动文档，否则会用错图的显示列表打出白页。
     */
    private static Document OpenExternalDocumentForPlot(string sourceFile)
    {
        var existing = FindOpenDocument(sourceFile);
        if (existing != null)
        {
            return existing;
        }

        try
        {
            OpenDocument(sourceFile);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法打开外部图纸: " + sourceFile + "\n" + ex.Message, ex);
        }

        var waited = 0;
        while (waited < ExternalDocumentReadyTimeoutMs)
        {
            var found = FindOpenDocument(sourceFile);
            if (found != null)
            {
                return found;
            }

            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(50);
            waited += 50;
        }

        throw new InvalidOperationException("未能打开外部图纸，已停止该文件打印以避免打到错误文档: " + sourceFile);
    }

    /**
     * ActivateExternalDocument：把外部图设为活动文档并等待切换完成，再刷屏生成显示列表。
     */
    private static void ActivateExternalDocument(Document doc, string sourceFile)
    {
        if (CadApp.DocumentManager.MdiActiveDocument != doc)
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
        }

        var waited = 0;
        while (CadApp.DocumentManager.MdiActiveDocument != doc && waited < ExternalDocumentReadyTimeoutMs)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(50);
            waited += 50;
        }

        if (CadApp.DocumentManager.MdiActiveDocument != doc)
        {
            throw new InvalidOperationException("无法将外部图纸切换为当前文档: " + sourceFile);
        }

        HostApplicationServices.WorkingDatabase = doc.Database;
        PumpExternalDocumentDisplay(doc);
    }

    /** TryRestoreActiveDocument：尽量切回预览/出图前的活动文档，失败不抛。 */
    private static void TryRestoreActiveDocument(Document? previousActive)
    {
        if (previousActive == null || previousActive.IsDisposed)
        {
            return;
        }

        try
        {
            if (CadApp.DocumentManager.MdiActiveDocument != previousActive)
            {
                CadApp.DocumentManager.MdiActiveDocument = previousActive;
            }

            HostApplicationServices.WorkingDatabase = previousActive.Database;
        }
        catch
        {
        }
    }

    /** TryResolveExternalXrefs：解析外部参照；宿主拒绝时仍按已打开内容出图。 */
    private static void TryResolveExternalXrefs(Document doc)
    {
        try
        {
            doc.Database.ResolveXrefs(true, false);
        }
        catch
        {
        }
    }

    /**
     * PrepareExternalSpace：把外部文档切到目标模型/布局并 Regen。
     * 同一 SpaceName 只重生成一次；模型非 DCS 窗口按每张图对齐视图。
     * 切布局后先释放文档锁再刷屏，让视口显示列表有机会生成。
     */
    private static void PrepareExternalSpace(Document doc, PlotJob job, ref string? lastSpaceKey)
    {
        var spaceKey = string.IsNullOrWhiteSpace(job.SpaceName) ? "__CURRENT__" : job.SpaceName.Trim();
        if (!string.Equals(lastSpaceKey, spaceKey, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using (doc.LockDocument())
                {
                    HostApplicationServices.WorkingDatabase = doc.Database;
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var layout = FindLayoutForJob(tr, doc.Database, job);
                        LayoutManager.Current.CurrentLayout = layout.LayoutName;
                        tr.Commit();
                    }

                    doc.Editor.Regen();
                }

                PumpExternalDocumentDisplay(doc);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"无法准备外部图布局“{job.SpaceName}”，已停止该张打印以避免输出错误区域。",
                    ex);
            }

            lastSpaceKey = spaceKey;
        }

        if (!job.IsPaperSpace && !job.IsDcsWindow)
        {
            using (doc.LockDocument())
            {
                PrepareEditorViewForPlot(doc, job);
            }
        }
    }

    /** PumpExternalDocumentDisplay：让 CAD 处理切文档/切布局后的重绘。 */
    private static void PumpExternalDocumentDisplay(Document doc)
    {
        try
        {
            doc.Editor.UpdateScreen();
        }
        catch
        {
        }

        try
        {
            CadApp.UpdateScreen();
        }
        catch
        {
        }

        System.Windows.Forms.Application.DoEvents();
        Thread.Sleep(50);
        System.Windows.Forms.Application.DoEvents();
    }

    /**
     * ExternalDocumentScope：占用一次外部文档；Dispose 时先切回原图，再关闭本次打开的文件。
     */
    private sealed class ExternalDocumentScope : IDisposable
    {
        private readonly Document? _previousActive;
        private readonly bool _openedByUs;
        private bool _disposed;

        public ExternalDocumentScope(Document document, Document? previousActive, bool openedByUs)
        {
            Document = document;
            _previousActive = previousActive;
            _openedByUs = openedByUs;
        }

        public Document Document { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseTemporaryDocument(_previousActive, Document, _openedByUs);
        }
    }
}
