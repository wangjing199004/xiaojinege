using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CadSignature;
using Newtonsoft.Json;
using TitleAttributeBatch.Core;

namespace ZwcadBatchPlot;

[Flags]
internal enum EnvironmentComponents { None = 0, Settings = 1, Library = 2, Calibration = 4, Signatures = 8, All = 15 }

internal static class EnvironmentTransfer
{
    internal static AppSettings ResetToFactory(out string backup)
    {
        var staging = Path.Combine(Path.GetTempPath(), "DrawingCenter-Factory-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var session = Path.Combine(staging, "factory"); Directory.CreateDirectory(session);
            var signatures = Path.Combine(session, "Signatures"); Directory.CreateDirectory(signatures);
            var people = PeopleConfig.Defaults(); PeopleConfig.Save(session, people);
            FrameOffsetConfig.EnsureTemplate(session);
            var personnel = Path.Combine(staging, SettingsPackage.PackageFileName);
            SettingsPackage.Export(personnel, people, signatures);
            var entries = new Dictionary<string, byte[]>
            {
                ["Components.txt"] = Encoding.UTF8.GetBytes("15"),
                ["Settings.json"] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(AppSettingsStore.Default())),
                ["TitleBlockLibrary.json"] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new TitleBlockLibrary())),
                ["SignatureSession.bin"] = GreenArchive.Pack(session),
                [SettingsPackage.PackageFileName] = File.ReadAllBytes(personnel)
            };
            return ImportEntries(entries, out backup, EnvironmentComponents.All);
        }
        finally { DeleteTemporary(staging); }
    }

    internal static IReadOnlyList<string> Export(string path, AppSettings settings, EnvironmentComponents components = EnvironmentComponents.All)
    {
        var missing = new List<string>();
        if (components == EnvironmentComponents.None || (components & ~EnvironmentComponents.All) != 0) throw new InvalidDataException("请至少勾选一种配置。");
        var entries = new Dictionary<string, byte[]> { ["Components.txt"] = Encoding.UTF8.GetBytes(((int)components).ToString()) };
        if (components.HasFlag(EnvironmentComponents.Settings)) entries["Settings.json"] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(AppSettingsStore.Normalize(settings), Formatting.Indented));
        if (components.HasFlag(EnvironmentComponents.Library)) entries["TitleBlockLibrary.json"] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(TitleBlockLibraryStore.Load(), Formatting.Indented));
        var temporary = Path.Combine(Path.GetTempPath(), "DrawingCenter-" + Guid.NewGuid().ToString("N"));
        try
        {
            if ((components & (EnvironmentComponents.Calibration | EnvironmentComponents.Signatures)) != 0) SignatureRuntime.Run(() => {
                if (!string.IsNullOrEmpty(GreenStore.LastNotice)) throw new IOException(GreenStore.LastNotice);
                var session = CoreEntry.BaseDirectory;
                var signatures = Path.Combine(session, "Signatures");
                SettingsPackage.ApplyIfNeeded(CoreEntry.PackageDirectory, session, signatures);
                Directory.CreateDirectory(temporary);
                foreach (var name in components.HasFlag(EnvironmentComponents.Calibration) ? new[] { FrameOffsetConfig.FileName } : Array.Empty<string>())
                    if (File.Exists(Path.Combine(session, name))) File.Copy(Path.Combine(session, name), Path.Combine(temporary, name));
                if (components.HasFlag(EnvironmentComponents.Signatures))
                {
                var targetSignatures = Path.Combine(temporary, "Signatures");
                if (Directory.Exists(signatures)) CopyTree(signatures, targetSignatures);
                Directory.CreateDirectory(targetSignatures);
                // 明文及绝对路径笔迹转存到随包携带的加密库，换机不依赖原盘符。
                foreach (var file in Directory.GetFiles(targetSignatures, "*.dwg"))
                { SignatureVault.Save(targetSignatures, Path.GetFileName(file), File.ReadAllBytes(file)); File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                var people = PeopleConfig.Load(session); int index = 0;
                foreach (var person in people.Values)
                {
                    index++; if (string.IsNullOrWhiteSpace(person.Name)) continue;
                    var file = string.IsNullOrWhiteSpace(person.SignatureFile) ? person.Name + ".dwg" : person.SignatureFile;
                    var bytes = SignatureVault.ReadSignatureBytes(signatures, file);
                    if (bytes == null)
                    {
                        if (Path.IsPathRooted(file)) throw new IOException("找不到人员配置指定的签名文件：" + file);
                        missing.Add(person.Name);
                        continue; // 允许只填写姓名、不使用笔迹。
                    }
                    if (file.IndexOfAny(new[] { ':', '/', '\\' }) >= 0) file = "portable-" + index + ".dwg";
                    SignatureVault.Save(targetSignatures, file, bytes); person.SignatureFile = file;
                }
                PeopleConfig.Save(temporary, people);
                // 空库也携带目录标记，避免目标设备把自己的种子笔迹补回。
                Directory.CreateDirectory(SignatureVault.VaultPath(targetSignatures));
                File.WriteAllBytes(Path.Combine(SignatureVault.VaultPath(targetSignatures), "environment.marker"), Array.Empty<byte>());
                var packagePath = Path.Combine(temporary, SettingsPackage.PackageFileName);
                SettingsPackage.Export(packagePath, people, targetSignatures);
                entries[SettingsPackage.PackageFileName] = File.ReadAllBytes(packagePath);
                File.Delete(packagePath);
                }
                entries["SignatureSession.bin"] = GreenArchive.Pack(temporary);
                return "";
            });
            EnvironmentPackage.Write(path, entries);
            return missing;
        }
        finally { DeleteTemporary(temporary); }
    }

    internal static EnvironmentComponents GetComponents(IReadOnlyDictionary<string, byte[]> entries)
    {
        EnvironmentComponents available;
        if (entries.TryGetValue("Components.txt", out var description))
        {
            if (!int.TryParse(Encoding.UTF8.GetString(description), out var value) || value <= 0 || (value & ~(int)EnvironmentComponents.All) != 0) throw new InvalidDataException("环境包分类不正确。");
            available = (EnvironmentComponents)value;
        }
        else available = EnvironmentComponents.All; // v30整包
        if (available.HasFlag(EnvironmentComponents.Settings) && !entries.ContainsKey("Settings.json") || available.HasFlag(EnvironmentComponents.Library) && !entries.ContainsKey("TitleBlockLibrary.json") || (available & (EnvironmentComponents.Calibration | EnvironmentComponents.Signatures)) != 0 && !entries.ContainsKey("SignatureSession.bin") || available.HasFlag(EnvironmentComponents.Signatures) && !entries.ContainsKey(SettingsPackage.PackageFileName)) throw new InvalidDataException("环境配置包内容不完整。");
        return available;
    }
    internal static Dictionary<string, byte[]> ReadEntries(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            var library = TitleBlockLibraryStore.Load(path);
            return new Dictionary<string, byte[]> { ["Components.txt"] = Encoding.UTF8.GetBytes("2"), ["TitleBlockLibrary.json"] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(library)) };
        }
        if (string.Equals(Path.GetExtension(path), ".cspkg", StringComparison.OrdinalIgnoreCase))
        {
            SettingsPackage.Read(path);
            var temporary = Path.Combine(Path.GetTempPath(), "DrawingCenter-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temporary); File.Copy(path, Path.Combine(temporary, SettingsPackage.PackageFileName));
                var session = Path.Combine(temporary, "session"); Directory.CreateDirectory(session);
                SettingsPackage.ApplyIfNeeded(temporary, session, Path.Combine(session, "Signatures"));
                return new Dictionary<string, byte[]> { ["Components.txt"] = Encoding.UTF8.GetBytes("8"), ["SignatureSession.bin"] = GreenArchive.Pack(session), [SettingsPackage.PackageFileName] = File.ReadAllBytes(path) };
            }
            finally { DeleteTemporary(temporary); }
        }
        return EnvironmentPackage.Read(path);
    }
    internal static AppSettings Import(string path, out string backup, EnvironmentComponents selected = EnvironmentComponents.All)
        => ImportEntries(ReadEntries(path), out backup, selected);
    private static AppSettings ImportEntries(IReadOnlyDictionary<string, byte[]> entries, out string backup, EnvironmentComponents selected)
    {
        var available = GetComponents(entries); selected &= available;
        if (selected == EnvironmentComponents.None) throw new InvalidDataException("配置包中没有已勾选的配置类型。");
        var settings = selected.HasFlag(EnvironmentComponents.Settings) ? AppSettingsStore.Normalize(JsonConvert.DeserializeObject<AppSettings>(Encoding.UTF8.GetString(entries["Settings.json"])) ?? throw new InvalidDataException("设置为空。")) : AppSettingsStore.Load();
        var files = new Dictionary<string, byte[]>();
        if (selected.HasFlag(EnvironmentComponents.Settings)) files[AppSettingsStore.Path] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(settings, Formatting.Indented));
        if (selected.HasFlag(EnvironmentComponents.Library))
        {
            var library = JsonConvert.DeserializeObject<TitleBlockLibrary>(Encoding.UTF8.GetString(entries["TitleBlockLibrary.json"])) ?? throw new InvalidDataException("图框库为空。");
            files[TitleBlockLibraryStore.DefaultPath] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(library, Formatting.Indented));
        }
        var staging = Path.Combine(Path.GetTempPath(), "DrawingCenter-" + Guid.NewGuid().ToString("N"));
        try
        {
            if ((selected & (EnvironmentComponents.Calibration | EnvironmentComponents.Signatures)) != 0)
            {
                var incoming = Path.Combine(staging, "incoming"); GreenArchive.Unpack(entries["SignatureSession.bin"], incoming);
                var session = GreenStore.SessionPath(staging);
                SignatureRuntime.Run(() => { CopyTree(CoreEntry.BaseDirectory, session); return ""; });
                if (selected.HasFlag(EnvironmentComponents.Calibration))
                {
                    var target = Path.Combine(session, FrameOffsetConfig.FileName);
                    if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); }
                    var source = Path.Combine(incoming, FrameOffsetConfig.FileName); if (File.Exists(source)) File.Copy(source, target);
                }
                if (selected.HasFlag(EnvironmentComponents.Signatures))
                {
                    var profilePackage = Path.Combine(staging, SettingsPackage.PackageFileName); File.WriteAllBytes(profilePackage, entries[SettingsPackage.PackageFileName]); SettingsPackage.Read(profilePackage);
                    var signatures = Path.Combine(session, "Signatures"); DeleteTemporary(signatures);
                    if (Directory.Exists(Path.Combine(incoming, "Signatures"))) CopyTree(Path.Combine(incoming, "Signatures"), signatures);
                    Directory.CreateDirectory(SignatureVault.VaultPath(signatures)); File.WriteAllBytes(Path.Combine(SignatureVault.VaultPath(signatures), "environment.marker"), Array.Empty<byte>());
                    var people = PeopleConfig.Load(incoming); PeopleConfig.Save(session, people);
                    files[Path.Combine(PluginRuntimePaths.BaseDirectory, SettingsPackage.PackageFileName)] = entries[SettingsPackage.PackageFileName];
                }
                SignatureSettings.SaveCustomDirectory(session, Path.Combine(GreenStore.SessionPath(SignatureRuntime.DataDirectory), "Signatures"));
                GreenStore.Seal(staging);
                if (Directory.Exists(session)) throw new IOException("签名数据封存失败，未导入。");
                files[Path.Combine(SignatureRuntime.DataDirectory, GreenStore.CacheFileName)] = File.ReadAllBytes(Path.Combine(staging, GreenStore.CacheFileName));
                files[Path.Combine(SignatureRuntime.DataDirectory, GreenStore.KeyFileName)] = File.ReadAllBytes(Path.Combine(staging, GreenStore.KeyFileName));
            }
            backup = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "EnvironmentBackups", "导入前_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".dcenv");
            Export(backup, AppSettingsStore.Load(), selected);
            if (Directory.Exists(GreenStore.SessionPath(SignatureRuntime.DataDirectory))) throw new IOException("签名工作目录仍在使用，未导入。");
            EnvironmentPackage.ApplyFiles(files); return settings;
        }
        finally { DeleteTemporary(staging); }
    }
    private static void CopyTree(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("签名目录包含链接，请改用本地素材。");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source)) CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
    private static void DeleteTemporary(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, true);
    }
}
