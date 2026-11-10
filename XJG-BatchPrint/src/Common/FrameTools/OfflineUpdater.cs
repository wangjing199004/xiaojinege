#nullable disable
#if AUTOCAD
#if ACAD_CORE
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
using CadDatabase = Autodesk.AutoCAD.DatabaseServices.Database;
using CadRuntime = Autodesk.AutoCAD.Runtime;
#else
using AcApp = ZwSoft.ZwCAD.ApplicationServices.Application;
using CadDatabase = ZwSoft.ZwCAD.DatabaseServices.Database;
using CadRuntime = ZwSoft.ZwCAD.Runtime;
#endif
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
#endif
using System;
using System.IO;
using System.Collections.Generic;
namespace TitleAttributeBatch.Core
{
    internal sealed class AttributeUpdateResult { internal bool Changed; internal string Detail; }
    internal static class OfflineUpdater
    {
        internal static SampleData ReadSample(Database db)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var representatives = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId spaceId in table)
                {
                    var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    if (!space.IsLayout || space.IsFromExternalReference) continue;
                    foreach (ObjectId id in space)
                    {
                        var block = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (block == null || block.AttributeCollection.Count == 0) continue;
                        var definition = (BlockTableRecord)tr.GetObject(block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord, OpenMode.ForRead);
                        if (definition.IsFromExternalReference) continue;
                        if (definition.Name.IndexOf("图框", StringComparison.Ordinal) < 0) continue;
                        bool editable = false;
                        foreach (ObjectId aid in block.AttributeCollection)
                        {
                            var attribute = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                            if (attribute != null && !attribute.IsConstant) { editable = true; break; }
                        }
                        if (!editable) continue;
                        string name = ZwcadBatchPlot.StandardFrameIdentity.GetCanonicalName(definition, tr);
                        if (!counts.ContainsKey(name)) { counts.Add(name, 0); representatives.Add(name, id); }
                        counts[name]++;
                    }
                }
                string best = null;
                foreach (var pair in counts)
                    if (best == null || pair.Value > counts[best] || (pair.Value == counts[best] && StringComparer.OrdinalIgnoreCase.Compare(pair.Key, best) < 0)) best = pair.Key;
                if (best == null) throw new InvalidOperationException("文件的模型空间和布局中没有块名包含“图框”且具有可编辑属性的图块。");
                var sample = (BlockReference)tr.GetObject(representatives[best], OpenMode.ForRead);
                var values = new List<Field>();
                var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId aid in sample.AttributeCollection)
                {
                    var attribute = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                    if (attribute == null || attribute.IsConstant || !tags.Add(attribute.Tag)) continue;
                    string value;
                    if (attribute.IsMTextAttribute) { using (var text = attribute.MTextAttribute) value = text.Contents; }
                    else value = attribute.TextString;
                    values.Add(new Field { Tag = attribute.Tag, Value = value });
                }
                tr.Commit();
                return new SampleData { BlockName = best, Count = counts[best], Fields = values };
            }
        }
        internal static string Update(string path, string blockName, Dictionary<string, string> fields)
        {
            if (!File.Exists(path)) throw new IOException("文件不存在。");
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("文件为只读，未修改。");
            if (File.Exists(Path.ChangeExtension(path, ".dwl")) || File.Exists(Path.ChangeExtension(path, ".dwl2")))
                return "跳过：检测到 CAD 占用标记，请关闭该图纸后重试";
            string before = SafeDwgSave.Digest(path);
            string unique = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N");
            string temporary = Path.Combine(Path.GetDirectoryName(path), "." + Path.GetFileNameWithoutExtension(path) + ".tkattr_" + unique + ".dwg");
            AttributeUpdateResult update;
            try
            {
                using (var db = new Database(false, true))
                {
                    db.ReadDwgFile(path, FileOpenMode.OpenForReadAndWriteNoShare, false, null);
                    db.CloseInput(true);
                    update = UpdateDatabase(db, blockName, fields);
                    if (!update.Changed) return "未保存：" + update.Detail;
                    db.SaveAs(temporary, db.OriginalFileVersion);
                }
                string backup = TempBackupStore.Session.NextBackup(path);
                SafeDwgSave.Replace(temporary, path, backup, before);
                return "已保存：" + update.Detail + "；备份：" + backup;
            }
            finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch { } } }
        }
        internal static AttributeUpdateResult UpdateDatabase(Database db, string blockName, Dictionary<string, string> fields)
        {
            int blocks = 0, changed = 0, locked = 0, matched = 0, missing = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId spaceId in table)
                {
                    var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    if (!space.IsLayout || space.IsFromExternalReference) continue;
                    foreach (ObjectId id in space)
                    {
                        var block = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (block == null || block.AttributeCollection.Count == 0) continue;
                        var definition = (BlockTableRecord)tr.GetObject(block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord, OpenMode.ForRead);
                        if (definition.IsFromExternalReference || !string.Equals(ZwcadBatchPlot.StandardFrameIdentity.GetCanonicalName(definition, tr), blockName, StringComparison.OrdinalIgnoreCase)) continue;
                        matched++;
                        if (((LayerTableRecord)tr.GetObject(block.LayerId, OpenMode.ForRead)).IsLocked) { locked++; continue; }
                        int count = 0;
                        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (ObjectId aid in block.AttributeCollection)
                        {
                            var attribute = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                            string value;
                            if (attribute == null || attribute.IsConstant || !fields.TryGetValue(attribute.Tag, out value)) continue;
                            found.Add(attribute.Tag);
                            string current;
                            if (attribute.IsMTextAttribute) { using (var text = attribute.MTextAttribute) current = text.Contents; }
                            else current = attribute.TextString;
                            if (current == value) continue;
                            var layer = (LayerTableRecord)tr.GetObject(attribute.LayerId, OpenMode.ForRead);
                            if (layer.IsLocked) throw new InvalidOperationException("属性图层已锁定：" + layer.Name + "，本文件未保存。");
                            attribute.UpgradeOpen();
                            if (attribute.IsMTextAttribute)
                            {
                                using (var text = attribute.MTextAttribute) { text.Contents = value; attribute.MTextAttribute = text; }
                                attribute.UpdateMTextAttribute();
                            }
                            else attribute.TextString = value;
                            count++;
                        }
                        missing += fields.Count - found.Count;
                        if (count > 0) { blocks++; changed += count; }
                    }
                }
                tr.Commit();
            }
            return new AttributeUpdateResult {
                Changed = changed > 0,
                Detail = string.Format("匹配 {0} 个图框，修改 {1} 个图框 / {2} 个属性；锁定跳过 {3} 个；缺少字段 {4} 项", matched, blocks, changed, locked, missing)
            };
        }
    }
}
