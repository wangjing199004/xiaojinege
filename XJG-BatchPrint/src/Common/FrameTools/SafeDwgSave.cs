#nullable disable
using System;
using System.IO;
using System.Security.Cryptography;

namespace TitleAttributeBatch.Core
{
    internal static class SafeDwgSave
    {
        internal static string Digest(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(input));
        }
        internal static void Replace(string temporary, string path, string backup, string expectedDigest)
        {
            if (Digest(path) != expectedDigest) throw new IOException("处理期间原文件发生变化，已停止替换，请重试。");
            // The backup may be on another drive: stage it in the backup folder first.
            string staging = backup + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                File.Copy(path, staging, false);
                if (Digest(staging) != expectedDigest || Digest(path) != expectedDigest)
                    throw new IOException("备份期间原文件发生变化，未替换原图纸。");
                DateTime stamp = DateTime.UtcNow;
                string folder = Path.GetDirectoryName(backup);
                string stem = Path.GetFileNameWithoutExtension(backup);
                for (int i = 1; i <= 3; i++)
                {
                    string slot = Path.Combine(folder, stem + ".bak" + i);
                    if (File.Exists(slot) && File.GetLastWriteTimeUtc(slot) >= stamp) stamp = File.GetLastWriteTimeUtc(slot).AddTicks(1);
                }
                File.SetLastWriteTimeUtc(staging, stamp);
                if (File.Exists(backup)) File.Replace(staging, backup, null);
                else File.Move(staging, backup);
                // Keep the original untouched when backup creation or atomic replacement fails.
                File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
        internal static void ReplaceWithoutBackup(string temporary, string path, string expectedDigest)
        {
            if (Digest(path) != expectedDigest) throw new IOException("处理期间原文件发生变化，已停止保存，请重试。");
            File.Replace(temporary, path, null);
        }
    }
}
