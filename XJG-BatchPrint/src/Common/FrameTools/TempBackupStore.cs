#nullable disable
using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace TitleAttributeBatch.Core
{
    internal sealed class TempBackupStore
    {
        private const string SessionKey = "BatchPlot.FrameTools.TempBackups.SessionRoot";
        internal readonly string Root;
        internal TempBackupStore(string root) { Root = Path.GetFullPath(root); }
        internal static TempBackupStore Session
        {
            get
            {
                string root = AppDomain.CurrentDomain.GetData(SessionKey) as string;
                if (root == null)
                {
                    root = Path.Combine(Path.GetTempPath(), "TitleAttributeBatch", "session_" + Guid.NewGuid().ToString("N"));
                    AppDomain.CurrentDomain.SetData(SessionKey, root);
                }
                return new TempBackupStore(root);
            }
        }
        internal string NextBackup(string source)
        {
            string key;
            using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant()))).Replace("-", "");
            string folder = Path.Combine(Root, key);
            Directory.CreateDirectory(folder);
            string oldest = null;
            for (int i = 1; i <= 3; i++)
            {
                string candidate = Path.Combine(folder, Path.GetFileName(source) + ".bak" + i);
                if (!File.Exists(candidate)) return candidate;
                if (oldest == null || File.GetLastWriteTimeUtc(candidate) < File.GetLastWriteTimeUtc(oldest)) oldest = candidate;
            }
            return oldest;
        }
        private static bool IsBackup(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".bak1" || extension == ".bak2" || extension == ".bak3";
        }
        internal bool HasBackups()
        {
            if (!Directory.Exists(Root)) return false;
            foreach (string folder in Directory.GetDirectories(Root))
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string file in Directory.GetFiles(folder)) if (IsBackup(file)) return true;
            }
            return false;
        }
        internal void Clear()
        {
            if (!Directory.Exists(Root)) return;
            foreach (string folder in Directory.GetDirectories(Root))
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string file in Directory.GetFiles(folder)) if (IsBackup(file)) File.Delete(file);
                if (Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder);
            }
            if (Directory.GetFileSystemEntries(Root).Length == 0) Directory.Delete(Root);
        }
    }
}
