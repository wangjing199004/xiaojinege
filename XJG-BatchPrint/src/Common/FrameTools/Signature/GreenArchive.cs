#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CadSignature
{
    /// <summary>把一棵目录树打成一串字节、再原样还原（绿色版加密缓存的载荷格式）。
    ///
    /// 格式（全部小端）：
    /// <code>
    ///   int32  条目数
    ///   每条目：
    ///     int32  相对路径字节数（UTF-8，'/' 分隔）
    ///     路径字节
    ///     int64  内容字节数
    ///     内容字节
    /// </code>
    /// 只记文件；空目录不记，还原时按需建。路径与内容都做长度校验，
    /// 缓存被改坏时宁可抛异常也不去分配一个天文数字的数组。
    ///
    /// 纯 BCL，可脱离 CAD 单测。</summary>
    internal static class GreenArchive
    {
        /// <summary>单条路径长度上限（防止坏缓存把我们带进 OOM）。</summary>
        private const int MaxNameBytes = 4096;
        /// <summary>单个文件大小上限（当前素材库最大也就百来 KB）。</summary>
        private const long MaxFileBytes = 256L * 1024 * 1024;

        /// <summary>把 <paramref name="root"/> 下的全部文件打包；目录不存在 = 空包。</summary>
        public static byte[] Pack(string root)
        {
            List<string> files = new List<string>();
            if (Directory.Exists(root)) Collect(root, files);
            files.Sort(StringComparer.Ordinal);

            using (MemoryStream buffer = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(buffer, Encoding.UTF8))
            {
                writer.Write(files.Count);
                foreach (string path in files)
                {
                    string relative = Relative(root, path).Replace('\\', '/');
                    byte[] name = Encoding.UTF8.GetBytes(relative);
                    byte[] data = File.ReadAllBytes(path);
                    writer.Write(name.Length);
                    writer.Write(name);
                    writer.Write((long)data.Length);
                    writer.Write(data);
                }
                writer.Flush();
                return buffer.ToArray();
            }
        }

        /// <summary>把 <see cref="Pack"/> 的产物还原到 <paramref name="root"/>（**根目录一定会建出来**）。
        ///
        /// 「根目录无条件建」是必须的：干净版（5.25.0 起绿色版出厂默认不内嵌任何内容）第一次封存的
        /// 载荷是**空包**（0 条目），如果只在写文件时才建目录，解包后 root 根本不存在，紧接着核心
        /// 往工作目录里写配置就会抛 DirectoryNotFoundException。
        /// 这个坑由脱离 CAD 的「双模式」单测抓到 —— 别把这一行当成冗余删掉。</summary>
        public static void Unpack(byte[] payload, string root)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (!string.IsNullOrEmpty(root)) Directory.CreateDirectory(root);
            using (MemoryStream buffer = new MemoryStream(payload, false))
            using (BinaryReader reader = new BinaryReader(buffer, Encoding.UTF8))
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > 100000) throw new InvalidDataException("条目数不合理：" + count);

                for (int i = 0; i < count; i++)
                {
                    int nameLength = reader.ReadInt32();
                    if (nameLength <= 0 || nameLength > MaxNameBytes)
                        throw new InvalidDataException("路径长度不合理：" + nameLength);

                    string relative = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
                    long size = reader.ReadInt64();
                    if (size < 0 || size > MaxFileBytes)
                        throw new InvalidDataException("文件大小不合理：" + size);

                    byte[] data = reader.ReadBytes((int)size);
                    if (data.Length != (int)size) throw new InvalidDataException("内容被截断：" + relative);

                    // 只接受相对路径：缓存里的 '../../x' 之类绝不落到 root 外面去
                    string safe = relative.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(safe) || safe.Contains(".."))
                        throw new InvalidDataException("路径不安全：" + relative);

                    string path = Path.Combine(root, safe);
                    string folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
                    File.WriteAllBytes(path, data);
                }
            }
        }

        private static void Collect(string root, List<string> files)
        {
            foreach (string file in Directory.GetFiles(root)) files.Add(file);
            foreach (string folder in Directory.GetDirectories(root)) Collect(folder, files);
        }

        private static string Relative(string root, string path)
        {
            string prefix = root.EndsWith("\\") || root.EndsWith("/") ? root : root + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : Path.GetFileName(path);
        }
    }
}
