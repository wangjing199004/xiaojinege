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
using Autodesk.AutoCAD.ApplicationServices;
#else
using ZwSoft.ZwCAD.ApplicationServices;
#endif
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
#endif
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
namespace TitleAttributeBatch.Core
{
    internal static class OpenDrawingBatch
    {
        private static readonly Dictionary<string, WeakReference> bindings = new Dictionary<string, WeakReference>(StringComparer.OrdinalIgnoreCase);
        internal static Document Find(string path)
        {
            WeakReference reference;
            Document remembered = bindings.TryGetValue(path, out reference) ? reference.Target as Document : null;
            foreach (Document doc in AcApp.DocumentManager)
                if (doc == remembered || string.Equals(Path.GetFullPath(doc.Name), path, StringComparison.OrdinalIgnoreCase)) return doc;
            return null;
        }
        internal static void Remember(string path)
        { var doc = Find(path); if (doc != null) bindings[path] = new WeakReference(doc); }
        internal static List<string> OpenPaths()
        {
            var paths = new List<string>();
            foreach (Document doc in AcApp.DocumentManager)
            {
                string path = Path.GetFullPath(doc.Name);
                bindings[path] = new WeakReference(doc);
                paths.Add(path);
            }
            return paths;
        }
        internal static string Run(Document doc, Func<Database, AttributeUpdateResult> action)
        {
            Database previous = HostApplicationServices.WorkingDatabase;
            AttributeUpdateResult result;
            using (doc.LockDocument())
            {
                try
                {
                    HostApplicationServices.WorkingDatabase = doc.Database;

                    using (var transaction = doc.Database.TransactionManager.StartTransaction())
                    {
                        result = action(doc.Database);
                        transaction.Commit();
                    }
                }
                finally { HostApplicationServices.WorkingDatabase = previous; }
            }

            if (result.Changed && AcApp.DocumentManager.MdiActiveDocument == doc)
            {
                try { doc.Editor.Regen(); }
                catch (System.Exception ex) { doc.Editor.WriteMessage("\n内容已更新，显示刷新未完成：" + ex.Message); }
            }
            return (result.Changed ? "已更新（已打开，待保存）：" : "未变化（已打开）：") + result.Detail;
        }
    }
}
