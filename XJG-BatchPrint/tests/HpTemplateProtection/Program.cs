using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using ZwcadBatchPlot;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Main()
    {
        foreach (var item in StandardFrameCatalog.Items)
        {
            string firstPath, secondPath;
            var original = File.ReadAllBytes(Path.Combine("目录", item.Name + ".dwg"));
            using (var first = HpDirectoryTemplates.OpenResource(item.Resource))
            using (var second = HpDirectoryTemplates.OpenResource(item.Resource))
            {
                firstPath = first.Path; secondPath = second.Path;
                Check(firstPath != secondPath && Path.GetExtension(firstPath) == ".tmp", "独立随机临时文件且不使用DWG扩展名");
                Check(File.ReadAllBytes(firstPath).SequenceEqual(original), "解密与原模板必须逐字节一致");
                Check(File.ReadAllBytes(secondPath).SequenceEqual(original), "重复读取模板必须一致");
            }
            Check(!File.Exists(firstPath) && !File.Exists(secondPath), "读取作用域结束必须清理临时文件");
            var name = item.Resource;
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream(); resource.CopyTo(buffer); var encrypted = buffer.ToArray();
            Check(!encrypted.Take(6).SequenceEqual(original.Take(6)), "资源不含明文DWG文件头");
            foreach (var position in new[] { 0, 8, 24, encrypted.Length - 1 })
            {
                var corrupted = (byte[])encrypted.Clone(); corrupted[position] ^= 1;
                try { HpDirectoryTemplates.Decode(corrupted); throw new Exception("损坏资源未被拒绝"); }
                catch (CryptographicException) { }
            }
            try { HpDirectoryTemplates.Decode(encrypted.Take(20).ToArray()); throw new Exception("截断资源未被拒绝"); }
            catch (CryptographicException) { }
        }
        string? path = null;
        try
        {
            using var template = HpDirectoryTemplates.Open("A3"); path = template.Path;
            throw new IOException("simulate CAD input failure");
        }
        catch (IOException) { }
        Check(path != null && !File.Exists(path), "异常路径也必须删除解密临时文件");
        try { HpDirectoryTemplates.Open("A2"); throw new Exception("非法模板尺寸未拒绝"); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS: authenticated encrypted resources, exact template bytes, repeat reads, tamper/truncation rejection, scoped temp cleanup");
    }
}
