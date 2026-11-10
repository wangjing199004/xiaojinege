#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CadSignature
{
    /// <summary>签名素材加密保险库（5.23.0 新增）。
    ///
    /// 诉求：制作出的签名 DWG 不能被人随手翻到、Windows 搜索搜不到，最好拷走了也打不开。
    /// 方案：签名文件夹下建一个隐藏子文件夹 <c>.db</c>（Hidden+System 属性），
    /// 里面的文件全部 AES-256-CBC 加密、文件名是随机 GUID——默认不显示、不被索引、
    /// 按姓名搜不到、就算翻到也认不出是谁的、没有密钥连 CAD 都打不开。
    ///
    /// 文件格式：<c>{guid}.csig</c> = 8 字节魔数 CDSIGV01 + 16 字节 IV + 密文（密文 = DWG 文件字节）。
    /// <c>index.dat</c> = 同样封装加密的「文件名 → guid」映射（UTF-8，每行 文件名<TAB>guid）。
    /// <c>key.bin</c> = 32 字节随机密钥，首次保存时生成；把整个 .db 文件夹拷走即可把素材库
    /// 原样迁移到别的电脑（密钥随库走，这是刻意的——否则换机就解不开了）。
    ///
    /// 本类**只用 BCL（无 AutoCAD 依赖）**，可以脱离 CAD 单测。
    /// 防护定位：防「随手翻到 / 搜索到 / 拷走打开」；不防逆向插件的人（插件自己要能解密）。</summary>
    internal static class SignatureVault
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CDSIGV01");
        private const string VaultFolderName = ".db";
        private const string KeyFileName = "key.bin";
        private const string IndexFileName = "index.dat";
        private const string Deleted = "deleted";

        /// <summary>素材库的完整路径（签名文件夹\.db）。目录可能还不存在——用到时才建。</summary>
        public static string VaultPath(string signatureDirectory)
        {
            return Path.Combine(signatureDirectory, VaultFolderName);
        }

        /// <summary>库里是否已存这个文件名（如「张三.dwg」）。库不存在 = false。</summary>
        public static bool Contains(string signatureDirectory, string fileName)
        {
            string guid;
            return LoadIndex(signatureDirectory).TryGetValue(Normalize(fileName), out guid) && guid != Deleted;
        }

        /// <summary>列出库里全部文件名（含 .dwg 扩展名）。库不存在返回空列表。</summary>
        public static List<string> Names(string signatureDirectory)
        {
            return LoadIndex(signatureDirectory).Where(pair => pair.Value != Deleted).Select(pair => pair.Key).ToList();
        }

        internal static bool WasDeleted(string directory, string fileName)
        { string value; return LoadIndex(directory).TryGetValue(Normalize(fileName), out value) && value == Deleted; }

        // 同时移除库内密文和同名旧明文，墓碑防止旧人员配置包下次自动补回。
        internal static void Delete(string directory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || !fileName.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("无效的笔迹文件名。");
            string name = Normalize(fileName), vault = EnsureVault(directory);
            var index = LoadIndex(directory); string guid;
            string payload = null;
            if (index.TryGetValue(name, out guid) && guid != Deleted)
            {
                Guid parsed; if (!Guid.TryParseExact(guid, "N", out parsed)) throw new IOException("笔迹索引无效，未删除。");
                payload = Path.Combine(vault, guid + ".csig");
            }
            string plain = Path.Combine(directory, fileName), indexPath = Path.Combine(vault, IndexFileName);
            var saved = new Dictionary<string, byte[]>(); var attributes = new Dictionary<string, FileAttributes>();
            foreach (string path in new[] { indexPath, payload, plain }.Where(path => path != null && File.Exists(path)))
            {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                saved[path] = File.ReadAllBytes(path); attributes[path] = File.GetAttributes(path);
            }
            try
            {
                index[name] = Deleted; SaveIndex(vault, LoadOrCreateKey(vault), index);
                foreach (string path in new[] { payload, plain }.Where(path => path != null && File.Exists(path)))
                { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            }
            catch
            {
                foreach (var pair in saved) { if (File.Exists(pair.Key)) File.SetAttributes(pair.Key, FileAttributes.Normal); File.WriteAllBytes(pair.Key, pair.Value); File.SetAttributes(pair.Key, attributes[pair.Key]); }
                if (!saved.ContainsKey(indexPath) && File.Exists(indexPath)) { File.SetAttributes(indexPath, FileAttributes.Normal); File.Delete(indexPath); }
                throw;
            }
        }

        /// <summary>把一份 DWG 字节加密存进库（同名 = 覆盖：换新 guid、删旧文件、更新索引）。</summary>
        public static void Save(string signatureDirectory, string fileName, byte[] dwgBytes)
        {
            if (dwgBytes == null || dwgBytes.Length == 0) throw new ArgumentException("内容为空");
            string key = Normalize(fileName);
            string vault = EnsureVault(signatureDirectory);
            byte[] aesKey = LoadOrCreateKey(vault);
            Dictionary<string, string> index = LoadIndex(signatureDirectory);

            string guid = Guid.NewGuid().ToString("N");
            WriteHidden(Path.Combine(vault, guid + ".csig"), EncodeEnvelope(aesKey, dwgBytes));

            string old;
            if (index.TryGetValue(key, out old) && !string.IsNullOrEmpty(old))
            {
                try
                {
                    string oldPath = Path.Combine(vault, old + ".csig");
                    if (File.Exists(oldPath))
                    {
                        File.SetAttributes(oldPath, FileAttributes.Normal);
                        File.Delete(oldPath);
                    }
                }
                catch (System.Exception) { }   // 旧文件删不掉不影响新文件生效
            }
            index[key] = guid;
            SaveIndex(vault, aesKey, index);
        }

        /// <summary>从库里解出一份 DWG 到临时目录（随机文件名），返回完整路径；**用完必须删**。
        /// 库不存在 / 没有这个名字 / 解密失败 → 返回 null（不抛异常，调用方回落明文文件）。</summary>
        public static string ExtractToTempDwg(string signatureDirectory, string fileName)
        {
            try
            {
                byte[] plain = ReadFromVault(signatureDirectory, fileName);
                if (plain == null) return null;
                string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dwg");
                File.WriteAllBytes(temp, plain);
                return temp;
            }
            catch (System.Exception)
            {
                return null;       // 库坏了 / 被占用：当没找到处理，调用方走明文或报缺素材
            }
        }

        /// <summary>把一份笔迹读成**明文 DWG 字节**（5.26.0，供「导出人员设置」打包用）：
        /// **加密库优先、签名文件夹里的明文 .dwg 回落**。全过程只在内存里，**不落任何临时文件**。
        /// 两边都没有 → 返回 null（不抛）。</summary>
        public static byte[] ReadSignatureBytes(string signatureDirectory, string fileName)
        {
            try
            {
                byte[] fromVault = ReadFromVault(signatureDirectory, fileName);
                if (fromVault != null) return fromVault;
            }
            catch (System.Exception) { }
            try
            {
                string plain = Path.Combine(signatureDirectory ?? "", fileName ?? "");
                if (File.Exists(plain)) return File.ReadAllBytes(plain);
            }
            catch (System.Exception) { }
            return null;
        }

        // ---------------- 内部实现 ----------------

        /// <summary>从加密库里解出明文 DWG 字节；库里没有 / 解不开 → null（不抛）。</summary>
        private static byte[] ReadFromVault(string signatureDirectory, string fileName)
        {
            Dictionary<string, string> index = LoadIndex(signatureDirectory);
            string guid;
            if (!index.TryGetValue(Normalize(fileName), out guid) || string.IsNullOrEmpty(guid) || guid == Deleted) return null;
            string vault = VaultPath(signatureDirectory);
            byte[] aesKey = File.ReadAllBytes(Path.Combine(vault, KeyFileName));
            byte[] envelope = File.ReadAllBytes(Path.Combine(vault, guid + ".csig"));
            return DecodeEnvelope(aesKey, envelope);
        }

        /// <summary>文件名统一成小写做键（配置里大小写写法不一也不至于找不到）。</summary>
        private static string Normalize(string fileName)
        {
            return (fileName ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>建库目录（含隐藏属性）；已存在原样返回。</summary>
        private static string EnsureVault(string signatureDirectory)
        {
            string vault = VaultPath(signatureDirectory);
            if (!Directory.Exists(vault))
            {
                Directory.CreateDirectory(vault);
                try
                {
                    DirectoryInfo info = new DirectoryInfo(vault);
                    info.Attributes |= FileAttributes.Hidden | FileAttributes.System;
                }
                catch (System.Exception) { }   // 属性设不上（个别网盘目录）不影响功能，只是能被看到
            }
            return vault;
        }

        /// <summary>写文件并设 Hidden+System（覆盖已隐藏文件前先摘掉属性，否则 WriteAllBytes 拒绝）。</summary>
        private static void WriteHidden(string path, byte[] content)
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllBytes(path, content);
            try { File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System); }
            catch (System.Exception) { }
        }

        private static byte[] LoadOrCreateKey(string vault)
        {
            string keyPath = Path.Combine(vault, KeyFileName);
            if (File.Exists(keyPath)) return File.ReadAllBytes(keyPath);
            byte[] key = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            WriteHidden(keyPath, key);
            return key;
        }

        private static Dictionary<string, string> LoadIndex(string signatureDirectory)
        {
            Dictionary<string, string> index = new Dictionary<string, string>();
            string path = Path.Combine(VaultPath(signatureDirectory), IndexFileName);
            if (!File.Exists(path)) return index;
            try
            {
                byte[] aesKey = File.ReadAllBytes(Path.Combine(VaultPath(signatureDirectory), KeyFileName));
                byte[] plain = DecodeEnvelope(aesKey, File.ReadAllBytes(path));
                if (plain == null) return index;
                foreach (string line in Encoding.UTF8.GetString(plain).Split('\n'))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    index[Normalize(line.Substring(0, tab))] = line.Substring(tab + 1).Trim();
                }
            }
            catch (System.Exception) { }
            return index;
        }

        private static void SaveIndex(string vault, byte[] aesKey, Dictionary<string, string> index)
        {
            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in index)
                text.Append(pair.Key).Append('\t').Append(pair.Value).Append('\n');
            WriteHidden(Path.Combine(vault, IndexFileName), EncodeEnvelope(aesKey, Encoding.UTF8.GetBytes(text.ToString())));
        }

        /// <summary>AES-256-CBC 封装：魔数 + IV + 密文。5.26.0 起对同程序集可见 ——
        /// 「人员设置.cspkg」要用**包自己的密钥**重新封装一份笔迹（见 SettingsPackage）。</summary>
        internal static byte[] EncodeEnvelope(byte[] key, byte[] plain)
        {
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.GenerateIV();
                using (ICryptoTransform enc = aes.CreateEncryptor())
                {
                    byte[] cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
                    byte[] result = new byte[Magic.Length + 16 + cipher.Length];
                    Buffer.BlockCopy(Magic, 0, result, 0, Magic.Length);
                    Buffer.BlockCopy(aes.IV, 0, result, Magic.Length, 16);
                    Buffer.BlockCopy(cipher, 0, result, Magic.Length + 16, cipher.Length);
                    return result;
                }
            }
        }

        /// <summary>解封装；魔数不对 / 长度不对 / 解密失败一律返回 null（不抛）。</summary>
        internal static byte[] DecodeEnvelope(byte[] key, byte[] envelope)
        {
            if (envelope == null || envelope.Length < Magic.Length + 16 + 16) return null;
            for (int i = 0; i < Magic.Length; i++) if (envelope[i] != Magic[i]) return null;
            byte[] iv = new byte[16];
            Buffer.BlockCopy(envelope, Magic.Length, iv, 0, 16);
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;
                try
                {
                    using (ICryptoTransform dec = aes.CreateDecryptor())
                        return dec.TransformFinalBlock(envelope, Magic.Length + 16, envelope.Length - Magic.Length - 16);
                }
                catch (CryptographicException)
                {
                    return null;
                }
            }
        }
    }
}
