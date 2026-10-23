using System;
using System.Collections.Generic;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

internal static class StandardFrameInserter
{
    public static ObjectId Insert(Database destination, ObjectId spaceId, string templateId, Point3d point)
        => InsertWithPlacement(destination, spaceId, templateId, (frame, transaction) =>
        {
            MoveCenter(frame, transaction, point);
            return true;
        });

    public static bool PromptAndInsert(Document document, string templateId)
        => !InsertWithPlacement(document.Database, document.Database.CurrentSpaceId, templateId,
            (frame, transaction) =>
            {
                var jig = new StandardFramePlacementJig(frame, transaction);
                return document.Editor.Drag(jig).Status == PromptStatus.OK;
            }).IsNull;

    internal static ObjectId InsertWithPlacement(Database destination, ObjectId spaceId, string templateId,
        Func<BlockReference, Transaction, bool> place)
    {
        var item = StandardFrameCatalog.Get(templateId);
        using var source = new Database(false, true);
        // source 在临时文件句柄前释放，读取失败时也能先关闭输入再删除临时文件。
        var materialized = HpDirectoryTemplates.OpenResource(item.Resource);
        try
        {
            source.ReadDwgFile(materialized.Path, FileOpenMode.OpenForReadAndAllShare, true, "");
            source.CloseInput(true);
        }
        catch
        {
            source.Dispose();
            throw;
        }
        finally { materialized.Dispose(); }

        ObjectId sourceFrame;
        var previousDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = source;
        try
        {
            using var tr = source.TransactionManager.StartTransaction();
            var layers = (LayerTable)tr.GetObject(source.LayerTableId, OpenMode.ForRead);
            var zero = layers["0"];
            DwgDatabaseCleanup.UnlockLayers(tr, source);
            var blocks = (BlockTable)tr.GetObject(source.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in blocks)
            {
                var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                if (block.IsFromExternalReference) continue;
                foreach (ObjectId id in block)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                    entity.UpgradeOpen();
                    entity.LayerId = zero;
                    if (entity is AttributeDefinition definition) definition.TextString = "";
                    if (entity is BlockReference reference)
                        foreach (ObjectId attributeId in reference.AttributeCollection)
                        {
                            var attribute = (AttributeReference)tr.GetObject(attributeId, OpenMode.ForWrite);
                            attribute.LayerId = zero;
                            attribute.TextString = "";
                        }
                }
            }
            var model = (BlockTableRecord)tr.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var roots = model.Cast<ObjectId>().ToList();
            if (roots.Count != 1 || tr.GetObject(roots[0], OpenMode.ForRead) is not BlockReference frame)
                throw new InvalidOperationException("内置标准图框应包含一个完整图框块。");
            // 同一模板使用固定的有效块名；动态匿名块仍由 CAD 按实例维护。
            var definitionId = frame.IsDynamicBlock ? frame.DynamicBlockTableRecord : frame.BlockTableRecord;
            var sharedDefinition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForWrite);
            sharedDefinition.Name = item.Name;
            if (sharedDefinition.ExtensionDictionary.IsNull) sharedDefinition.CreateExtensionDictionary();
            var metadata = (DBDictionary)tr.GetObject(sharedDefinition.ExtensionDictionary, OpenMode.ForWrite);
            var marker = new Xrecord { Data = new ResultBuffer(new TypedValue(1, item.Id)) };
            metadata.SetAt("XJG_STANDARD_TEMPLATE", marker);
            tr.AddNewlyCreatedDBObject(marker, true);
            sourceFrame = frame.ObjectId;
            tr.Commit();
        }
        finally { HostApplicationServices.WorkingDatabase = previousDatabase; }

        using var mapping = new IdMapping();
        var insertedId = ObjectId.Null;
        var stagingId = ObjectId.Null;
        var placed = false;
        var committed = false;
        List<(ObjectId LayerId, bool WasLocked)>? locks = null;
        try
        {
            using (var targetTransaction = destination.TransactionManager.StartTransaction())
            {
                var targetLayer = destination.Clayer;
                locks = DwgDatabaseCleanup.UnlockLayers(targetTransaction, destination);
                // 预览前不把参照加入模型/布局空间，否则宿主会把它和Jig一起绘制，产生原位残影。
                var blockTable = (BlockTable)targetTransaction.GetObject(destination.BlockTableId, OpenMode.ForWrite);
                var sharedName = item.Name;
                if (blockTable.Has(sharedName))
                {
                    var existing = (BlockTableRecord)targetTransaction.GetObject(blockTable[sharedName], OpenMode.ForRead);
                    var valid = false;
                    if (!existing.ExtensionDictionary.IsNull)
                    {
                        var metadata = (DBDictionary)targetTransaction.GetObject(existing.ExtensionDictionary, OpenMode.ForRead);
                        if (metadata.Contains("XJG_STANDARD_TEMPLATE"))
                        {
                            var marker = (Xrecord)targetTransaction.GetObject(metadata.GetAt("XJG_STANDARD_TEMPLATE"), OpenMode.ForRead);
                            using var data = marker.Data;
                            valid = data != null && data.AsArray().Any(value => value.TypeCode == 1 && Equals(value.Value, item.Id));
                        }
                    }
                    if (!valid) throw new InvalidOperationException("当前图纸存在同名非标准图框块：" + sharedName + "，请先重命名该块。");
                }
                var staging = new BlockTableRecord { Name = "XJG_PREVIEW_" + Guid.NewGuid().ToString("N") };
                stagingId = blockTable.Add(staging);
                targetTransaction.AddNewlyCreatedDBObject(staging, true);
                source.WblockCloneObjects(new ObjectIdCollection(new[] { sourceFrame }), stagingId, mapping,
                    DuplicateRecordCloning.Ignore, false);
                insertedId = mapping[sourceFrame].Value;
                var inserted = (BlockReference)targetTransaction.GetObject(insertedId, OpenMode.ForWrite);
                SetMinimumDistance(inserted);
                inserted.LayerId = targetLayer;
                placed = place(inserted, targetTransaction);
                if (placed)
                {
                    var targetSpace = (BlockTableRecord)targetTransaction.GetObject(spaceId, OpenMode.ForWrite);
                    targetSpace.AssumeOwnershipOf(new ObjectIdCollection(new[] { insertedId }));
                    staging.Erase();
                    DwgDatabaseCleanup.RestoreLayerLocks(targetTransaction, locks);
                    targetTransaction.Commit();
                    committed = true;
                }
            }
        }
        catch
        {
            CleanupCancelledClone(destination, insertedId, stagingId, mapping);
            throw;
        }
        finally
        {
            // 克隆期间的宿主内部提交也可能使解锁状态越过外层回滚，明确还原原图层锁。
            if (!committed && locks != null)
            {
                using var restore = destination.TransactionManager.StartTransaction();
                DwgDatabaseCleanup.RestoreLayerLocks(restore, locks);
                restore.Commit();
            }
        }
        if (placed) return insertedId;
        CleanupCancelledClone(destination, insertedId, stagingId, mapping);
        return ObjectId.Null;
    }

    private static void CleanupCancelledClone(Database database, ObjectId root, ObjectId staging, IdMapping mapping)
    {
        // WblockCloneObjects 的对象不全受外层事务回滚管理；取消后明确删除本次参照，
        // 再仅清理本次克隆产生、已不再引用的符号，不清理用户原图的其他对象。
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            if (!root.IsNull && root.IsValid && !root.IsErased)
                transaction.GetObject(root, OpenMode.ForWrite, false, true).Erase();
            if (!staging.IsNull && staging.IsValid && !staging.IsErased)
                transaction.GetObject(staging, OpenMode.ForWrite).Erase();
            transaction.Commit();
        }
        var symbols = new List<ObjectId>();
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            foreach (IdPair pair in mapping)
                if (pair.IsCloned && pair.Value.IsValid && !pair.Value.IsErased
                    && transaction.GetObject(pair.Value, OpenMode.ForRead) is SymbolTableRecord)
                    symbols.Add(pair.Value);
            transaction.Commit();
        }
        for (var pass = 0; pass < symbols.Count; pass++)
        {
            var remaining = symbols.Where(id => id.IsValid && !id.IsErased).ToArray();
            if (remaining.Length == 0) break;
            var candidates = new ObjectIdCollection(remaining);
            database.Purge(candidates);
            if (candidates.Count == 0) break;
            using var transaction = database.TransactionManager.StartTransaction();
            foreach (ObjectId id in candidates)
                if (!id.IsErased) transaction.GetObject(id, OpenMode.ForWrite, false, true).Erase();
            transaction.Commit();
        }
    }

    internal static Point3d GetCenter(BlockReference frame)
    {
        var bounds = frame.GeometricExtents;
        return bounds.MinPoint + (bounds.MaxPoint - bounds.MinPoint) * 0.5;
    }

    internal static void MoveCenter(BlockReference frame, Transaction transaction, Point3d point)
        => MoveBy(frame, transaction, point - GetCenter(frame));

    internal static void MoveBy(BlockReference frame, Transaction transaction, Vector3d displacement)
    {
        var anchors = new List<(AttributeReference Attribute, Point3d Position)>();
        foreach (ObjectId id in frame.AttributeCollection)
        {
            var attribute = (AttributeReference)transaction.GetObject(id, OpenMode.ForWrite);
            anchors.Add((attribute, attribute.Position));
        }
        // 仅平移参照，不改块定义原点、动态参数基点或模板原文件。
        frame.TransformBy(Matrix3d.Displacement(displacement));
        foreach (var anchor in anchors)
        {
            anchor.Attribute.TransformBy(Matrix3d.Displacement(anchor.Position + displacement - anchor.Attribute.Position));
            anchor.Attribute.AdjustAlignment(frame.Database);
        }
    }

    private static void SetMinimumDistance(BlockReference frame)
    {
        if (!frame.IsDynamicBlock) return;
        foreach (DynamicBlockReferenceProperty property in frame.DynamicBlockReferencePropertyCollection)
        {
            if (property.ReadOnly || !string.Equals(property.PropertyName.Trim(), "距离1", StringComparison.Ordinal)) continue;
            var allowed = property.GetAllowedValues();
            if (allowed.Length == 0)
                throw new InvalidOperationException("图框的“距离1”未提供允许值，无法确定最小插入尺寸。");
            // 使用模板本身的允许值，保留宿主要求的数值类型，不将距离强制设为0。
            var minimum = allowed.OrderBy(value => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)).First();
            property.Value = minimum;
            var actual = Convert.ToDouble(property.Value, System.Globalization.CultureInfo.InvariantCulture);
            var expected = Convert.ToDouble(minimum, System.Globalization.CultureInfo.InvariantCulture);
            if (Math.Abs(actual - expected) > 1e-6)
                throw new InvalidOperationException("无法将图框的“距离1”设置为最小允许值。");
        }
    }

}
