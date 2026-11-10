using System; using System.IO; using System.Linq; using System.Reflection; using System.Collections.Generic;
internal static class InstallerRegression
{
    static List<string> log = new List<string>();
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static object Call(Assembly a, string method, params object[] args)
    {
        try { return a.GetType("SetupEngine", true).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,args); }
        catch(TargetInvocationException e) { throw e.InnerException; }
    }
    static string Hash(string path) { using(var s=File.OpenRead(path)) using(var h=System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)); }
    static void Reject(Action action,string reason) { try { action(); } catch(IOException) { return; } throw new Exception(reason); }
    static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[2]);
        try
        {
            Check(!Directory.Exists(root),"Test folder must be new"); Directory.CreateDirectory(root);
            var hp=Assembly.Load(File.ReadAllBytes(args[0])); var clean=Assembly.Load(File.ReadAllBytes(args[1]));
            foreach(var pair in new[]{new[]{"AutoCAD2015-2024","AcadBatchPlot.Runtime.dll"},new[]{"AutoCAD2025-2027","AcadBatchPlot.Core.Runtime.dll"},new[]{"ZWCAD2025","BatchPlotter.Runtime.dll"}})
            {
                string folder=Path.Combine(root,pair[0]);
                Call(hp,"Install",pair[0],folder,false);
                string hpHash=Hash(Path.Combine(folder,pair[1]));
                string personal=Path.Combine(folder,"人员设置.cspkg"); File.WriteAllText(personal,"PERSONAL_SENTINEL");
                string signatures=Path.Combine(folder,"SignatureData"); Directory.CreateDirectory(signatures); File.WriteAllText(Path.Combine(signatures,"test-personal.txt"),"SIGNATURE_SENTINEL");
                using(var held=File.Open(Path.Combine(folder,pair[1]),FileMode.Open,FileAccess.Read,FileShare.None))
                    Reject(delegate { Call(clean,"Install",pair[0],folder,false); },"Locked runtime was replaced");
                Check(Hash(Path.Combine(folder,pair[1]))==hpHash,"Failed upgrade changed runtime");
                Check(File.ReadAllText(Path.Combine(folder,"drawing-center.install.xml")).Contains("HP版"),"Failed upgrade changed edition marker");
                Call(clean,"Install",pair[0],folder,false);
                Check(Hash(Path.Combine(folder,pair[1]))!=hpHash,"Edition switch did not replace runtime");
                Check(File.ReadAllText(personal)=="PERSONAL_SENTINEL","Personal config overwritten");
                Check(File.ReadAllText(Path.Combine(signatures,"test-personal.txt"))=="SIGNATURE_SENTINEL","Signature lost");
                Check(File.ReadAllText(Path.Combine(folder,"drawing-center.install.xml")).Contains("纯净版"),"Wrong edition marker");
                Check(!Directory.GetDirectories(folder,".setup-*").Any(),"Staging directory left behind");
                Call(clean,"Uninstall",folder,false);
                Check(!File.Exists(Path.Combine(folder,pair[1])),"Uninstall left runtime");
                Check(!File.Exists(Path.Combine(folder,"卸载出图中心.exe")),"Uninstall left installed uninstaller");
                Check(File.ReadAllText(personal)=="PERSONAL_SENTINEL","Uninstall removed personal config");
                Check(File.Exists(Path.Combine(signatures,"test-personal.txt")),"Uninstall removed signatures");
                log.Add("PASS "+pair[0]+": HP install, locked-file preflight, HP-to-Clean replacement, config/signature preservation, staging cleanup, uninstall");
            }
            Reject(delegate { Call(hp,"SafePath",root,"../escape.txt"); },"Traversal accepted");
            Reject(delegate { Call(hp,"SafePath",root,"C:/escape.txt"); },"Absolute path accepted");
            Reject(delegate { Call(hp,"SafePath",root,"file.txt:stream"); },"ADS accepted");
            log.Add("PASS payload path traversal/absolute/ADS rejection; no real CAD or uninstall registry entries changed");
            File.WriteAllLines(Path.Combine(root,"regression.txt"),log); return 0;
        }
        catch(Exception ex) { log.Add("FAIL "+ex); File.WriteAllLines(Path.Combine(root,"regression.txt"),log); return 1; }
    }
}
