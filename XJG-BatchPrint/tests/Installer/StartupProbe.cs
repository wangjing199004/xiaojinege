using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;
public class StartupProbe
{
    [CommandMethod("DRAWING_CENTER_STARTUP_PROBE")]
    public void Run()
    {
        string output=Environment.GetEnvironmentVariable("DRAWING_CENTER_PROBE_OUTPUT");
        string folder=Environment.GetEnvironmentVariable("DRAWING_CENTER_PROBE_FOLDER");
        try
        {
            var assemblies=AppDomain.CurrentDomain.GetAssemblies();
            var loader=assemblies.Single(a=>a.GetName().Name.EndsWith(".Loader",StringComparison.Ordinal)&&string.Equals(Path.GetDirectoryName(a.Location),folder,StringComparison.OrdinalIgnoreCase));
            var runtime=assemblies.Single(a=>a.GetName().Name.Contains(".Runtime.")&&a.GetType("ZwcadBatchPlot.PluginRuntimePaths")!=null&&string.Equals((string)a.GetType("ZwcadBatchPlot.PluginRuntimePaths").GetProperty("BaseDirectory",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null,null),folder,StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(output,"PASS business loader and runtime already loaded at CAD startup, before probe DLL: "+loader.GetName().Name+" / "+runtime.GetName().Name);
        }
        catch(System.Exception ex){File.WriteAllText(output,"FAIL "+ex);}
        if(System.Diagnostics.Process.GetCurrentProcess().ProcessName.Equals("accoreconsole",StringComparison.OrdinalIgnoreCase))Environment.Exit(0);
    }
}
