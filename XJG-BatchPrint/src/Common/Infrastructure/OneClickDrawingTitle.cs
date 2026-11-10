using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
#endif
namespace ZwcadBatchPlot;

/// <summary>普通文档命令：逐对选择文字和属性图框，不依赖已读取的属性样板。</summary>
internal static class OneClickDrawingTitle
{
    internal static void Run(Document? document)
    {
        if (document == null) return;
        var editor = document.Editor;
        editor.WriteMessage("\n一键图名：先点文字，再点目标图框；可连续操作，Esc结束。目标属性标记为“图名”。");
        while (true)
        {
            var source = editor.GetNestedEntity("\n选择图名文字（单行、多行、天正文字或块内文字，Esc结束）: ");
            if (source.Status != PromptStatus.OK) return;
            string title;
            using (var transaction = source.ObjectId.Database.TransactionManager.StartTransaction())
            {
                title = ReadText(transaction.GetObject(source.ObjectId, OpenMode.ForRead));
                transaction.Commit();
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                editor.WriteMessage("\n未读取到唯一图名（比例文字已忽略）。请点图名文字部分，并确认已加载天正或对应的对象解释器。");
                continue;
            }
            editor.WriteMessage("\n已记录图名：{0}", title.Replace("\n", " "));
            var options = new PromptEntityOptions("\n选择要写入“图名”属性的图框（Esc结束，本次不写入）: ");
            options.SetRejectMessage("\n请选择属性图框块参照。");
            options.AddAllowedClass(typeof(BlockReference), true);
            var target = editor.GetEntity(options);
            if (target.Status != PromptStatus.OK) return;
            try
            {
                int count;
                using (document.LockDocument()) count = Assign(document.Database, target.ObjectId, title);
                editor.Regen();
                editor.WriteMessage("\n已写入图名（" + count + "个同标记属性）；可继续选择下一处文字，Esc结束。");
                CadPickDiagnostics.Log("one-click-title", "assigned target=" + target.ObjectId + " count=" + count);
            }
            catch (Exception ex)
            {
                editor.WriteMessage("\n图名未写入：{0}", ex.Message);
                CadPickDiagnostics.Log("one-click-title-failed", ex.ToString());
            }
        }
    }

    internal static string ReadText(DBObject source)
    {
        if (source is AttributeReference attribute && attribute.IsMTextAttribute)
        {
            using var text = attribute.MTextAttribute;
            return Normalize(text.Text);
        }
        if (source is MText multiline) return Normalize(multiline.Text);
        if (source is DBText single) return Normalize(single.TextString);
        if (source is Entity entity)
        {
            if (TianzhengTextReader.TryGetText(entity, out var value, out _))
                return FilterTianzhengTitle(value);
            var dxf = entity.GetRXClass()?.DxfName ?? "";
            if (entity is not ProxyEntity && dxf.StartsWith("TCH_", StringComparison.OrdinalIgnoreCase))
            {
                var titles = new HashSet<string>(StringComparer.Ordinal);
                int budget = 128;
                CollectTitleParts(entity, titles, 0, ref budget);
                if (titles.Count == 1)
                    foreach (var title in titles) return title;
            }
        }
        return "";
    }

    // Apply only to Tianzheng objects: native text retains its original contents.
    internal static string FilterTianzhengTitle(string value)
    {
        var plain = Normalize(value).Replace("\\P", "\n");
        plain = Regex.Replace(plain,
            @"(?<![\p{L}\p{N}])(?:比例\s*[:：]?\s*)?\d+(?:\.\d+)?\s*[:：∶]\s*\d+(?:\.\d+)?(?![\p{L}\p{N}])",
            "", RegexOptions.CultureInvariant);
        plain = Regex.Replace(plain, @"(?m)^\s*比例\s*[:：]?\s*$", "");
        return Normalize(plain);
    }

    private static void CollectTitleParts(Entity entity, ISet<string> titles, int depth, ref int budget)
    {
        if (depth >= 4 || budget <= 0) { titles.Add(""); return; }
        var parts = new DBObjectCollection();
        try
        {
            entity.Explode(parts);
            foreach (DBObject part in parts)
            {
                if (--budget < 0) { titles.Add(""); return; }
                string text = "";
                if (part is MText multi) text = multi.Text;
                else if (part is DBText single) text = single.TextString;
                else if (part is Entity child && child is not ProxyEntity)
                {
                    var name = child.GetRXClass()?.DxfName ?? "";
                    if (TianzhengTextReader.TryGetText(child, out var value, out _)) text = value;
                    else if (child is BlockReference || name.StartsWith("TCH_", StringComparison.OrdinalIgnoreCase))
                        CollectTitleParts(child, titles, depth + 1, ref budget);
                }
                text = FilterTianzhengTitle(text);
                if (!string.IsNullOrWhiteSpace(text)) titles.Add(text);
            }
        }
        catch (Exception ex)
        {
            titles.Add(""); // Do not accept partial extraction as a unique title.
            CadPickDiagnostics.Log("one-click-title-read", ex.Message);
        }
        finally
        {
            foreach (DBObject part in parts) part.Dispose();
            parts.Dispose();
        }
    }

    internal static int Assign(Database database, ObjectId blockId, string value)
    {
        var title = Normalize(value);
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("所选文字没有内容。");
        using var transaction = database.TransactionManager.StartTransaction();
        var frame = transaction.GetObject(blockId, OpenMode.ForRead) as BlockReference
            ?? throw new InvalidOperationException("目标不是属性图框块参照。");
        var definition = (BlockTableRecord)transaction.GetObject(frame.BlockTableRecord, OpenMode.ForRead);
        if (definition.IsFromExternalReference) throw new InvalidOperationException("不能写入外部参照，请选择当前图纸中的属性图框。");
        if (((LayerTableRecord)transaction.GetObject(frame.LayerId, OpenMode.ForRead)).IsLocked)
            throw new InvalidOperationException("图框所在图层已锁定，请先解锁。");
        var targets = new List<AttributeReference>();
        foreach (ObjectId id in frame.AttributeCollection)
        {
            var attribute = (AttributeReference)transaction.GetObject(id, OpenMode.ForRead);
            if (!string.Equals(attribute.Tag.Trim(), "图名", StringComparison.OrdinalIgnoreCase)) continue;
            if (attribute.IsConstant) throw new InvalidOperationException("图名是常量属性，不能修改。");
            if (((LayerTableRecord)transaction.GetObject(attribute.LayerId, OpenMode.ForRead)).IsLocked)
                throw new InvalidOperationException("图名属性所在图层已锁定，本次未写入。");
            targets.Add(attribute);
        }
        if (targets.Count == 0)
            throw new InvalidOperationException("未找到标记为“图名”的属性，请插入更新后的模板或先同步图框属性。");
        foreach (var attribute in targets)
        {
            attribute.UpgradeOpen();
            if (attribute.IsMTextAttribute)
            {
                using var text = attribute.MTextAttribute;
                text.Contents = title.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("\n", "\\P");
                attribute.MTextAttribute = text;
                attribute.UpdateMTextAttribute();
            }
            else attribute.TextString = title.Replace("\n", " ");
            attribute.AdjustAlignment(database);
        }
        transaction.Commit();
        return targets.Count;
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
}
