using System;
using System.IO;
using System.Security.Cryptography;
namespace ZwcadBatchPlot;
/// <summary>带签名打印的磁盘原件备份。打印不写原文件，校验原件未变后才清理备份。</summary>
internal sealed class SignedPlotBackup : IDisposable
{
    internal string Source { get; }
    internal string Folder { get; }
    internal string BackupPath { get; }
    private readonly string digest;
    internal SignedPlotBackup(string source)
    {
        Source = Path.GetFullPath(source);
        if (!File.Exists(Source)) throw new IOException("带签名打印需要先保存DWG文件。");
        Folder = Path.Combine(Path.GetTempPath(), "DrawingCenter", "SignedPrint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        BackupPath = Path.Combine(Folder, Path.GetFileName(Source));
        digest = Hash(Source);
        File.Copy(Source, BackupPath, false);
        if (Hash(BackupPath) != digest || Hash(Source) != digest)
            throw new IOException("原文件在备份时发生变化，已停止带签名打印。备份：" + BackupPath);
        File.WriteAllText(Path.Combine(Folder, "恢复说明.txt"), "原文件：" + Source + Environment.NewLine + "此文件是签名前的磁盘原件备份；打印签名只在内存中使用，不写回原文件。正常回滚后自动清理。");
    }
    internal static string Hash(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(input));
    }
    internal void Complete()
    {
        if (!File.Exists(Source) || Hash(Source) != digest)
            throw new IOException("原文件在打印期间被其他操作修改，未覆盖该文件，已保留签名前备份：" + BackupPath);
        File.Delete(BackupPath); File.Delete(Path.Combine(Folder, "恢复说明.txt")); Directory.Delete(Folder);
    }
    // 未确认数据库回滚成功时不清理备份，供异常/崩溃后人工恢复。
    public void Dispose() { }
}
