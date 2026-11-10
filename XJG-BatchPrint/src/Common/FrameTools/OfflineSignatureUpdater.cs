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
using CadSignature;

namespace TitleAttributeBatch.Core
{
    internal static class OfflineSignatureUpdater
    {
        internal static string Update(string path, bool remove)
        {
            if (!File.Exists(path)) throw new IOException("文件不存在。");
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("文件为只读，未修改。");
            if (File.Exists(Path.ChangeExtension(path, ".dwl")) || File.Exists(Path.ChangeExtension(path, ".dwl2"))) return "跳过：文件有 CAD 占用标记";
            string digest = SafeDwgSave.Digest(path);
            string temporary = Path.Combine(Path.GetDirectoryName(path), "." + Path.GetFileNameWithoutExtension(path) + ".tksign_" + Guid.NewGuid().ToString("N") + ".dwg");
            try
            {
                string message;
                using (var db = new Database(false, true))
                {
                    db.ReadDwgFile(path, FileOpenMode.OpenForReadAndWriteNoShare, false, null);
                    db.CloseInput(true);
                    int changed;
                    message = Commands.ProcessOffline(db, remove, out changed);
                    if (changed == 0) return "未保存：" + message;
                    db.SaveAs(temporary, db.OriginalFileVersion);
                }
                SafeDwgSave.ReplaceWithoutBackup(temporary, path, digest);
                return "已保存（不备份）：" + message;
            }
            finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch { } } }
        }
    }
}
