using System;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;

namespace ZwcadBatchPlot;
internal static class InstalledPluginUninstaller
{
    internal static ProcessStartInfo CreateStartInfo(string folder, int cadProcessId)
    {
        if (cadProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(cadProcessId));
        var root = Path.GetFullPath(folder);
        var marker = Path.Combine(root, "drawing-center.install.xml");
        var uninstaller = Path.Combine(root, "卸载出图中心.exe");
        if (!File.Exists(marker) || !File.Exists(uninstaller))
            throw new InvalidOperationException("未找到此插件的 EXE 安装记录或卸载程序，请从 Windows 的已安装应用中检查出图中心的安装位置。");
        if ((string?)XDocument.Load(marker).Root?.Attribute("product") != "DrawingCenter")
            throw new InvalidDataException("安装记录不匹配，未启动卸载。");
        return new ProcessStartInfo(uninstaller, "/from-cad " + cadProcessId) { UseShellExecute = true };
    }
    internal static void Start() => Process.Start(CreateStartInfo(PluginRuntimePaths.BaseDirectory, Process.GetCurrentProcess().Id));
}
