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
namespace CadSignature
{
    public partial class Commands
    {
        private static ObjectId offlineSpaceId = ObjectId.Null;
        private static ObjectId[] offlineFrameIds;
        private static HashSet<int> offlineZones;
        private static int offlineSigned;
        private static int offlineRemoved;
        private static bool strictPlotSigning;

        private static ObjectId SignatureSpace(Database db)
        { return !offlineSpaceId.IsNull && offlineSpaceId.Database == db ? offlineSpaceId : db.CurrentSpaceId; }

        internal static void ValidateOfflineSigning(bool? includeDrafter = null, bool requireSignatureFiles = true)
        {
            if (LoadFrameCalibrations().Count == 0) throw new InvalidOperationException("请先完成图幅标定，再批量签名。");
            bool any = false;
            var people = LoadPeople();
            string directory = SignatureDirectory();
            foreach (var pair in people)
            {
                var person = pair.Value;
                if (includeDrafter == false && pair.Key == "制图") continue;
                if (person == null || string.IsNullOrWhiteSpace(person.Name)) continue;
                any = true;
                if (!includeDrafter.HasValue || !requireSignatureFiles) continue;
                string file = string.IsNullOrEmpty(person.SignatureFile) ? person.Name + ".dwg" : person.SignatureFile;
                if (SignatureVault.Contains(directory, file))
                {
                    byte[] bytes = SignatureVault.ReadSignatureBytes(directory, file);
                    if (bytes == null || bytes.Length == 0) throw new InvalidOperationException(pair.Key + "的签名素材无法读取，请在人员设置中重新选择。");
                }
                else if (!File.Exists(Path.Combine(directory, file)))
                    throw new InvalidOperationException(pair.Key + "缺少签名素材，请在人员设置中补齐后再批量签名。");
            }
            if (!any) throw new InvalidOperationException("尚未配置签字人员，请先完成人员设置。");
        }

        internal static void ValidatePlotSigning()
        {
            LoadPeople(); // 同时兼容旧人员配置包。
            if (!File.Exists(PeopleConfig.ConfigPath(BaseFolder())) && !File.Exists(PeopleConfig.ConfigPath(SignatureDirectory())))
                throw new InvalidOperationException("尚未配置人员设置，请在图框签名 > 人员设置中保存人员及签名素材。");
            ValidateOfflineSigning();
        }

        internal static string SignSelectedForPlot(Database db, IEnumerable<string> handles)
        {
            ValidatePlotSigning();
            Database previous = HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase = db;
                var spaces = new Dictionary<ObjectId, List<ObjectId>>();
                bool includeDrafter = false;
                bool requireSignatureFiles = false;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var seen = new HashSet<ObjectId>();
                    foreach (string handle in handles)
                    {
                        long value;
                        if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value))
                            throw new InvalidOperationException("图框标识无效，请重新扫描后带签名打印。");
                        var id = db.GetObjectId(false, new Handle(value), 0);
                        var frame = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (frame == null || frame.IsErased) throw new InvalidOperationException("图框已变化，请重新扫描。");
                        if (!seen.Add(id)) continue;
                        var definition = (BlockTableRecord)tr.GetObject(frame.IsDynamicBlock ? frame.DynamicBlockTableRecord : frame.BlockTableRecord, OpenMode.ForRead);
                        string coverName = FrameOffsetConfig.LoadCoverBlockName(BaseFolder());
                        if (!IsDirectoryFrame(frame, tr))
                        {
                            requireSignatureFiles = true;
                            if (definition.Name.IndexOf("封面", StringComparison.Ordinal) < 0 && !string.Equals(definition.Name, coverName, StringComparison.OrdinalIgnoreCase)) includeDrafter = true;
                        }
                        List<ObjectId> ids;
                        if (!spaces.TryGetValue(frame.OwnerId, out ids)) spaces.Add(frame.OwnerId, ids = new List<ObjectId>());
                        ids.Add(id);
                    }
                    tr.Commit();
                }
                ValidateOfflineSigning(includeDrafter, requireSignatureFiles);
                strictPlotSigning = true;
                var messages = new List<string>();
                foreach (var pair in spaces)
                {
                    offlineSpaceId = pair.Key; offlineFrameIds = pair.Value.ToArray();
                    offlineZones = new HashSet<int>(); offlineSigned = 0;
                    string message = RunFrameSign();
                    if (offlineSigned != pair.Value.Count) throw new InvalidOperationException("部分待打印图框未能签名，请检查图幅标定、图层锁定和图框范围：" + message);
                    messages.Add(message);
                }
                return string.Join("；", messages.ToArray());
            }
            finally
            {
                strictPlotSigning = false;
                offlineSpaceId = ObjectId.Null; offlineFrameIds = null; offlineZones = null; offlineSigned = 0;
                HostApplicationServices.WorkingDatabase = previous;
            }
        }

        internal static string ProcessOffline(Database db, bool remove, out int changed)
        {
            Database previous = HostApplicationServices.WorkingDatabase;
            changed = 0;
            try
            {
                HostApplicationServices.WorkingDatabase = db;
                if (remove)
                {
                    offlineRemoved = 0;
                    string message = RunClearAll();
                    changed = offlineRemoved;
                    return message;
                }
                var spaces = new Dictionary<ObjectId, List<ObjectId>>();
                bool hasStandardFrames = false;
                bool requireSignatureFiles = false;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId sid in table)
                    {
                        var space = (BlockTableRecord)tr.GetObject(sid, OpenMode.ForRead);
                        if (!space.IsLayout || space.IsFromExternalReference) continue;
                        var ids = new List<ObjectId>();
                        foreach (ObjectId id in space)
                        {
                            var block = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (block == null || block.Layer == MarkerLayer || block.Layer == SignatureLayer || IsSignatureBlock(block, tr)) continue;
                            var definition = (BlockTableRecord)tr.GetObject(block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord, OpenMode.ForRead);
                            string coverName = FrameOffsetConfig.LoadCoverBlockName(BaseFolder());
                            bool isCover = definition.Name.IndexOf("封面", StringComparison.Ordinal) >= 0 || string.Equals(definition.Name, coverName, StringComparison.OrdinalIgnoreCase);
                            if (!definition.IsFromExternalReference && (definition.Name.IndexOf("图框", StringComparison.Ordinal) >= 0 || isCover || IsDirectoryFrame(block, tr)))
                            {
                                ids.Add(id);
                                if (!IsDirectoryFrame(block, tr)) { requireSignatureFiles = true; if (!isCover) hasStandardFrames = true; }
                            }
                        }
                        if (ids.Count > 0) spaces.Add(sid, ids);
                    }
                    tr.Commit();
                }
                if (spaces.Count == 0) return "没有找到块名包含“图框”的图框，文件未修改。";
                ValidateOfflineSigning(hasStandardFrames, requireSignatureFiles);
                var messages = new List<string>();
                foreach (var space in spaces)
                {
                    offlineSpaceId = space.Key;
                    offlineFrameIds = space.Value.ToArray();
                    offlineZones = new HashSet<int>();
                    offlineSigned = 0;
                    string message = RunFrameSign();
                    changed += offlineSigned;
                    messages.Add(message);
                }
                return "模型及全部布局：" + string.Join("；", messages.ToArray());
            }
            finally
            {
                offlineSpaceId = ObjectId.Null;
                offlineFrameIds = null;
                offlineZones = null;
                offlineSigned = 0;
                offlineRemoved = 0;
                HostApplicationServices.WorkingDatabase = previous;
            }
        }
    }
}
