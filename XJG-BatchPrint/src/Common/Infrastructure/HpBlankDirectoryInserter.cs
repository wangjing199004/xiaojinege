#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if ACAD_CORE
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

internal static class HpBlankDirectoryInserter
{
    // 在正常文档命令中调用；每次切换先清理预览，再预览另一种尺寸。
    internal static bool PromptAndInsert(Document document)
    {
        var previousShortcutMenu = CadApplication.GetSystemVariable("SHORTCUTMENU");
        try
        {
            // 右键作为回车交给原生 Jig，结束后恢复用户设置。
            CadApplication.SetSystemVariable("SHORTCUTMENU", 0);
            var paper = "A4";
            Point3d? cursor = null;
            while (true)
            {
                var switchRequested = false;
                var inserted = StandardFrameInserter.InsertWithPlacement(document.Database,
                    document.Database.CurrentSpaceId, paper == "A4" ? "a4-directory" : "a3-directory",
                    (frame, transaction) =>
                    {
                        if (cursor.HasValue) StandardFrameInserter.MoveCenter(frame, transaction, cursor.Value);
                        var jig = new StandardFramePlacementJig(frame, transaction, paper);
                        var status = document.Editor.Drag(jig).Status;
                        cursor = jig.CursorPoint;
                        switchRequested = jig.SwitchRequested;
                        return !switchRequested && status == PromptStatus.OK;
                    });
                if (!switchRequested) return !inserted.IsNull;
                paper = paper == "A4" ? "A3" : "A4";
                document.Editor.WriteMessage("\n已切换为空白 " + paper + "目录；右键或空格继续切换，单击确定位置，Esc取消。");
            }
        }
        finally { CadApplication.SetSystemVariable("SHORTCUTMENU", previousShortcutMenu); }
    }
}
