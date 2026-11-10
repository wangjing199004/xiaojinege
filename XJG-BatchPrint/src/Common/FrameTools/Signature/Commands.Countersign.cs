#nullable disable
using System;
using System.Collections.Generic;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif
namespace CadSignature
{
    public partial class Commands
    {
        private sealed class CounterLabel
        {
            public string Text;
            public Point3d Center;
        }

        // Work in the actual (including dynamic visibility/stretch) block definition.
        private static void CounterLabels(BlockTableRecord block, Transaction tr, Matrix3d transform,
            List<CounterLabel> labels, HashSet<ObjectId> path, int depth, ref int budget)
        {
            if (depth > 12 || !path.Add(block.ObjectId)) return;
            try
            {
                foreach (ObjectId id in block)
                {
                    if (--budget < 0) throw new InvalidOperationException("会签栏图框内容过多，未自动填写。");
                    Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null || !entity.Visible) continue;
                    var nested = entity as BlockReference;
                    if (nested != null)
                    {
                        var definition = (BlockTableRecord)tr.GetObject(nested.BlockTableRecord, OpenMode.ForRead);
                        if (!definition.IsFromExternalReference)
                            CounterLabels(definition, tr, transform * nested.BlockTransform, labels, path, depth + 1, ref budget);
                        continue;
                    }
                    string value = entity is MText ? ((MText)entity).Text : entity is DBText ? ((DBText)entity).TextString : "";
                    value = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", "");
                    if (value != "实名打印栏" && value != "项目负责人" && value != "专业负责人" && value != "设计人") continue;
                    var ext = entity.GeometricExtents;
                    var center = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0).TransformBy(transform);
                    labels.Add(new CounterLabel { Text = value, Center = center });
                }
            }
            finally { path.Remove(block.ObjectId); }
        }

        private static bool TryCountersignCenters(BlockReference frame, Transaction tr, out Point3d[] centers)
        {
            centers = null;
            var labels = new List<CounterLabel>();
            var definition = (BlockTableRecord)tr.GetObject(frame.BlockTableRecord, OpenMode.ForRead);
            if (definition.IsFromExternalReference) return false;
            int budget = 20000;
            CounterLabels(definition, tr, Matrix3d.Identity, labels, new HashSet<ObjectId>(), 0, ref budget);
            var headers = labels.FindAll(x => x.Text == "实名打印栏");
            if (headers.Count != 1) return false;
            var header = headers[0];
            string[] roles = { "项目负责人", "专业负责人", "设计人" };
            centers = new Point3d[3];
            for (int i = 0; i < roles.Length; i++)
            {
                CounterLabel best = null;
                foreach (var label in labels)
                    if (label.Text == roles[i] && label.Center.Y < header.Center.Y && label.Center.X < header.Center.X
                        && (best == null || label.Center.Y > best.Center.Y)) best = label;
                if (best == null) { centers = null; return false; }
                centers[i] = new Point3d(header.Center.X, best.Center.Y, header.Center.Z).TransformBy(frame.BlockTransform);
            }
            return true;
        }

        private static bool IsDirectoryName(string name)
        {
            return name.IndexOf("目录", StringComparison.Ordinal) >= 0 &&
                (name.IndexOf("签章", StringComparison.Ordinal) >= 0 || name.IndexOf("图签", StringComparison.Ordinal) >= 0);
        }

        private static bool IsDirectoryFrame(BlockReference frame, Transaction tr)
        {
            if (frame == null) return false;
            var definition = (BlockTableRecord)tr.GetObject(frame.IsDynamicBlock ? frame.DynamicBlockTableRecord : frame.BlockTableRecord, OpenMode.ForRead);
            return IsDirectoryName(definition.Name);
        }

        private static BlockReference FindCountersignFrame(BlockTableRecord space, Transaction tr, Zone zone, out Point3d[] positions)
        {
            // Manual signing has only a signature-zone selection: find its containing frame.
            var center = new Point3d((zone.Left + zone.Right) / 2, (zone.Bottom + zone.Top) / 2, 0);
            BlockReference found = null;
            positions = null;
            foreach (ObjectId id in space)
            {
                var frame = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (frame == null || !frame.Visible || frame.Layer == SignatureLayer) continue;
                if (offlineFrameIds != null && Array.IndexOf(offlineFrameIds, id) < 0) continue;
                try
                {
                    var ext = frame.GeometricExtents;
                    if (center.X < ext.MinPoint.X || center.X > ext.MaxPoint.X || center.Y < ext.MinPoint.Y || center.Y > ext.MaxPoint.Y) continue;
                    Point3d[] points;
                    if (!TryCountersignCenters(frame, tr, out points)) continue;
                    if (found != null)
                    {
                        ZwcadBatchPlot.CadPickDiagnostics.Log("countersign", "ambiguous containing frame; skipped");
                        return null;
                    }
                    found = frame; positions = points;
                }
                catch (Exception ex) { ZwcadBatchPlot.CadPickDiagnostics.Log("countersign-read", ex.Message); }
            }
            return found;
        }

        private static void AddCountersignNames(Database db, BlockTableRecord space, Transaction tr, Zone zone,
            Dictionary<string, PersonEntry> people)
        {
            Point3d[] positions;
            var found = FindCountersignFrame(space, tr, zone, out positions);
            if (found == null) return;
            // Remove only names previously written by this feature for this exact frame.
            var obsolete = new List<ObjectId>();
            foreach (ObjectId id in space)
            {
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null) continue;
                using (var data = entity.GetXDataForApplication(AppName))
                {
                    if (data == null) continue;
                    var values = data.AsArray();
                    if (values.Length >= 4 && Convert.ToString(values[1].Value) == "COUNTER_NAME"
                        && Convert.ToString(values[3].Value) == found.Handle.ToString()) obsolete.Add(id);
                }
            }
            foreach (var id in obsolete) tr.GetObject(id, OpenMode.ForWrite).Erase();
            string[] roles = { "项目负责人", "专业负责人", "设计" };
            for (int i = 0; i < roles.Length; i++)
            {
                PersonEntry person;
                var slot = zone.Slots.Find(x => x.Kind == "NAME" && x.Role == roles[i]);
                if (slot == null && zone.IsCover)
                {
                    var sign = zone.Slots.Find(x => x.Kind == "SIGN" && x.Role == roles[i]);
                    if (sign != null) slot = new Slot { Height = sign.Height, Width = sign.Width * NameColumnRatio,
                        Rotation = sign.Rotation };
                }
                if (slot == null || !people.TryGetValue(roles[i], out person) || person == null || string.IsNullOrWhiteSpace(person.Name)) continue;
                var name = new DBText(); name.SetDatabaseDefaults(db); name.Layer = SignatureLayer;
                name.TextStyleId = EnsureNameTextStyle(db, tr);
                name.TextString = FormatName(person.Name);
                // The cover signature zone is intentionally enlarged. Its upper real-name
                // grid retains a 7 mm row pitch: use 3 mm paper text, scaled by that grid.
                name.Height = zone.IsCover ? positions[0].DistanceTo(positions[1]) * (3.0 / 7.0)
                    : NameHeight(slot, name.TextString);
                name.WidthFactor = NameWidthFactor;
                name.Justify = AttachmentPoint.MiddleCenter; name.Position = positions[i]; name.AlignmentPoint = positions[i];
                name.Rotation = slot.Rotation; name.ColorIndex = 4;
                name.XData = new ResultBuffer(new TypedValue(1001, AppName), new TypedValue(1000, "COUNTER_NAME"),
                    new TypedValue(1071, zone.Id), new TypedValue(1000, found.Handle.ToString()));
                space.AppendEntity(name); tr.AddNewlyCreatedDBObject(name, true); name.AdjustAlignment(db);
            }
        }
    }
}
