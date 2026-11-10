using System;using System.IO;using System.Linq;using System.Text;using System.Diagnostics;using Microsoft.Win32;
internal static class ActualStartupRegression
{
 static int Main(string[] args)
 {
  if(Process.GetProcessesByName("acad").Length>0||Process.GetProcessesByName("zwcad").Length>0){Console.WriteLine("SKIP live CAD is running; actual startup registration was not changed");return 2;}
  string workspace=args[0];string fixture=Path.Combine(workspace,"local-artifacts","installer-v31");
  foreach(var target in new[]{new[]{"AutoCAD2015-2024",@"D:\Program Files\Autodesk\AutoCAD 2024\accoreconsole.exe","StartupProbe.dll"},new[]{"AutoCAD2025-2027",@"D:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe","StartupProbe.Core.dll"}})
  {
   string folder=Path.Combine(fixture,"HP",target[0]);string report=Path.Combine(fixture,"actual-startup-"+target[0]+".txt");
   string script=Path.Combine(fixture,"startup-"+target[0]+".scr");
   File.WriteAllLines(script,new[]{"FILEDIA","0","NETLOAD",Path.Combine(fixture,target[2]),"DRAWING_CENTER_STARTUP_PROBE","_QUIT","_N"},Encoding.GetEncoding(936));
   using(var rollback=CadStartupRegistration.Apply(target[0],folder,Registry.CurrentUser,Registry.LocalMachine))
   {
    var start=new ProcessStartInfo(target[1],"/s \""+script+"\" /l zh-CN"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
    start.EnvironmentVariables["DRAWING_CENTER_PROBE_OUTPUT"]=report;start.EnvironmentVariables["DRAWING_CENTER_PROBE_FOLDER"]=folder;
    using(var process=new Process {StartInfo=start}){
     var log=new StringBuilder();process.OutputDataReceived+=(s,e)=>{if(e.Data!=null)lock(log)log.AppendLine(e.Data);};process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(log)log.AppendLine(e.Data);};
     process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
     if(!process.WaitForExit(45000)){process.Kill();process.WaitForExit();throw new System.Exception("Core console timed out: "+target[0]);}process.WaitForExit();
     File.WriteAllText(Path.Combine(fixture,"startup-console-"+target[0]+".txt"),log.ToString());
    }
    // 不提交；离开using即恢复此前真实CAD注册表和可信路径。
   }
   if(!File.Exists(report)||!File.ReadAllText(report).StartsWith("PASS"))throw new System.Exception(File.Exists(report)?File.ReadAllText(report):"Probe did not execute: "+target[0]);
   Console.WriteLine(File.ReadAllText(report));
  }
  Console.WriteLine("PASS actual startup registrations and trusted paths restored");return 0;
 }
}
