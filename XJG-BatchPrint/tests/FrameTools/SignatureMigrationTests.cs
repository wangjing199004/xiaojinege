using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

internal static class SignatureMigrationTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static string Digest(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(stream)); }
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string folder in Directory.GetDirectories(source)) CopyTree(folder, Path.Combine(target, Path.GetFileName(folder)));
    }
    public static int Main(string[] args)
    {
        string work = Path.GetFullPath(args[1]);
        if (Directory.Exists(work)) throw new Exception("Test directory must not already exist");
        string dataSource = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "SignatureData");
        string packageSource = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "人员设置.cspkg");
        string originalHash = Digest(Path.Combine(dataSource, "CadSignature.cache"));
        try
        {
            CopyTree(dataSource, work);
            var assembly = Assembly.Load(File.ReadAllBytes(args[0]));
            var store = assembly.GetType("CadSignature.GreenStore", true);
            Check((string)store.GetField("CacheFileName", Flags).GetValue(null) == "CadSignature.cache", "Memory-loaded cache name does not match migrated files");
            string session = (string)store.GetMethod("Unlock", Flags).Invoke(null, new object[] { work });
            Check(store.GetProperty("LastNotice", Flags).GetValue(null, null) == null, "Migrated cache did not decrypt cleanly");
            Console.WriteLine("PASS: actual migrated cache decrypts when the combined DLL is loaded from bytes");
            var runtime = assembly.GetType("TitleAttributeBatch.Core.SignatureRuntime", true);
            runtime.GetMethod("PrepareSession", Flags).Invoke(null, new object[] { work, session });
            Check(File.Exists(Path.Combine(session, "图框定位.txt")), "Frame calibration was lost");
            string vaultFolder = Path.Combine(session, "Signatures");
            Check(Directory.Exists(Path.Combine(vaultFolder, ".db")), "Migrated signature library missing");
            string selectedFolder = (string)assembly.GetType("CadSignature.SignatureSettings", true).GetMethod("LoadCustomDirectory", Flags).Invoke(null, new object[] { session });
            Check(string.Equals(selectedFolder, vaultFolder, StringComparison.OrdinalIgnoreCase), "Signature operations still point to the old project");
            Console.WriteLine("PASS: calibration and encrypted library migrate; operations use an independent folder");
            string packageRoot = Path.Combine(work, "package"); Directory.CreateDirectory(packageRoot);
            File.Copy(packageSource, Path.Combine(packageRoot, "人员设置.cspkg"));
            assembly.GetType("CadSignature.SettingsPackage", true).GetMethod("ApplyIfNeeded", Flags).Invoke(null, new object[] { packageRoot, session, vaultFolder });
            var names = (System.Collections.ICollection)assembly.GetType("CadSignature.SignatureVault", true).GetMethod("Names", Flags).Invoke(null, new object[] { vaultFolder });
            Check(names.Count > 0, "Signature library could not be read");
            var readSignature = assembly.GetType("CadSignature.SignatureVault", true).GetMethod("ReadSignatureBytes", Flags);
            foreach (string name in names)
            {
                byte[] content = (byte[])readSignature.Invoke(null, new object[] { vaultFolder, name });
                Check(content != null && content.Length > 6 && content[0] == 'A' && content[1] == 'C' && content[2] == '1' && content[3] == '0', "A migrated signature cannot be decrypted as DWG");
            }
            Check(File.Exists(Path.Combine(session, "人员名单.txt")), "People settings missing");
            Console.WriteLine("PASS: real people package is readable; every migrated signature decrypts as DWG");
            store.GetMethod("Seal", Flags).Invoke(null, new object[] { work });
            Check(store.GetProperty("LastNotice", Flags).GetValue(null, null) == null, "Re-encryption failed");
            Check(!Directory.Exists(session), "Plaintext working folder was not removed");
            Check(Digest(Path.Combine(dataSource, "CadSignature.cache")) == originalHash, "Test altered the distributed settings");
            Console.WriteLine("PASS: updated session is encrypted again; source settings remain unchanged");
            return 0;
        }
        catch (Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            Console.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally
        {
            if (Directory.Exists(work))
            {
                foreach (string file in Directory.GetFiles(work, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(work, true);
            }
        }
    }
}
