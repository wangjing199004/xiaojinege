#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using TitleAttributeBatch.Core;
namespace ZwcadBatchPlot;
/// <summary>每个源文档单独备份和签名；嵌套签名事务始终由外层事务回滚。</summary>
internal sealed class SignedPlotRestoreException : Exception
{
    internal SignedPlotRestoreException(string message, Exception inner) : base(message, inner) { }
}
internal sealed class TemporarySignedPlotScope : IDisposable
{
    private readonly Document document;
    private readonly DocumentLock documentLock;
    private readonly SignedPlotBackup backup;
    private Transaction? transaction;
    private bool disposed;
    internal static TemporarySignedPlotScope? Begin(Document document, IReadOnlyList<PlotJob> jobs)
        => jobs.Any(job => job.SignBeforePlot) ? new TemporarySignedPlotScope(document, jobs.Where(job => job.SignBeforePlot).ToList()) : null;
    private TemporarySignedPlotScope(Document doc, IReadOnlyList<PlotJob> jobs)
    {
        document = doc;
        documentLock = doc.LockDocument();
        try
        {
            string source = jobs.Select(job => job.SourceFile).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? doc.Name;
            backup = new SignedPlotBackup(source);
            doc.Editor.WriteMessage("\n带签名打印原件备份：" + backup.BackupPath);
            transaction = doc.Database.TransactionManager.StartTransaction();
            SignatureRuntime.Run(() => CadSignature.Commands.SignSelectedForPlot(doc.Database, jobs.Select(job => job.BlockHandle)));
            doc.Editor.Regen();
        }
        catch
        {
            // 签名失败也必须先撤销已经提交的内层事务；失败现场保留备份。
            try { transaction?.Abort(); transaction?.Dispose(); } finally { documentLock.Dispose(); }
            throw;
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try
        {
            transaction!.Abort(); transaction.Dispose(); transaction = null;
            document.Editor.Regen();
            backup.Complete();
            document.Editor.WriteMessage("\n带签名打印结束：临时签名已还原，原DWG未写入，备份已清理。");
        }
        catch (Exception ex)
        {
            throw new SignedPlotRestoreException("带签名打印还原未完全完成，已保留签名前备份：" + backup.BackupPath + "。" + ex.Message, ex);
        }
        finally { documentLock.Dispose(); }
    }
}
