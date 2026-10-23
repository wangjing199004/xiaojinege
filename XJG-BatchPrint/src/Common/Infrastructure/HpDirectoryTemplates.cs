using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace ZwcadBatchPlot;

/// <summary>加密资源随 DLL 分发。CAD 只接受文件路径，因此读取期间短暂解密到随机 .tmp，关闭输入后立即删除。</summary>
internal static class HpDirectoryTemplates
{
    // 本地插件需持有解密材料；此保护用于避免明文分发，不宣称可阻止逆向提取。
    private const string Material = "cODSy0lutVJX316Vsvb0lczTTl6Mcl2NiM1LJs0oiTPF/2lkurL1cKj63Q/jCR+CX3Hvcz29lp1Ceqp/J5YlxQ==";

    public static TemplateFile Open(string paper)
    {
        var resource = paper == "A3" ? "ZwcadBatchPlot.Private.Hp.a"
            : paper == "A4" ? "ZwcadBatchPlot.Private.Hp.b"
            : throw new ArgumentException("不支持的 HP 目录尺寸。", nameof(paper));
        return OpenResource(resource);
    }

    internal static TemplateFile OpenResource(string resource)
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("内置 HP 目录模板缺失，请重新安装完整插件。");
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var plaintext = Decode(buffer.ToArray());
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(plaintext, 0, plaintext.Length);
            return new TemplateFile(path);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        finally { Array.Clear(plaintext, 0, plaintext.Length); }
    }

    internal static byte[] Decode(byte[] envelope)
    {
        const int headerLength = 8, ivLength = 16, tagLength = 32;
        if (envelope.Length < headerLength + ivLength + 16 + tagLength)
            throw new CryptographicException("内置 HP 目录模板不完整。");
        var material = Convert.FromBase64String(Material);
        var encryptionKey = new byte[32];
        var authenticationKey = new byte[32];
        Buffer.BlockCopy(material, 0, encryptionKey, 0, 32);
        Buffer.BlockCopy(material, 32, authenticationKey, 0, 32);
        try
        {
            var dataLength = envelope.Length - tagLength;
            using (var hmac = new HMACSHA256(authenticationKey))
            {
                var expected = hmac.ComputeHash(envelope, 0, dataLength);
                var difference = 0;
                for (var i = 0; i < tagLength; i++) difference |= expected[i] ^ envelope[dataLength + i];
                if (difference != 0) throw new CryptographicException("内置 HP 目录模板校验失败。");
            }
            var magic = System.Text.Encoding.ASCII.GetString(envelope, 0, headerLength);
            if (magic != "XJGHP001") throw new CryptographicException("内置 HP 目录模板格式无效。");
            var iv = new byte[ivLength];
            Buffer.BlockCopy(envelope, headerLength, iv, 0, ivLength);
            using var aes = Aes.Create();
            aes.Key = encryptionKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(envelope, headerLength + ivLength,
                dataLength - headerLength - ivLength);
        }
        finally
        {
            Array.Clear(material, 0, material.Length);
            Array.Clear(encryptionKey, 0, encryptionKey.Length);
            Array.Clear(authenticationKey, 0, authenticationKey.Length);
        }
    }

    internal sealed class TemplateFile : IDisposable
    {
        internal TemplateFile(string path) { Path = path; }
        public string Path { get; }
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
