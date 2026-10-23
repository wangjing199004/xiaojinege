using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>HP 专用模板输出，不接受 AppSettings，原目录设置完全不参与。</summary>
internal static class HpDirectoryExporter
{
    public static int Insert(Database destination, ObjectId spaceId, IReadOnlyList<PlotJob> jobs,
        Point3d topLeft)
    {
        var selected = jobs.Where(job => job.Selected).ToList();
        var pages = HpDirectoryPlan.Create(selected.Count);
        using var targetTransaction = destination.TransactionManager.StartTransaction();
        var layerLocks = DwgDatabaseCleanup.UnlockLayers(targetTransaction, destination);
        var nextX = topLeft.X;
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            using var prepared = PreparePage(
                selected.Skip(page.Offset).Take(page.Count).ToList(), page, index + 1, out var bounds);
            ObjectIdCollection entities;
            using (var sourceTransaction = prepared.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)sourceTransaction.GetObject(prepared.BlockTableId, OpenMode.ForRead);
                var model = (BlockTableRecord)sourceTransaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                entities = new ObjectIdCollection(model.Cast<ObjectId>().ToArray());
                sourceTransaction.Commit();
            }
            using var mapping = new IdMapping();
            // 每次插入独立块定义，避免同名 A3/A4 图块改变此前生成的目录。
            prepared.WblockCloneObjects(entities, spaceId, mapping, DuplicateRecordCloning.MangleName, false);
            var delta = new Vector3d(nextX - bounds.MinPoint.X, topLeft.Y - bounds.MaxPoint.Y, topLeft.Z - bounds.MinPoint.Z);
            foreach (ObjectId sourceId in entities)
            {
                var entity = (Entity)targetTransaction.GetObject(mapping[sourceId].Value, OpenMode.ForWrite);
                var attributes = new List<(AttributeReference Attribute, Point3d Position)>();
                if (entity is BlockReference reference)
                    foreach (ObjectId attributeId in reference.AttributeCollection)
                    {
                        var attribute = (AttributeReference)targetTransaction.GetObject(attributeId, OpenMode.ForWrite);
                        attributes.Add((attribute, attribute.Position));
                    }
                entity.TransformBy(Matrix3d.Displacement(delta));
                // 不同宿主对块的 TransformBy 是否带动属性行为不同，明确校正到原位置+位移。
                foreach (var attribute in attributes)
                {
                    attribute.Attribute.TransformBy(Matrix3d.Displacement(attribute.Position + delta - attribute.Attribute.Position));
                    attribute.Attribute.AdjustAlignment(destination);
                }
            }
            nextX += bounds.MaxPoint.X - bounds.MinPoint.X + Math.Max(500, (bounds.MaxPoint.X - bounds.MinPoint.X) * 0.05);
        }
        DwgDatabaseCleanup.RestoreLayerLocks(targetTransaction, layerLocks);
        targetTransaction.Commit();
        return pages.Count;
    }

    private static Database PreparePage(IReadOnlyList<PlotJob> rows,
        HpDirectoryPlan.Page page, int pageNumber, out Extents3d frameBounds)
    {
        var db = new Database(false, true);
        var previous = HostApplicationServices.WorkingDatabase;
        HpDirectoryTemplates.TemplateFile? template = null;
        try
        {
            template = HpDirectoryTemplates.Open(page.Paper);
            db.ReadDwgFile(template.Path, FileOpenMode.OpenForReadAndAllShare, true, "");
            db.CloseInput(true);
            template.Dispose();
            template = null;
            HostApplicationServices.WorkingDatabase = db;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var blockName = page.Paper + "目录";
                if (!table.Has(blockName)) throw new InvalidOperationException("模板缺少目录图框块：" + blockName);
                var block = (BlockTableRecord)tr.GetObject(table[blockName], OpenMode.ForWrite);
                var texts = block.Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead))
                    .OfType<DBText>().Where(text => text is not AttributeDefinition).ToList();
                var headers = texts.Where(text => text.TextString.Trim() == "序号")
                    .OrderBy(text => text.AlignmentPoint.X).ToList();
                if (headers.Count != (page.Capacity == 59 ? 2 : 1))
                    throw new InvalidOperationException("模板目录列结构不符，请恢复原始 A3/A4 模板。");

                var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var references = model.Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead))
                    .OfType<BlockReference>().Where(reference => reference.BlockTableRecord == block.ObjectId).ToList();
                if (references.Count != 1) throw new InvalidOperationException("模板模型空间应包含一个 HP 目录图框块。");
                var frame = references[0];
                frameBounds = frame.GeometricExtents;
                // 清空实例值和定义默认值，保留属性本身，避免新插入或属性同步恢复模板示例内容。
                foreach (ObjectId id in block)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition definition) continue;
                    definition.UpgradeOpen();
                    definition.TextString = "";
                }
                foreach (ObjectId id in frame.AttributeCollection)
                {
                    var attribute = (AttributeReference)tr.GetObject(id, OpenMode.ForWrite);
                    attribute.TextString = "";
                }


                for (var slot = 1; slot <= page.Capacity; slot++)
                {
                    var candidates = texts.Where(text => text.TextString.Trim() == slot.ToString(CultureInfo.InvariantCulture)).ToList();
                    if (candidates.Count != 1) throw new InvalidOperationException($"模板第 {slot} 行序号不唯一或缺失。");
                    var marker = candidates[0];
                    var column = headers.OrderBy(header => Math.Abs(header.AlignmentPoint.X - marker.Position.X)).First();
                    var x = column.AlignmentPoint.X;
                    var y = marker.Position.Y + marker.Height * 0.48;
                    var row = slot <= rows.Count ? rows[slot - 1] : null;
                    marker.UpgradeOpen();
                    marker.TextString = (page.Offset + slot).ToString(CultureInfo.InvariantCulture);
                    marker.Justify = AttachmentPoint.MiddleCenter;
                    marker.AlignmentPoint = new Point3d(x, y, marker.Position.Z);
                    marker.AdjustAlignment(db);
                    if (row == null) continue;
                    // 本模板五列宽度固定为 2000/2000/11100/2000/2000；以序号列中心校准半单位差异。
                    AddText(tr, db, model, frame.BlockTransform, marker, x + 2000, y, 1800, row.DrawingNumber);
                    AddText(tr, db, model, frame.BlockTransform, marker, x + 8550, y, 10900, row.Title);
                    AddText(tr, db, model, frame.BlockTransform, marker, x + 15100, y, 1800,
                        string.IsNullOrWhiteSpace(row.PaperName) ? row.SizeText : row.PaperName);
                    AddText(tr, db, model, frame.BlockTransform, marker, x + 17100, y, 1800, row.Revision);
                }
                // 页码为标题栏的普通文字 01，不是目录第 1 行。
                var footer = texts.SingleOrDefault(text => text.TextString.Trim() == "01");
                if (footer != null)
                {
                    footer.UpgradeOpen();
                    footer.TextString = pageNumber.ToString("00", CultureInfo.InvariantCulture);
                }
                tr.Commit();
            }
            return db;
        }
        catch
        {
            HostApplicationServices.WorkingDatabase = previous;
            db.Dispose();
            throw;
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = previous;
            template?.Dispose();
        }
    }

    private static void AddText(Transaction tr, Database db, BlockTableRecord model, Matrix3d frameTransform,
        DBText style, double x, double y, double width, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var text = new DBText();
        text.SetDatabaseDefaults(db);
        text.TextStyleId = style.TextStyleId;
        text.LayerId = style.LayerId;
        text.Color = style.Color;
        text.Height = style.Height;
        text.WidthFactor = style.WidthFactor;
        text.TextString = value!.Trim().Replace("\r", " ").Replace("\n", " ");
        text.Justify = AttachmentPoint.MiddleCenter;
        text.AlignmentPoint = new Point3d(x, y, style.Position.Z);
        text.AdjustAlignment(db);
        var extents = text.GeometricExtents;
        var measuredWidth = extents.MaxPoint.X - extents.MinPoint.X;
        if (measuredWidth > width)
        {
            text.WidthFactor *= width / measuredWidth;
            text.AdjustAlignment(db);
        }
        text.TransformBy(frameTransform);
        model.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
    }
}
