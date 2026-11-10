using System; using System.IO; using System.Linq; using System.Collections.Generic; using CadSignature; using ZwcadBatchPlot; using Newtonsoft.Json; using TitleAttributeBatch.Core;
internal static class Program
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Reject(Action action){try{action();}catch(Exception ex) when(ex is IOException || ex is System.IO.InvalidDataException){return;}throw new Exception("Expected rejection");}
    static void Main()
    {
        string root=Path.Combine(Environment.CurrentDirectory,"local-artifacts","environment-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        PluginRuntimePaths.BaseDirectory=Path.Combine(root,"device-A"); Directory.CreateDirectory(PluginRuntimePaths.BaseDirectory);
        AppSettingsStore.Directory=Path.Combine(root,"profile-A"); Directory.CreateDirectory(AppSettingsStore.Directory);
        AppSettingsStore.Save(new AppSettings { Value="A-current",CommandAliases=new(){{"TKATITLE","TM"}} });
        File.WriteAllText(TitleBlockLibraryStore.DefaultPath,JsonConvert.SerializeObject(new TitleBlockLibrary { Blocks=new(){"A0+图框","A1+图框"} }));
        byte[] signature=System.Text.Encoding.UTF8.GetBytes("AC1027-test-signature");
        var external=Path.Combine(root,"external.dwg");File.WriteAllBytes(external,signature);
        SignatureRuntime.Run(()=>{var session=CoreEntry.BaseDirectory;var people=PeopleConfig.Load(session);var role=people.Keys.First();people[role].Name="测试人员";people[role].SignatureFile=external;PeopleConfig.Save(session,people);FrameOffsetConfig.Save(session,"A1",84100,new double[]{1,2,3,4});return "";});
        var bundle=Path.Combine(root,"portable.dcenv");EnvironmentTransfer.Export(bundle,AppSettingsStore.Load());
        var entries=EnvironmentPackage.Read(bundle);Check(entries.Count==5,"Wrong entry count");Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(bundle)).Contains("测试人员"),"Plaintext names leaked");
        File.Delete(external); // 原设备的笔迹绝对路径已经失效。
        PluginRuntimePaths.BaseDirectory=Path.Combine(root,"device-B");Directory.CreateDirectory(PluginRuntimePaths.BaseDirectory);AppSettingsStore.Directory=Path.Combine(root,"profile-B");Directory.CreateDirectory(AppSettingsStore.Directory);
        AppSettingsStore.Save(new AppSettings {Value="B-before"});File.WriteAllText(TitleBlockLibraryStore.DefaultPath,"{\"Blocks\":[]}");
        var imported=EnvironmentTransfer.Import(bundle,out var backup);Check(imported.Value=="A-current","Settings not imported");Check(File.Exists(backup),"Pre-import backup absent");
        Check(AppSettingsStore.Load().CommandAliases["TKATITLE"]=="TM","Aliases lost");Check(TitleBlockLibraryStore.Load().Blocks.Count==2,"Library lost");
        SignatureRuntime.Run(()=>{var session=CoreEntry.BaseDirectory;var person=PeopleConfig.Load(session).Values.Single(p=>p.Name=="测试人员");Check(!Path.IsPathRooted(person.SignatureFile),"Absolute source path retained");Check(SignatureVault.ReadSignatureBytes(Path.Combine(session,"Signatures"),person.SignatureFile).SequenceEqual(signature),"Signature cannot decrypt after migration");Check(FrameOffsetConfig.Load(session,"A1").SequenceEqual(new double[]{84100,1,2,3,4}),"Calibration changed");return "";});
        Check(!Directory.Exists(GreenStore.SessionPath(SignatureRuntime.DataDirectory)),"Plain session left after import");
        var tampered=File.ReadAllBytes(bundle);tampered[tampered.Length-40]^=1;var bad=Path.Combine(root,"bad.dcenv");File.WriteAllBytes(bad,tampered);Reject(()=>EnvironmentTransfer.Import(bad,out _));Check(AppSettingsStore.Load().Value=="A-current","Bad import changed settings");
        Reject(()=>EnvironmentPackage.Write(Path.Combine(root,"unsafe.dcenv"),new Dictionary<string,byte[]>{{"../escape",new byte[1]}}));
        var original=File.ReadAllBytes(AppSettingsStore.Path);using(var held=File.Open(AppSettingsStore.Path,FileMode.Open,FileAccess.Read,FileShare.None))Reject(()=>EnvironmentTransfer.Import(bundle,out _));Check(original.SequenceEqual(File.ReadAllBytes(AppSettingsStore.Path)),"Locked import changed configuration");
        var malformed=new Dictionary<string,byte[]>(entries);malformed[SettingsPackage.PackageFileName]=new byte[]{1,2,3};var malformedPath=Path.Combine(root,"malformed.dcenv");EnvironmentPackage.Write(malformedPath,malformed);Reject(()=>EnvironmentTransfer.Import(malformedPath,out _));Check(original.SequenceEqual(File.ReadAllBytes(AppSettingsStore.Path)),"Malformed inner package changed configuration");
        var first=Path.Combine(root,"rollback-first.txt");File.WriteAllText(first,"before");var blocker=Path.Combine(root,"not-a-directory");File.WriteAllText(blocker,"block");Reject(()=>EnvironmentPackage.ApplyFiles(new Dictionary<string,byte[]>{{first,new byte[]{42}},{Path.Combine(blocker,"second.txt"),new byte[]{43}}}));Check(File.ReadAllText(first)=="before","Mid-import failure did not roll back previous files");
        var before=EnvironmentPackage.Read(backup);Check(System.Text.Encoding.UTF8.GetString(before["Settings.json"]).Contains("B-before"),"Backup did not capture previous target environment");
        AppSettingsStore.Save(new AppSettings {Value="keep-settings"});
        SignatureRuntime.Run(()=>{FrameOffsetConfig.Save(CoreEntry.BaseDirectory,"A1",123,new double[]{9,8,7,6});return "";});
        EnvironmentTransfer.Import(bundle,out _,EnvironmentComponents.Library);
        Check(AppSettingsStore.Load().Value=="keep-settings","Library-only import overwrote settings");
        SignatureRuntime.Run(()=>{Check(FrameOffsetConfig.Load(CoreEntry.BaseDirectory,"A1")[0]==123,"Library-only import overwrote calibration");return "";});
        EnvironmentTransfer.Import(bundle,out _,EnvironmentComponents.Signatures);
        SignatureRuntime.Run(()=>{Check(FrameOffsetConfig.Load(CoreEntry.BaseDirectory,"A1")[0]==123,"Signatures-only import overwrote calibration");var people=PeopleConfig.Load(CoreEntry.BaseDirectory);people.Values.First().Name="目标保留姓名";PeopleConfig.Save(CoreEntry.BaseDirectory,people);return "";});
        EnvironmentTransfer.Import(bundle,out _,EnvironmentComponents.Calibration);
        SignatureRuntime.Run(()=>{Check(FrameOffsetConfig.Load(CoreEntry.BaseDirectory,"A1")[0]==84100,"Calibration-only import missed calibration");Check(PeopleConfig.Load(CoreEntry.BaseDirectory).Values.Any(p=>p.Name=="目标保留姓名"),"Calibration-only import overwrote people");return "";});
        var calibrationOnly=Path.Combine(root,"calibration-only.dcenv");EnvironmentTransfer.Export(calibrationOnly,AppSettingsStore.Load(),EnvironmentComponents.Calibration);
        var calibrationEntries=EnvironmentPackage.Read(calibrationOnly);Check(calibrationEntries.Count==2&&!calibrationEntries.ContainsKey("Settings.json")&&!calibrationEntries.ContainsKey(SettingsPackage.PackageFileName),"Selective export included unrelated types");
        var extracted=Path.Combine(root,"calibration-content");GreenArchive.Unpack(calibrationEntries["SignatureSession.bin"],extracted);Check(!File.Exists(Path.Combine(extracted,PeopleConfig.FileName))&&!Directory.Exists(Path.Combine(extracted,"Signatures")),"Calibration export included signature assets");
        var settingsOnly=Path.Combine(root,"settings-only.dcenv");EnvironmentTransfer.Export(settingsOnly,AppSettingsStore.Load(),EnvironmentComponents.Settings);Check(EnvironmentPackage.Read(settingsOnly).Count==2,"Settings-only export included unrelated data");
        var legacy=new Dictionary<string,byte[]>(entries);legacy.Remove("Components.txt");var legacyFile=Path.Combine(root,"v30.dcenv");EnvironmentPackage.Write(legacyFile,legacy);EnvironmentTransfer.Import(legacyFile,out _);
        var legacyPeople=Path.Combine(root,"old.cspkg");File.WriteAllBytes(legacyPeople,entries[SettingsPackage.PackageFileName]);EnvironmentTransfer.Import(legacyPeople,out _,EnvironmentComponents.Signatures);
        var legacyLibrary=Path.Combine(root,"old.json");File.WriteAllBytes(legacyLibrary,entries["TitleBlockLibrary.json"]);EnvironmentTransfer.Import(legacyLibrary,out _,EnvironmentComponents.Library);
        Console.WriteLine("PASS selective export/import: settings/library/calibration/signatures isolated; unselected values and assets preserved; v30 full package, JSON and CSPKG import supported");
        SignatureRuntime.Run(()=>{
            string directory=Path.Combine(CoreEntry.BaseDirectory,"Signatures");Directory.CreateDirectory(directory);
            SignatureVault.Save(directory,"delete-test.dwg",signature);SignatureVault.Save(directory,"keep-test.dwg",signature);
            File.WriteAllBytes(Path.Combine(directory,"delete-test.dwg"),signature);
            var people=PeopleConfig.Defaults();people["设计"].SignatureFile="delete-test.dwg";
            string oldPackage=Path.Combine(root,"old-deletion-package");Directory.CreateDirectory(oldPackage);
            SettingsPackage.Export(Path.Combine(oldPackage,SettingsPackage.PackageFileName),people,directory);
            SignatureVault.Delete(directory,"delete-test.dwg");
            Check(!SignatureVault.Contains(directory,"delete-test.dwg")&&!SignatureVault.Names(directory).Contains("delete-test.dwg")&&!File.Exists(Path.Combine(directory,"delete-test.dwg")),"Deletion left cipher/plain/list entry");
            Check(SignatureVault.Contains(directory,"keep-test.dwg"),"Deletion removed unrelated signature");
            SettingsPackage.ApplyIfNeeded(oldPackage,CoreEntry.BaseDirectory,directory);
            Check(!SignatureVault.Contains(directory,"delete-test.dwg"),"Old personnel package resurrected deletion");
            SignatureVault.Save(directory,"delete-test.dwg",signature);Check(SignatureVault.Contains(directory,"delete-test.dwg"),"Explicit re-add cannot revive deleted name");
            string locked=Path.Combine(directory,"delete-test.dwg");File.WriteAllBytes(locked,signature);
            using(var handle=File.Open(locked,FileMode.Open,FileAccess.Read,FileShare.None))Reject(()=>SignatureVault.Delete(directory,"delete-test.dwg"));
            Check(SignatureVault.Contains(directory,"delete-test.dwg")&&File.Exists(locked),"Locked deletion partially changed data");
            return "";
        });
        Console.WriteLine("PASS signature delete: encrypted/plain duplicates removed, unrelated assets preserved, old package cannot resurrect, explicit re-add works, locked deletion rejected without partial changes");
        AppSettingsStore.Save(new AppSettings{Value="before-factory",CommandAliases=new(){{"TKATITLE","CUSTOM"}}});
        var factory=EnvironmentTransfer.ResetToFactory(out var factoryBackup);
        Check(factory.Value==""&&factory.CommandAliases.Count==0,"Factory settings/aliases not restored");
        Check(TitleBlockLibraryStore.Load().Blocks.Count==0,"Factory library not empty");
        SignatureRuntime.Run(()=>{Check(PeopleConfig.Load(CoreEntry.BaseDirectory)["项目负责人"].Name=="张三","Factory personnel not restored");Check(FrameOffsetConfig.Load(CoreEntry.BaseDirectory,"A1")==null,"Factory calibration retained");Check(!SignatureVault.Names(Path.Combine(CoreEntry.BaseDirectory,"Signatures")).Any(),"Factory retained signatures");return "";});
        Check(File.Exists(factoryBackup),"Factory recovery backup missing");
        EnvironmentTransfer.Import(factoryBackup,out _);
        Check(AppSettingsStore.Load().Value=="before-factory","Factory reset cannot be undone from backup");
        Console.WriteLine("PASS full factory reset: default settings/aliases/personnel, empty library/calibration/signatures, recoverable pre-reset backup");
        Console.WriteLine("PASS: full export/import between isolated devices; settings/library/aliases/calibration/signature bytes; absolute signature path portability; encrypted package; original backup; tamper/path/locked-file rejection; no live user files modified");
    }
}
namespace ZwcadBatchPlot {
 public class AppSettings {public string Value{get;set;}="";public Dictionary<string,string> CommandAliases{get;set;}=new();}
 public class TitleBlockLibrary {public List<string> Blocks{get;set;}=new();}
 public static class AppSettingsStore {public static string Directory=""; public static string Path=>System.IO.Path.Combine(Directory,"Settings.json");public static AppSettings Default()=>new();public static AppSettings Normalize(AppSettings x)=>x;public static AppSettings Load()=>File.Exists(Path)?JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(Path))!:new();public static void Save(AppSettings x)=>File.WriteAllText(Path,JsonConvert.SerializeObject(x));}
 public static class TitleBlockLibraryStore {public static string DefaultDirectory=>AppSettingsStore.Directory;public static string DefaultPath=>System.IO.Path.Combine(DefaultDirectory,"TitleBlockLibrary.json");public static TitleBlockLibrary Load(string? path=null)=>File.Exists(path??DefaultPath)?JsonConvert.DeserializeObject<TitleBlockLibrary>(File.ReadAllText(path??DefaultPath))!:new();}
 internal static class PluginRuntimePaths {internal static string BaseDirectory="";}
}
namespace CadSignature {internal static class CoreEntry {public static string BaseDirectory="";public static string PackageDirectory=>PluginRuntimePaths.BaseDirectory;}}
namespace TitleAttributeBatch.Core {internal static class SignatureRuntime {internal static string DataDirectory=>Path.Combine(PluginRuntimePaths.BaseDirectory,"SignatureData");internal static string Run(Func<string> action){Directory.CreateDirectory(DataDirectory);CoreEntry.BaseDirectory=GreenStore.Unlock(DataDirectory);try{return action();}finally{GreenStore.Seal(DataDirectory);}}}}
