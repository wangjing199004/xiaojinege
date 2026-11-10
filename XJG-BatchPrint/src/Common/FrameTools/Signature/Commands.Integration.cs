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
namespace CadSignature
{
    // Reuse the original implementation without reopening its old modal menu.
    public partial class Commands
    {
        internal static string ExecuteDocked(PanelAction action, bool clearAll)
        {
            FrameOffsetConfig.EnsureTemplate(BaseFolder());
            string result;
            switch (action)
            {
                case PanelAction.ManualSign: result = RunManualSign(); break;
                case PanelAction.FrameSign: result = RunFrameSign(); break;
                case PanelAction.FrameCalib: result = RunFrameCalibEditor(); break;
                case PanelAction.FrameNumber: result = RunFrameNumber(); break;
                case PanelAction.EditPeople: result = RunEditPeople(); break;
                case PanelAction.ToggleHidden: result = RunToggleHidden(); break;
                case PanelAction.Clear: result = clearAll ? RunClearAll() : RunClear(); break;
                default: return "未执行操作";
            }
            result = result ?? "已取消，未执行新的操作。";
            if (action == PanelAction.EditPeople && result.StartsWith("人员配置已保存"))
                result = "人员配置已保存，下一次签字立即生效；操作结束后配置会重新加密。";
            Write(result);
            Editor().Regen();
            Editor().UpdateScreen();
            return result;
        }
        internal static string DockedStatus()
        {
            int zones, markers, broken;
            MarkerStats(out zones, out markers, out broken);
            return string.Format("{0}：{1} 处签字区域，{2} 个定位点；不完整区域 {3} 处。签字图层：{4}。", SpaceName(), zones, markers, broken, SignatureLayerVisible() ? "显示" : "隐藏");
        }
    }
}
