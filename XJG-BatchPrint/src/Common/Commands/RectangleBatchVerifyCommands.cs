using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.Runtime;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 矩形批打纸张识别与扫描的内部验证命令（可供 AccoreConsole 批跑）。
/// </summary>
public sealed partial class BatchPlotCommands
{
    /// <summary>运行纸张检测与当前图扫描用例，结果写入临时报告。</summary>
    [CommandMethod("_ZBP_INTERNAL_VERIFY_RECTANGLE_BATCH")]
    public void VerifyRectangleBatch()
    {
        var reportPath = Path.Combine(
            Path.GetTempPath(),
            "zbp-rectangle-batch-verify-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        var log = new StringBuilder();
        var passed = 0;
        var failed = 0;

        void Case(string name, bool ok, string detail)
        {
            if (ok)
            {
                passed++;
                log.AppendLine("[PASS] " + name + " | " + detail);
            }
            else
            {
                failed++;
                log.AppendLine("[FAIL] " + name + " | " + detail);
            }
        }

        try
        {
            log.AppendLine("ZBP Rectangle Batch Verify");
            log.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            log.AppendLine("Process: " + System.Diagnostics.Process.GetCurrentProcess().ProcessName);
            log.AppendLine();

            RunPaperDetectionCases(Case);
            log.AppendLine();
            RunDrawingScanCases(Case);
        }
        catch (System.Exception ex)
        {
            failed++;
            log.AppendLine("[FAIL] fatal | " + ex);
        }

        log.AppendLine();
        log.AppendLine($"Summary: passed={passed} failed={failed} total={passed + failed}");
        File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);

        var doc = CadApp.DocumentManager.MdiActiveDocument;
        var ed = doc?.Editor;
        ed?.WriteMessage("\n[ZBP] Rectangle verify report: " + reportPath + "\n");
        ed?.WriteMessage($"[ZBP] passed={passed} failed={failed}\n");

        // AccoreConsole 也会抓取标准输出。
        System.Diagnostics.Trace.WriteLine("ZBP_RECT_VERIFY_REPORT=" + reportPath);
        System.Diagnostics.Trace.WriteLine($"ZBP_RECT_VERIFY_SUMMARY=passed={passed};failed={failed}");
        Console.WriteLine("ZBP_RECT_VERIFY_REPORT=" + reportPath);
        Console.WriteLine($"ZBP_RECT_VERIFY_SUMMARY=passed={passed};failed={failed}");
        Console.WriteLine(log.ToString());
    }

    /// <summary>纸张候选：比例库短边匹配、长宽比任意比例及 A 系列展开。</summary>
    private static void RunPaperDetectionCases(Action<string, bool, string> Case)
    {
        var modelOptions = PaperSizeDetector.CreateRectangleBatchOptions(
            paperMatchToleranceMm: 1d,
            isPaperSpace: false,
            longPaperSnapToleranceMm: 3d,
            customScales: null);
        var layoutOptions = PaperSizeDetector.CreateRectangleBatchOptions(
            paperMatchToleranceMm: 1d,
            isPaperSpace: true,
            longPaperSnapToleranceMm: 3d,
            customScales: null);

        // 模型空间：精确 A3 @ 1:100 应贴库。
        {
            var w = 420d * 100d;
            var h = 297d * 100d;
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(w, h, modelOptions);
            var first = list.FirstOrDefault();
            Case(
                "stage1_A3_1to100_default",
                first != null
                && string.Equals(first.PaperName, "A3", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 100d) < 0.001d,
                first == null
                    ? "empty"
                    : $"{first.PaperName} {first.ScaleText} count={list.Count}");
        }

        // 布局空间：同尺寸优先 1:1。
        {
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(420d, 297d, layoutOptions);
            var first = list.FirstOrDefault();
            Case(
                "stage1_layout_A3_1to1_preferred",
                first != null
                && string.Equals(first.PaperName, "A3", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 1d) < 0.001d,
                first == null ? "empty" : $"{first.PaperName} {first.ScaleText}");
        }

        // A2 @ 1:100 默认贴库。
        {
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(594d * 100d, 420d * 100d, modelOptions);
            var first = list.FirstOrDefault();
            Case(
                "stage1_A2_1to100_default",
                first != null
                && string.Equals(first.PaperName, "A2", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 100d) < 0.001d,
                first == null ? "empty" : $"{first.PaperName} {first.ScaleText}");
        }

        // 加长图 A1+1/4 @ 1:100（长边 = 841 * 10/8）。
        {
            var shortSide = 594d * 100d;
            var longSide = 841d * 10d / 8d * 100d;
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(longSide, shortSide, modelOptions);
            var first = list.FirstOrDefault();
            Case(
                "stage1_A1_plus_1_4",
                first != null
                && first.IsLong
                && first.PaperName.StartsWith("A1+", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 100d) < 0.001d,
                first == null ? "empty" : $"{first.PaperName} {first.ScaleText} IsLong={first.IsLong}");
        }

        // 短边超容差：不贴库，默认 A3 ~1:100.5。
        {
            var h = 297d * 100d + 150d;
            var longAdjusted = h * (420d / 297d);
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(longAdjusted, h, modelOptions);
            var first = list.FirstOrDefault();
            var stage1Empty = PaperSizeDetector.DetectCandidates(longAdjusted, h, modelOptions).Count == 0;
            Case(
                "aspect_merge_near_A3_not_wrong_A4",
                stage1Empty
                && first != null
                && string.Equals(first.PaperName, "A3", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 100.5d) < 0.1d,
                $"stage1Empty={stage1Empty}; "
                + (first == null ? "empty" : $"{first.PaperName} {first.ScaleText} count={list.Count}"));
        }

        // A3 @ 1:143：默认 A3@143，且候选含可换算的 A2 等。
        {
            const double scale = 143d;
            var w = 420d * scale;
            var h = 297d * scale;
            var stage1 = PaperSizeDetector.DetectCandidates(w, h, modelOptions);
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(w, h, modelOptions);
            var first = list.FirstOrDefault();
            var hasA3 = list.Any(x => string.Equals(x.PaperName, "A3", StringComparison.OrdinalIgnoreCase));
            var a3 = list.FirstOrDefault(x => string.Equals(x.PaperName, "A3", StringComparison.OrdinalIgnoreCase));
            Case(
                "aspect_merge_A3_1to143_has_A3",
                stage1.Count == 0 && hasA3 && a3 != null && Math.Abs(a3.ScaleValue - scale) < 0.05d,
                $"stage1={stage1.Count}; first={(first == null ? "null" : first.PaperName + " " + first.ScaleText)}; "
                + $"A3={(a3 == null ? "null" : a3.ScaleText)}; names={string.Join(",", list.Select(x => x.PaperName))}");

            Case(
                "aspect_merge_A3_1to143_default_not_A2",
                first != null && !string.Equals(first.PaperName, "A2", StringComparison.OrdinalIgnoreCase),
                first == null ? "empty" : $"{first.PaperName} {first.ScaleText}");

            Case(
                "aspect_merge_A3_1to143_default_is_A3",
                first != null
                && string.Equals(first.PaperName, "A3", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - scale) < 0.05d,
                first == null ? "empty" : $"{first.PaperName} {first.ScaleText}");

            Case(
                "aspect_merge_A3_1to143_offers_A2",
                list.Any(x => string.Equals(x.PaperName, "A2", StringComparison.OrdinalIgnoreCase)),
                $"names={string.Join(",", list.Select(x => x.PaperName))}");
        }

        // 精确 A1 比例：默认 A1，下拉仍提供 A2/A3 换算项。
        {
            const double scale = 111.35d;
            var w = 841d * scale;
            var h = 594d * scale;
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(w, h, modelOptions);
            var first = list.FirstOrDefault();
            Case(
                "aspect_expand_A1_default_and_offers_siblings",
                first != null
                && string.Equals(first.PaperName, "A1", StringComparison.OrdinalIgnoreCase)
                && list.Any(x => string.Equals(x.PaperName, "A2", StringComparison.OrdinalIgnoreCase))
                && list.Any(x => string.Equals(x.PaperName, "A3", StringComparison.OrdinalIgnoreCase)),
                first == null
                    ? "empty"
                    : $"{first.PaperName} {first.ScaleText}; names={string.Join(",", list.Select(x => x.PaperName))}");
        }

        // 非 ISO 正方形应拒绝。
        {
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(10000d, 10000d, modelOptions);
            Case(
                "reject_square_non_iso",
                list.Count == 0,
                $"count={list.Count}");
        }

        // 自定义比例库含 143 时，A3@1:143 应直接贴库。
        {
            var options = PaperSizeDetector.CreateRectangleBatchOptions(1d, false, 3d, new[] { 143d });
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(420d * 143d, 297d * 143d, options);
            var first = list.FirstOrDefault();
            Case(
                "custom_scale_143_A3_defaults",
                first != null
                && string.Equals(first.PaperName, "A3", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 143d) < 0.001d,
                first == null ? "empty" : $"{first.PaperName} {first.ScaleText}");
        }

        // 不同比例下长宽比候选的公共图幅名应含 A3/A2。
        {
            var a = PaperSizeDetector.DetectRectangleBatchAspectRatioCandidates(420d * 143d, 297d * 143d);
            var b = PaperSizeDetector.DetectRectangleBatchAspectRatioCandidates(420d * 200d, 297d * 200d);
            var common = a.Select(x => x.PaperName)
                .Intersect(b.Select(x => x.PaperName), StringComparer.OrdinalIgnoreCase)
                .ToList();
            Case(
                "batch_aspect_common_names_include_A3",
                common.Any(x => string.Equals(x, "A3", StringComparison.OrdinalIgnoreCase))
                && common.Any(x => string.Equals(x, "A2", StringComparison.OrdinalIgnoreCase)),
                "common=" + string.Join(",", common));
        }
    }

    /// <summary>在当前图模型空间写入验证矩形并扫描（含非 ISO 干扰框）。</summary>
    private static void RunDrawingScanCases(Action<string, bool, string> Case)
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            Case("drawing_scan_has_document", false, "no active document");
            return;
        }

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // 写入独立图层，避免污染用户图元。
                const string layerName = "ZBP_VERIFY_RECT";
                EnsureLayer(tr, doc.Database, layerName);

                ClearLayerEntities(tr, ms, layerName);

                // A3@100、A2@100，以及一个非 ISO 矩形（应被过滤）。
                AddContentRectangle(tr, ms, layerName, 0, 0, 42000, 29700, "A3-100");
                AddContentRectangle(tr, ms, layerName, 50000, 0, 50000 + 59400, 42000, "A2-100");
                AddContentRectangle(tr, ms, layerName, 120000, 0, 130000, 10000, "NONISO");

                tr.Commit();
            }

            var results = RectangleFrameScanner.ScanScope(
                doc,
                TitleBlockScanScope.ModelSpace,
                paperMatchToleranceMm: 1d,
                recognizeFourLineRectangles: false);

            var papers = results.Select(r => r.Job.PaperName + "@" + r.Job.ScaleText).ToList();
            Case(
                "scan_finds_at_least_A3_and_A2",
                results.Any(r => string.Equals(r.Job.PaperName, "A3", StringComparison.OrdinalIgnoreCase))
                && results.Any(r => string.Equals(r.Job.PaperName, "A2", StringComparison.OrdinalIgnoreCase)),
                $"count={results.Count}; " + string.Join("; ", papers));

            Case(
                "scan_rejects_or_ignores_noniso_square",
                !results.Any(r =>
                {
                    var w = Math.Abs(r.Job.MaxX - r.Job.MinX);
                    var h = Math.Abs(r.Job.MaxY - r.Job.MinY);
                    // 干扰框约 10000×10000
                    return Math.Abs(w - 10000d) < 1d && Math.Abs(h - 10000d) < 1d;
                }),
                $"count={results.Count}; " + string.Join("; ", papers));

            Case(
                "scan_each_has_paper_options",
                results.Count > 0 && results.All(r => r.PaperOptions.Count > 0),
                results.Count == 0
                    ? "no results"
                    : string.Join("; ", results.Select(r => r.Job.PaperName + " opts=" + r.PaperOptions.Count)));
        }
        catch (System.Exception ex)
        {
            Case("drawing_scan_exception", false, ex.ToString());
        }
    }

    private static void EnsureLayer(Transaction tr, Database db, string layerName)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(layerName))
        {
            return;
        }

        lt.UpgradeOpen();
        var layer = new LayerTableRecord { Name = layerName };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    private static void ClearLayerEntities(Transaction tr, BlockTableRecord space, string layerName)
    {
        var erase = new List<ObjectId>();
        foreach (ObjectId id in space)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity
                && string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase))
            {
                erase.Add(id);
            }
        }

        foreach (var id in erase)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite, false);
            entity.Erase();
        }
    }

    /// <summary>写入带内容的闭合多段线矩形（含中心短线与文字标记），避免被空框过滤。</summary>
    private static void AddContentRectangle(
        Transaction tr,
        BlockTableRecord space,
        string layerName,
        double minX,
        double minY,
        double maxX,
        double maxY,
        string tag)
    {
        var pl = new Polyline();
        pl.SetDatabaseDefaults();
        pl.Layer = layerName;
        pl.Closed = true;
        pl.AddVertexAt(0, new Point2d(minX, minY), 0, 0, 0);
        pl.AddVertexAt(1, new Point2d(maxX, minY), 0, 0, 0);
        pl.AddVertexAt(2, new Point2d(maxX, maxY), 0, 0, 0);
        pl.AddVertexAt(3, new Point2d(minX, maxY), 0, 0, 0);
        space.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);

        var midX = (minX + maxX) * 0.5d;
        var midY = (minY + maxY) * 0.5d;
        var line = new Line(
            new Point3d(midX - 100d, midY, 0),
            new Point3d(midX + 100d, midY, 0));
        line.SetDatabaseDefaults();
        line.Layer = layerName;
        space.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);

        var text = new DBText();
        text.SetDatabaseDefaults();
        text.Layer = layerName;
        text.Position = new Point3d(midX, midY + 200d, 0);
        text.Height = 200d;
        text.TextString = tag;
        space.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
    }
}
