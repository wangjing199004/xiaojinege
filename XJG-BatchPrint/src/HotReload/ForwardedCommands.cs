#if AUTOCAD
using Autodesk.AutoCAD.Runtime;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.Runtime;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif
namespace BatchPlotLoader;
public sealed partial class Commands
{
    [CommandMethod("TKATTR", CommandFlags.Session)]
    public void Forward0() => Dispatch("ShowFrameTools");
    [CommandMethod("TKASIGN", CommandFlags.Session)]
    public void Forward1() => Dispatch("ShowFrameSignatures");
    [CommandMethod("TKATITLE")]
    public void Forward2() => Dispatch("OneClickTitle");
    [CommandMethod("TKAREAD")]
    public void Forward3() => Dispatch("ReadFrameTools");
    [CommandMethod("TKAAPPLY")]
    public void Forward4() => Dispatch("ApplyFrameTools");
    [CommandMethod("ZBP_LIBRARY_INPUT", CommandFlags.NoHistory)]
    public void Forward5() => Dispatch("RunLibraryInput");
    [CommandMethod("ZBP_SINGLE_FRAME_INPUT", CommandFlags.NoHistory)]
    public void Forward6() => Dispatch("RunSingleFrameInput");
    [CommandMethod("ZBP_SINGLE_PREVIEW", CommandFlags.NoHistory)]
    public void Forward7() => Dispatch("RunSinglePreview");
    [CommandMethod("ZBP_PANEL_INPUT", CommandFlags.NoHistory)]
    public void Forward8() => Dispatch("RunPanelInput");
    [CommandMethod("ZBP_PICK_LOG", CommandFlags.Session)]
    public void Forward9() => Dispatch("ShowPickLog");
    [CommandMethod("ZBP_ADD_TITLE_BLOCK")]
    public void Forward10() => Dispatch("AddTitleBlock");
    [CommandMethod("_ZBP_INTERNAL_ADD_TITLE_BLOCK")]
    public void Forward11() => Dispatch("AddTitleBlockLegacy");
    [CommandMethod("ZBP_SHOW_PANEL", CommandFlags.Session)]
    public void Forward12() => Dispatch("ShowBatchPlotWindow");
    [CommandMethod("_ZBP_INTERNAL_SHOW_PANEL", CommandFlags.Session)]
    public void Forward13() => Dispatch("ShowBatchPlotWindowLegacy");
    [CommandMethod("ZBP_SINGLE_PLOT", CommandFlags.Session)]
    public void Forward14() => Dispatch("SinglePlot");
    [CommandMethod("_ZBP_INTERNAL_SINGLE_PLOT", CommandFlags.Session)]
    public void Forward15() => Dispatch("SinglePlotLegacy");
    [CommandMethod("ZBP_RECTANGLE_BATCH_PLOT", CommandFlags.Session)]
    public void Forward16() => Dispatch("RectangleBatchPlot");
    [CommandMethod("_ZBP_INTERNAL_RECTANGLE_BATCH_PLOT", CommandFlags.Session)]
    public void Forward17() => Dispatch("RectangleBatchPlotLegacy");
    [CommandMethod("_ZBP_INTERNAL_PREVIEW", CommandFlags.NoHistory)]
    public void Forward18() => Dispatch("InternalPreview");
    [CommandMethod("ZBP_OPEN_CONFIG")]
    public void Forward19() => Dispatch("OpenConfigDirectory");
    [CommandMethod("_ZBP_INTERNAL_OPEN_CONFIG")]
    public void Forward20() => Dispatch("OpenConfigDirectoryLegacy");
    [CommandMethod("ZBP_MANAGE_LIBRARY", CommandFlags.Session)]
    public void Forward21() => Dispatch("ManageLibrary");
    [CommandMethod("_ZBP_INTERNAL_MANAGE_LIBRARY", CommandFlags.Session)]
    public void Forward22() => Dispatch("ManageLibraryLegacy");
    [CommandMethod("ZBP_SETTINGS", CommandFlags.Session)]
    public void Forward23() => Dispatch("ShowSettings");
    [CommandMethod("_ZBP_INTERNAL_SETTINGS", CommandFlags.Session)]
    public void Forward24() => Dispatch("ShowSettingsLegacy");
    [CommandMethod("ZBP_SHORTCUT_SETTINGS", CommandFlags.Session)]
    public void Forward25() => Dispatch("ShortcutSettings");
    [CommandMethod("_ZBP_INTERNAL_SHORTCUT_SETTINGS", CommandFlags.Session)]
    public void Forward26() => Dispatch("ShortcutSettingsLegacy");
    [CommandMethod("ZBP_ABOUT")]
    public void Forward27() => Dispatch("About");
    [CommandMethod("_ZBP_INTERNAL_ABOUT")]
    public void Forward28() => Dispatch("AboutLegacy");
    [CommandMethod("ZBP_INSTALL_AUTOLOAD")]
    public void Forward29() => Dispatch("InstallAutoload");
    [CommandMethod("_ZBP_INTERNAL_INSTALL_AUTOLOAD")]
    public void Forward30() => Dispatch("InstallAutoloadLegacy");
    [CommandMethod("ZBP_UNINSTALL_AUTOLOAD")]
    public void Forward31() => Dispatch("UninstallAutoload");
    [CommandMethod("_ZBP_INTERNAL_UNINSTALL_AUTOLOAD")]
    public void Forward32() => Dispatch("UninstallAutoloadLegacy");
    [CommandMethod("_ZBP_INTERNAL_VERIFY_RECTANGLE_BATCH")]
    public void Forward33() => Dispatch("VerifyRectangleBatch");
    [CommandMethod("_ZBP_INTERNAL_PROFILE_RECTANGLE_SCAN")]
    public void Forward34() => Dispatch("ProfileRectangleScan");
}
