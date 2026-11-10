using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ZwcadBatchPlot;

// 可携带的加密包。密钥随包携带，避免签名素材以明文散落；不作为口令保护。
internal static class EnvironmentPackage
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DCENV001");
    private const int MaxBytes = 256 * 1024 * 1024;
    internal static void Write(string path, IReadOnlyDictionary<string, byte[]> entries)
    {
        if (entries.Count > 10000) throw new InvalidDataException("配置条目过多。");
        byte[] payload;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
            {
                writer.Write(entries.Count);
                foreach (var entry in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    ValidateName(entry.Key);
                    var name = Encoding.UTF8.GetBytes(entry.Key);
                    writer.Write(name.Length); writer.Write(name); writer.Write(entry.Value.Length); writer.Write(entry.Value);
                    if (buffer.Length > MaxBytes) throw new InvalidDataException("配置包超过256MB。");
                }
            }
            payload = buffer.ToArray();
        }
        byte[] key = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
        byte[] encrypted, iv;
        using (var aes = Aes.Create()) { aes.Key = key; aes.GenerateIV(); iv = aes.IV; using var encrypt = aes.CreateEncryptor(); encrypted = encrypt.TransformFinalBlock(payload, 0, payload.Length); }
        byte[] body = Magic.Concat(key).Concat(iv).Concat(encrypted).ToArray();
        byte[] tag; using (var mac = new HMACSHA256(key)) tag = mac.ComputeHash(body);
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, body.Concat(tag).ToArray()); if (File.Exists(full)) File.Replace(temporary, full, null); else File.Move(temporary, full); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static Dictionary<string, byte[]> Read(string path)
    {
        var size = new FileInfo(path).Length;
        if (size < 104 || size > MaxBytes + 128) throw new InvalidDataException("不是有效的环境配置包。");
        var data = File.ReadAllBytes(path);
        if (!data.Take(8).SequenceEqual(Magic)) throw new InvalidDataException("环境包格式或版本不支持。");
        var key = data.Skip(8).Take(32).ToArray(); byte[] expected;
        using (var mac = new HMACSHA256(key)) expected = mac.ComputeHash(data, 0, data.Length - 32);
        int diff = 0; for (int i = 0; i < 32; i++) diff |= expected[i] ^ data[data.Length - 32 + i];
        if (diff != 0) throw new InvalidDataException("环境包损坏，未导入任何配置。");
        byte[] payload;
        using (var aes = Aes.Create()) { aes.Key = key; aes.IV = data.Skip(40).Take(16).ToArray(); using var decrypt = aes.CreateDecryptor(); payload = decrypt.TransformFinalBlock(data, 56, data.Length - 88); }
        using var buffer = new MemoryStream(payload); using var reader = new BinaryReader(buffer, new UTF8Encoding(false, true));
        int count = reader.ReadInt32(); if (count < 0 || count > 10000) throw new InvalidDataException("配置条目数量不正确。");
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            int length = reader.ReadInt32(); if (length < 1 || length > 4096) throw new InvalidDataException("配置条目名称不正确。");
            var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
            var name = new UTF8Encoding(false, true).GetString(bytes); ValidateName(name);
            length = reader.ReadInt32(); if (length < 0 || length > buffer.Length - buffer.Position) throw new InvalidDataException("配置条目长度不正确。");
            if (result.ContainsKey(name)) throw new InvalidDataException("配置包包含重复条目。"); result.Add(name, reader.ReadBytes(length));
        }
        if (buffer.Position != buffer.Length) throw new InvalidDataException("配置包包含额外内容。");
        return result;
    }
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 1024 || name.Contains("\\") || name.Contains(":") || name.Split('/').Any(part => part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("配置包路径不正确。");
    }
    private static void WriteTarget(string path, byte[] bytes)
    {
        // OpenOrCreate 支持覆盖既有的 Hidden 缓存/密钥，不清除原文件属性。
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        stream.SetLength(0); stream.Write(bytes, 0, bytes.Length); stream.Flush();
    }
    // 全量校验在调用前完成；每个目标先备份，写入失败时回滚。
    internal static void ApplyFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        var before = new Dictionary<string, byte[]?>(); var changed = new List<string>();
        foreach (var path in files.Keys)
        {
            if (File.Exists(path)) { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } before[path] = File.ReadAllBytes(path); }
            else before[path] = null;
        }
        try
        {
            foreach (var pair in files) { Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!); changed.Add(pair.Key); WriteTarget(pair.Key, pair.Value); }
        }
        catch
        {
            foreach (var path in changed.AsEnumerable().Reverse()) { if (before[path] == null) { if (File.Exists(path)) File.Delete(path); } else WriteTarget(path, before[path]!); }
            throw;
        }
    }
}
