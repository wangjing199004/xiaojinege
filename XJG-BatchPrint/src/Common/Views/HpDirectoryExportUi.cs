using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.EditorInput;
#endif

namespace ZwcadBatchPlot;

internal static class HpDirectoryExportUi
{
    public static void Run(Window? owner, Document document, IReadOnlyList<PlotJob> jobs)
    {
        var selected = jobs.Where(job => job.Selected).ToList();
        if (jobs.Count > 0 && selected.Count == 0)
        {
            MessageBox.Show(owner, "请先扫描并勾选需要生成目录的图纸。", "生成HP目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var success = false;
        var message = "已取消生成 HP 目录。";
        PendingLibraryInput.QueuePanel(owner, document, "插入HP目录", () =>
        {
            if (jobs.Count == 0)
            {
                try
                {
                    using (document.LockDocument())
                        success = HpBlankDirectoryInserter.PromptAndInsert(document);
                    if (success) document.Editor.Regen();
                    message = success ? "已在当前图纸插入空白 HP 目录。" : "已取消插入空白 HP 目录。";
                    CadPickDiagnostics.Log("hp-blank-directory", message);
                }
                catch (Exception ex)
                {
                    message = "插入空白 HP 目录失败：" + ex.Message;
                    CadPickDiagnostics.Log("hp-blank-directory-failed", ex.ToString());
                }
                return;
            }
            var point = document.Editor.GetPoint(new PromptPointOptions("\n指定 HP 目录左上角插入点（多页向右排列）: "));
            if (point.Status != PromptStatus.OK) return;
            try
            {
                int pages;
                using (document.LockDocument())
                    pages = HpDirectoryExporter.Insert(document.Database, document.Database.CurrentSpaceId,
                        selected, point.Value);
                document.Editor.Regen();
                success = true;
                message = $"已在当前图纸生成 {pages} 页 HP 目录，共填写 {selected.Count} 行。\n图号、图名、图幅和版本号均为图块外的单行文字，可直接编辑。";
                CadPickDiagnostics.Log("hp-directory", message);
            }
            catch (Exception ex)
            {
                message = "生成 HP 目录失败：" + ex.Message;
                CadPickDiagnostics.Log("hp-directory-failed", ex.ToString());
            }
        }, afterRestore: () => MessageBox.Show(owner, message, "生成HP目录", MessageBoxButton.OK,
            success ? MessageBoxImage.Information : MessageBoxImage.Warning));
    }

}
