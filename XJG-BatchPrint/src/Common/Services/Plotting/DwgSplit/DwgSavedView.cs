using System;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

internal static class DwgSavedView
{
    internal static void CenterModel(Database output, Database source, Point3d[] frame)
    {
        var aspect = ReadModelAspect(source);
        var fit = Fit(frame, aspect);
        using var tr = output.TransactionManager.StartTransaction();
        var table = (ViewportTable)tr.GetObject(output.ViewportTableId, OpenMode.ForRead);
        var updated = 0;
        foreach (ObjectId id in table)
        {
            var view = (ViewportTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!string.Equals(view.Name, "*Active", StringComparison.OrdinalIgnoreCase)) continue;
            view.UpgradeOpen();
            view.ViewDirection = Vector3d.ZAxis;
            view.ViewTwist = 0;
            view.Target = new Point3d(fit.CenterX, fit.CenterY, 0);
            // CenterPoint 是相对 Target 的 DCS 坐标，不能再次填入 WCS 中心。
            view.CenterPoint = Point2d.Origin;
            view.Width = fit.Width;
            view.Height = fit.Height;
            updated++;
        }
        if (updated == 0) throw new InvalidOperationException("输出 DWG 缺少模型默认视口。");
        output.TileMode = true;
        tr.Commit();
        CadPickDiagnostics.Log("dwg-saved-view", $"model target=({fit.CenterX},{fit.CenterY}) size={fit.Width}x{fit.Height}");
    }

    internal static void CenterPaper(Database output, PlotJob job)
    {
        using var tr = output.TransactionManager.StartTransaction();
        var table = (BlockTable)tr.GetObject(output.BlockTableId, OpenMode.ForRead);
        var owner = DwgDatabaseCleanup.FindSpaceRecord(tr, table, job.SpaceName)
            ?? throw new InvalidOperationException("输出 DWG 缺少目标布局。");
        var rootId = DwgPaperSplitter.TryGetPaperSpaceRootViewportId(tr, owner);
        if (rootId.IsNull)
            throw new InvalidOperationException("输出 DWG 缺少纸面总览视口，无法设置打开视图。");
        var root = (Viewport)tr.GetObject(rootId, OpenMode.ForWrite);
        var aspect = root.Height > 0 ? root.Width / root.Height : 16d / 9d;
        var fit = Fit(DwgSplitGeometry.BuildKeepPolygon(job), aspect);
        // 只改纸面总览视口；不改图纸中的模型浮动视口和它们的比例。
        root.ViewDirection = Vector3d.ZAxis;
        root.TwistAngle = 0;
        root.ViewTarget = Point3d.Origin;
        root.ViewCenter = new Point2d(fit.CenterX, fit.CenterY);
        root.ViewHeight = fit.Height;
        output.TileMode = false;
        tr.Commit();
        CadPickDiagnostics.Log("dwg-saved-view", $"paper center=({fit.CenterX},{fit.CenterY}) height={fit.Height}");
    }

    private static DwgViewFit.Result Fit(Point3d[] frame, double aspect)
    {
        if (frame.Length < 3) throw new InvalidOperationException("拆图范围角点不足。");
        return DwgViewFit.Calculate(frame.Min(p => p.X), frame.Min(p => p.Y),
            frame.Max(p => p.X), frame.Max(p => p.Y), aspect);
    }

    private static double ReadModelAspect(Database source)
    {
        using var tr = source.TransactionManager.StartTransaction();
        var table = (ViewportTable)tr.GetObject(source.ViewportTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var view = (ViewportTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (string.Equals(view.Name, "*Active", StringComparison.OrdinalIgnoreCase) && view.Height > 0)
                return view.Width / view.Height;
        }
        return 16d / 9d;
    }
}
