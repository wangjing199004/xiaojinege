using System;
using System.Windows;
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

/// <summary>非模态面板只投递请求，图框录入和单张外框取点交给文档命令执行。</summary>
internal static class PendingLibraryInput
{
    private sealed class Request
    {
        internal Document Document = null!;
        internal Window Window = null!;
        internal Action Action = null!;
        internal Action? AfterRestore;
        internal string CommandName = "";
        internal bool Completed;

        internal void Finish()
        {
            if (Completed) return;
            Completed = true;
            CadApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentClosing;
            CadWindowFocus.RestoreDialog(Window);
        }

        internal void OnDocumentClosing(object? sender, DocumentCollectionEventArgs e)
        {
            if (!ReferenceEquals(e.Document, Document) || !ReferenceEquals(_pending, this)) return;
            _pending = null;
            CadPickDiagnostics.Log("command-context", "CANCEL pending input: document closing");
            Finish();
        }
    }

    private static Request? _pending;

    internal static void QueuePanel(Window? window, Document document, string operation,
        Action action, Action? afterRestore = null)
    {
        try
        {
            if (window == null) throw new InvalidOperationException("未找到当前操作面板。");
            Queue(window, () =>
            {
                CadPickDiagnostics.Log("panel-operation", "BEGIN " + operation);
                CadPickDiagnostics.Dump("panel-enter:" + operation);
                try { action(); }
                finally
                {
                    CadPickDiagnostics.Log("panel-operation", "END " + operation);
                    CadPickDiagnostics.Dump("panel-leave:" + operation);
                }
            }, afterRestore, document, "ZBP_PANEL_INPUT");
        }
        catch (System.Exception ex)
        {
            CadPickDiagnostics.Log("panel-queue-error", operation + ": " + ex);
            MessageBox.Show("无法开始“" + operation + "”: " + ex.Message, "批量打印");
        }
    }

    internal static void Queue(Window window, Action action, Action? afterRestore = null,
        Document? expectedDocument = null, string commandName = "ZBP_LIBRARY_INPUT")
    {
        if (_pending != null) throw new InvalidOperationException("已有图框取点请求等待执行。");
        var document = CadApp.DocumentManager.MdiActiveDocument
            ?? throw new InvalidOperationException("请先激活需要录入图框的图纸。");
        if (expectedDocument != null && !ReferenceEquals(document, expectedDocument))
            throw new InvalidOperationException("当前图纸与面板绑定的图纸不同，请切回原图纸后重试。");
        var request = new Request { Document = document, Window = window, Action = action,
            AfterRestore = afterRestore, CommandName = commandName };
        _pending = request;
        try
        {
            CadApp.DocumentManager.DocumentToBeDestroyed += request.OnDocumentClosing;
            // 不 DoEvents、不在按钮事件内 GetPoint，返回宿主后由正常命令循环接管。
            window.Hide();
            CadPickDiagnostics.Log("command-context", "QUEUE " + commandName + " document=" + document.Name);
            document.SendStringToExecute(commandName + " ", true, false, false);
        }
        catch
        {
            _pending = null;
            request.Finish();
            throw;
        }
    }

    internal static void Execute(string commandName = "ZBP_LIBRARY_INPUT")
    {
        var request = _pending;
        if (request == null || request.CommandName != commandName) return;
        _pending = null;
        var succeeded = false;
        try
        {
            if (!ReferenceEquals(CadApp.DocumentManager.MdiActiveDocument, request.Document))
                throw new InvalidOperationException("当前图纸已改变，请重新发起图框录入。");
            CadPickDiagnostics.Dump("document-command-enter:" + commandName);
            request.Action();
            succeeded = true;
        }
        catch (System.Exception ex)
        {
            CadPickDiagnostics.Log("command-error", ex.ToString());
            MessageBox.Show("CAD 交互失败: " + ex.Message, "批量打印");
        }
        finally
        {
            CadPickDiagnostics.Log("command-context", "END " + commandName);
            request.Finish();
        }
        if (succeeded && request.AfterRestore != null)
        {
            try { request.AfterRestore(); }
            catch (System.Exception ex)
            {
                CadPickDiagnostics.Log("after-input-error", ex.ToString());
                MessageBox.Show("更新框选结果失败: " + ex.Message, "批量打印");
            }
        }
    }
}
