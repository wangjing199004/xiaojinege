using System;
using System.IO;
namespace ZwcadBatchPlot;
internal static class PluginRuntimePaths
{
    internal static string LoaderPath { get; set; } = "";
    internal static string BaseDirectory { get; set; } = DefaultDirectory();
    private static string DefaultDirectory()
    {
        var location = typeof(PluginRuntimePaths).Assembly.Location;
        return string.IsNullOrEmpty(location) ? AppDomain.CurrentDomain.BaseDirectory
            : Path.GetDirectoryName(location) ?? AppDomain.CurrentDomain.BaseDirectory;
    }
    internal static string EntryPath => string.IsNullOrEmpty(LoaderPath) ? typeof(PluginRuntimePaths).Assembly.Location : LoaderPath;
}
