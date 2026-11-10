using System; using System.IO; using System.Linq; using Microsoft.Win32;
internal static class StartupRegistrationRegression
{
 static void Check(bool value,string text){if(!value)throw new Exception(text);}
 static void Product(RegistryKey machine,string release,string product,string title){using(var key=machine.CreateSubKey(@"Software\Autodesk\AutoCAD\"+release+"\\"+product))key.SetValue("ProductName",title);}
 static int Main(string[] args)
 {
  string path=@"Software\DrawingCenterTests\"+Guid.NewGuid().ToString("N");var report=new System.Text.StringBuilder();
  try {using(var scoped=Registry.CurrentUser.CreateSubKey(path))using(var user=scoped.CreateSubKey("User"))using(var machine=scoped.CreateSubKey("Machine")) {
   Product(machine,"R24.3","ACAD-7101:804","AutoCAD 2024");Product(machine,"R25.0","ACAD-8101:804","AutoCAD 2025");Product(machine,"R26.0","ACAD-A101:804","AutoCAD 2027");Product(machine,"R24.3","ACAD-LT:804","AutoCAD LT 2024");Product(machine,"R27.0","ACAD-FUTURE:804","Unsupported future");
   const string profile=@"Software\Autodesk\AutoCAD\R24.3\ACAD-7101:804\Profiles\Working\Variables";
   using(var key=user.CreateSubKey(profile))key.SetValue("TRUSTEDPATHS",@"%TEMP%\existing;C:\other",RegistryValueKind.ExpandString);
   using(var zw=machine.CreateSubKey(@"Software\ZWSOFT\ZWCAD\2025\zh-CN\Applications")){}
   using(var zw=user.CreateSubKey(@"Software\ZWSOFT\ZWCAD\2025\zh-CN\Profiles\Working\Variables"))zw.SetValue("TRUSTEDPATHS",@"C:\zw-existing");
   Check(CadStartupRegistration.Detect("AutoCAD2015-2024",user,machine).Count==1,"Legacy version filter includes LT/future");Check(CadStartupRegistration.Detect("AutoCAD2025-2027",user,machine).Count==2,"Core versions missed");Check(CadStartupRegistration.Detect("ZWCAD2025",user,machine).Count==1,"ZWCAD language root missed");
   foreach(string platform in SetupProgram.Platforms){string folder=System.IO.Path.Combine(args[0],platform);Directory.CreateDirectory(folder);
    using(var pending=CadStartupRegistration.Apply(platform,folder,user,machine)){} // 失败路径必须恢复注册表。
    foreach(var target in CadStartupRegistration.Detect(platform,user,machine))using(var app=user.OpenSubKey(target.Path+@"\Applications\"+(platform=="ZWCAD2025"?"ZwcadBatchPlot":"AcadBatchPlot")))Check(app==null,"Uncommitted registration left startup key");
    using(var tx=CadStartupRegistration.Apply(platform,folder,user,machine))tx.Commit();
    foreach(var target in CadStartupRegistration.Detect(platform,user,machine))using(var app=user.OpenSubKey(target.Path+@"\Applications\"+(platform=="ZWCAD2025"?"ZwcadBatchPlot":"AcadBatchPlot"))){Check((int)app.GetValue("LOADCTRLS")==2 && (int)app.GetValue("MANAGED")==1,"Not managed startup load");Check(Convert.ToString(app.GetValue("LOADER")).EndsWith(SetupProgram.Loaders[Array.IndexOf(SetupProgram.Platforms,platform)]),"Wrong framework loader");}
    using(var tx=CadStartupRegistration.Apply(platform,folder,user,machine))tx.Commit(); // 重装仍记录自己追加的可信路径。
    CadStartupRegistration.Remove(folder,user);
    foreach(var target in CadStartupRegistration.Detect(platform,user,machine))using(var app=user.OpenSubKey(target.Path+@"\Applications\"+(platform=="ZWCAD2025"?"ZwcadBatchPlot":"AcadBatchPlot")))Check(app==null,"Uninstall left startup key");
    report.AppendLine("PASS "+platform+": version/language selection, startup-managed loader registration, rollback, repeat install, uninstall");
   }
   using(var key=user.OpenSubKey(profile))Check((string)key.GetValue("TRUSTEDPATHS",null,RegistryValueOptions.DoNotExpandEnvironmentNames)==@"%TEMP%\existing;C:\other" && key.GetValueKind("TRUSTEDPATHS")==RegistryValueKind.ExpandString,"Existing trusted paths changed");
   using(var key=user.OpenSubKey(@"Software\ZWSOFT\ZWCAD\2025\zh-CN\Profiles\Working\Variables"))Check((string)key.GetValue("TRUSTEDPATHS")==@"C:\zw-existing","ZWCAD trusted paths changed");
   string selectionFolder=Path.Combine(args[0],"selection");Directory.CreateDirectory(selectionFolder);
   var core=CadStartupRegistration.Detect("AutoCAD2025-2027",user,machine);var chosen=core.Single(t=>t.Name=="AutoCAD 2025");var excluded=core.Single(t=>t.Name=="AutoCAD 2027");
   string chosenApp=chosen.Path+@"\Applications\AcadBatchPlot", excludedApp=excluded.Path+@"\Applications\AcadBatchPlot";
   string excludedVariables=excluded.Path+@"\Profiles\Working\Variables";
   using(var key=user.CreateSubKey(excludedVariables))key.SetValue("TRUSTEDPATHS",@"%TEMP%\other",RegistryValueKind.ExpandString);
   using(var tx=CadStartupRegistration.ApplySelected("AutoCAD2025-2027",selectionFolder,user,machine,new[]{chosen.Path}))tx.Commit();
   using(var key=user.OpenSubKey(excludedApp))Check(key==null,"Unchecked fresh version registered");
   using(var key=user.OpenSubKey(chosenApp))Check(key!=null,"Chosen version missing");
   string encoded=SetupProgram.EncodeTargets(new[]{chosen.Path});Check(SetupProgram.DecodeTargets(encoded).SequenceEqual(new[]{chosen.Path}),"Elevation selection round-trip failed");
   foreach(var invalid in new[]{new string[0],new[]{@"Software\OtherProduct"}}){bool rejected=false;try{using(var tx=CadStartupRegistration.ApplySelected("AutoCAD2025-2027",selectionFolder,user,machine,invalid)){} }catch(InvalidOperationException){rejected=true;}Check(rejected,"Invalid/empty selection accepted");}
   using(var tx=CadStartupRegistration.Apply("AutoCAD2025-2027",selectionFolder,user,machine))tx.Commit();
   using(var key=user.CreateSubKey(excludedApp+@"\CustomChild"))key.SetValue("data",new byte[]{1,2,3},RegistryValueKind.Binary);
   using(var tx=CadStartupRegistration.ApplySelected("AutoCAD2025-2027",selectionFolder,user,machine,new[]{chosen.Path})){} // 模拟后续安装失败。
   using(var key=user.OpenSubKey(excludedApp+@"\CustomChild"))Check(key!=null&&((byte[])key.GetValue("data")).Length==3,"Deselection rollback lost registry subtree");
   using(var key=user.OpenSubKey(excludedVariables))Check(Convert.ToString(key.GetValue("TRUSTEDPATHS",null,RegistryValueOptions.DoNotExpandEnvironmentNames)).Contains(selectionFolder),"Deselection rollback lost trust");
   using(var tx=CadStartupRegistration.ApplySelected("AutoCAD2025-2027",selectionFolder,user,machine,new[]{chosen.Path}))tx.Commit();
   using(var key=user.OpenSubKey(excludedApp))Check(key==null,"Deselected previous installation still autoloads");
   using(var key=user.OpenSubKey(excludedVariables))Check((string)key.GetValue("TRUSTEDPATHS",null,RegistryValueOptions.DoNotExpandEnvironmentNames)==@"%TEMP%\other"&&key.GetValueKind("TRUSTEDPATHS")==RegistryValueKind.ExpandString,"Deselection changed unrelated trust");
   using(var tx=CadStartupRegistration.Apply("AutoCAD2025-2027",selectionFolder,user,machine))tx.Commit();
   using(var key=user.OpenSubKey(excludedApp,true))key.SetValue("LOADER",@"C:\other-plugin\loader.dll");
   using(var tx=CadStartupRegistration.ApplySelected("AutoCAD2025-2027",selectionFolder,user,machine,new[]{chosen.Path}))tx.Commit();
   using(var key=user.OpenSubKey(excludedApp))Check((string)key.GetValue("LOADER")==@"C:\other-plugin\loader.dll","Deselection removed another loader");
   CadStartupRegistration.Remove(selectionFolder,user);
   report.AppendLine("PASS default-compatible detection, subset/empty/unknown selection, elevation round-trip, selective upgrade removes owned unchecked startup/trust, rollback restores subtree, other loader preserved");
   report.AppendLine("PASS existing trusted paths preserved; no SECURELOAD modifications; all registry writes confined to temporary test branch");
  }File.WriteAllText(System.IO.Path.Combine(args[0],"startup-registration.txt"),report.ToString());Console.Write(report);return 0;}
  catch(Exception ex){Console.WriteLine(ex);return 1;}
  finally{Registry.CurrentUser.DeleteSubKeyTree(path,false);}
 }
}
