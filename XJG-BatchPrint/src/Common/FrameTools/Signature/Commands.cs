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
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
#else
using ZwSoft.ZwCAD.EditorInput;
#endif
#if AUTOCAD
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.Geometry;
#endif
#if AUTOCAD
using Autodesk.AutoCAD.GraphicsInterface;
#else
using ZwSoft.ZwCAD.GraphicsInterface;
#endif
using System;
using System.Collections.Generic;
using System.IO;
namespace CadSignature
{
    /// <summary>
    /// A1 图框签字栏工具。推荐用 TKQM 打开签字面板，也可以直接输入
    /// CADSIGNSETUP（签字范围）/ CADSIGN（一键签名）/ CADSIGNCLEAR（清除签名）。
    /// 一张图纸上可以有多处签字栏（多个图框）：每框选一处就生成一组独立的「签字区域」，
    /// 各区域的定位点与签名互不干扰——签第二处不会动到第一处。
    ///
    /// 本文件属于核心程序集 CadSignatureCore.dll，由外壳 CadSignature.dll 从内存载入，
    /// 所以**不要在这里写 [CommandMethod]** —— 核心不是 AutoCAD 直接加载的，注册不了命令；
    /// 命令声明统一放在 Source\Loader\LoaderCommands.cs。改完这里编译一下，
    /// CAD 里再敲一次命令就会自动用上新代码（不需要关闭 AutoCAD）。
    /// </summary>
    public partial class Commands
    {
        private const string MarkerLayer = "CAD_签名定位";
        private const string SignatureLayer = "CAD_签名";
        private const string AppName = "CadSignature";
        /// <summary>签字栏内列宽比例：姓名列 : 签名列 = 1 : 2，即姓名列占框选总宽的 1/3。
        /// 换成别的图框时只改这一处即可（例如等宽写 1.0 / 2.0）。</summary>
        private const double NameColumnRatio = 1.0 / 3.0;
        /// <summary>姓名文字使用的文字样式名；图中没有该样式时按下面的字体自动新建。</summary>
        private const string NameTextStyleName = "黑体";
        /// <summary>黑体的 typeface 名。simhei.ttf 同时注册了 SimHei / 黑体 两个名字，
        /// 用英文名是为了在英文版 Windows 上也不丢字体（渲染出来同样是黑体）。</summary>
        private const string NameFontTypeface = "SimHei";
        /// <summary>姓名文字高度 = 行高 × 该比例（3/7 ≈ 0.4286）。
        /// 参考图框（A1+图框）签字栏每行高 700 单位、姓名高 300 单位，故取 3/7；
        /// 图框整体缩放时槽高跟着缩放，姓名字号也随之缩放，不再固定。</summary>
        private const double NameHeightRatio = 3.0 / 7.0;
        /// <summary>姓名文字宽度因子（压缩字宽，避免长名字超出姓名列）。</summary>
        private const double NameWidthFactor = 0.8;
        /// <summary>姓名可用宽度占姓名列宽的比例，用于兜底限制字号（正常情况下不会触发）。</summary>
        private const double NameWidthFill = 0.95;
        /// <summary>签字栏 7 行的岗位名（从上到下）。岗位名要和定位点 XData 里存的岗位对得上，
        /// 所以固定不动；签谁在面板「人员设置」里改（5.9.0 起），名单存在核心 DLL 旁边的
        /// 「人员名单.txt」，没有该文件时用 PeopleConfig 内置的默认名单。</summary>
        private static string[] Roles { get { return PeopleConfig.Roles; } }

        /// <summary>打开签字面板（命令 TKQM，5.19.0 起 2 列 × 3 行）。
        /// 「单张签字」= 框选签字栏 + 立即签名（合并成一步）；「图框签字」= 框选批量按图幅签名，
        /// 右侧 > 按钮是「图幅标定」二级入口；「人员设置」与上面四个动作执行完弹回面板；
        /// 「隐藏签字」「清除签字」执行完直接结束命令并关闭面板，结果立刻呈现在图上。
        /// 点「关闭」或按 ESC 结束命令。</summary>
        public void ShowSignaturePanel()
        {
            // 5.24.1：干净安装包按设计不含标定数据，新电脑上「图框定位.txt」原本要等第一次
            // 标定成功才出现 —— 用户既不知道有这个文件、也不知道放哪。打开面板就先把它建出来
            // （带注释的模板），配合「图框签字」提示里的完整路径，照做即可。
            FrameOffsetConfig.EnsureTemplate(BaseFolder());

            string lastMessage = "";
            while (true)
            {
                int zoneCount, markerCount, brokenZones;
                MarkerStats(out zoneCount, out markerCount, out brokenZones);
                bool signatureVisible = SignatureLayerVisible();
                string spaceName = SpaceName();

                string slotStatus;
                bool statusReady;
                if (zoneCount == 0)
                {
                    slotStatus = spaceName + " · 未找到签字定位点，请先点「单张签字」";
                    statusReady = false;
                }
                else if (brokenZones > 0)
                {
                    slotStatus = string.Format("{0} · 共 {1} 处签字区域，其中 {2} 处定位点不完整，请对那处重跑「单张签字」", spaceName, zoneCount, brokenZones);
                    statusReady = false;
                }
                else
                {
                    slotStatus = string.Format("{0} · 已找到 {1} 处签字区域（共 {2} 个定位点），可以直接「单张签字」或「图框签字」", spaceName, zoneCount, markerCount);
                    statusReady = true;
                }

                PanelAction action;
                bool clearAll = false;
                using (SignaturePanel panel = new SignaturePanel(slotStatus, statusReady, signatureVisible, lastMessage))
                {
                    ZwcadBatchPlot.CadWinFormsDialog.Show(panel);
                    action = panel.Action;
                    // 「全部」必须在窗体 Dispose 之前读走（5.20.0）：控件销毁后就读不到勾选状态了
                    clearAll = panel.ClearAllRequested;
                }
                if (action == PanelAction.None) return;

                if (action == PanelAction.ManualSign)
                {
                    // 单张签字 = 框选签字栏 + 立即签名（一次动作）；执行完弹回面板，
                    // 一张图有多张要手签的图时可以挨个来。详细结果写命令行，面板只留简报（5.20.1）。
                    string message = RunManualSign() ?? "已取消";
                    Write(message);
                    lastMessage = BriefOf(action, message);
                    continue;
                }

                if (action == PanelAction.FrameSign)
                {
                    // 执行完弹回面板（与「单张签字」一致）：一张图有多个图框时可以挨个选，
                    // 签完最后一个点「关闭」结束。面板简报只显示各图幅张数（A1×2、A2×3…）。
                    string message = RunFrameSign() ?? "已取消";
                    Write(message);
                    lastMessage = BriefOf(action, message);
                    continue;
                }

                if (action == PanelAction.FrameCalib)
                {
                    // 二级菜单：分别固定 A0/A1/A2/A3 的签名位置（标定完回到面板，可接着「图框签字」）
                    string message = RunFrameCalibEditor() ?? "已取消";
                    Write(message);
                    lastMessage = BriefOf(action, message);
                    continue;
                }

                if (action == PanelAction.EditPeople)
                {
                    // 纯界面操作（不碰图纸），弹编辑对话框；保存后停在面板，方便接着点「单张签字」
                    string message = RunEditPeople() ?? "已取消";
                    Write(message);
                    lastMessage = BriefOf(action, message);
                    continue;
                }

                if (action == PanelAction.FrameNumber)
                {
                    // 图框编号（5.22.0，原 TKBH / 图框编号2.lsp 的原生移植）；
                    // 执行完弹回面板，编完一批可以接着编下一批。
                    string message = RunFrameNumber() ?? "已取消";
                    Write(message);
                    lastMessage = BriefOf(action, message);
                    continue;
                }

                // 「隐藏签字」「清除签字」属于「干完收工」：执行后不再弹面板。
                // 「清除签字」看「全部」复选框（5.20.0）：不勾 = 框选范围清除；勾 = 全图清除 + 清签名块定义。
                string result;
                switch (action)
                {
                    case PanelAction.ToggleHidden: result = RunToggleHidden(); break;
                    case PanelAction.Clear: result = clearAll ? RunClearAll() : RunClear(); break;
                    default: result = null; break;
                }
                Write(result ?? "已取消");
                return;
            }
        }

        /// <summary>把命令的详细结果压成面板「上次操作」的一行简报（5.20.1）。
        /// 详细信息照旧**全部写到命令行**，面板只显示要点：
        /// 图框签字 → 只取各图幅张数（「A0×1、A1×2」这种，有跳过则附一句）；
        /// 其他操作 → 第一句，超过 42 字截断加省略号。</summary>
        private static string BriefOf(PanelAction action, string detail)
        {
            if (string.IsNullOrEmpty(detail)) return detail;

            if (action == PanelAction.FrameSign)
            {
                // 详细信息形如「已签 5 个图框（A0×1、A1×2、A2×2）。签名已添加：…」
                // 面板只要括号里的各图幅张数；「跳过 N 个」也带一句。
                int open = detail.IndexOf('（');
                int close = detail.IndexOf('）');
                if (open >= 0 && close > open)
                {
                    string brief = detail.Substring(open + 1, close - open - 1);
                    int skipAt = detail.IndexOf("跳过 ");
                    if (skipAt >= 0)
                    {
                        int unit = detail.IndexOf('个', skipAt);
                        if (unit > skipAt) brief += "，" + detail.Substring(skipAt, unit - skipAt + 1);
                    }
                    return brief;
                }
            }

            int end = detail.IndexOfAny(new char[] { '。', '；', '\n' });
            string first = end > 0 ? detail.Substring(0, end) : detail;
            if (first.Length > 42) first = first.Substring(0, 42) + "…";
            return first;
        }

        /// <summary>签字范围（命令 CADSIGNSETUP）。</summary>
        public void SetupA1SignatureSlots()
        {
            Write(RunSetup() ?? "已取消。");
        }

        /// <summary>一键签名（命令 CADSIGN）。</summary>
        public void ApplyA1Signatures()
        {
            Write(RunSign());
        }

        /// <summary>清除签名（命令 CADSIGNCLEAR）：弹框勾选要清的签字区域，
        /// 清掉该区域的定位点 + 姓名/签名 + 无引用的签名块定义。取消时不改动图纸。</summary>
        public void ClearA1Signatures()
        {
            Write(RunClear() ?? "已取消，图纸未改动。");
        }

        /// <summary>签字范围：框选签字栏，建立 7 行定位点。
        /// 不要求图中存在特定名称的图框块，任意图纸上框选即用。
        /// 只重建**本次框选到的那一处**，图上其他签字区域的定位点原样保留。取消时返回 null。</summary>
        private static string RunSetup()
        {
            Editor ed = Editor();
            Database db = HostApplicationServices.WorkingDatabase;

            PromptPointResult topLeft = ed.GetPoint("\n指定签字栏“姓名”列左上角: ");
            if (topLeft.Status != PromptStatus.OK) return null;
            PromptPointOptions bottomRightOptions = new PromptPointOptions("\n指定签字栏“签名”列右下角: "); bottomRightOptions.BasePoint = topLeft.Value; bottomRightOptions.UseBasePoint = true;
            PromptPointResult bottomRight = ed.GetPoint(bottomRightOptions);
            if (bottomRight.Status != PromptStatus.OK) return null;

            double left = Math.Min(topLeft.Value.X, bottomRight.Value.X), right = Math.Max(topLeft.Value.X, bottomRight.Value.X);
            double top = Math.Max(topLeft.Value.Y, bottomRight.Value.Y), bottom = Math.Min(topLeft.Value.Y, bottomRight.Value.Y);
            if (right - left < 1 || top - bottom < 1) return "定位区域无效：两个角点太近，请重新框选。";

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 建点的公共逻辑抽到了 CreateZoneMarkers（5.17.0 起「单张签字」「图框签名」共用）
                CreateZoneMarkers(db, tr, left, right, bottom, top, topLeft.Value.Z);
                tr.Commit();
            }

            int zoneCount, markerCount, brokenZones;
            MarkerStats(out zoneCount, out markerCount, out brokenZones);
            return string.Format("已建立签字栏的 7 行隐藏定位点（当前共 {0} 处签字区域），现在可以签名。", zoneCount);
        }

        /// <summary>单张签字（5.19.0 合并「签字范围」+「一键签名」；5.21.0 改连续作业循环）：
        /// 框选这一张图的签字栏（姓名列左上角 → 签名列右下角）→ 随即直接签名 →
        /// **立即上屏**（UpdateScreen，不用等退出才能看到签名）→ 空格/回车确认继续框选下一处，
        /// E 或 ESC 结束回面板。人员预检：7 个岗位全没填姓名时不让用户白框选，先提示去「人员设置」。
        /// 全程没签成任何一处时返回 null（面板显示「已取消」）。</summary>
        private static string RunManualSign()
        {
            Dictionary<string, PersonEntry> people = LoadPeople();
            bool anyName = false;
            foreach (string role in Roles)
            {
                PersonEntry entry;
                if (people.TryGetValue(role, out entry) && entry != null && !string.IsNullOrWhiteSpace(entry.Name)) { anyName = true; break; }
            }
            if (!anyName) return "7 个岗位都没有填姓名，无法签名——请先点「人员设置」把名单填上。";

            Editor ed = Editor();
            int signed = 0;
            while (true)
            {
                if (RunSetup() == null) break;              // 框选签字栏（ESC 结束整个流程）
                string result = RunSign();                  // 随即直接签名
                if (result != null) Write(result);
                signed++;

                // 关键一步：命令还在运行时新签的实体默认不会上屏——以前只能退出面板后才看到签名。
                // UpdateScreen 立即刷新显示，让用户**先看到这一处的签名再决定下一步**。
                ed.UpdateScreen();

                // 二次确认：空格/回车 = 取默认「继续」直接框选下一处；E 或 ESC = 结束回面板。
                PromptKeywordOptions ask = new PromptKeywordOptions("\n已签名。[继续下一处(J)/结束(E)] <J>: ");
                ask.Keywords.Add("J", "J", "继续下一处");
                ask.Keywords.Add("E", "E", "结束");
                ask.Keywords.Default = "J";
                ask.AllowNone = true;
                PromptResult answer = ed.GetKeywords(ask);
                if (answer.Status != PromptStatus.OK
                    && answer.Status != PromptStatus.None
                    && answer.Status != PromptStatus.Keyword) break;    // ESC 结束
                if (answer.Status == PromptStatus.Keyword && answer.StringResult == "E") break;
            }
            if (signed == 0) return null;
            return "单张签字完成：共签 " + signed + " 处（每处明细见命令行）。";
        }

        /// <summary>为一处签字栏矩形建立 7 行（姓名/签名各 7 = 14 个）隐藏定位点，
        /// 返回所用的签字区域编号。建立前先清掉与该范围重叠的旧定位点（整组清，见
        /// <see cref="ClearMarkersIn"/>），所以对同一处重复执行不会叠加、不会留半组残点。</summary>
        private static int CreateZoneMarkers(Database db, Transaction tr, double left, double right, double bottom, double top, double z)
        {
            // 轴对齐矩形 = 左上角在 (left, top)，宽向量向右、高向量向下（单张签字一直是这样）
            return CreateZoneMarkersOriented(db, tr, new Point3d(left, top, z),
                new Vector3d(right - left, 0, 0), new Vector3d(0, bottom - top, 0));
        }

        /// <summary>带方向的定位点建立（5.24.0，图框签字用）：签字栏矩形用「左上角 + 全宽向量 +
        /// 全高向量」描述，向量经图框 BlockTransform 变换——旋转/缩放/拉伸过的图框，
        /// 14 个定位点跟着图框一起转，每个点还记下旋转角（签名时姓名与笔迹按它转）。
        /// 横平竖直的图框走 <see cref="CreateZoneMarkers"/>，向量退化为水平/垂直，行为与旧版完全一致。</summary>
        private static int CreateZoneMarkersOriented(Database db, Transaction tr, Point3d topLeftWorld, Vector3d wVec, Vector3d hVec, bool cover = false)
        {
            EnsureLayers(db, tr);
            double width = wVec.Length, height = hVec.Length;
            double rotation = Math.Atan2(wVec.Y, wVec.X);
            // 轴对齐外接矩形：清旧定位点（ClearMarkersIn 收轴对齐矩形）用
            Point3d topRight = topLeftWorld.Add(wVec), bottomLeft = topLeftWorld.Add(hVec), bottomRight = topLeftWorld.Add(wVec).Add(hVec);
            double left = Math.Min(Math.Min(topLeftWorld.X, topRight.X), Math.Min(bottomLeft.X, bottomRight.X));
            double right = Math.Max(Math.Max(topLeftWorld.X, topRight.X), Math.Max(bottomLeft.X, bottomRight.X));
            double bottom = Math.Min(Math.Min(topLeftWorld.Y, topRight.Y), Math.Min(bottomLeft.Y, bottomRight.Y));
            double top = Math.Max(Math.Max(topLeftWorld.Y, topRight.Y), Math.Max(bottomLeft.Y, bottomRight.Y));
            int zoneId = ClearMarkersIn(db, tr, left, right, bottom, top);
            BlockTableRecord space = (BlockTableRecord)tr.GetObject(SignatureSpace(db), OpenMode.ForWrite);
            string[] zoneRoles = cover ? CoverRoles() : Roles;
            for (int i = 0; i < zoneRoles.Length; i++)
            {
                Vector3d down = hVec.MultiplyBy((i + .5) / zoneRoles.Length);
                if (!cover) CreateMarker(space, tr, topLeftWorld.Add(wVec.MultiplyBy(NameColumnRatio / 2)).Add(down), zoneRoles[i], "NAME", width * NameColumnRatio, height / zoneRoles.Length, zoneId, rotation);
                double centerRatio = cover ? .5 : (NameColumnRatio + 1) / 2;
                CreateMarker(space, tr, topLeftWorld.Add(wVec.MultiplyBy(centerRatio)).Add(down), zoneRoles[i], "SIGN", cover ? width : width * (1 - NameColumnRatio), height / zoneRoles.Length, zoneId, rotation, cover);
            }
            return zoneId;
        }

        /// <summary>图框签名（5.18.0 改为**框选批量**）：鼠标框选一个范围，范围内的图框
        /// 一次全部签名。每个图框按**自身实际内容的高度**（块内坐标，见
        /// <see cref="LocalBottomRight"/>）匹配「图幅标定」里的图幅（A0/A1/A2/A3，
        /// 高度差 ≤5% 取最接近者——5.18.2 起用高度：向右拉伸不变，A1+0.25/A1+0.5 等加长
        /// 图幅与 A1 高度相同，**同一类图框标定一次全类共用**），套用该图幅的标定偏移：
        /// 锚点 = 本实例内容右下角，签字栏两角 = 锚点 + 偏移（经 BlockTransform 变换回
        /// 世界坐标——平移/缩放/旋转/动态拉伸全对准，拉伸后右缘移动、签字栏跟着右缘走，
        /// 偏移不变）。匹配不上（图幅没标定、不是图框的块）或安全网校验不过（签名栏落到
        /// 图框外）的块跳过并在结果里点名。取消时返回 null。</summary>
        private static string RunFrameSign()
        {
            Editor ed = Editor();
            Database db = HostApplicationServices.WorkingDatabase;

            Dictionary<string, double[]> calibrations = LoadFrameCalibrations();
            if (calibrations.Count == 0)
                return "还没有标定任何图幅的签名位置——请先点「图框签字」右边的「>」做图幅标定，A0/A1/A2/A3 各标一次。"
                    + "标定结果存在：" + FrameOffsetConfig.EnsureTemplate(BaseFolder()) + "。";

            // 框选：图框是可见的块参照，直接用选择集拿（SelectionFilter 只认 INSERT，
            // 避免误抓文字标注）；拉窗口/窗交/逐个点选都行，与 CAD 内置命令同一套交互。
            ObjectId[] ids = offlineFrameIds;
            if (ids == null)
            {
                PromptSelectionOptions options = new PromptSelectionOptions();
                options.MessageForAdding = "\n框选要签名的图框（框内每个图框按各自图幅自动签名，ESC 取消）: ";
                SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "INSERT") });
                PromptSelectionResult pick = ed.GetSelection(options, filter);
                if (pick.Status != PromptStatus.OK) return null;
                ids = pick.Value.GetObjectIds();
            }
            if (ids.Length == 0) return "框选范围内没有图框（块参照），图纸未改动。";

            int signed = 0, skipped = 0;
            Dictionary<string, int> perSlot = new Dictionary<string, int>();
            List<string> skippedNotes = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    if (id.IsNull || id.IsErased) continue;
                    BlockReference frame = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (frame == null) continue;

                    // 只处理图框块（5.21.0）：签名块（CAD_SIG_*）与签名/定位图层上的东西一律无视——
                    // 既不算签成也不算「跳过」。重复框选已签过的范围时，落在框里的旧签名块
                    // 不再刷「不在已标定图幅内」的跳过噪音。
                    if (IsSignatureBlock(frame, tr)) continue;
                    if (frame.Layer == SignatureLayer || frame.Layer == MarkerLayer) continue;
                    if (offlineFrameIds != null && ((LayerTableRecord)tr.GetObject(frame.LayerId, OpenMode.ForRead)).IsLocked)
                    { skipped++; AddNote(skippedNotes, "图框所在图层锁定"); continue; }

                    Point3d cornerLocal; double localHeight;
                    if (!LocalBottomRight(frame, tr, out cornerLocal, out localHeight))
                    {
                        skipped++; AddNote(skippedNotes, "有块取不到内容范围（块里可能没有图元）");
                        continue;
                    }

                    // 按块内内容高度匹配图幅（±5%，取最接近的）。高度不受向右拉伸影响——
                    // A1 拉伸成 A1+0.25/A1+0.5 后高度不变，共用 A1 的标定；
                    // 高度取块内坐标，不受插入缩放/旋转影响。
                    string frameName = ((BlockTableRecord)tr.GetObject(frame.IsDynamicBlock ? frame.DynamicBlockTableRecord : frame.BlockTableRecord, OpenMode.ForRead)).Name;
                    string coverName = FrameOffsetConfig.LoadCoverBlockName(BaseFolder());
                    bool isCover = string.Equals(frameName, coverName, StringComparison.OrdinalIgnoreCase) || frameName.IndexOf("封面", StringComparison.Ordinal) >= 0;
                    string matched = FrameOffsetConfig.MatchSlot(BaseFolder(), frameName, localHeight, calibrations);
                    if (matched == null)
                    {
                        skipped++; AddNote(skippedNotes, isCover ? "封面尚未独立标定" : "内容高 ≈ " + localHeight.ToString("F0") + " 的块不在已标定图幅内");
                        continue;
                    }
                    double[] calibration = calibrations[matched];

                    // 5.24.0：签字栏矩形直接在图框块坐标系里求出（锚点 + 标定偏移），
                    // 三个角经 BlockTransform 变到世界，宽/高向量 = 角点差——旋转、缩放、
                    // 拉伸的图框，签字栏跟着图框一起转。此前取世界坐标的轴对齐 min/max，
                    // 横放图框没问题，旋转图框的签字栏形状/位置就错了，签名也是横着放的。
                    double localX0 = Math.Min(calibration[1], calibration[3]);
                    double localX1 = Math.Max(calibration[1], calibration[3]);
                    double localY0 = Math.Min(calibration[2], calibration[4]);
                    double localY1 = Math.Max(calibration[2], calibration[4]);
                    Point3d localTopLeft = cornerLocal.Add(new Vector3d(localX0, localY1, 0));
                    Point3d worldTopLeft = localTopLeft.TransformBy(frame.BlockTransform);
                    Vector3d wVec = worldTopLeft.GetVectorTo(localTopLeft.Add(new Vector3d(localX1 - localX0, 0, 0)).TransformBy(frame.BlockTransform));   // 全宽向量（含图框缩放与旋转）
                    Vector3d hVec = worldTopLeft.GetVectorTo(localTopLeft.Add(new Vector3d(0, localY0 - localY1, 0)).TransformBy(frame.BlockTransform));   // 全高向量（自上而下）
                    double zoneWidth = wVec.Length, zoneHeight = hVec.Length;
                    if (zoneWidth < 1 || zoneHeight < 1)
                    {
                        skipped++; AddNote(skippedNotes, matched + " 的标定偏移无效（签字栏范围太小），请重新标定");
                        continue;
                    }

                    // 安全网：签字栏矩形应落在这个图框范围内（5% 余量）。对不上说明该图框
                    // 的版式和标定时用的不一样（比如签字栏没跟着右缘走），宁可跳过也不把签名摆到图外。
                    // 旋转图框比较的是轴对齐外接矩形（双方都是外接矩形，够用）。
                    Extents3d frameExtents;
                    try { frameExtents = frame.GeometricExtents; }
                    catch (CadRuntime.Exception)
                    {
                        skipped++; AddNote(skippedNotes, "有块取不到范围");
                        continue;
                    }
                    Point3d zoneTopRight = worldTopLeft.Add(wVec), zoneBottomLeft = worldTopLeft.Add(hVec), zoneBottomRight = worldTopLeft.Add(wVec).Add(hVec);
                    double zoneLeft = Math.Min(Math.Min(worldTopLeft.X, zoneTopRight.X), Math.Min(zoneBottomLeft.X, zoneBottomRight.X));
                    double zoneRight = Math.Max(Math.Max(worldTopLeft.X, zoneTopRight.X), Math.Max(zoneBottomLeft.X, zoneBottomRight.X));
                    double zoneBottom = Math.Min(Math.Min(worldTopLeft.Y, zoneTopRight.Y), Math.Min(zoneBottomLeft.Y, zoneBottomRight.Y));
                    double zoneTop = Math.Max(Math.Max(worldTopLeft.Y, zoneTopRight.Y), Math.Max(zoneBottomLeft.Y, zoneBottomRight.Y));
                    double padX = (frameExtents.MaxPoint.X - frameExtents.MinPoint.X) * .05 + 1;
                    double padY = (frameExtents.MaxPoint.Y - frameExtents.MinPoint.Y) * .05 + 1;
                    if (zoneLeft < frameExtents.MinPoint.X - padX || zoneRight > frameExtents.MaxPoint.X + padX
                        || zoneBottom < frameExtents.MinPoint.Y - padY || zoneTop > frameExtents.MaxPoint.Y + padY)
                    {
                        skipped++; AddNote(skippedNotes, "有 " + matched + " 图框与标定对不上（签名栏落到图框外），多半是签字栏没跟着右缘走");
                        continue;
                    }

                    int createdZone = CreateZoneMarkersOriented(db, tr, worldTopLeft, wVec, hVec, matched == "封面");
                    if (offlineZones != null) offlineZones.Add(createdZone);
                    signed++;
                    perSlot[matched] = perSlot.ContainsKey(matched) ? perSlot[matched] + 1 : 1;
                }
                tr.Commit();
            }

            if (signed == 0)
                return "框选范围内没有签成任何图框"
                    + (skippedNotes.Count > 0 ? "，跳过原因：" + string.Join("；", skippedNotes.ToArray()) : "")
                    + "。没有标定的图幅请先用「图幅标定」标一次。";

            List<string> parts = new List<string>();
            foreach (string slot in FrameOffsetConfig.Slots)
            {
                int count;
                if (perSlot.TryGetValue(slot, out count) && count > 0) parts.Add(slot + "×" + count);
            }
            string message = "已签 " + signed + " 个图框（" + string.Join("、", parts.ToArray()) + "）。" + RunSign();
            if (offlineFrameIds != null) offlineSigned = signed;
            if (skipped > 0) message += "跳过 " + skipped + " 个块：" + string.Join("；", skippedNotes.ToArray()) + "。";
            return message;
        }

        /// <summary>图框编号（5.22.0，原 TKBH / 图框编号2.lsp 的原生移植，面板按钮）：
        /// 固定写入『图号』属性，输入编号前缀、起始数字（默认 1）、排序方向
        ///（5.22.3 起在命令行点选关键字：横向[左→右,上→下：先完成一横排再下一横排] / 纵向[上→下,左→右：先完成一纵列再下一纵列] /
        ///  逆轴[下→上,左→右：先完成一纵列的从下到上，再下一纵列]——鼠标点链接、输关键字或输 1/2/3 均可，回车/默认 = 横向），
        /// 再框选图框块——按排序把编号逐个写进同名属性，10 以内自动补零（01、02…）。
        /// 行/列归组容差 100（与 LSP 版一致）；签名块与签名/定位图层内容静默无视（与图框签字一致）。
        /// 命中属性的块才递增编号；没有该属性的块跳过并在结果里报数。取消返回 null。</summary>
        private static string RunFrameNumber()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            Editor ed = Editor();

            const string targetTag = "图号";

            // 2) 编号前缀（可空）
            PromptStringOptions askPrefix = new PromptStringOptions("\n请输入编号前缀（不需要直接回车）: ");
            PromptResult prefixAnswer = ed.GetString(askPrefix);
            if (prefixAnswer.Status != PromptStatus.OK && prefixAnswer.Status != PromptStatus.None) return null;
            string prefix = prefixAnswer.Status == PromptStatus.OK ? prefixAnswer.StringResult : "";

            // 3) 起始数字（默认 1）
            PromptIntegerOptions askStart = new PromptIntegerOptions("\n请输入起始数字 <1>: ");
            askStart.DefaultValue = 1;
            askStart.UseDefaultValue = true;
            PromptIntegerResult startAnswer = ed.GetInteger(askStart);
            if (startAnswer.Status != PromptStatus.OK && startAnswer.Status != PromptStatus.None) return null;
            int number = startAnswer.Status == PromptStatus.OK ? startAnswer.Value : 1;

            // 4) 排序方向（5.22.3：命令行关键字点选——CAD 会把提示里方括号的选项渲染成
            //    可点击链接，鼠标点选 / 输关键字 / 输旧习惯的 1、2、3 都行；回车空格取默认横向。
            //    5.22.0 的 bug：GetKeywords 命中关键字时状态是 OK 而非 Keyword，旧代码只认
            //    Keyword → 输什么都落默认 "1"，永远横向。5.22.2 的弹窗版被用户否决，改回命令行。）
            //    5.22.4 修：提示语里别再手写方括号选项——CAD 会自动把关键字列表渲染成
            //    可点击链接附在提示语后，手写一份就变成两段重复（用户截图确认）。
            PromptKeywordOptions askMode = new PromptKeywordOptions("\n请选择排序方向 ");
            askMode.Keywords.Add("横向", "横向", "横向(左→右,上→下)");
            askMode.Keywords.Add("纵向", "纵向", "纵向(上→下,左→右)");
            askMode.Keywords.Add("逆轴", "逆轴", "逆轴(下→上,左→右)");
            askMode.Keywords.Add("1");            // 兼容旧习惯：输 1/2/3 同样有效（不进提示语）
            askMode.Keywords.Add("2");
            askMode.Keywords.Add("3");
            askMode.Keywords.Default = "横向";
            askMode.AllowNone = true;
            PromptResult modeAnswer = ed.GetKeywords(askMode);
            if (modeAnswer.Status != PromptStatus.OK && modeAnswer.Status != PromptStatus.None) return null;
            string picked = modeAnswer.Status == PromptStatus.OK ? modeAnswer.StringResult : "横向";
            string mode = picked == "纵向" || picked == "2" ? "2"
                : picked == "逆轴" || picked == "3" ? "3"
                : "1";

            // 5) 框选图框块（只收 INSERT；签名块/签名图层内容静默无视，与图框签字一致）
            PromptSelectionOptions options = new PromptSelectionOptions();
            options.MessageForAdding = "\n框选要统一编号的图框（ESC 取消）: ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            PromptSelectionResult pick = ed.GetSelection(options, filter);
            if (pick.Status != PromptStatus.OK) return null;
            ObjectId[] ids = pick.Value.GetObjectIds();
            if (ids.Length == 0) return "框选范围内没有块参照，未编号。";

            List<FrameNode> nodes = new List<FrameNode>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    if (id.IsNull || id.IsErased) continue;
                    BlockReference frame = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (frame == null) continue;
                    if (IsSignatureBlock(frame, tr)) continue;
                    if (frame.Layer == SignatureLayer || frame.Layer == MarkerLayer) continue;
                    nodes.Add(new FrameNode(frame.Position.X, frame.Position.Y, id));
                }
                tr.Commit();
            }
            if (nodes.Count == 0) return "框选范围内没有图框块，未编号。";

            // 6) 排序（行/列归组容差 100，与 LSP 版一致）
            const double tolerance = 100.0;
            nodes.Sort(delegate (FrameNode a, FrameNode b)
            {
                if (mode == "2")
                {
                    // 纵向：先 X 跨列（左→右），同列按 Y（上→下）
                    if (Math.Abs(a.X - b.X) > tolerance) return a.X.CompareTo(b.X);
                    return b.Y.CompareTo(a.Y);
                }
                if (mode == "3")
                {
                    // 逆轴（5.22.1 按用户定义修正）：先 X 跨列（左→右），同列按 Y（下→上）——
                    // 先完成一纵列的从下到上，再进行下一纵列的从下到上。
                    //（原 LSP 与 5.22.0 首版按「行」从下往上排，是按行不是按列，与用户要的不符。）
                    if (Math.Abs(a.X - b.X) > tolerance) return a.X.CompareTo(b.X);
                    return a.Y.CompareTo(b.Y);
                }
                // 横向（默认 1）：先 Y 跨行（上→下），同行按 X（左→右）
                if (Math.Abs(a.Y - b.Y) > tolerance) return b.Y.CompareTo(a.Y);
                return a.X.CompareTo(b.X);
            });

            // 7) 逐个写入编号（<10 补零，与 LSP 一致）；命中属性的块才递增
            int numbered = 0, noTag = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (FrameNode node in nodes)
                {
                    if (node.Id.IsNull || node.Id.IsErased) continue;
                    BlockReference frame = tr.GetObject(node.Id, OpenMode.ForWrite) as BlockReference;
                    if (frame == null) continue;
                    bool hit = false;
                    foreach (ObjectId attribId in frame.AttributeCollection)
                    {
                        AttributeReference attrib = tr.GetObject(attribId, OpenMode.ForWrite) as AttributeReference;
                        if (attrib == null) continue;
                        if (!string.Equals(attrib.Tag, targetTag, StringComparison.OrdinalIgnoreCase)) continue;
                        attrib.TextString = number < 10 ? prefix + "0" + number : prefix + number;
                        number++;                                   // 与 LSP 一致：命中才递增
                        hit = true;
                    }
                    if (hit) numbered++; else noTag++;
                }
                tr.Commit();
            }
            ed.UpdateScreen();                                       // 编号立即上屏，不用等退出

            string modeName = mode == "2" ? "纵向排序（上→下，左→右）"
                : mode == "3" ? "逆轴排序（下→上，左→右）"
                : "横向排序（左→右，上→下）";
            if (numbered == 0)
                return "选中的块里没有带「" + targetTag + "」属性的，未编号。";
            string message = "已按" + modeName + "给 " + numbered + " 个图框编号（" + targetTag + " 属性）。";
            if (noTag > 0) message += "另有 " + noTag + " 个块没有该属性，未编号。";
            return message;
        }

        /// <summary>图框编号用的节点：插入点坐标 + 块参照 ObjectId。</summary>
        private sealed class FrameNode
        {
            public readonly double X;
            public readonly double Y;
            public readonly ObjectId Id;
            public FrameNode(double x, double y, ObjectId id) { X = x; Y = y; Id = id; }
        }

        /// <summary>「图幅标定」二级菜单（5.18.1 起带确认/取消）：管理 A0/A1/A2/A3 四种图幅
        /// 各自的签名位置。对话框里点某图幅的「标定…」→ 关窗回图上交互（选图框 + 点两下，
        /// 结果先记在本轮改动里、**不落盘**）→ 对话框重新弹出（新状态带「待保存」标记）；
        /// 「清除」同样只记账。点「确认」才一次性写入「图框定位.txt」；「取消」/关窗/ESC
        /// 全部放弃（这就是撤销语义：先记账、后落盘）。关闭时返回保存结果，什么都没做返回 null。</summary>
        private static string RunFrameCalibEditor()
        {
            string baseDir = BaseFolder();
            Dictionary<string, double[]> calibrations = LoadFrameCalibrations();
            Dictionary<string, double[]> pendingSave = new Dictionary<string, double[]>();
            HashSet<string> pendingRemove = new HashSet<string>();
            string pendingCoverName = null;
            while (true)
            {
                using (FrameCalibEditor editor = new FrameCalibEditor(calibrations, pendingSave, pendingRemove))
                {
                    ZwcadBatchPlot.CadWinFormsDialog.Show(editor);
                    if (editor.Result == FrameCalibResult.Calibrate)
                    {
                        string calibratedBlockName;
                        double[] data = RunFrameCalibrate(editor.CalibSlot, out calibratedBlockName);
                        if (data != null)
                        {
                            calibrations[editor.CalibSlot] = data;
                            pendingSave[editor.CalibSlot] = data;
                            pendingRemove.Remove(editor.CalibSlot);
                            if (editor.CalibSlot == "封面") pendingCoverName = calibratedBlockName;
                            Write("已标定 " + editor.CalibSlot + " 图框（内容高 ≈ " + data[0].ToString("F0") + "，该类加长图幅共用），点「确认」后生效。");
                        }
                        else
                        {
                            Write("已取消本次标定，对话框重新打开。");
                        }
                        continue;
                    }
                    if (editor.Result != FrameCalibResult.Save)
                    {
                        // 取消/关窗/ESC：放弃本轮全部改动
                        return pendingSave.Count > 0 || pendingRemove.Count > 0
                            ? "已取消，本轮图幅标定改动未保存。"
                            : null;
                    }
                    // 确认：把本轮改动一次性落盘（其他图幅与旧行原样保留，由 Save/Remove 内部保证）
                    int saved = 0, removed = 0;
                    foreach (KeyValuePair<string, double[]> pair in pendingSave)
                    {
                        double[] data = pair.Value;
                        FrameOffsetConfig.Save(baseDir, pair.Key, data[0], new[] { data[1], data[2], data[3], data[4] });
                        if (pair.Key == "封面" && pendingCoverName != null) FrameOffsetConfig.SaveCoverBlockName(baseDir, pendingCoverName);
                        saved++;
                    }
                    foreach (string slot in pendingRemove)
                    {
                        if (FrameOffsetConfig.Remove(baseDir, slot)) removed++;
                    }
                    if (saved == 0 && removed == 0) return "没有改动需要保存。";
                    return "已保存图幅标定（新建/更新 " + saved + " 项、删除 " + removed + " 项）："
                        + FrameOffsetConfig.ConfigPath(baseDir) + "。";
                }
            }
        }

        /// <summary>标定某一个图幅（A0/A1/A2/A3）的签名位置：选一个该图幅的图框 → 点两下
        /// （姓名列左上角 / 签名列右下角）→ 返回 5 个数（内容**高** + 两角偏移），**不写文件**——
        /// 由对话框的「确认」统一落盘（5.18.1 起对话框有取消语义）。取消/失败返回 null
        /// （失败原因已写到命令行）。</summary>
        private static double[] RunFrameCalibrate(string slot, out string calibratedBlockName)
        {
            calibratedBlockName = null;
            Editor ed = Editor();
            Database db = HostApplicationServices.WorkingDatabase;

            PromptEntityOptions options = new PromptEntityOptions("\n选择一个 " + slot + " 图框（用于标定该图幅的签名位置）: ");
            PromptEntityResult pick = ed.GetEntity(options);
            if (pick.Status != PromptStatus.OK) return null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockReference frame = tr.GetObject(pick.ObjectId, OpenMode.ForRead) as BlockReference;
                if (frame == null)
                {
                    Write("选中的不是块参照——图框需要是一个整体块（INSERT）。");
                    return null;
                }

                Point3d cornerLocal; double localHeight;
                if (!LocalBottomRight(frame, tr, out cornerLocal, out localHeight))
                {
                    Write("取不到这个图框的内容范围（块里可能没有图元）。");
                    return null;
                }

                calibratedBlockName = ((BlockTableRecord)tr.GetObject(frame.IsDynamicBlock ? frame.DynamicBlockTableRecord : frame.BlockTableRecord, OpenMode.ForRead)).Name;
                double[] offsets = CalibrateFrame(ed, frame, cornerLocal, slot == "封面");
                if (offsets == null) return null;       // 点角点时 ESC 取消

                if (Math.Abs(offsets[2] - offsets[0]) < 1 || Math.Abs(offsets[3] - offsets[1]) < 1)
                {
                    Write("标定的签字栏范围太小（两个角点太近）。");
                    return null;
                }
                return new[] { localHeight, offsets[0], offsets[1], offsets[2], offsets[3] };
            }
        }

        /// <summary>读四个图幅的标定（未标定的图幅不在返回的字典里）。
        /// 返回值里每条是 double[5]：内容高 + 两角偏移。</summary>
        private static Dictionary<string, double[]> LoadFrameCalibrations()
        {
            Dictionary<string, double[]> calibrations = new Dictionary<string, double[]>();
            foreach (string slot in FrameOffsetConfig.Slots)
            {
                double[] calibration = FrameOffsetConfig.Load(BaseFolder(), slot);
                if (calibration != null) calibrations[slot] = calibration;
            }
            return calibrations;
        }

        /// <summary>把跳过原因记进列表（同一条原因只记一次，框到一堆块时提示不刷屏）。</summary>
        private static void AddNote(List<string> notes, string note)
        {
            if (!notes.Contains(note)) notes.Add(note);
        }

        /// <summary>现场标定（在「图幅标定」里点了两下之后）：在图上点两下（姓名列左上角 /
        /// 签名列右下角），换算成**相对本实例内容右下角的偏移**（块内坐标）返回；取消返回 null。
        /// 存盘由调用方负责。存偏移而不是存绝对块内坐标，是因为动态图框拉伸后内容会在
        /// 匿名块记录里挪位——只要签字栏跟着图框右缘动（拉伸动作把它圈进去了），偏移不变。</summary>
        private static double[] CalibrateFrame(Editor ed, BlockReference frame, Point3d cornerLocal, bool cover = false)
        {
            Write(cover ? "封面：框选六岗位的完整笔迹区（从项目负责人到设计），不包含制图和姓名列。" : "请在这个图框上点两下：先点签字栏「姓名」列左上角，再点「签名」列右下角。");
            PromptPointResult topLeft = ed.GetPoint(cover ? "\n指定封面笔迹区左上角: " : "\n指定该图框签字栏“姓名”列左上角: ");
            if (topLeft.Status != PromptStatus.OK) return null;
            PromptPointOptions bottomRightOptions = new PromptPointOptions("\n指定签字栏“签名”列右下角: ");
            bottomRightOptions.BasePoint = topLeft.Value; bottomRightOptions.UseBasePoint = true;
            PromptPointResult bottomRight = ed.GetPoint(bottomRightOptions);
            if (bottomRight.Status != PromptStatus.OK) return null;

            Matrix3d toLocal = frame.BlockTransform.Inverse();
            Point3d localTopLeft = topLeft.Value.TransformBy(toLocal);
            Point3d localBottomRight = bottomRight.Value.TransformBy(toLocal);
            return new[] { localTopLeft.X - cornerLocal.X, localTopLeft.Y - cornerLocal.Y,
                           localBottomRight.X - cornerLocal.X, localBottomRight.Y - cornerLocal.Y };
        }

        /// <summary>取图框**本实例实际内容**的右下角（块内坐标）与内容**高度**。关键在
        /// <c>frame.BlockTableRecord</c> 指向的是参照当前使用的块记录：动态块拉伸后是匿名记录
        /// （*U###，内容已被拉伸动作改过），没拉伸过就是源定义——所以这里算出来的右下角随每个
        /// 实例的拉伸状态走（签字栏跟右缘走，锚点不变）。**高度即图幅识别依据**（5.18.2 起）：
        /// 图幅只有向右拉伸，高度不受拉伸影响——A1 拉伸成 A1+0.25/A1+0.5 后高度还是 594，
        /// 标定一次全类共用；而 A0/A1/A2/A3 四类的高度（841/594/420/297）互不相同，照样分得开。
        /// 高度取**块内坐标**，不受插入缩放/世界旋转影响。遍历块记录里的图元取外包框并集；
        /// 个别图元取不到范围（空块参照等）跳过不碍事，整个块取不到才返回 false。</summary>
        private static bool LocalBottomRight(BlockReference frame, Transaction tr, out Point3d corner, out double localHeight)
        {
            corner = Point3d.Origin;
            localHeight = 0;
            bool any = false;
            double maxX = 0, minY = 0, maxY = 0;
            BlockTableRecord record = tr.GetObject(frame.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (record == null) return false;
            foreach (ObjectId id in record)
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null) continue;
                try
                {
                    Extents3d extents = entity.GeometricExtents;
                    if (!any)
                    {
                        maxX = extents.MaxPoint.X;
                        minY = extents.MinPoint.Y;
                        maxY = extents.MaxPoint.Y;
                        any = true;
                    }
                    else
                    {
                        maxX = Math.Max(maxX, extents.MaxPoint.X);
                        minY = Math.Min(minY, extents.MinPoint.Y);
                        maxY = Math.Max(maxY, extents.MaxPoint.Y);
                    }
                }
                catch (CadRuntime.Exception)
                {
                    // 取不到范围的图元（空块参照等）跳过
                }
            }
            if (!any) return false;
            corner = new Point3d(maxX, minY, 0);
            localHeight = maxY - minY;
            return true;
        }

        /// <summary>一键签名：逐个签字区域分别写入姓名与签名，互不影响。</summary>
        private static string RunSign()
        {
            Database db = HostApplicationServices.WorkingDatabase; Editor ed = Editor();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureLayers(db, tr);
                List<Zone> zones = GroupZones(FindSlots(db, tr));
                if (zones.Count == 0) return "未找到签字定位点，请先点「单张签字」。";

                List<Zone> ready = new List<Zone>();
                int broken = 0;
                foreach (Zone zone in zones)
                {
                    if (offlineZones != null && !offlineZones.Contains(zone.Id)) continue;
                    if (zone.IsComplete) ready.Add(zone); else broken++;
                }
                if (ready.Count == 0)
                    return string.Format("未找到完整签字定位（每处应为 {0} 个点，当前 {1} 处区域都不完整）。请重新点「单张签字」。", Roles.Length * 2, zones.Count);

                // 5.9.0：人员是可修改参数 —— 每次签名前现读「人员名单.txt」，改名单立即生效
                Dictionary<string, PersonEntry> people = LoadPeople();
                List<string> unsigned = new List<string>();
                foreach (string role in Roles)
                {
                    if (role == "制图" && !ready.Exists(z => !z.IsCover)) continue;
                    PersonEntry entry;
                    if (!people.TryGetValue(role, out entry) || entry == null || string.IsNullOrWhiteSpace(entry.Name)) unsigned.Add(role);
                }

                ShowSignatureLayer(db, tr);
                BlockTableRecord space = (BlockTableRecord)tr.GetObject(SignatureSpace(db), OpenMode.ForWrite);
                int signedRows = 0;
                foreach (Zone zone in ready)
                {
                    ClearSignatures(db, tr, zone, zones);
                    Point3d[] counterPositions;
                    var counterFrame = FindCountersignFrame(space, tr, zone, out counterPositions);
                    bool directoryOnly = IsDirectoryFrame(counterFrame, tr);
                    foreach (string role in Roles)
                    {
                        if (directoryOnly) break;
                        Slot nameSlot = zone.Slots.Find(s => s.Role == role && s.Kind == "NAME");
                        Slot signSlot = zone.Slots.Find(s => s.Role == role && s.Kind == "SIGN");
                        if ((!zone.IsCover && nameSlot == null) || signSlot == null) continue;
                        PersonEntry person;
                        if (!people.TryGetValue(role, out person) || person == null || string.IsNullOrWhiteSpace(person.Name)) continue;
                        if (!zone.IsCover) AddName(space, tr, nameSlot, person.Name);
                        InsertSignature(db, space, tr, signSlot, person, ed);
                        signedRows++;
                    }
                    AddCountersignNames(db, space, tr, zone, people);
                }
                tr.Commit();

                string message = string.Format("签名已添加：{0} 处签字区域，共 {1} 行。", ready.Count, signedRows);
                if (broken > 0) message += string.Format("另有 {0} 处定位点不完整，已跳过（请对该处重跑「单张签字」）。", broken);
                if (unsigned.Count > 0) message += string.Format("未配置姓名的岗位没有签（{0}），点「人员设置」可补。", string.Join("、", unsigned.ToArray()));
                return message;
            }
        }

        /// <summary>清除签名（5.16.0 改）：改成**鼠标框选删**，不再弹「区域 1 / 区域 2」勾选框。
        /// 框住什么就删什么，框外一律不动：
        /// ①落在框内的定位点删掉；②落在框内的签名/姓名删掉；③已无人引用的签名块定义删掉。
        /// 所以框住某区域的 3 行 → 只删这 3 行的签名与定位点，该区域剩下 4 行照旧保留、照旧有效。
        /// 「是否落在框内」按图元**外包框全部落在矩形内**判定（与 CAD 的交叉窗口区分开，不会误删擦边的）。
        /// 取消时返回 null。</summary>
        private static string RunClear()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            Editor ed = Editor();

            Point3d first, second;
            if (!PickClearWindow(ed, out first, out second)) return null;
            double left = Math.Min(first.X, second.X), right = Math.Max(first.X, second.X);
            double bottom = Math.Min(first.Y, second.Y), top = Math.Max(first.Y, second.Y);

            int markerCount = 0, signCount = 0;
            int markerSkip = 0, signSkip = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<Slot> slots = FindSlots(db, tr);
                List<ObjectId> markers = MarkersIn(slots, left, right, bottom, top);
                List<ObjectId> signs = (markers.Count > 0 || HasSignatureEntities(db, tr))
                    ? SignatureEntitiesIn(db, tr, left, right, bottom, top) : new List<ObjectId>();

                if (markers.Count == 0 && signs.Count == 0)
                    return "框选范围内没有签名定位点，也没有签名，图纸未改动。";

                // ⚠️ 必须先判 IsErased 再删（5.16.1）：已被删除的对象无法以 ForWrite 打开，
                // tr.GetObject 会抛 eWasErased，「无法清除」就是这么来的。这里跳过并记账，
                // 让结果提示如实反映「真正删掉几个」，不静默吞掉。
                foreach (ObjectId id in markers) EraseIfAlive(tr, id, ref markerCount, ref markerSkip);
                foreach (ObjectId id in signs) EraseIfAlive(tr, id, ref signCount, ref signSkip);
                tr.Commit();
            }

            // 块定义必须等上面的删除事务**提交并关闭之后**再清：此时块参照才真的没了引用。
            // 这里让函数自己开一个独立事务，不与上面的 using 嵌套（嵌套会报错）。
            int removed = RemoveUnusedSignatureBlocks(db);
            offlineRemoved = markerCount + signCount + removed;

            return BuildClearMessage(markerCount, signCount, removed, markerSkip + signSkip);
        }

        /// <summary>全图清除（5.20.0，「清除签字」+ 勾选「全部」）：不进框选，直接清整个图形。
        /// ①删掉 <see cref="MarkerLayer"/>（CAD_签名定位）与 <see cref="SignatureLayer"/>（CAD_签名）
        ///   两个图层上的**所有**内容——遍历块表里的全部块表记录（模型空间、所有布局、普通块定义内部），
        ///   外部参照里的不动；
        /// ②顺手删掉**任何图层上**指向签名块（CAD_SIG_*）的块参照——用户手工 INSERT 到别的图层的签名块也一并清掉；
        /// ③最后等删除事务提交后，清掉整个图形中已无引用的签名块定义（相当于对签名块做一次 PURGE）。
        /// 图层本身保留（下次签名还要用）。</summary>
        private static string RunClearAll()
        {
            Database db = HostApplicationServices.WorkingDatabase;

            int markerCount = 0, signCount = 0, markerSkip = 0, signSkip = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                // 先收集后删除：枚举 BlockTableRecord 的过程中 Erase 会让枚举器失效，
                // 必须先把要删的 ObjectId 全部收集完，再统一删（与 RunClear 的两段式一致）。
                List<ObjectId> markers = new List<ObjectId>();
                List<ObjectId> signs = new List<ObjectId>();
                foreach (ObjectId recordId in table)
                {
                    BlockTableRecord record = tr.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                    if (record == null || record.IsFromExternalReference) continue;

                    List<ObjectId> entityIds = new List<ObjectId>();
                    foreach (ObjectId id in record) entityIds.Add(id);

                    foreach (ObjectId id in entityIds)
                    {
                        Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (entity == null) continue;
                        if (entity.Layer == MarkerLayer) { markers.Add(id); continue; }
                        if (entity.Layer == SignatureLayer) { signs.Add(id); continue; }
                        // 不在两个签名图层上、但本身是签名块参照的（用户手工 INSERT 到别的图层的）也算签名
                        BlockReference reference = entity as BlockReference;
                        if (reference != null && IsSignatureBlock(reference, tr)) signs.Add(id);
                    }
                }

                // ⚠️ 先判 IsErased 再删（5.16.1 的教训）：对已删对象 ForWrite 打开会抛 eWasErased。
                foreach (ObjectId id in markers) EraseIfAlive(tr, id, ref markerCount, ref markerSkip);
                foreach (ObjectId id in signs) EraseIfAlive(tr, id, ref signCount, ref signSkip);
                tr.Commit();
            }

            // 块定义必须等删除事务提交并关闭之后再清：此时块参照才真的没了引用。
            // 上面已把所有签名块参照（含手工 INSERT 的）删光，这一步会把 CAD_SIG_* 全部清掉，
            // 相当于对签名块做了一次 PURGE（做法与 CAD 的 _PU 一致：只删无引用的）。
            int removed = RemoveUnusedSignatureBlocks(db);
            offlineRemoved = markerCount + signCount + removed;

            if (markerCount == 0 && signCount == 0 && removed == 0)
                return "全图没有签名、定位点与签名块，图纸未改动。";

            string message = string.Format("已全图清除：删掉 {0} 项签名、{1} 个定位点", signCount, markerCount);
            if (removed > 0) message += string.Format("，并清掉 {0} 个签名块定义", removed);
            message += "。";
            if (markerSkip + signSkip > 0)
                message += string.Format("（另有 {0} 个对象此前已是「已删除」状态，未动。）", markerSkip + signSkip);
            return message;
        }

        /// <summary>块参照指向的是不是签名块（块名以 CAD_SIG_ 开头）。
        /// 取**块定义记录的名字**（动态块拉伸后参照名是匿名 *U###，源名要开 BlockTableRecord 读）。</summary>
        private static bool IsSignatureBlock(BlockReference reference, Transaction tr)
        {
            if (reference == null || reference.BlockTableRecord.IsNull) return false;
            BlockTableRecord definition = tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            string name = definition != null ? definition.Name : reference.Name;
            return name != null && name.StartsWith("CAD_SIG_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>删除一个对象；已被删除的（IsErased）跳过并计入 <paramref name="skipped"/>，
        /// 不抛 eWasErased。计数只在真的删掉时 +1。</summary>
        private static void EraseIfAlive(Transaction tr, ObjectId id, ref int done, ref int skipped)
        {
            if (id.IsNull || id.IsErased) { skipped++; return; }
            tr.GetObject(id, OpenMode.ForWrite).Erase();
            done++;
        }

        /// <summary>取清除范围：只问「两个角点」构成一个矩形（5.16.2 重写）。
        ///
        /// **为什么不用 GetSelection**：`GetSelection` 要求用户**选中图元对象**，而定位点是
        /// 关闭且不打印的 `DBPoint`（图上几乎看不见），框选时选不中它们 —— 用户明明框住了
        /// 一大片，`GetSelection` 还是返回空，程序就以为「范围内没有内容」，于是
        /// 「怎么框都没反应」。**取范围不该依赖「选中了什么图元」**，只取几何矩形即可。
        ///
        /// 现在与 CAD 的 `ZOOM Window` 同一个交互：点第一个角 → 拖出橡皮筋框 → 点对角完成。
        /// ESC 在任一步取消，返回 false。</summary>
        private static bool PickClearWindow(Editor ed, out Point3d first, out Point3d second)
        {
            first = Point3d.Origin;
            second = Point3d.Origin;

            PromptPointResult corner1 = ed.GetPoint("\n指定清除范围的第一个角点（ESC 取消）: ");
            if (corner1.Status != PromptStatus.OK) return false;

            // PromptCornerOptions 的构造器已经吃下基点（等价于 UseBasePoint=true），
            // 不必也不该再设 UseBasePoint —— 该类型没有这个属性。
            PromptCornerOptions options = new PromptCornerOptions("\n指定对角点（框内的签名与定位点全删，框外不动）: ", corner1.Value);
            PromptPointResult corner2 = ed.GetCorner(options);
            if (corner2.Status != PromptStatus.OK) return false;

            first = corner1.Value;
            second = corner2.Value;
            return true;
        }

        /// <summary>框内定位点：按外包框**完全落在矩形内**判定（严格框住才删）。</summary>
        private static List<ObjectId> MarkersIn(List<Slot> slots, double left, double right, double bottom, double top)
        {
            List<ObjectId> ids = new List<ObjectId>();
            foreach (Slot slot in slots)
            {
                if (slot.Center.X < left || slot.Center.X > right) continue;
                if (slot.Center.Y < bottom || slot.Center.Y > top) continue;
                ids.Add(slot.Id);
            }
            return ids;
        }

        /// <summary>框内签名实体（图层 = CAD_签名 的姓名与签名块）：外包框全部落在矩形内才算。
        /// 取不到外包框的实体退回用中心点判定，避免整块都擦着边界时被漏掉。</summary>
        private static List<ObjectId> SignatureEntitiesIn(Database db, Transaction tr, double left, double right, double bottom, double top)
        {
            BlockTableRecord space = (BlockTableRecord)tr.GetObject(SignatureSpace(db), OpenMode.ForRead);
            List<ObjectId> ids = new List<ObjectId>();
            foreach (ObjectId id in space)
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || entity.Layer != SignatureLayer) continue;
                if (IsInside(entity, left, right, bottom, top)) ids.Add(id);
            }
            return ids;
        }

        private static bool IsInside(Entity entity, double left, double right, double bottom, double top)
        {
            try
            {
                Extents3d extents = entity.GeometricExtents;
                return extents.MinPoint.X >= left && extents.MaxPoint.X <= right
                    && extents.MinPoint.Y >= bottom && extents.MaxPoint.Y <= top;
            }
            catch (System.Exception)
            {
                Point3d center;
                if (!TryGetCenter(entity, out center)) return false;
                return center.X >= left && center.X <= right && center.Y >= bottom && center.Y <= top;
            }
        }

        /// <summary>清除结果提示。删干净了就走「无需清除」的措辞，只删了部分则点明还剩多少、怎么恢复。
        /// <paramref name="skipped"/> = 已被删除、本次跳过没动的对象数（正常为 0；非 0 说明图上有残迹，
        /// 报出来避免「按了没反应」的错觉）。</summary>
        private static string BuildClearMessage(int markerCount, int signCount, int removedBlocks, int skipped)
        {
            if (markerCount == 0 && signCount == 0)
            {
                if (skipped == 0) return "框选范围内没有签名定位点，也没有签名，图纸未改动。";
                return string.Format("框选范围内的 {0} 个对象已经是「已删除」状态（图上不可见），本次未做改动。", skipped);
            }

            string message = string.Format("已清除框选范围内的 {0} 项签名与 {1} 个定位点；框外内容未改动。", signCount, markerCount);
            if (removedBlocks > 0) message += string.Format("并删掉 {0} 个无引用的签名块定义。", removedBlocks);
            if (skipped > 0) message += string.Format("另有 {0} 个对象此前已是「已删除」状态，未动。", skipped);
            message += "若只剩部分定位点，请点「单张签字」重新框选该处补全，或再框选一次清除剩下的。";
            return message;
        }

        /// <summary>本空间里是否还有插件写入的签名实体（图层 = CAD_签名）。</summary>
        private static bool HasSignatureEntities(Database db, Transaction tr)
        {
            BlockTableRecord space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity != null && entity.Layer == SignatureLayer) return true;
            }
            return false;
        }

        /// <summary>删掉「没有任何块参照引用」的签名块定义（块名以 CAD_SIG_ 开头）。
        /// 用户手工 INSERT 过、图上还在用的签名块会被保留，不会误删。
        ///
        /// ⚠️ **必须在调用方的删除事务提交并关闭之后调用** —— 本函数自带一个事务，
        /// 套在另一个已提交/未提交的事务里会抛 eInvalidInput。</summary>
        private static int RemoveUnusedSignatureBlocks(Database db)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                int count = RemoveUnusedSignatureBlocksCore(db, tr);
                tr.Commit();
                return count;
            }
        }

        private static int RemoveUnusedSignatureBlocksCore(Database db, Transaction tr)
        {
            BlockTable table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            // 1) 圈定候选：块名以 CAD_SIG_ 开头的块定义
            List<ObjectId> candidates = new List<ObjectId>();
            foreach (ObjectId id in table)
            {
                string name = GetBlockName(id, tr);
                if (name != null && name.StartsWith("CAD_SIG_", StringComparison.OrdinalIgnoreCase)) candidates.Add(id);
            }
            if (candidates.Count == 0) return 0;
            HashSet<ObjectId> candidateSet = new HashSet<ObjectId>(candidates);

            // 2) 找出「还被块参照指向」的候选。遍历块表里的每一项块表记录（含模型空间、各布局页、
            //    以及别的块定义），看其中有没有 BlockReference 指向候选块 —— 指向了就说明还有人用。
            //    这样既覆盖“图上的签名块”，也覆盖“被别的块嵌套引用”的情况，不会误删。
            HashSet<ObjectId> referenced = new HashSet<ObjectId>();
            foreach (ObjectId recordId in table)
            {
                BlockTableRecord record = tr.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                if (record == null || record.IsAnonymous) continue;
                foreach (ObjectId entityId in record)
                {
                    BlockReference reference = tr.GetObject(entityId, OpenMode.ForRead) as BlockReference;
                    if (reference == null) continue;
                    ObjectId target = reference.BlockTableRecord;
                    if (candidateSet.Contains(target)) referenced.Add(target);
                }
            }

            // 3) 没有任何块参照指向的候选 → 删掉
            List<ObjectId> dead = new List<ObjectId>();
            foreach (ObjectId id in candidates)
            {
                if (!referenced.Contains(id)) dead.Add(id);
            }
            foreach (ObjectId id in dead)
            {
                if (id.IsNull || id.IsErased) continue;      // 已删除的不能以 ForWrite 打开（eWasErased）
                tr.GetObject(id, OpenMode.ForWrite).Erase();
            }
            return dead.Count;
        }

        private static string GetBlockName(ObjectId id, Transaction tr)
        {
            SymbolTableRecord record = tr.GetObject(id, OpenMode.ForRead) as SymbolTableRecord;
            return record == null ? null : record.Name;
        }

        /// <summary>人员设置（5.9.0）：弹出人员编辑对话框，改「岗位 → 姓名 → 签名笔迹」。
        /// 保存后立即生效：下一次「一键签名」就按新名单执行，不用重新编译也不用重启 AutoCAD。
        /// 5.10.0 起对话框底部有「制作签名文件」：填姓名点「选取…」就回图上框选签名笔迹、
        /// 存成 姓名.dwg，然后重新弹出对话框。取消时返回 null。
        /// 5.12.0 起可「设置默认文件夹」换签名 .dwg 的存放位置（设置后对话框用新文件夹重新弹出）；
        /// **5.26.0 起这个按钮撤掉**（改用「导出人员设置」配置包带走全部设置），于是对话框不再
        /// 回传 DirectoryChanged，这个循环少一个分支；要换文件夹可直接编辑核心 DLL 旁的
        /// 「签名文件夹.txt」（读取逻辑原样保留，老用户设过的照旧生效）。</summary>
        private static string RunEditPeople()
        {
            string directory = SignatureDirectory();
            Dictionary<string, PersonEntry> people = LoadPeople();
            while (true)
            {
                using (PeopleEditor editor = new PeopleEditor(people, directory, BaseFolder(), CoreEntry.PackageDirectory))
                {
                    ZwcadBatchPlot.CadWinFormsDialog.Show(editor);
                    if (editor.Saved)
                    {
                        // 名单固定存核心 DLL 旁（PeopleEditor 保存的就是这个位置），提示里回显真实路径
                        people = LoadPeople();
                        return "人员配置已保存（" + editor.SavedPath + "），再点「单张签字」或「图框签字」就按新名单执行。";
                    }
                    if (editor.CaptureName == null) return null;   // 点了「取消」或直接关闭
                    string result = CaptureSignature(directory, editor.CaptureName);
                    Write(result);
                    continue;       // 重新弹出对话框，可以接着做下一个
                }
            }
        }

        /// <summary>读人员名单。5.12.0 起名单固定存核心 DLL 旁（不再跟签名文件夹走，
        /// 换文件夹不影响名单）；5.9–5.11 曾把名单写在签名文件夹里，那里还留着旧文件、
        /// 新位置没有时就继续读旧文件，首次「保存」后写到新位置，旧文件自然弃用。
        ///
        /// ⚠️ 注意 <c>PeopleConfig.Load</c> 的参数语义是**目录**（内部会再拼上文件名）。
        /// 5.13.1 这里曾把 ConfigPath 的结果（完整文件路径）又传了回去，等于拼了两次文件名
        /// （…\人员名单.txt\人员名单.txt），文件永远找不到 → 静默回退默认名单，
        /// 表现为「保存后重开对话框还是默认值」。5.13.3 起统一传目录。</summary>
        private static Dictionary<string, PersonEntry> LoadPeople()
        {
            // 5.26.0：读名单之前先瞄一眼 DLL 旁边的「人员设置.cspkg」配置包 ——
            // 本机还没有自己的名单（或还是出厂示例姓名）时，就按包里的名单填、把它缺的笔迹补进
            // 加密库。两条都是"只补不覆盖"且幂等的，所以挂在这里每次跑一遍也没副作用；
            // 没有包时只花一次 File.Exists，不拖慢签名。
            PackageResult applied = SettingsPackage.ApplyIfNeeded(CoreEntry.PackageDirectory, BaseFolder(), SignatureDirectory());
            if (applied != null && !string.IsNullOrEmpty(applied.Message)) Write(applied.Message);

            string homeDir = BaseFolder();
            if (File.Exists(PeopleConfig.ConfigPath(homeDir))) return PeopleConfig.Load(homeDir);
            string legacyDir = SignatureDirectory();
            if (!string.Equals(PeopleConfig.ConfigPath(legacyDir), PeopleConfig.ConfigPath(homeDir), StringComparison.OrdinalIgnoreCase)
                && File.Exists(PeopleConfig.ConfigPath(legacyDir)))
                return PeopleConfig.Load(legacyDir);
            return PeopleConfig.Load(homeDir);     // 都没有：内置默认名单
        }

        /// <summary>制作签名文件（5.10.0）：把图上框选的签名笔迹跨库克隆进新的 DWG，
        /// 按用户指定的插入点平移到原点（默认 = 所选图元的外包框中心），存到签名素材目录，
        /// 命名 姓名.dwg —— 与「一键签名」的默认找图规则（Signatures\姓名.dwg）直接对接。
        ///
        /// 5.15.0：克隆进目标库后把**所有图元与嵌套块内图元统一改到 0 图层**（图层、颜色全部随层）。
        /// 此前笔迹在哪个图层就带哪个图层，插入图纸后「隐藏签名」只关 CAD_签名 一层，
        /// 别的图层上的笔迹关不掉 —— 改到 0 图层后，块内图元跟随块参照所在图层（CAD_签名），
        /// 隐藏/显示就整齐了。</summary>
        private static string CaptureSignature(string directory, string name)
        {
            Editor ed = Editor();
            Database db = HostApplicationServices.WorkingDatabase;

            PromptSelectionOptions selectOptions = new PromptSelectionOptions();
            selectOptions.MessageForAdding = "\n框选签名笔迹图元: ";
            PromptSelectionResult selection = ed.GetSelection(selectOptions);
            if (selection.Status != PromptStatus.OK || selection.Value.Count == 0) return "已取消：未选择签名图元。";

            // 算所选图元的外包框（个别取不到范围的图元跳过），默认插入点 = 外包框中心
            bool found = false;
            Extents3d bounds = new Extents3d();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null) continue;
                    Entity entity = tr.GetObject(selected.ObjectId, OpenMode.ForRead) as Entity;
                    if (entity == null) continue;
                    try
                    {
                        if (found) bounds.AddExtents(entity.GeometricExtents);
                        else { bounds = entity.GeometricExtents; found = true; }
                    }
                    catch (CadRuntime.Exception) { }
                }
                tr.Commit();
            }
            if (!found) return "所选图元取不到外包框，无法定位签名，已放弃。";
            Point3d center = new Point3d((bounds.MinPoint.X + bounds.MaxPoint.X) / 2, (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2, 0);

            PromptPointResult point = ed.GetPoint("\n指定签名插入点（回车 = 外包框中心）: ");
            if (point.Status == PromptStatus.Cancel || point.Status == PromptStatus.Error) return "已取消。";
            Point3d insertPoint = point.Status == PromptStatus.OK ? point.Value : center;

            ObjectIdCollection ids = new ObjectIdCollection();
            foreach (SelectedObject selected in selection.Value)
            {
                if (selected != null) ids.Add(selected.ObjectId);
            }
            int count = ids.Count;
            bool overwritten;
            int relaid;
            string storeName = SafeFileName(name) + ".dwg";
            using (Database dest = new Database(true, true))
            {
                // 跨库克隆必须用 Wblock 克隆：DeepCloneObjects 只支持同一数据库内部
                // （owner 不属于源库会抛 eWrongDatabase，5.10.1 实测踩过）；图层、文字样式
                // 等符号表依赖由 DuplicateRecordCloning 规则一并带入目标库
                IdMapping map = new IdMapping();
                map.DestinationDatabase = dest;
                db.WblockCloneObjects(ids, dest.CurrentSpaceId, map, DuplicateRecordCloning.Ignore, false);
                using (Transaction dtr = dest.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)dtr.GetObject(dest.CurrentSpaceId, OpenMode.ForWrite);

                    // 5.15.0：统一改到 0 图层（见方法注释）。放在平移之前，两者互不影响。
                    relaid = RelayerToZero(dest, dtr, space);

                    foreach (ObjectId id in space)
                    {
                        Entity entity = dtr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (entity != null) entity.TransformBy(Matrix3d.Displacement(Point3d.Origin - insertPoint));
                    }
                    dtr.Commit();
                }
                // 5.23.0：不再明文落盘 Signatures\姓名.dwg —— 先存临时文件，读进内存后
                // AES 加密进隐藏素材库（Signatures\.db，GUID 文件名 + Hidden+System），
                // 临时文件随即删除。明文 .dwg 资源管理器能看到、按姓名能搜到、谁都能拷走；
                // 加密库默认不可见、不索引、搜不到，拷走没有密钥也解不开。
                string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dwg");
                try
                {
                    dest.SaveAs(temp, DwgVersion.Current);
                    byte[] bytes = File.ReadAllBytes(temp);
                    overwritten = SignatureVault.Contains(directory, storeName);
                    SignatureVault.Save(directory, storeName, bytes);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (System.Exception) { }
                }
            }

            string message = string.Format("签名已加密保存进隐藏素材库：{0}（{1} 个图元，插入点{2}）——文件夹里看不到、搜索也搜不到，「一键签名」照常使用。", storeName, count,
                insertPoint == center ? "取外包框中心" : "按指定点对齐原点");
            if (relaid > 0) message += string.Format("已把 {0} 个图元归到 0 图层，隐藏签名时能一起关掉。", relaid);
            if (overwritten) message += "（覆盖了同名旧签名）";
            return message;
        }

        /// <summary>把目标库里这些图元（含嵌套块定义内部的图元）统一改到 0 图层。
        /// 块定义只需处理一次，用一个集合记着避免重复遍历（同一块被多次插入时）。
        /// 返回改动过的图元数。图层不存在时（理论上 0 层必有）静默跳过。
        /// 5.15.1 修：实体必须以 ForWrite 打开才能改图层 —— 首版顶层循环用了 ForRead，
        /// 笔迹顶层一旦有非 0 图层图元就直接「内部错误 eNotOpenForWrite」中断；
        /// 同时把克隆带过来的锁定图层临时解锁（锁着的图层上图元改不了属性），收尾恢复。</summary>
        private static int RelayerToZero(Database dest, Transaction tr, BlockTableRecord space)
        {
            int changed = 0;
            LayerTable layers = (LayerTable)tr.GetObject(dest.LayerTableId, OpenMode.ForRead);
            if (!layers.Has("0")) return 0;
            ObjectId zero = layers["0"];
            HashSet<ObjectId> done = new HashSet<ObjectId>();

            List<ObjectId> locked = new List<ObjectId>();
            foreach (ObjectId id in layers)
            {
                LayerTableRecord layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (layer.IsLocked) locked.Add(id);
            }
            foreach (ObjectId id in locked) ((LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite)).IsLocked = false;
            try
            {
                foreach (ObjectId id in space)
                {
                    Entity entity = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (entity != null) changed += RelayerEntity(entity, tr, zero, done);
                }
            }
            finally
            {
                foreach (ObjectId id in locked) ((LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite)).IsLocked = true;
            }
            return changed;
        }

        /// <summary>把一个实体改到 0 图层；是块参照就递归处理它的块定义。
        /// <paramref name="done"/> 记录已处理的块定义，避免同一块被多次遍历。</summary>
        private static int RelayerEntity(Entity entity, Transaction tr, ObjectId zero, HashSet<ObjectId> done)
        {
            int changed = 0;
            if (entity.Layer != "0")
            {
                entity.Layer = "0";
                changed++;
            }
            BlockReference reference = entity as BlockReference;
            if (reference == null) return changed;

            ObjectId definitionId = reference.BlockTableRecord;
            if (!done.Add(definitionId)) return changed;     // 这个块定义已经处理过
            BlockTableRecord definition = tr.GetObject(definitionId, OpenMode.ForRead) as BlockTableRecord;
            if (definition == null) return changed;
            foreach (ObjectId childId in definition)
            {
                // 块定义里的图元要以 ForWrite 打开才能改图层
                Entity child = tr.GetObject(childId, OpenMode.ForWrite) as Entity;
                if (child != null) changed += RelayerEntity(child, tr, zero, done);
            }
            return changed;
        }

        /// <summary>把姓名转成合法文件名（非法字符换成下划线）。</summary>
        private static string SafeFileName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return name.Trim();
        }

        /// <summary>切换签名图层的开/关。</summary>
        private static string RunToggleHidden()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!table.Has(SignatureLayer)) return "图纸上还没有签名，请先点「单张签字」。";
                LayerTableRecord layer = (LayerTableRecord)tr.GetObject(table[SignatureLayer], OpenMode.ForWrite);
                layer.IsOff = !layer.IsOff;
                bool hidden = layer.IsOff;
                tr.Commit();
                return hidden ? "签名已隐藏（“" + SignatureLayer + "” 图层已关闭，不会被打印）。" : "签名已恢复显示。";
            }
        }

        private static void Write(string message)
        {
            if (!string.IsNullOrEmpty(message)) Editor().WriteMessage("\n" + message);
        }

        private static Editor Editor() { return AcApp.DocumentManager.MdiActiveDocument.Editor; }

        /// <summary>定位点统计：区域数、定位点总数、定位点不完整的区域数（只读，不改动图纸）。</summary>
        private static void MarkerStats(out int zoneCount, out int markerCount, out int brokenZones)
        {
            Database db = HostApplicationServices.WorkingDatabase;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<Slot> slots = FindSlots(db, tr);
                List<Zone> zones = GroupZones(slots);
                zoneCount = zones.Count;
                markerCount = slots.Count;
                brokenZones = 0;
                foreach (Zone zone in zones)
                {
                    if (!zone.IsComplete) brokenZones++;
                }
            }
        }

        /// <summary>签名图层是否可见；图层还不存在时视为可见。</summary>
        private static bool SignatureLayerVisible()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!table.Has(SignatureLayer)) return true;
                LayerTableRecord layer = (LayerTableRecord)tr.GetObject(table[SignatureLayer], OpenMode.ForRead);
                return !layer.IsOff;
            }
        }

        /// <summary>当前空间名称，提示用户定位点跟着空间走。</summary>
        private static string SpaceName()
        {
            Database db = HostApplicationServices.WorkingDatabase;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Layout layout = tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as Layout;
                if (layout == null) return "当前空间";
                return layout.LayoutName == "Model" ? "模型空间" : "布局「" + layout.LayoutName + "」";
            }
        }

        private static void ShowSignatureLayer(Database db, Transaction tr)
        {
            LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!table.Has(SignatureLayer)) return;
            LayerTableRecord layer = (LayerTableRecord)tr.GetObject(table[SignatureLayer], OpenMode.ForWrite);
            layer.IsOff = false;
        }

        private static void EnsureLayers(Database db, Transaction tr)
        {
            LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            AddLayer(table, tr, MarkerLayer, true); AddLayer(table, tr, SignatureLayer, false);
            var markerLayer = (LayerTableRecord)tr.GetObject(table[MarkerLayer], OpenMode.ForWrite);
            markerLayer.IsOff = true;
            markerLayer.IsPlottable = false;
            EnsureRegApp(db, tr);
        }

        /// <summary>确保图中存在「黑体」文字样式（字体 SimHei、字型常规、其余取默认），返回其 Id；
        /// 图中已有同名样式就直接用，不覆盖用户已有的设置。</summary>
        private static ObjectId EnsureNameTextStyle(Database db, Transaction tr)
        {
            TextStyleTable table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (table.Has(NameTextStyleName)) return table[NameTextStyleName];
            table.UpgradeOpen();
            TextStyleTableRecord style = new TextStyleTableRecord();
            style.Name = NameTextStyleName;
            style.Font = new FontDescriptor(NameFontTypeface, false, false, 0, 0);
            table.Add(style);
            tr.AddNewlyCreatedDBObject(style, true);
            return style.ObjectId;
        }

        private static void AddLayer(LayerTable table, Transaction tr, string name, bool hidden)
        {
            if (table.Has(name)) return;
            table.UpgradeOpen(); LayerTableRecord layer = new LayerTableRecord(); layer.Name = name; layer.IsPlottable = !hidden; layer.IsOff = hidden;
            table.Add(layer); tr.AddNewlyCreatedDBObject(layer, true);
        }

        private static void EnsureRegApp(Database db, Transaction tr)
        {
            RegAppTable apps = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (apps.Has(AppName)) return;
            apps.UpgradeOpen(); RegAppTableRecord app = new RegAppTableRecord(); app.Name = AppName; apps.Add(app); tr.AddNewlyCreatedDBObject(app, true);
        }

        /// <summary>建立定位点。XData 依次存：岗位 / NAME|SIGN / 槽宽 / 槽高 / 所属签字区域编号。</summary>
        private static void CreateMarker(BlockTableRecord space, Transaction tr, Point3d point, string role, string kind, double width, double height, int zoneId, double rotation, bool cover = false)
        {
            DBPoint marker = new DBPoint(point); marker.SetDatabaseDefaults(); marker.Layer = MarkerLayer;
            marker.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, role),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, kind),
                new TypedValue((int)DxfCode.ExtendedDataReal, width),
                new TypedValue((int)DxfCode.ExtendedDataReal, height),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, zoneId),
                new TypedValue((int)DxfCode.ExtendedDataReal, rotation), new TypedValue((int)DxfCode.ExtendedDataAsciiString, cover ? "封面" : "标准"));   // 5.24.0：签字栏旋转角（弧度）
            space.AppendEntity(marker); tr.AddNewlyCreatedDBObject(marker, true);
        }

        private static List<Slot> FindSlots(Database db, Transaction tr)
        {
            List<Slot> result = new List<Slot>();
            BlockTableRecord space = (BlockTableRecord)tr.GetObject(SignatureSpace(db), OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                DBPoint point = tr.GetObject(id, OpenMode.ForRead) as DBPoint;
                if (point == null || point.Layer != MarkerLayer || point.XData == null) continue;
                TypedValue[] values = point.XData.AsArray();
                if (values.Length < 5 || Convert.ToString(values[0].Value) != AppName) continue;
                result.Add(new Slot
                {
                    Id = id,
                    Role = Convert.ToString(values[1].Value),
                    Kind = Convert.ToString(values[2].Value),
                    Center = point.Position,
                    Width = Convert.ToDouble(values[3].Value),
                    Height = Convert.ToDouble(values[4].Value),
                    // 5.5.0 之前建立的定位点没有区域编号，统一归入区域 0（仍是独立的一组，照样能用）
                    ZoneId = values.Length >= 6 ? Convert.ToInt32(values[5].Value) : 0,
                    // 5.24.0 之前建立的定位点没有旋转角（横平竖直），按 0 读
                    Rotation = values.Length >= 7 ? Convert.ToDouble(values[6].Value) : 0,
                    IsCover = values.Length >= 8 && Convert.ToString(values[7].Value) == "封面"
                });
            }
            return result;
        }

        /// <summary>按所属区域把定位点分组，并算出每处区域的矩形范围（供判断签名归属）。</summary>
        private static List<Zone> GroupZones(List<Slot> slots)
        {
            List<Zone> zones = new List<Zone>();
            foreach (Slot slot in slots)
            {
                Zone zone = zones.Find(z => z.Id == slot.ZoneId);
                if (zone == null)
                {
                    zone = new Zone { Id = slot.ZoneId, Left = double.MaxValue, Right = double.MinValue, Bottom = double.MaxValue, Top = double.MinValue };
                    zones.Add(zone);
                }
                zone.Slots.Add(slot);
                double halfWidth = slot.Width / 2, halfHeight = slot.Height / 2;
                zone.Left = Math.Min(zone.Left, slot.Center.X - halfWidth);
                zone.Right = Math.Max(zone.Right, slot.Center.X + halfWidth);
                zone.Bottom = Math.Min(zone.Bottom, slot.Center.Y - halfHeight);
                zone.Top = Math.Max(zone.Top, slot.Center.Y + halfHeight);
            }
            zones.Sort((a, b) => a.Id.CompareTo(b.Id));
            return zones;
        }

        /// <summary>重建某一处签字区域前，清掉与该框选范围重叠的旧定位点。
        /// 判定方式：只要有任一定位点落在框选矩形内，就把它**所属的那一组**整体清掉，避免留下半组残点；
        /// 与之无关的其他签字区域原样保留。返回本次可用的新区域编号。</summary>
        private static int ClearMarkersIn(Database db, Transaction tr, double left, double right, double bottom, double top)
        {
            List<Slot> slots = FindSlots(db, tr);
            int maxZone = 0;
            foreach (Slot slot in slots)
            {
                if (slot.ZoneId > maxZone) maxZone = slot.ZoneId;
            }

            HashSet<int> hit = new HashSet<int>();
            foreach (Slot slot in slots)
            {
                if (slot.Center.X >= left && slot.Center.X <= right && slot.Center.Y >= bottom && slot.Center.Y <= top) hit.Add(slot.ZoneId);
            }
            foreach (Slot slot in slots)
            {
                // 同一区域的定位点会被逐个删；已删的跳过，避免重复 Erase 抛 eWasErased
                if (!hit.Contains(slot.ZoneId) || slot.Id.IsNull || slot.Id.IsErased) continue;
                tr.GetObject(slot.Id, OpenMode.ForWrite).Erase();
            }
            return maxZone + 1;
        }

        /// <summary>清除签名。<paramref name="zone"/> 为 null 时清掉本空间全部签名；
        /// 否则只清落在那处签字区域内的签名，以及不属于任何区域的孤立签名（早期版本写入、无位置归属的），
        /// 其他签字区域的签名原样保留。</summary>
        private static void ClearSignatures(Database db, Transaction tr, Zone zone, List<Zone> zones)
        {
            BlockTableRecord space = (BlockTableRecord)tr.GetObject(SignatureSpace(db), OpenMode.ForRead);
            List<ObjectId> ids = new List<ObjectId>();
            foreach (ObjectId id in space)
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || entity.Layer != SignatureLayer) continue;
                if (zone == null) { ids.Add(id); continue; }
                int? owner = ZoneAt(entity, zones);
                if ((owner == null && offlineZones == null) || (owner.HasValue && owner.Value == zone.Id)) ids.Add(id);
            }
            foreach (ObjectId id in ids)
            {
                if (id.IsNull || id.IsErased) continue;
                tr.GetObject(id, OpenMode.ForWrite).Erase();
            }
        }

        /// <summary>签名实体落在哪一处签字区域内；取不到位置时返回 null（视为无归属）。</summary>
        private static int? ZoneAt(Entity entity, List<Zone> zones)
        {
            using (var data = entity.GetXDataForApplication(AppName))
            {
                if (data != null)
                {
                    var values = data.AsArray();
                    if (values.Length >= 3 && Convert.ToString(values[1].Value) == "COUNTER_NAME")
                        return Convert.ToInt32(values[2].Value);
                }
            }
            if (zones == null) return null;
            Point3d center;
            if (!TryGetCenter(entity, out center)) return null;
            foreach (Zone zone in zones)
            {
                if (zone.Contains(center)) return zone.Id;
            }
            return null;
        }

        private static bool TryGetCenter(Entity entity, out Point3d center)
        {
            try
            {
                Extents3d extents = entity.GeometricExtents;
                center = new Point3d((extents.MinPoint.X + extents.MaxPoint.X) / 2, (extents.MinPoint.Y + extents.MaxPoint.Y) / 2, 0);
                return true;
            }
            catch (System.Exception)
            {
                // 少数实体取不到外包框，下面退回插入点
            }
            DBText text = entity as DBText;
            if (text != null) { center = text.Position; return true; }
            BlockReference reference = entity as BlockReference;
            if (reference != null) { center = reference.Position; return true; }
            center = Point3d.Origin;
            return false;
        }

        private static void AddName(BlockTableRecord space, Transaction tr, Slot slot, string value)
        {
            Point3d center = slot.Center;
            string text = FormatName(value);
            DBText name = new DBText(); name.SetDatabaseDefaults(); name.Layer = SignatureLayer;
            name.TextStyleId = EnsureNameTextStyle(HostApplicationServices.WorkingDatabase, tr);
            name.TextString = text; name.Height = NameHeight(slot, text); name.WidthFactor = NameWidthFactor; name.Justify = AttachmentPoint.MiddleCenter; name.Position = center; name.AlignmentPoint = center; name.ColorIndex = 4;
            name.Rotation = slot.Rotation;   // 5.24.0：图框转过角度时姓名跟着转（MiddleCenter 对齐，绕中心转）
            space.AppendEntity(name); tr.AddNewlyCreatedDBObject(name, true); name.AdjustAlignment(HostApplicationServices.WorkingDatabase);
        }

        /// <summary>姓名文字高度：随槽位（行高）缩放，图框整体缩放时字号同步变化。
        /// 再按姓名列宽兜底限制一次，保证多字姓名不会横向溢出姓名列。</summary>
        private static double NameHeight(Slot slot, string text)
        {
            double height = slot.Height * NameHeightRatio;
            int glyphs = string.IsNullOrEmpty(text) ? 1 : text.Length;
            double maxByWidth = slot.Width * NameWidthFill / (NameWidthFactor * glyphs);
            return height < maxByWidth ? height : maxByWidth;
        }

        private static string FormatName(string value)
        {
            return value != null && value.Length == 2 ? value.Insert(1, "  ") : value;
        }

        /// <summary>插入签名块。笔迹文件取人员参数里的指定文件（5.9.0 起），
        /// 没指定就用 Signatures 文件夹里的「姓名.dwg」；找不到只提示，不中断其他行。</summary>
        private static void InsertSignature(Database db, BlockTableRecord space, Transaction tr, Slot slot, PersonEntry person, Editor ed)
        {
            string fileName = string.IsNullOrEmpty(person.SignatureFile) ? person.Name + ".dwg" : person.SignatureFile;
            string file = Path.Combine(SignatureDirectory(), fileName);
            // 5.23.0：加密素材库优先（重新制作过的签名在库里，比明文旧文件新）；
            // 库里没有再回落明文 .dwg（手工放进文件夹的照旧能用）。两者都没有 = 缺素材。
            string tempDwg = SignatureVault.ExtractToTempDwg(SignatureDirectory(), fileName);
            if (tempDwg == null && !File.Exists(file)) { if (strictPlotSigning) throw new InvalidOperationException("缺少签名素材：" + fileName); ed.WriteMessage("\n未找到签名文件：" + fileName + "（明文文件夹与加密素材库都没有）"); return; }
            string sourcePath = tempDwg != null ? tempDwg : file;
            try
            {
            // 块名按笔迹文件命名：同一个人在不同岗位用不同笔迹时，各自有独立的块定义，不会串
            string blockName = "CAD_SIG_" + Path.GetFileNameWithoutExtension(fileName);
            BlockTable table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            bool needReload = !table.Has(blockName);
            if (!needReload && DefinitionHasForeignLayer(tr.GetObject(table[blockName], OpenMode.ForRead) as BlockTableRecord, tr))
            {
                // 旧版块：内部图元带着自己的图层（不是 0 图层），隐藏签名时这些图元不跟着走。
                // 用户要求「原有的块在再次签名前清除或者改名为其他」：没人引用就删掉，
                // 还有人引用就改个名（老签名照旧显示、照旧能隐藏），随后重新从 .dwg 导入新的 0 图层块。
                DropLegacyBlock(db, tr, blockName, ed);
                needReload = true;
            }
            ObjectId blockId;
            if (!needReload) blockId = table[blockName];
            else
            {
                try
                {
                    using (Database source = new Database(false, true))
                    {
                        // FileShare重载在AutoCAD2016会抛eNotImplementedYet；统一使用旧版已支持的FileOpenMode重载。
                        source.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, true, "");
                        source.CloseInput(true);
                        db.Insert(blockName, source, false);
                    }
                }
                finally
                {
                    if (tempDwg != null) { try { File.Delete(tempDwg); } catch (System.Exception) { } }   // 解密出的临时 dwg 用完即删，不留明文
                }
                table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!table.Has(blockName)) { if (strictPlotSigning) throw new InvalidOperationException("签名文件导入失败：" + fileName); ed.WriteMessage("\n签名文件导入失败：" + fileName); return; }
                blockId = table[blockName];
            }
            Extents3d bounds = GetBlockExtents(blockId, tr);
            double sourceWidth = bounds.MaxPoint.X - bounds.MinPoint.X, sourceHeight = bounds.MaxPoint.Y - bounds.MinPoint.Y;
            if (sourceWidth <= 0 || sourceHeight <= 0) { if (strictPlotSigning) throw new InvalidOperationException("签名文件没有可用图元：" + fileName); ed.WriteMessage("\n签名文件没有可用图元：" + fileName); return; }
            double scale = Math.Min(slot.Width * .88 / sourceWidth, slot.Height * .78 / sourceHeight);
            Point3d center = new Point3d((bounds.MinPoint.X + bounds.MaxPoint.X) / 2, (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2, 0);
            // 5.24.0：签名随图框旋转——块参照转 theta 后，内容中心从块基点转到
            // pos + R(theta)·(center*scale)，要让它落在槽位中心，插入点就得往回转：
            // pos = 槽位中心 - R(theta)·(center*scale)。横放的图框 theta=0，退化为旧算法。
            double theta = slot.Rotation;
            double cos = Math.Cos(theta), sin = Math.Sin(theta);
            double offsetX = -center.X * scale, offsetY = -center.Y * scale;
            Point3d placement = new Point3d(
                slot.Center.X + offsetX * cos - offsetY * sin,
                slot.Center.Y + offsetX * sin + offsetY * cos,
                slot.Center.Z);
            BlockReference reference = new BlockReference(placement, blockId);
            reference.SetDatabaseDefaults(); reference.Layer = SignatureLayer; reference.ScaleFactors = new Scale3d(scale); reference.Rotation = theta; space.AppendEntity(reference); tr.AddNewlyCreatedDBObject(reference, true);
            }
            finally { if (tempDwg != null && File.Exists(tempDwg)) { try { File.Delete(tempDwg); } catch (System.Exception) { } } }
        }

        /// <summary>块定义内部（含嵌套）是否有任何图元不在 0 图层。
        /// 5.15.0 起用来判定「这是不是旧版块」：0 图层图元会跟随块参照所在图层的可见性，
        /// 所以隐藏签名图层时能一起隐藏；带自身图层的旧块则隐藏不掉。</summary>
        private static bool DefinitionHasForeignLayer(BlockTableRecord definition, Transaction tr)
        {
            if (definition == null) return false;
            HashSet<ObjectId> seen = new HashSet<ObjectId>();
            return DefinitionHasForeignLayerCore(definition, tr, seen);
        }

        private static bool DefinitionHasForeignLayerCore(BlockTableRecord definition, Transaction tr, HashSet<ObjectId> seen)
        {
            foreach (ObjectId id in definition)
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null) continue;
                if (!string.Equals(entity.Layer, "0", StringComparison.OrdinalIgnoreCase)) return true;
                BlockReference reference = entity as BlockReference;
                if (reference == null) continue;
                if (!seen.Add(reference.BlockTableRecord)) continue;
                BlockTableRecord child = tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                if (child != null && DefinitionHasForeignLayerCore(child, tr, seen)) return true;
            }
            return false;
        }

        /// <summary>把「没有 0 图层化」的旧签名块让位：没有块参照引用就直接删掉，
        /// 还有引用就整体改名（新名 = 原名_old、再撞就 _old2…），这样老签名不受影响、
        /// 也不会挡住新块导入时的同名冲突。改名后同时清理被它独占的匿名块引用链不必要，
        /// 因为嵌套块定义本身并不被上层块「拥有」，块表里各自独立。</summary>
        private static void DropLegacyBlock(Database db, Transaction tr, string blockName, Editor ed)
        {
            BlockTable table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!table.Has(blockName)) return;
            ObjectId blockId = table[blockName];
            if (!IsBlockReferenced(db, tr, blockId))
            {
                tr.GetObject(blockId, OpenMode.ForWrite).Erase();
                ed.WriteMessage("\n已移除旧签名块定义（内部图元不在 0 图层）：" + blockName);
                return;
            }
            for (int suffix = 1; suffix < 1000; suffix++)
            {
                string candidate = suffix == 1 ? blockName + "_old" : blockName + "_old" + suffix;
                if (table.Has(candidate)) continue;
                BlockTableRecord definition = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForWrite);
                definition.Name = candidate;
                ed.WriteMessage("\n旧签名块（内部图元不在 0 图层）仍有引用，已改名为：" + candidate);
                return;
            }
            ed.WriteMessage("\n旧签名块「" + blockName + "」仍有引用且改名失败，本次签名可能仍显示旧样式。");
        }

        /// <summary>块定义是否被任何块参照指向（模型空间、布局、以及别的块定义内部都算）。</summary>
        private static bool IsBlockReferenced(Database db, Transaction tr, ObjectId blockId)
        {
            BlockTable table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                BlockTableRecord record = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                if (record == null) continue;
                foreach (ObjectId childId in record)
                {
                    BlockReference reference = tr.GetObject(childId, OpenMode.ForRead) as BlockReference;
                    if (reference != null && reference.BlockTableRecord == blockId) return true;
                }
            }
            return false;
        }

        private static Extents3d GetBlockExtents(ObjectId blockId, Transaction tr)
        {
            BlockTableRecord definition = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead); bool found = false; Extents3d result = new Extents3d();
            foreach (ObjectId id in definition) { Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity; if (entity == null) continue; try { if (found) result.AddExtents(entity.GeometricExtents); else { result = entity.GeometricExtents; found = true; } } catch (CadRuntime.Exception) { } }
            if (!found) throw new InvalidOperationException("签名块不含图元。"); return result;
        }

        /// <summary>核心 DLL 所在目录（装机包的 Contents\Windows），由外壳载入时注入。
        /// 单独抽出来是因为用 <c>Assembly.Load(byte[])</c> 载入的程序集没有磁盘路径
        /// （<c>Location</c> 是空串），只能靠注入进来的路径定位素材。
        ///
        /// 5.13.2 起**保证返回绝对路径**：此前若三道来源都拿不到（外壳注入失败、Location
        /// 为空、BaseDirectory 也为空），这里会返回空串，于是 <c>Path.Combine("", "人员名单.txt")</c>
        /// 变成相对路径 —— 「保存」会把名单写进 CAD 进程的当前工作目录（而不是装机包），
        /// 用户在装机包里永远看不到那个文件，重启 CAD 后工作目录一变，名单就像没保存一样。
        /// 现在统一用 <see cref="Path.GetFullPath(string)"/> 兜底成绝对路径。</summary>
        private static string BaseFolder()
        {
            string folder = null;
            if (!string.IsNullOrEmpty(CoreEntry.BaseDirectory)) folder = CoreEntry.BaseDirectory;
            if (string.IsNullOrEmpty(folder))
            {
                string location = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(location)) folder = Path.GetDirectoryName(location);
            }
            if (string.IsNullOrEmpty(folder)) folder = AppDomain.CurrentDomain.BaseDirectory;
            try { return Path.GetFullPath(folder); }
            catch (System.Exception) { return folder; }
        }

        /// <summary>签名素材目录。5.12.0 起面板「设置默认文件夹」设置过的目录优先
        /// （存在核心 DLL 旁的「签名文件夹.txt」里，一键签名与制作签名都用它）；
        /// 没设置时从核心 DLL 所在目录往上逐级找 Signatures（装机包里素材在
        /// Contents\Windows 的上两级）。</summary>
        private static string SignatureDirectory()
        {
            string custom = SignatureSettings.LoadCustomDirectory(BaseFolder());
            if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom)) return custom;
            string start = BaseFolder();
            DirectoryInfo folder = new DirectoryInfo(start);
            while (folder != null)
            {
                string candidate = Path.Combine(folder.FullName, "Signatures");
                if (Directory.Exists(candidate)) return candidate;
                folder = folder.Parent;
            }
            return Path.Combine(start, "Signatures");
        }

        private class Slot
        {
            public ObjectId Id;
            public string Role;
            public string Kind;
            public Point3d Center;
            public double Width;
            public double Height;
            /// <summary>签字栏的旋转角（弧度，5.24.0）：图框转过角度时姓名/签名跟着一起转；
            /// 单张签字建的点恒为 0（横平竖直）。旧定位点（5.24.0 之前建）没有这个值，按 0 读。</summary>
            public double Rotation;
            /// <summary>所属签字区域编号；同一图框的 14 个定位点编号相同。</summary>
            public int ZoneId;
            public bool IsCover;
        }

        /// <summary>一处签字区域（一个图框的签字栏）：一组定位点 + 由它们算出的矩形范围。</summary>
        private class Zone
        {
            public int Id;
            public readonly List<Slot> Slots = new List<Slot>();
            public double Left, Right, Bottom, Top;

            /// <summary>定位点是否齐全（正常为 7 个岗位 × 姓名/签名 = 14 个）。</summary>
            public bool IsCover { get { return Slots.Exists(s => s.IsCover); } }
            public bool IsComplete
            {
                get
                {
                    if (!IsCover) return Slots.Count == Roles.Length * 2;
                    if (Slots.Count != CoverRoles().Length) return false;
                    foreach (string role in CoverRoles())
                        if (!Slots.Exists(s => s.IsCover && s.Role == role && s.Kind == "SIGN")) return false;
                    return true;
                }
            }

            public bool Contains(Point3d point)
            {
                return point.X >= Left && point.X <= Right && point.Y >= Bottom && point.Y <= Top;
            }
        }
    }
}
