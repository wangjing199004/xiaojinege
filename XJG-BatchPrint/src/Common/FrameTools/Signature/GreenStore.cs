#nullable disable
using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace CadSignature
{
    /// <summary>单文件绿色版的「加密缓存」—— 只编进 Source\Single，装机版完全用不到。
    ///
    /// 目标：DLL 旁边**不留明文**。签名素材库本来就是密文（`Signatures\.db`），
    /// 但人员名单、图幅标定、签名文件夹这些设置以前是明文 `.txt`，翻到就看到全部人名。
    /// 现在把它们连同素材库一起收进**一个 AES-256 加密的缓存文件**：
    /// <code>
    ///   &lt;DLL 目录&gt;\CadSignature.cache   密文 = 人员名单.txt + 图框定位.txt + 签名文件夹.txt + Signatures\.db\*
    ///   &lt;DLL 目录&gt;\CadSignature.key     32 字节密钥（隐藏；密钥随包走，换电脑拷这仨就能接着用）
    ///   &lt;DLL 目录&gt;\.session\           工作目录：只在命令执行期间存在（隐藏），命令一结束就被加密收回并删除
    /// </code>
    ///
    /// 生命周期（<see cref="SingleCommands"/> 每条命令两头各调一次）：
    /// <list type="number">
    ///   <item><see cref="Unlock"/> —— 缓存 → 解密到 `.session`；没有缓存就用 DLL 内嵌的那份做种子。
    ///         5.25.0 起绿色版出厂默认**不内嵌任何内容**（干净加载），此时只把空的工作目录建出来，
    ///         核心找不到配置文件就回内置默认 —— 人员名单是张三/李四/王五…，图幅标定为空。
    ///         上次没来得及收（崩溃/强杀）留下的 `.session` 比缓存新，直接接着用。</item>
    ///   <item>核心照常干活（它只认文件，`BaseDirectory` 指向 `.session`）。</item>
    ///   <item><see cref="Seal"/> —— `.session` → 加密写回缓存 → 删掉明文目录。</item>
    /// </list>
    /// 于是**CAD 没在跑的时候，磁盘上只剩密文**；下次加载自动解开，改动都还在。
    ///
    /// 缓存读不出来（密钥被删、文件损坏）时不会覆盖它，而是改名成 `.bak` 留证，
    /// 再用 DLL 内嵌内容重新开始 —— 数据宁可留一份也不悄悄抹掉。
    ///
    /// 纯 BCL（只用 <c>System.Security.Cryptography</c>），可脱离 CAD 单测。</summary>
    internal static class GreenStore
    {
        /// <summary>缓存文件（隐藏）：全部设置 + 素材库的密文。
        ///
        /// **名字跟着 DLL 文件名叫**（见 <see cref="SelfBaseName"/>）：同一个目录里同时放
        /// `CadSignature-ACAD2016.dll` 与 `CadSignature-ACAD2024.dll` 时各用各的缓存，不会
        /// 互相覆盖设置。主文件名仍是 `CadSignature` 时结果就是 <c>CadSignature.cache</c>，
        /// 与 5.25.0 完全一致（老包升级过来照旧认自己的缓存）。</summary>
        public static readonly string CacheFileName = SelfBaseName() + ".cache";
        /// <summary>密钥文件（隐藏）：32 字节随机密钥，与缓存同目录、同基名不同后缀。</summary>
        public static readonly string KeyFileName = SelfBaseName() + ".key";
        /// <summary>工作目录名（隐藏）：命令执行期间的明文所在地。</summary>
        public const string SessionFolderName = ".session";

        /// <summary>本程序集的文件名（不含扩展名）；取不到时退回 <c>CadSignature</c>。
        /// 绿色版是 NETLOAD 进来的，`Location` 一定有值；用 byte[] 加载的老路径没有，
        /// 那就退回默认名，行为和以前一样。</summary>
        private static string SelfBaseName()
        {
            try
            {
                string path = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(path))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch (System.Exception)
            {
            }
            return "CadSignature";
        }
        /// <summary>缓存读不出来时，原文件改名的后缀（留证，不删）。</summary>
        private const string BrokenSuffix = ".bak";

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CDCACHE1");
        private const int KeyBytes = 32;
        private const int IvBytes = 16;

        /// <summary>上一次 <see cref="Unlock"/>/<see cref="Seal"/> 需要告诉用户的一句话；没有 = null。</summary>
        public static string LastNotice { get; private set; }

        /// <summary>命令执行期间的工作目录（明文都在这儿）。</summary>
        public static string SessionPath(string baseDirectory)
        {
            return Path.Combine(baseDirectory ?? "", SessionFolderName);
        }

        /// <summary>把缓存解开到工作目录，返回工作目录。任何失败都退回 DLL 内嵌内容，不抛异常。</summary>
        public static string Unlock(string baseDirectory)
        {
            LastNotice = null;
            string session = SessionPath(baseDirectory);
            if (string.IsNullOrEmpty(baseDirectory)) return session;

            try
            {
                // 上次命令没收尾（崩溃、任务管理器强杀）：工作目录里的内容比缓存新，接着用
                if (Directory.Exists(session)) return session;

                string cache = Path.Combine(baseDirectory, CacheFileName);
                if (File.Exists(cache))
                {
                    try
                    {
                        byte[] payload = Decrypt(File.ReadAllBytes(cache), LoadKey(baseDirectory, false));
                        GreenArchive.Unpack(payload, session);
                        Hide(session);
                        return session;
                    }
                    catch (System.Exception)
                    {
                        // 密钥没了对不上、或者文件被改坏：**不覆盖**它，改名留证后用自带内容重来
                        Preserve(cache);
                        LastNotice = "[CadSignature] 加密缓存解不开（密钥被删或文件损坏），已把原文件改名成 "
                                   + Path.GetFileName(cache) + BrokenSuffix + " 留存，本次先用 DLL 自带的内容。";
                        TryDeleteTree(session);
                    }
                }

                ExtractEmbedded(session);   // 第一次用：把 DLL 里自带的内容摊开
                Hide(session);
                return session;
            }
            catch (System.Exception error)
            {
                LastNotice = "[CadSignature] 准备工作目录失败：" + error.Message;
                return session;
            }
        }

        /// <summary>把工作目录加密写回缓存，并删掉明文目录。失败时明文暂时保留（缓存不会写坏）。</summary>
        public static void Seal(string baseDirectory)
        {
            LastNotice = null;
            if (string.IsNullOrEmpty(baseDirectory)) return;

            string session = SessionPath(baseDirectory);
            if (!Directory.Exists(session)) return;

            try
            {
                byte[] payload = GreenArchive.Pack(session);
                byte[] key = LoadKey(baseDirectory, true);
                WriteHidden(Path.Combine(baseDirectory, CacheFileName), Encrypt(payload, key));
                TryDeleteTree(session);
            }
            catch (System.Exception error)
            {
                LastNotice = "[CadSignature] 写回加密缓存失败，工作目录里的明文暂时保留：" + error.Message;
            }
        }

        // ---------------------------------------------------------------- 密钥

        /// <summary>读密钥；<paramref name="create"/> 为 true 且文件不存在时生成一份新的。
        /// <paramref name="create"/> 为 false（解锁缓存）时，密钥缺失或长度不对就抛出来，
        /// 让调用方走"缓存留证 + 用自带内容重来"的分支，而不是拿一把新钥匙去开旧锁。</summary>
        private static byte[] LoadKey(string baseDirectory, bool create)
        {
            string path = Path.Combine(baseDirectory ?? "", KeyFileName);
            if (File.Exists(path))
            {
                byte[] key = File.ReadAllBytes(path);
                if (key.Length == KeyBytes) return key;
                if (!create) throw new InvalidDataException("密钥长度不对：" + key.Length);
            }
            if (!create) throw new FileNotFoundException("找不到密钥文件", path);

            byte[] fresh = new byte[KeyBytes];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(fresh);
            WriteHidden(path, fresh);
            return fresh;
        }

        // ---------------------------------------------------------------- 加解密

        private static byte[] Encrypt(byte[] plain, byte[] key)
        {
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Key = key;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();

                byte[] iv = aes.IV;
                byte[] body;
                using (ICryptoTransform transform = aes.CreateEncryptor()) body = transform.TransformFinalBlock(plain, 0, plain.Length);

                byte[] result = new byte[Magic.Length + IvBytes + body.Length];
                Buffer.BlockCopy(Magic, 0, result, 0, Magic.Length);
                Buffer.BlockCopy(iv, 0, result, Magic.Length, iv.Length);
                Buffer.BlockCopy(body, 0, result, Magic.Length + IvBytes, body.Length);
                return result;
            }
        }

        private static byte[] Decrypt(byte[] envelope, byte[] key)
        {
            if (envelope == null || key == null || key.Length != KeyBytes) throw new InvalidDataException("缓存或密钥不对");
            if (envelope.Length <= Magic.Length + IvBytes) throw new InvalidDataException("缓存太短");

            for (int i = 0; i < Magic.Length; i++)
                if (envelope[i] != Magic[i]) throw new InvalidDataException("不是本插件的缓存文件");

            byte[] iv = new byte[IvBytes];
            Buffer.BlockCopy(envelope, Magic.Length, iv, 0, IvBytes);

            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Key = key;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.IV = iv;
                using (ICryptoTransform transform = aes.CreateDecryptor())
                    return transform.TransformFinalBlock(envelope, Magic.Length + IvBytes, envelope.Length - Magic.Length - IvBytes);
            }
        }

        // ---------------------------------------------------------------- 内嵌种子

        /// <summary>嵌入资源前缀（ASCII）：配置文件。</summary>
        private const string ConfigPrefix = "CadSignature.Embedded.Config.";
        /// <summary>嵌入资源前缀（ASCII）：签名素材库。</summary>
        private const string VaultPrefix = "CadSignature.Embedded.Vault.";

        /// <summary>嵌入名 → 摊开后的文件名。故意用 ASCII 资源名 + 中文目标名：
        /// csc 命令行传非 ASCII 资源名不稳。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> ConfigFiles =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        {
            { ConfigPrefix + "people.txt",      "人员名单.txt" },
            { ConfigPrefix + "frameoffset.txt", "图框定位.txt" },
            { ConfigPrefix + "sigfolder.txt",   "签名文件夹.txt" },
        };

        /// <summary>把 DLL 里自带的配置与素材库摊到工作目录（只写不存在的文件）。
        ///
        /// **不管有没有内嵌内容，都先把工作目录建出来** —— 5.25.0 起绿色版出厂默认不内嵌任何东西
        /// （干净加载），一个文件都不会写；此时目录若不存在，核心往 `.session` 里写配置就会失败
        /// （`SignatureSettings.SaveCustomDirectory` 不建目录），后面 <see cref="Seal"/> 也会因为
        /// 目录不存在直接跳过，改动全丢。</summary>
        private static void ExtractEmbedded(string session)
        {
            Directory.CreateDirectory(session);   // 即使没有任何内嵌内容，也要把工作目录建出来
            Assembly self = Assembly.GetExecutingAssembly();
            string vault = Path.Combine(Path.Combine(session, "Signatures"), ".db");
            bool vaultMissing = !Directory.Exists(vault);

            foreach (string resource in self.GetManifestResourceNames())
            {
                string target = null;
                if (resource.StartsWith(VaultPrefix, StringComparison.Ordinal))
                {
                    if (vaultMissing) target = Path.Combine(vault, resource.Substring(VaultPrefix.Length));
                }
                else
                {
                    string name;
                    if (ConfigFiles.TryGetValue(resource, out name)) target = Path.Combine(session, name);
                }
                if (target == null || File.Exists(target)) continue;
                ExtractOne(self, resource, target);
            }
        }

        private static void ExtractOne(Assembly self, string resource, string target)
        {
            try
            {
                string folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);

                using (Stream input = self.GetManifestResourceStream(resource))
                {
                    if (input == null) return;
                    byte[] buffer = new byte[input.Length];
                    int total = 0;
                    while (total < buffer.Length)
                    {
                        int read = input.Read(buffer, total, buffer.Length - total);
                        if (read <= 0) break;
                        total += read;
                    }
                    using (FileStream output = new FileStream(target, FileMode.Create, FileAccess.Write))
                        output.Write(buffer, 0, total);
                }
            }
            catch (System.Exception)
            {
            }
        }

        // ---------------------------------------------------------------- 文件小工具

        /// <summary>命令执行期间工作目录（以及素材库）是隐藏的，免得看着乱。</summary>
        private static void Hide(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Hidden | FileAttributes.System);
            }
            catch (System.Exception)
            {
            }
        }

        /// <summary>写文件并置「隐藏 + 系统」。
        ///
        /// **必须先清掉已有属性再写**：本机实测（2026-10-10，Python 走同一套 Win32 API 复现），
        /// 覆盖写一个带 Hidden/System 属性的文件会被系统拒绝（访问被拒绝），桌面与 %TEMP% 都一样。
        /// `SignatureVault.WriteHidden` 一直是这么做的，这里照抄，别省这一步。</summary>
        private static void WriteHidden(string path, byte[] data)
        {
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllBytes(path, data);
            try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden | FileAttributes.System); }
            catch (System.Exception) { }
        }

        /// <summary>缓存读不出来时留证：改名成 .bak（绝不直接删）。</summary>
        private static void Preserve(string cache)
        {
            try
            {
                string backup = cache + BrokenSuffix;
                TryDelete(backup);
                if (File.Exists(cache))
                {
                    File.SetAttributes(cache, FileAttributes.Normal);
                    File.Move(cache, backup);
                }
            }
            catch (System.Exception)
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (System.Exception)
            {
            }
        }

        private static void TryDeleteTree(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                ClearAttributes(folder);
                Directory.Delete(folder, true);
            }
            catch (System.Exception)
            {
            }
        }

        private static void ClearAttributes(string folder)
        {
            try
            {
                foreach (string file in Directory.GetFiles(folder)) File.SetAttributes(file, FileAttributes.Normal);
                foreach (string child in Directory.GetDirectories(folder)) ClearAttributes(child);
                File.SetAttributes(folder, FileAttributes.Normal);
            }
            catch (System.Exception)
            {
            }
        }
    }
}
