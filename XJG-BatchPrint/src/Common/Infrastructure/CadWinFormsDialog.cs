using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
#if AUTOCAD
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif
namespace ZwcadBatchPlot;
internal static class CadWinFormsDialog
{
    internal static DialogResult Show(Form form)
    {
#if ACAD_CORE
        // Core SDK 不声明完整UI API；实际桌面CAD的AcMgd提供此入口。
        var application = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Autodesk.AutoCAD.ApplicationServices.Application", false))
            .FirstOrDefault(type => type != null && type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(method => method.Name == "ShowModalDialog" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(Form)));
        var show = application?.GetMethod("ShowModalDialog", new[] { typeof(Form) });
        if (show == null) throw new InvalidOperationException("当前CAD宿主未提供WinForms模态对话框接口，请在桌面CAD中运行。");
        return (DialogResult)show.Invoke(null, new object[] { form })!;
#else
        return CadApp.ShowModalDialog(form);
#endif
    }
}
