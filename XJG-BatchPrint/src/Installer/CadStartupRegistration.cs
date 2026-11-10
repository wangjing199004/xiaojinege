using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Win32;

internal sealed class CadStartupRegistration : IDisposable
{
    internal sealed class Target { internal string Path; internal string Name; }
    private readonly List<Action> undo = new List<Action>();
    private readonly RegistryKey user;
    private bool committed;
    private CadStartupRegistration(RegistryKey userRoot) { user = userRoot; }
    internal static List<Target> Detect(string platform, RegistryKey user, RegistryKey machine)
    {
        var targets = new List<Target>();
        if (platform.StartsWith("AutoCAD", StringComparison.Ordinal))
        {
            const string prefix = @"Software\Autodesk\AutoCAD";
            foreach (string release in Names(user, prefix).Union(Names(machine, prefix), StringComparer.OrdinalIgnoreCase))
            {
                double number; if (!release.StartsWith("R") || !double.TryParse(release.Substring(1), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number)) continue;
                bool compatible = platform == "AutoCAD2015-2024" ? number >= 20 && number <= 24.3 : number == 25.0 || number == 25.1 || number == 26.0;
                if (!compatible) continue;
                string versionRoot = prefix + "\\" + release;
                var products = Names(user, versionRoot).Union(Names(machine, versionRoot), StringComparer.OrdinalIgnoreCase).Where(n => n.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (string product in products)
                {
                    if (!product.Contains(":") && products.Any(n => n.StartsWith(product + ":", StringComparison.OrdinalIgnoreCase))) continue;
                    string path = versionRoot + "\\" + product;
                    using (var info = machine.OpenSubKey(path))
                    {
                        if (info == null) continue; // 忽略已经卸载、仅留下用户配置的旧版本。
                        string title = Convert.ToString(info.GetValue("ProductName"));
                        if (title.IndexOf("AutoCAD LT", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        string location = Convert.ToString(info.GetValue("AcadLocation", info.GetValue("Location", "")));
                        if (location.Length > 0 && !File.Exists(System.IO.Path.Combine(location, "acad.exe"))) continue;
                        targets.Add(new Target { Path = path, Name = title.Length == 0 ? "AutoCAD " + release + " / " + product : title });
                    }
                }
            }
        }
        else
        {
            const string prefix = @"Software\ZWSOFT\ZWCAD";
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            FindZw(user, prefix, 0, candidates); FindZw(machine, prefix, 0, candidates);
            foreach (string candidate in candidates)
                if (!candidates.Any(other => other.StartsWith(candidate + "\\", StringComparison.OrdinalIgnoreCase))) targets.Add(new Target { Path = candidate, Name = "中望 CAD 2025 / " + candidate.Substring(prefix.Length).Trim('\\') });
        }
        return targets.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
    private static IEnumerable<string> Names(RegistryKey root, string path) { using (var key = root.OpenSubKey(path)) return key == null ? new string[0] : key.GetSubKeyNames(); }
    private static void FindZw(RegistryKey hive, string path, int depth, HashSet<string> result)
    {
        if (depth > 4) return;
        var names = Names(hive, path).ToArray();
        if (path.Split('\\').Any(segment => segment.IndexOf("2025", StringComparison.OrdinalIgnoreCase) >= 0) && (names.Contains("Applications", StringComparer.OrdinalIgnoreCase) || names.Contains("Profiles", StringComparer.OrdinalIgnoreCase))) result.Add(path);
        foreach (string name in names.Where(n => !new[] { "Applications", "Profiles", "Recent File List" }.Contains(n, StringComparer.OrdinalIgnoreCase))) FindZw(hive, path + "\\" + name, depth + 1, result);
    }
    internal static CadStartupRegistration Apply(string platform, string folder, RegistryKey user, RegistryKey machine)
    { return ApplySelected(platform, folder, user, machine, null); }
    internal static CadStartupRegistration ApplySelected(string platform, string folder, RegistryKey user, RegistryKey machine, string[] selectedPaths)
    {
        var targets = Detect(platform, user, machine);
        if (selectedPaths != null)
        {
            if (selectedPaths.Length == 0 || selectedPaths.Any(path => !targets.Any(target => string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase)))) throw new InvalidOperationException("CAD选择已失效，请重新打开安装程序并勾选版本。");
            targets = targets.Where(target => selectedPaths.Contains(target.Path, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        if (targets.Count == 0) throw new InvalidOperationException("未检测到所选平台的 CAD。请安装对应 CAD 后重新运行安装程序。");
        var transaction = new CadStartupRegistration(user);
        try
        {
            string loader = System.IO.Path.Combine(folder, SetupProgram.Loaders[Array.IndexOf(SetupProgram.Platforms, platform)]);
            var document = new XDocument(new XElement("autoload", new XAttribute("folder", folder)));
            string recordPath = System.IO.Path.Combine(folder, "drawing-center.autoload.xml");
            XDocument previous = File.Exists(recordPath) ? XDocument.Load(recordPath) : null;
            if (File.Exists(recordPath)) using (File.Open(recordPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            // 重装时取消本平台中的某版本：只移除本安装拥有的旧注册，其他插件/目录保持原样。
            if (previous != null)
            {
                var applications = new HashSet<string>(targets.Select(target => target.Path + @"\Applications\" + (platform == "ZWCAD2025" ? "ZwcadBatchPlot" : "AcadBatchPlot")), StringComparer.OrdinalIgnoreCase);
                var profiles = new HashSet<string>(targets.SelectMany(target => Names(user, target.Path + @"\Profiles").Select(profile => target.Path + @"\Profiles\" + profile + @"\Variables")), StringComparer.OrdinalIgnoreCase);
                foreach (var item in previous.Root.Elements("application").Where(item => !applications.Contains((string)item.Attribute("path"))))
                {
                    string path = (string)item.Attribute("path");
                    using (var key = user.OpenSubKey(path))
                        if (key != null && string.Equals(Convert.ToString(key.GetValue("LOADER")), (string)item.Attribute("loader"), StringComparison.OrdinalIgnoreCase)) transaction.DeleteApplication(path);
                }
                foreach (var item in previous.Root.Elements("trust").Where(item => (string)item.Attribute("added") == "true" && !profiles.Contains((string)item.Attribute("path"))))
                {
                    string path = (string)item.Attribute("path");
                    using (var key = user.OpenSubKey(path))
                    {
                        if (key == null || !key.GetValueNames().Contains("TRUSTEDPATHS", StringComparer.OrdinalIgnoreCase)) continue;
                        string value = Convert.ToString(key.GetValue("TRUSTEDPATHS", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
                        transaction.Set(path, "TRUSTEDPATHS", string.Join(";", value.Split(';').Where(token => !HasPath(token, folder))), key.GetValueKind("TRUSTEDPATHS"));
                    }
                }
            }
            foreach (var target in targets)
            {
                string app = target.Path + @"\Applications\" + (platform == "ZWCAD2025" ? "ZwcadBatchPlot" : "AcadBatchPlot");
                transaction.Set(app, "DESCRIPTION", "出图中心 " + SetupProgram.Edition, RegistryValueKind.String);
                transaction.Set(app, "LOADCTRLS", 2, RegistryValueKind.DWord);
                transaction.Set(app, "LOADER", loader, RegistryValueKind.String);
                transaction.Set(app, "MANAGED", 1, RegistryValueKind.DWord);
                document.Root.Add(new XElement("application", new XAttribute("path", app), new XAttribute("loader", loader), new XAttribute("name", target.Name)));
                // 每个现有配置只追加本插件精确目录，保留用户已有的所有路径。
                foreach (string profile in Names(user, target.Path + @"\Profiles"))
                {
                    string variables = target.Path + @"\Profiles\" + profile + @"\Variables";
                    string value; var trustKind = RegistryValueKind.String;
                    using (var key = user.OpenSubKey(variables))
                    {
                        value = key == null ? "" : Convert.ToString(key.GetValue("TRUSTEDPATHS", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
                        if (key != null && key.GetValueNames().Contains("TRUSTEDPATHS", StringComparer.OrdinalIgnoreCase)) trustKind = key.GetValueKind("TRUSTEDPATHS");
                    }
                    bool exists = HasPath(value, folder);
                    bool owned = !exists || previous != null && previous.Root.Elements("trust").Any(e => (string)e.Attribute("path") == variables && (string)e.Attribute("added") == "true");
                    if (!exists) transaction.Set(variables, "TRUSTEDPATHS", value.TrimEnd(';') + (value.Length == 0 ? "" : ";") + folder, trustKind);
                    document.Root.Add(new XElement("trust", new XAttribute("path", variables), new XAttribute("added", owned ? "true" : "false")));
                }
            }
            var old = File.Exists(recordPath) ? File.ReadAllBytes(recordPath) : null;
            transaction.undo.Add(delegate { if (old == null) { if (File.Exists(recordPath)) File.Delete(recordPath); } else File.WriteAllBytes(recordPath, old); });
            document.Save(recordPath);
            return transaction;
        }
        catch { transaction.Dispose(); throw; }
    }
    private sealed class KeySnapshot
    {
        internal readonly Dictionary<string, Tuple<object, RegistryValueKind>> Values = new Dictionary<string, Tuple<object, RegistryValueKind>>();
        internal readonly Dictionary<string, KeySnapshot> Children = new Dictionary<string, KeySnapshot>();
        internal static KeySnapshot Read(RegistryKey key)
        {
            var result = new KeySnapshot();
            foreach (string name in key.GetValueNames()) result.Values.Add(name, Tuple.Create(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind(name)));
            foreach (string name in key.GetSubKeyNames()) using (var child = key.OpenSubKey(name)) result.Children.Add(name, Read(child));
            return result;
        }
        internal void Restore(RegistryKey key)
        {
            foreach (var pair in Values) key.SetValue(pair.Key, pair.Value.Item1, pair.Value.Item2);
            foreach (var pair in Children) using (var child = key.CreateSubKey(pair.Key)) pair.Value.Restore(child);
        }
    }
    private void DeleteApplication(string path)
    {
        KeySnapshot snapshot;
        using (var key = user.OpenSubKey(path)) snapshot = KeySnapshot.Read(key);
        undo.Add(delegate { using (var key = user.CreateSubKey(path)) snapshot.Restore(key); });
        user.DeleteSubKeyTree(path, false);
    }
    private void Set(string path, string name, object value, RegistryValueKind kind)
    {
        bool existed; object before = null; RegistryValueKind beforeKind = RegistryValueKind.String;
        using (var current = user.OpenSubKey(path)) { existed = current != null; if (current != null && current.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) { before = current.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); beforeKind = current.GetValueKind(name); } }
        Action rollback = delegate {
            if (!existed) { user.DeleteSubKeyTree(path, false); return; }
            using (var key = user.CreateSubKey(path)) { if (before == null) key.DeleteValue(name, false); else key.SetValue(name, before, beforeKind); }
        };
        using (var key = user.CreateSubKey(path)) key.SetValue(name, value, kind);
        undo.Add(rollback);
    }
    private static bool HasPath(string value, string folder) { return value.Split(';').Any(p => string.Equals(p.Trim().Trim('"').TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)); }
    internal void Commit() { committed = true; }
    public void Dispose() { if (!committed) foreach (var action in undo.AsEnumerable().Reverse()) action(); undo.Clear(); }
    internal static void Remove(string folder, RegistryKey user)
    {
        string recordPath = System.IO.Path.Combine(folder, "drawing-center.autoload.xml");
        if (!File.Exists(recordPath)) return;
        var document = XDocument.Load(recordPath);
        if (!string.Equals((string)document.Root.Attribute("folder"), folder, StringComparison.OrdinalIgnoreCase)) throw new IOException("自动加载记录与安装位置不匹配。");
        foreach (var item in document.Root.Elements("application"))
        {
            string path = (string)item.Attribute("path");
            using (var key = user.OpenSubKey(path)) if (key == null || !string.Equals(Convert.ToString(key.GetValue("LOADER")), (string)item.Attribute("loader"), StringComparison.OrdinalIgnoreCase)) continue;
            user.DeleteSubKeyTree(path, false);
        }
        foreach (var item in document.Root.Elements("trust").Where(e => (string)e.Attribute("added") == "true"))
        {
            using (var key = user.OpenSubKey((string)item.Attribute("path"), true))
            {
                if (key == null) continue; string value = Convert.ToString(key.GetValue("TRUSTEDPATHS", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
                var kind = key.GetValueNames().Contains("TRUSTEDPATHS", StringComparer.OrdinalIgnoreCase) ? key.GetValueKind("TRUSTEDPATHS") : RegistryValueKind.String;
                key.SetValue("TRUSTEDPATHS", string.Join(";", value.Split(';').Where(p => !HasPath(p, folder))), kind);
            }
        }
        File.Delete(recordPath);
    }
}
