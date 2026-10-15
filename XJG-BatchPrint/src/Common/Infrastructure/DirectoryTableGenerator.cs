using System;
using System.Collections.Generic;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

public static class DirectoryTableGenerator
{
    public static bool PromptAndGenerate(Document document, IReadOnlyList<PlotJob> jobs, AppSettings settings, out string message)
    {
        message = "";
        var selected = jobs.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            message = "没有勾选任何图纸。";
            return false;
        }

        if (GetEnabledColumns(settings).Count == 0)
        {
            message = "图纸目录没有启用任何字段，请先在设置中启用目录列。";
            return false;
        }

        var editor = document.Editor;
        var pointResult = editor.GetPoint(new PromptPointOptions("\n指定图纸目录左上角基点: "));
        if (pointResult.Status != PromptStatus.OK)
        {
            message = "已取消生成目录。";
            return false;
        }

        Generate(document, selected, settings, pointResult.Value);
        message = $"已生成图纸目录，共 {selected.Count} 行。";
        return true;
    }

    public static void Generate(Document document, IReadOnlyList<PlotJob> jobs, AppSettings settings, Point3d origin)
    {
        var columns = GetEnabledColumns(settings);
        if (columns.Count == 0)
        {
            return;
        }

        var headerRows = settings.DirectoryDrawHeader ? 1 : 0;
        var totalRows = jobs.Count + headerRows;
        var totalCols = columns.Count;
        var defaultRowHeight = Math.Max(1, settings.DirectoryRowHeight);
        var textHeight = Math.Max(1, settings.DirectoryTextHeight);
        var widthFactor = settings.DirectoryTextWidthFactor > 1e-6 ? settings.DirectoryTextWidthFactor : 0.7;

        using (document.LockDocument())
        using (var tr = document.Database.TransactionManager.StartTransaction())
        {
            var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
            var textStyleId = EnsureTextStyleId(tr, document.Database, settings.DirectoryTextStyleName, widthFactor);
            var layerName = EnsureLayer(tr, document.Database, settings.DirectoryLayerName);

            if (settings.DirectoryUseLinesAndText)
            {
                GenerateAsLinesAndText(tr, document, space, columns, jobs, settings, origin, textStyleId, layerName);
                tr.Commit();
                return;
            }

            // 参考 TAD：创建原生 Table 实体并初始化
            var tb = new Table { Position = origin };
            tb.SetDatabaseDefaults();
            tb.Layer = layerName;

            if (settings.DirectoryColorIndex > 0 && settings.DirectoryColorIndex <= 256)
            {
                tb.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)settings.DirectoryColorIndex);
            }

            tb.SetSize(totalRows, totalCols);
            tb.GenerateLayout();

            // 若表格默认带合并单元格，先行尝试取消合并
            try
            {
                if (tb.Cells[0, 0].IsMerged == true)
                {
                    tb.UnmergeCells(tb.Cells[0, 0].GetMergeRange());
                }
            }
            catch { }

            // 填充表头数据
            if (settings.DirectoryDrawHeader)
            {
                for (var col = 0; col < totalCols; col++)
                {
                    var cell = tb.Cells[0, col];
                    var rawText = ToCadDirectoryText(columns[col].Header);
                    // 1. 通过 \W{factor}; 格式化标签强制应用宽度因子
                    cell.TextString = FormatTextWithWidthFactor(rawText, widthFactor);
                    cell.TextHeight = textHeight;
                    cell.Alignment = columns[col].Centered ? CellAlignment.MiddleCenter : CellAlignment.MiddleLeft;
                    if (!textStyleId.IsNull)
                    {
                        cell.TextStyleId = textStyleId;
                    }
                }
            }

            // 填充图纸数据行
            for (var r = 0; r < jobs.Count; r++)
            {
                var tableRow = r + headerRows;
                for (var col = 0; col < totalCols; col++)
                {
                    var column = columns[col];
                    var value = GetColumnValue(column.Key, jobs[r], r, settings);
                    var cell = tb.Cells[tableRow, col];
                    var rawText = ToCadDirectoryText(value);

                    // 1. 通过 \W{factor}; 格式化标签强制应用宽度因子
                    cell.TextString = FormatTextWithWidthFactor(rawText, widthFactor);
                    cell.TextHeight = textHeight;
                    cell.Alignment = column.Centered ? CellAlignment.MiddleCenter : CellAlignment.MiddleLeft;
                    if (!textStyleId.IsNull)
                    {
                        cell.TextStyleId = textStyleId;
                    }
                }
            }

            // 统一行高
            for (var r = 0; r < totalRows; r++)
            {
                tb.Rows[r].Height = defaultRowHeight;
            }

            // 2. 自适应列宽算法（参考 TAD 中的 MText 测算逻辑）
            var maxColWidths = new double[totalCols];
            for (var col = 0; col < totalCols; col++)
            {
                for (var row = 0; row < totalRows; row++)
                {
                    var text = tb.Cells[row, col].TextString;
                    if (string.IsNullOrEmpty(text)) continue;

                    using (var mt = new MText())
                    {
                        mt.Contents = text;
                        mt.TextHeight = textHeight;
                        if (!textStyleId.IsNull) mt.TextStyleId = textStyleId;
                        mt.Width = 0; // 不自动折行
                        mt.LineSpacingFactor = 1.0;

                        double actW = mt.ActualWidth;
                        if (actW > maxColWidths[col])
                        {
                            maxColWidths[col] = actW;
                        }
                    }
                }
            }

            // 内外边距设置
#pragma warning disable 0618
            try
            {
                tb.HorizontalCellMargin = textHeight * 0.2;
                tb.VerticalCellMargin = textHeight * 0.1;
            }
            catch { }
#pragma warning restore 0618

            // 左右安全 padding（单元格边距外再保留适度余量，防止贴线）
            double absoluteColPadding = textHeight * 1.5;

            for (var c = 0; c < totalCols; c++)
            {
                double autoFitWidth = maxColWidths[c] + absoluteColPadding;
                // 若设置中有预设列宽，则取预设值与文字自适应所需宽度的较大者，保证装得下
                double configuredWidth = columns[c].Width;
                tb.Columns[c].Width = Math.Max(configuredWidth, autoFitWidth);
            }

            // 如果设置不绘制网格线，隐藏内外网格线
            if (!settings.DirectoryDrawGridLines)
            {
                try
                {
                    for (var r = 0; r < totalRows; r++)
                    {
                        for (var c = 0; c < totalCols; c++)
                        {
                            tb.Cells[r, c].Borders.Top.IsVisible = false;
                            tb.Cells[r, c].Borders.Bottom.IsVisible = false;
                            tb.Cells[r, c].Borders.Left.IsVisible = false;
                            tb.Cells[r, c].Borders.Right.IsVisible = false;
                        }
                    }
                }
                catch { }
            }

            tb.GenerateLayout();
            try { tb.RecomputeTableBlock(true); } catch { }

            space.AppendEntity(tb);
            tr.AddNewlyCreatedDBObject(tb, true);

            tr.Commit();
        }
    }

    /// <summary>
    /// 用「直线 + 单行文字」绘制目录（替代 CAD 原生 Table）。
    /// 直线画网格边框，DBText 填内容，导出后是普通单线表格和单行文字。
    /// </summary>
    private static void GenerateAsLinesAndText(
        Transaction tr,
        Document document,
        BlockTableRecord space,
        List<DirectoryColumnSetting> columns,
        IReadOnlyList<PlotJob> jobs,
        AppSettings settings,
        Point3d origin,
        ObjectId textStyleId,
        string layerName)
    {
        var headerRows = settings.DirectoryDrawHeader ? 1 : 0;
        var totalRows = jobs.Count + headerRows;
        var totalCols = columns.Count;
        var rowHeight = Math.Max(1, settings.DirectoryRowHeight);
        var textHeight = Math.Max(1, settings.DirectoryTextHeight);
        var widthFactor = settings.DirectoryTextWidthFactor > 1e-6 ? settings.DirectoryTextWidthFactor : 0.7;

        var color = ResolveDirectoryColor(settings);

        // 1. 收集所有单元格文本，并用 MText 测算每列最大宽度（与 Table 模式一致的算法）。
        var cellTexts = new string[totalRows, totalCols];
        var maxColWidths = new double[totalCols];
        for (var r = 0; r < totalRows; r++)
        {
            for (var c = 0; c < totalCols; c++)
            {
                var rawText = r < headerRows
                    ? ToCadDirectoryText(columns[c].Header)
                    : ToCadDirectoryText(GetColumnValue(columns[c].Key, jobs[r - headerRows], r - headerRows, settings));
                cellTexts[r, c] = rawText;
                if (string.IsNullOrEmpty(rawText))
                {
                    continue;
                }

                using var mt = new MText();
                mt.Contents = rawText;
                mt.TextHeight = textHeight;
                if (!textStyleId.IsNull)
                {
                    mt.TextStyleId = textStyleId;
                }

                mt.Width = 0;
                mt.LineSpacingFactor = 1.0;
                var actW = mt.ActualWidth;
                if (actW > maxColWidths[c])
                {
                    maxColWidths[c] = actW;
                }
            }
        }

        // 2. 计算每列最终宽度与累计 x 坐标。
        var colWidths = new double[totalCols];
        var xs = new double[totalCols + 1];
        xs[0] = origin.X;
        var absoluteColPadding = textHeight * 1.5;
        for (var c = 0; c < totalCols; c++)
        {
            colWidths[c] = Math.Max(columns[c].Width, maxColWidths[c] + absoluteColPadding);
            xs[c + 1] = xs[c] + colWidths[c];
        }

        var totalWidth = xs[totalCols] - origin.X;
        var totalHeight = totalRows * rowHeight;

        // 3. 画网格线（单线）：先竖线再横线，都放在目录图层、用目录颜色。
        for (var c = 0; c <= totalCols; c++)
        {
            var line = new Line(new Point3d(xs[c], origin.Y, 0), new Point3d(xs[c], origin.Y - totalHeight, 0));
            ApplyDirectoryEntityProps(line, color, layerName);
            space.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
        }

        for (var r = 0; r <= totalRows; r++)
        {
            var y = origin.Y - r * rowHeight;
            var line = new Line(new Point3d(origin.X, y, 0), new Point3d(origin.X + totalWidth, y, 0));
            ApplyDirectoryEntityProps(line, color, layerName);
            space.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
        }

        // 4. 填单行文字。
        for (var r = 0; r < totalRows; r++)
        {
            for (var c = 0; c < totalCols; c++)
            {
                var text = cellTexts[r, c];
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var cellLeft = xs[c];
                var cellTop = origin.Y - r * rowHeight;
                var centerX = (cellLeft + xs[c + 1]) / 2;
                var centerY = cellTop - rowHeight / 2;

                var dbText = new DBText
                {
                    TextString = text,
                    Height = textHeight,
                    WidthFactor = widthFactor,
                    HorizontalMode = columns[c].Centered ? TextHorizontalMode.TextCenter : TextHorizontalMode.TextLeft,
                    VerticalMode = TextVerticalMode.TextVerticalMid,
                    AlignmentPoint = columns[c].Centered
                        ? new Point3d(centerX, centerY, 0)
                        : new Point3d(cellLeft + textHeight * 0.2, centerY, 0)
                };
                ApplyDirectoryEntityProps(dbText, color, layerName);
                if (!textStyleId.IsNull)
                {
                    dbText.TextStyleId = textStyleId;
                }

                dbText.Position = dbText.AlignmentPoint;
                try
                {
                    dbText.AdjustAlignment(document.Database);
                }
                catch
                {
                    // 个别宿主不支持对非数据库文字调整对齐时，保留 Position=AlignmentPoint。
                }

                space.AppendEntity(dbText);
                tr.AddNewlyCreatedDBObject(dbText, true);
            }
        }
    }

    /// <summary>给目录线/文字设置颜色与图层（颜色索引无效时保持随层）。</summary>
    private static void ApplyDirectoryEntityProps(Entity entity, Color color, string layerName)
    {
        entity.Layer = layerName;
        entity.Color = color;
    }

    /// <summary>解析目录颜色：颜色索引 1~256 有效时按 ACI 取色，否则随层。</summary>
    private static Color ResolveDirectoryColor(AppSettings settings)
    {
        if (settings.DirectoryColorIndex > 0 && settings.DirectoryColorIndex <= 256)
        {
            return Color.FromColorIndex(ColorMethod.ByAci, (short)settings.DirectoryColorIndex);
        }

        return Color.FromColorIndex(ColorMethod.ByLayer, 256);
    }

    public static bool PromptColumnSize(
        Document document,
        AppSettings settings,
        string columnKey,
        out AppSettings updated,
        out string message)
    {
        updated = settings;
        var column = settings.DirectoryColumns.FirstOrDefault(x =>
            string.Equals(x.Key, columnKey, StringComparison.OrdinalIgnoreCase));
        if (column == null)
        {
            message = "没有找到要设置的目录字段。";
            return false;
        }

        var editor = document.Editor;
        var first = editor.GetPoint(new PromptPointOptions($"\n框选目录“{column.Header}”单元格第一个角点: "));
        if (first.Status != PromptStatus.OK)
        {
            message = "已取消目录列宽设置。";
            return false;
        }

        var second = editor.GetCorner(new PromptCornerOptions($"\n框选目录“{column.Header}”单元格对角点: ", first.Value));
        if (second.Status != PromptStatus.OK)
        {
            message = "已取消目录列宽设置。";
            return false;
        }

        var width = Math.Abs(second.Value.X - first.Value.X);
        if (width <= 1e-6)
        {
            message = "框选区域的宽度为 0，目录列宽未修改。";
            return false;
        }

        column.Width = width;
        if (!TrySaveSettings(updated, out message))
        {
            return false;
        }

        message = $"“{column.Header}”列宽已设置为 {width:0.##}。";
        return true;
    }

    public static bool PromptRowHeight(Document document, AppSettings settings, out AppSettings updated, out string message)
    {
        updated = settings;
        var options = new PromptDistanceOptions("\n在图中点取目录行高的两个端点: ")
        {
            UseDefaultValue = false,
            Only2d = true
        };
        using var trace = new CadPickTrace(document.Editor);
        CadPickDiagnostics.Log("directory-row-height", "GetDistance BEGIN");
        var result = document.Editor.GetDistance(options);
        CadPickDiagnostics.Log("directory-row-height", "GetDistance END status=" + result.Status
            + (result.Status == PromptStatus.OK ? " distance=" + result.Value : ""));
        if (result.Status != PromptStatus.OK)
        {
            message = "已取消目录行高设置。";
            return false;
        }

        var height = Math.Abs(result.Value);
        if (height <= 1e-6)
        {
            message = "量取的目录行高为 0，设置未修改。";
            return false;
        }

        updated.DirectoryRowHeight = height;
        if (!TrySaveSettings(updated, out message))
        {
            return false;
        }

        message = $"目录行高已设置为 {height:0.##}。";
        return true;
    }

    public static bool PromptTextAppearance(
        Document document,
        AppSettings settings,
        out AppSettings updated,
        out string message)
    {
        updated = settings;
        if (document == null)
        {
            message = "当前没有可用的 CAD 图纸。";
            return false;
        }

        try
        {
            var options = new PromptEntityOptions("\n点选一段文字作为图纸目录文字样式: ");
            options.SetRejectMessage("\n请选择单行文字、多行文字或属性文字。");
            options.AddAllowedClass(typeof(DBText), false);
            options.AddAllowedClass(typeof(MText), false);

            var selection = document.Editor.GetEntity(options);
            if (selection.Status != PromptStatus.OK)
            {
                message = "已取消点选目录文字样式。";
                return false;
            }

            using var tr = document.Database.TransactionManager.StartTransaction();
            if (tr.GetObject(selection.ObjectId, OpenMode.ForRead, false) is not Entity entity)
            {
                message = "选择的对象不是有效文字。";
                return false;
            }

            double textHeight;
            double widthFactor;
            ObjectId textStyleId;
            if (entity is DBText dbText)
            {
                textHeight = dbText.Height;
                widthFactor = dbText.WidthFactor;
                textStyleId = dbText.TextStyleId;
            }
            else if (entity is MText mText)
            {
                textHeight = mText.TextHeight;
                textStyleId = mText.TextStyleId;
                widthFactor = 1d;
                if (!textStyleId.IsNull
                    && tr.GetObject(textStyleId, OpenMode.ForRead, false) is TextStyleTableRecord mTextStyle
                    && mTextStyle.XScale > 0)
                {
                    widthFactor = mTextStyle.XScale;
                }
            }
            else
            {
                message = "请选择单行文字、多行文字或属性文字。";
                return false;
            }

            var textStyleName = "";
            if (!textStyleId.IsNull
                && tr.GetObject(textStyleId, OpenMode.ForRead, false) is TextStyleTableRecord textStyle)
            {
                textStyleName = textStyle.Name ?? "";
            }

            settings.DirectoryColorIndex = Math.Max(0, Math.Min(256, entity.ColorIndex));
            if (textHeight > 1e-6)
            {
                settings.DirectoryTextHeight = textHeight;
            }
            if (widthFactor > 1e-6)
            {
                settings.DirectoryTextWidthFactor = widthFactor;
            }
            settings.DirectoryTextStyleName = textStyleName;
            settings.DirectoryLayerName = string.IsNullOrWhiteSpace(entity.Layer) ? "0" : entity.Layer;

            tr.Commit();
            AppSettingsStore.Save(settings);
            updated = settings;
            message =
                $"已从所选文字读取目录样式：颜色 {settings.DirectoryColorIndex}，" +
                $"字高 {settings.DirectoryTextHeight:0.##}，宽度因子 {settings.DirectoryTextWidthFactor:0.##}，" +
                $"文字样式“{(string.IsNullOrWhiteSpace(settings.DirectoryTextStyleName) ? "默认" : settings.DirectoryTextStyleName)}”，" +
                $"图层“{settings.DirectoryLayerName}”。";
            return true;
        }
        catch (Exception ex)
        {
            message = "点选目录文字样式失败，请确认当前活动图纸仍然打开：" + ex.Message;
            return false;
        }
    }

    /// <summary>显式保存目录设置；失败时返回提示文本，由调用方弹窗告知用户。</summary>
    private static bool TrySaveSettings(AppSettings settings, out string message)
    {
        try
        {
            AppSettingsStore.Save(settings);
            message = "";
            return true;
        }
        catch (System.Exception ex)
        {
            message = AppSettingsStore.FormatSaveError(ex);
            return false;
        }
    }

    private static List<DirectoryColumnSetting> GetEnabledColumns(AppSettings settings)
    {
        return (settings.DirectoryColumns ?? new List<DirectoryColumnSetting>())
            .Where(x => x.Enabled && x.Width > 0)
            .Select(x => x.Clone())
            .ToList();
    }

    private static string GetColumnValue(string key, PlotJob job, int rowIndex, AppSettings settings)
    {
        return key switch
        {
            "Sequence" => (rowIndex + 1).ToString(),
            "DrawingNumber" => job.DrawingNumber,
            "Title" => job.Title,
            "PaperName" => FileNameSanitizer.NormalizeLongPaperFraction(
                OutputPaperNameResolver.Resolve(job, settings.LongPaperSnapToleranceMm),
                settings.LongPaperNameFormat),
            "Date" => job.Date,
            "Revision" => job.Revision,
            "Phase" => job.Phase,
            "Info1" => job.Info1,
            "Info2" => job.Info2,
            _ => ""
        } ?? "";
    }

    private static string ToCadDirectoryText(string value)
    {
        return (value ?? "").Replace('\u2215', '/');
    }

    /// <summary>
    /// 为文本增加 MText 宽度因子格式控制符，确保单元格严格执行该因子
    /// </summary>
    private static string FormatTextWithWidthFactor(string text, double factor)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return $"\\W{factor:0.##};{text}";
    }

    private static string EnsureLayer(Transaction tr, Database db, string? configuredName)
    {
        var layerName = string.IsNullOrWhiteSpace(configuredName) ? "0" : configuredName!.Trim();
        try
        {
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(layerName))
            {
                return layerName;
            }

            table.UpgradeOpen();
            var record = new LayerTableRecord { Name = layerName };
            table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return layerName;
        }
        catch
        {
            return "0";
        }
    }

    private static ObjectId EnsureTextStyleId(Transaction tr, Database db, string? textStyleName, double widthFactor)
    {
        if (string.IsNullOrWhiteSpace(textStyleName))
        {
            return ObjectId.Null;
        }

        try
        {
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            var styleName = textStyleName!.Trim();
            if (table.Has(styleName))
            {
                return table[styleName];
            }

            if (!string.Equals(styleName, "宋体", StringComparison.OrdinalIgnoreCase))
            {
                return ObjectId.Null;
            }

            table.UpgradeOpen();
            var record = new TextStyleTableRecord
            {
                Name = styleName,
                FileName = "simsun.ttc",
                XScale = widthFactor // TextStyle 级别同步设置 XScale
            };
            var id = table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return id;
        }
        catch
        {
            return ObjectId.Null;
        }
    }
}
