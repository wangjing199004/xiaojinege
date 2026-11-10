using System; using System.IO; using System.Linq; using System.Text; using System.Reflection; using System.Resources; using System.Collections;
internal static class VariantResources
{
    static bool Contains(byte[] data,string text,bool unicode=false) { byte[] needle=(unicode?Encoding.Unicode:Encoding.UTF8).GetBytes(text); for(int i=0;i<=data.Length-needle.Length;i++){int j=0;for(;j<needle.Length&&data[i+j]==needle[j];j++);if(j==needle.Length)return true;}return false; }
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static int Main(string[] args)
    {
        foreach(string edition in new[]{"HP","Clean"})foreach(string file in Directory.GetFiles(Path.Combine(args[0],edition),"*.Runtime.dll",SearchOption.AllDirectories))
        {
            var raw=File.ReadAllBytes(file);
            Check(!Contains(raw,"请输入属性标记名称",true),"Frame numbering still prompts for attribute tag");
            Check(!Contains(raw,"OnHotReloadClick"),"Hot update event handler remains");
            var loader=File.ReadAllBytes(Directory.GetFiles(Path.GetDirectoryName(file),"*.Loader.dll").Single());
            Check(!Contains(loader,"ZBP_RELOAD",true)&&!Contains(loader,"TKARELOAD",true)&&!Contains(loader,"LoadCurrent")&&!Contains(loader,"Versions"),"Hot update commands or switching implementation remain");
            Check(Contains(raw,"配置管理",true)&&Contains(raw,"导出所选配置",true)&&Contains(raw,"导入所选配置",true),"Configuration management UI missing");
            var assembly=Assembly.Load(raw);
            string[] names=assembly.GetManifestResourceNames();
            Check(names.Count(n=>n.StartsWith("ZwcadBatchPlot.Private."))==(edition=="HP"?7:0),"Unexpected encrypted resource count: "+file);
            string resource=names.Single(n=>n.EndsWith(".g.resources"));int checkedPages=0;bool checkedEnvironment=false;
            using(var stream=assembly.GetManifestResourceStream(resource))using(var reader=new ResourceReader(stream))
            foreach(DictionaryEntry pair in reader)
            {
                string key=(string)pair.Key;
                if(key=="common/views/settingsform.baml") { using(var copy=new MemoryStream()){((Stream)pair.Value).CopyTo(copy);var data=copy.ToArray();Check(!Contains(data,"热更新插件")&&!Contains(data,"OnHotReloadClick"),"Hot update button remains");Check(!Contains(data,"导出环境配置")&&!Contains(data,"导入环境配置"),"Environment actions still present in global footer");checkedEnvironment=true;}continue; }
                if(!new[]{"common/views/batchplotform.baml","common/views/rectanglebatchplotform.baml","common/views/titleblocklibrarymanagerform.baml"}.Contains(key))continue;
                checkedPages++;using(var copy=new MemoryStream()){((Stream)pair.Value).CopyTo(copy);byte[] b=copy.ToArray();
                    string button=key.Contains("titleblocklibrary")?"插入标准图框":"生成HP目录";
                    Check(Contains(b,button)==(edition=="HP"),"Wrong compiled button visibility: "+file+"/"+key);
                    if(!key.Contains("titleblocklibrary"))Check(Contains(b,"生成普通目录"),"Normal directory missing");
                }
            }
            Check(checkedEnvironment,"Settings page absent");
            Check(checkedPages==3,"Compiled view resource paths changed: "+file);
            Console.WriteLine("PASS "+edition+" "+Path.GetFileName(Path.GetDirectoryName(file))+": embedded resource count and 3 compiled WPF pages");
        }
        return 0;
    }
}
