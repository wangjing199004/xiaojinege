using System;
using System.IO;
using System.Reflection;
namespace ZwcadBatchPlot;
/// <summary>固定加载器调用的入口；业务程序集仅以字节加载，不重复注册 CAD 命令。</summary>
public static class PluginRuntimeEntry
{
    private static BatchPlotCommands? _commands;
    public static void Initialize(string folder, string loaderPath)
    {
        PluginRuntimePaths.BaseDirectory = Path.GetFullPath(folder);
        PluginRuntimePaths.LoaderPath = loaderPath;
        LoadedAssemblyResolver.Register();
        _commands = new BatchPlotCommands();
        _commands.Initialize();
    }
    public static void Execute(string method)
    {
        if (_commands == null) throw new InvalidOperationException("插件尚未初始化。");
        var entry = typeof(BatchPlotCommands).GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
        if (entry == null || entry.GetParameters().Length != 0) throw new MissingMethodException(method);
        entry.Invoke(_commands, null);
    }
    public static void Shutdown()
    {
        _commands?.Terminate();
        _commands = null;
    }
}
