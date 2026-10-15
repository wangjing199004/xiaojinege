using System;
using ZwcadBatchPlot;
using ZwSoft.ZwCAD.ApplicationServices;

// 用替身宿主验证请求调度；不模拟 CAD 捕捉，不作为实机验证。
internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Main()
    {
        PaperAndLogRegression.Run();
        DwgViewFitRegression.Run();
        var manager = Application.DocumentManager;
        var doc = new Document();
        manager.MdiActiveDocument = doc;
        var window = new System.Windows.Window();
        var calls = 0;
        PendingLibraryInput.Queue(window, () => calls++);
        Check(calls == 0 && !window.Visible, "按钮应仅排队，不能直接取点");
        Check(doc.Sent == "ZBP_LIBRARY_INPUT ", "必须投递正式文档命令");
        try { PendingLibraryInput.Queue(window, () => calls += 100); throw new Exception("重复请求未被拒绝"); }
        catch (InvalidOperationException) { }
        PendingLibraryInput.Execute();
        PendingLibraryInput.Execute();
        Check(calls == 1 && window.Restores == 1, "请求只能执行一次并恢复一次窗口");

        PendingLibraryInput.Queue(window, () => throw new Exception("取消或失败"));
        PendingLibraryInput.Execute();
        Check(window.Restores == 2, "失败后仍应恢复窗口");

        PendingLibraryInput.Queue(window, () => calls++);
        manager.MdiActiveDocument = new Document();
        PendingLibraryInput.Execute();
        Check(calls == 1 && window.Restores == 3, "切换图纸不能在错误文档执行");

        manager.MdiActiveDocument = doc;
        PendingLibraryInput.Queue(window, () => calls++);
        manager.Close(doc);
        PendingLibraryInput.Execute();
        Check(calls == 1 && window.Restores == 4, "排队文档关闭时应释放请求并恢复窗口");

        doc.ThrowOnSend = true;
        try { PendingLibraryInput.Queue(window, () => calls++); throw new Exception("应传播排队失败"); }
        catch (InvalidOperationException) { }
        Check(window.Restores == 5, "发送失败应归还窗口");
        doc.ThrowOnSend = false;
        PendingLibraryInput.Queue(window, () => calls++);
        PendingLibraryInput.Execute();
        Check(calls == 2 && window.Restores == 6, "失败后允许下一次录入");
        var filled = 0;
        PendingLibraryInput.Queue(window, () => calls++, afterRestore: () =>
        {
            Check(window.Visible, "纸张识别前必须恢复属主窗口");
            filled++;
        }, expectedDocument: doc, commandName: "ZBP_SINGLE_FRAME_INPUT");
        Check(doc.Sent == "ZBP_SINGLE_FRAME_INPUT " && filled == 0, "单张框选必须投递独立命令");
        PendingLibraryInput.Execute();
        Check(calls == 2 && !window.Visible, "图框库命令不能消费单张框选请求");
        PendingLibraryInput.Execute("ZBP_SINGLE_FRAME_INPUT");
        Check(calls == 3 && filled == 1, "单张命令执行一次并填纸张一次");
        PendingLibraryInput.Queue(window, () => throw new Exception("pick failed"),
            afterRestore: () => filled++, commandName: "ZBP_SINGLE_FRAME_INPUT");
        PendingLibraryInput.Execute("ZBP_SINGLE_FRAME_INPUT");
        Check(window.Visible && filled == 1, "取点失败后恢复面板，不能识别错误范围");
        manager.MdiActiveDocument = new Document();
        try
        {
            PendingLibraryInput.Queue(window, () => calls++, expectedDocument: doc);
            throw new Exception("应拒绝在不同图纸启动单张框选");
        }
        catch (InvalidOperationException) { }
        Check(window.Visible && calls == 3, "绑定图纸不符时面板仍应可见");
        manager.MdiActiveDocument = doc;
        var previews = 0;
        PendingLibraryInput.Queue(window, () =>
        {
            Check(!window.Visible, "预览期间面板必须隐藏");
            previews++;
        }, expectedDocument: doc, commandName: "ZBP_SINGLE_PREVIEW");
        Check(doc.Sent == "ZBP_SINGLE_PREVIEW " && previews == 0, "预览不能从按钮同步进入");
        PendingLibraryInput.Execute("ZBP_SINGLE_FRAME_INPUT");
        Check(previews == 0 && !window.Visible, "框选命令不能消费预览请求");
        PendingLibraryInput.Execute("ZBP_SINGLE_PREVIEW");
        Check(previews == 1 && window.Visible, "预览返回后恢复面板");
        PendingLibraryInput.Queue(window, () => throw new Exception("preview cancelled"),
            expectedDocument: doc, commandName: "ZBP_SINGLE_PREVIEW");
        PendingLibraryInput.Execute("ZBP_SINGLE_PREVIEW");
        Check(window.Visible, "预览失败仍恢复面板");
        var rowHeight = 10;
        var uiReloads = 0;
        PendingLibraryInput.QueuePanel(window, doc, "读取目录行高", () => rowHeight = 20,
            afterRestore: () => { Check(window.Visible, "刷新设置前应恢复面板"); uiReloads++; });
        Check(rowHeight == 10 && doc.Sent == "ZBP_PANEL_INPUT ", "量取行高前必须等待面板交互命令");
        PendingLibraryInput.Execute("ZBP_PANEL_INPUT");
        Check(rowHeight == 20 && uiReloads == 1 && window.Visible, "量取完成后更新并刷新设置");
        PendingLibraryInput.QueuePanel(window, doc, "取消量取行高", () => { },
            afterRestore: () => uiReloads++);
        PendingLibraryInput.Execute("ZBP_PANEL_INPUT");
        Check(rowHeight == 20 && uiReloads == 2 && window.Visible, "取消取值不能改写原设置");
        Console.WriteLine("PASS: defer, native command, duplicate, once, failure, document switch, document close, retry");
        Console.WriteLine("PASS: single frame command, command isolation, restore before detection, no detection on failure, bound document check");
        Console.WriteLine("PASS: deferred preview, hidden panel, frame/preview isolation, preview return/failure restore");
        Console.WriteLine("PASS: panel input deferred, settings refresh after restore, cancelled input preserves value");
    }
}

namespace System.Windows
{
    internal sealed class Window
    {
        internal bool Visible = true;
        internal int Restores;
        public void Hide() => Visible = false;
    }
    internal static class MessageBox { public static void Show(string text, string title) { } }
}
namespace ZwcadBatchPlot
{
    internal static class CadPickDiagnostics
    {
        internal static void Log(string key, string text) { }
        internal static void Dump(string tag) { }
    }
    internal static class CadWindowFocus
    {
        internal static void RestoreDialog(System.Windows.Window window)
        { window.Visible = true; window.Restores++; }
    }
}
namespace ZwSoft.ZwCAD.ApplicationServices
{
    internal sealed class Document
    {
        internal string Name = "test.dwg";
        internal string? Sent;
        internal bool ThrowOnSend;
        public void SendStringToExecute(string command, bool activate, bool wrap, bool echo)
        {
            if (ThrowOnSend) throw new InvalidOperationException("send failed");
            Sent = command;
        }
    }
    internal sealed class DocumentCollectionEventArgs : EventArgs { public Document Document = null!; }
    internal sealed class DocumentCollection
    {
        public Document? MdiActiveDocument;
        public event EventHandler<DocumentCollectionEventArgs>? DocumentToBeDestroyed;
        internal void Close(Document doc) => DocumentToBeDestroyed?.Invoke(this, new DocumentCollectionEventArgs { Document = doc });
    }
    internal static class Application { public static DocumentCollection DocumentManager = new(); }
}
