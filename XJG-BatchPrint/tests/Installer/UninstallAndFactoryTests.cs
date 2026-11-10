using System; using System.IO; using System.Linq; using System.Reflection; using System.Collections; using System.Diagnostics;
internal static class UninstallAndFactoryTests
{
 static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
 static object Call(Type type,string name,params object[] args){try{return type.GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);}catch(TargetInvocationException ex){throw ex.InnerException;}}
 static void Reject(Action action){try{action();}catch(InvalidOperationException){return;}catch(InvalidDataException){return;}throw new Exception("Invalid installation was accepted");}
 static void Main(string[] args)
 {
  foreach(var edition in new[]{"HP","纯净"})
  {
   string payload=Path.Combine(args[0],(args.Length > 1 ? args[1] : "verify-final-")+edition);
   foreach(var platform in Directory.GetDirectories(payload))
   {
    foreach(var file in Directory.GetFiles(platform,"*",SearchOption.AllDirectories)){
     var relative=Path.GetRelativePath(platform,file).Replace('\\','/');
     Check(!new[]{".cspkg",".cache",".key",".csig"}.Contains(Path.GetExtension(file)),"Private personnel data in actual EXE payload: "+relative);
     if(relative.StartsWith("SignatureData/",StringComparison.OrdinalIgnoreCase))Check(relative=="SignatureData/Seed/图框定位.txt","Unexpected signature seed: "+relative);
    }
    var runtime=Directory.GetFiles(platform,"*.Runtime.dll").Single();var a=Assembly.Load(File.ReadAllBytes(runtime));
    var factory=(IDictionary)Call(a.GetType("CadSignature.PeopleConfig",true),"Defaults");
    Check(factory.Count==7,"Default roles changed");
    Check((string)factory["项目负责人"].GetType().GetField("Name").GetValue(factory["项目负责人"])=="张三","First sample name wrong");
    Check((string)factory["专业负责人"].GetType().GetField("Name").GetValue(factory["专业负责人"])=="李四","Second sample name wrong");
    Check((string)factory["审定"].GetType().GetField("Name").GetValue(factory["审定"])=="王五","Third sample name wrong");
    var loaded=(IDictionary)Call(a.GetType("CadSignature.PeopleConfig",true),"Load",Path.Combine(platform,"SignatureData",".session"));
    Check((string)loaded["项目负责人"].GetType().GetField("Name").GetValue(loaded["项目负责人"])=="张三","Fresh installation did not fall back to factory names");
    var helper=a.GetType("ZwcadBatchPlot.InstalledPluginUninstaller",true);
    string fixture=Path.Combine(args[0],"uninstall-fixture-"+Guid.NewGuid().ToString("N")+edition+"-"+Path.GetFileName(platform));Directory.CreateDirectory(fixture);
    Reject(()=>Call(helper,"CreateStartInfo",fixture,4242));
    File.WriteAllText(Path.Combine(fixture,"drawing-center.install.xml"),"<installation product=\"OtherProduct\" />");File.WriteAllBytes(Path.Combine(fixture,"卸载出图中心.exe"),new byte[]{1});
    Reject(()=>Call(helper,"CreateStartInfo",fixture,4242));
    File.WriteAllText(Path.Combine(fixture,"drawing-center.install.xml"),"<installation product=\"DrawingCenter\" platform=\""+Path.GetFileName(platform)+"\" />");
    var start=(ProcessStartInfo)Call(helper,"CreateStartInfo",fixture,4242);
    Check(start.FileName==Path.Combine(fixture,"卸载出图中心.exe")&&start.Arguments=="/from-cad 4242"&&start.UseShellExecute,"CAD menu does not target installed EXE workflow");
    Console.WriteLine("PASS "+edition+" "+Path.GetFileName(platform)+": actual package has no personal profile/vault/cache, factory names active, CAD uninstall routing and marker rejection");
   }
  }
 }
}
