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
using System;
using System.IO;
using System.Linq;
using System.Reflection;
[assembly: CommandClass(typeof(BatchPlotLoader.Commands))]
[assembly: ExtensionApplication(typeof(BatchPlotLoader.Commands))]
namespace BatchPlotLoader;
public sealed partial class Commands : IExtensionApplication
{
    private static Type? _entry;
    private static readonly string Folder = Path.GetDirectoryName(typeof(Commands).Assembly.Location)!;
#if ZWCAD
    private const string RuntimeFile = "BatchPlotter.Runtime.dll";
#elif ACAD_CORE
    private const string RuntimeFile = "AcadBatchPlot.Core.Runtime.dll";
#else
    private const string RuntimeFile = "AcadBatchPlot.Runtime.dll";
#endif
    public void Initialize()
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try { Ensure(); } catch (System.Exception ex) { Report(ex); }
    }
    public void Terminate()
    {
        try { if (_entry != null) Call(_entry, "Shutdown"); } catch { }
        AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
    }
    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(name)) return null;
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        if (loaded != null) return loaded;
        // 业务程序集无磁盘LoadFrom上下文，私有依赖统一从加载器同目录解析。
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var path = Path.Combine(Folder, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
    private static object? Call(Type entry, string method, params object[] args)
        => entry.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);
    private static void Ensure()
    {
        if (_entry != null) return;
        var assembly = Assembly.Load(File.ReadAllBytes(Path.Combine(Folder, RuntimeFile)));
        var entry = assembly.GetType("ZwcadBatchPlot.PluginRuntimeEntry", true)!;
        foreach (var method in new[] { "Initialize", "Execute", "Shutdown" })
            if (entry.GetMethod(method, BindingFlags.Public | BindingFlags.Static) == null) throw new MissingMethodException(method);
        try
        {
            Call(entry, "Initialize", Folder, typeof(Commands).Assembly.Location);
            _entry = entry;
        }
        catch
        {
            try { Call(entry, "Shutdown"); } catch { }
            throw;
        }
    }
    private static void Dispatch(string method)
    {
        try { Ensure(); Call(_entry!, "Execute", method); }
        catch (System.Exception ex) { Report(ex); }
    }
    private static void Report(System.Exception ex)
    {
        while ((ex is TargetInvocationException || ex is TypeInitializationException) && ex.InnerException != null) ex = ex.InnerException;
        CadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n批量打印插件：" + ex.Message);
    }
}
