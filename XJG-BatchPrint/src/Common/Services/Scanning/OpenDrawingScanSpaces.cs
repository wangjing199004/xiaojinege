using System.Collections.Generic;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
#endif
namespace ZwcadBatchPlot;
internal static class OpenDrawingScanSpaces
{
    internal static List<string> Keys(Document document, TitleBlockScanScope scope)
    {
        var db = document.Database;
        var source = string.IsNullOrWhiteSpace(db.Filename) ? document.Name : db.Filename;
        var result = new List<string>();
        using var tr = db.TransactionManager.StartTransaction();
        var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var space = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!space.IsLayout || space.LayoutId.IsNull) continue;
            var layout = (Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead);
            if (scope == TitleBlockScanScope.ModelSpace && !layout.ModelType) continue;
            if (scope == TitleBlockScanScope.PaperLayouts && layout.ModelType) continue;
            if (scope == TitleBlockScanScope.CurrentSpace && id != db.CurrentSpaceId) continue;
            result.Add(MultiFileJobMerge.SpaceKey(source, layout.LayoutName));
        }
        return result;
    }
}
