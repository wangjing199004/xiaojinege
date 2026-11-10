#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CadSignature
{
    /// <summary>「导出人员设置」的结果（给界面弹总结用）。</summary>
    internal sealed class PackageExport
    {
        /// <summary>包写到了哪。</summary>
        public string Path;

        /// <summary>打包进去的岗位姓名（按签字栏顺序）。</summary>
        public readonly List<string> Names = new List<string>();

        /// <summary>打包进去的笔迹文件名。</summary>
        public readonly List<string> Included = new List<string>();

        /// <summary>名单里点名要了、但本机找不到素材的笔迹文件名。</summary>
        public readonly List<string> Missing = new List<string>();
    }

    /// <summary>自动应用配置包的结果（给命令行提示用）。</summary>
    internal sealed class PackageResult
    {
        public string Path;
        public bool ListApplied;
        public int SignaturesAdded;

        /// <summary>本来想补、但解不开而跳过的笔迹文件名。</summary>
        public readonly List<string> Failed = new List<string>();

        /// <summary>要不要在命令行说一句；null / 空 = 什么也没发生，别打扰用户。</summary>
        public string Message;
    }

    /// <summary>
    /// 「人员设置.cspkg」——可携带的人员设置加密包（5.26.0）。
    ///
    /// **用户诉求**：「把岗位姓名及签名加密文件加密打包；下次只要在 dll 文件夹中找到该文件，
    /// 就作为默认配置填写姓名并关联签名。」
    ///
    /// **做法**：导出 = 把当前名单 + 名单里点到的那些笔迹合成**一个**文件；应用 = 插件每次读
    /// 人员名单之前瞄一眼 DLL 旁边有没有这个文件，有就按它填。文件放在 DLL 所在目录
    /// （见 <see cref="CoreEntry.PackageDirectory"/>：绿色版 = 用户放 DLL 的那个文件夹，
    /// 装机版 = Contents\Windows），所以换台电脑 / 发给同事时它自然跟着走。
    ///
    /// **文件格式**：<c>人员设置.cspkg</c>
    /// <code>
    /// 8 字节魔数 CSPKG001 | 32 字节密钥 | 16 字节 IV | AES-256-CBC(载荷)
    /// 载荷 = 条目数 + 每条（名字 UTF-8 字节 + 内容字节）
    /// 条目：
    ///   list                → 人员名单文本（与「人员名单.txt」逐字节同源，见 PeopleConfig.ToText）
    ///   signatures/张三.dwg  → 该笔迹的 .csig 信封（**密文**，不是明文 dwg）
    /// </code>
    ///
    /// **密钥随包走**（和加密素材库的 <c>key.bin</c> 同一个道理）：正因为密钥在包里，
    /// 「放到 DLL 旁边就自动生效」才能做到**不需要口令、不需要任何输入**。
    /// 防护定位与 <see cref="SignatureVault"/> 完全一致：防「被随手翻到 / 被 Windows 搜索搜到 /
    /// 被别的软件打开」，**不防**会看这个格式的人 —— 这是刻意的取舍，面板提示里也如实写了。
    ///
    /// **应用规则（两条，都是"只补不覆盖"）**：
    ///   1. 名单：本地「人员名单.txt」**不存在**，或内容还是**出厂默认的示例姓名**（= 用户没配过）
    ///      → 按包里的名单写一份；否则**一律不动**（本机自己配的名单永远优先）。
    ///   2. 笔迹：包里每份笔迹，本地加密库里**已有同名**就跳过，缺的才补进去。
    ///   两条都是幂等的，所以每次命令跑一遍也不会重复写、不会覆盖用户后来的改动。
    ///   判"配过没配过"靠**比内容**，不能比文件时间 —— 绿色版每次命令都把配置从缓存摊到新的
    ///   工作目录，时间戳永远是"刚刚"（5.25.2 的踩坑记录里有同一条教训）。
    ///
    /// 纯 BCL（无 AutoCAD 依赖），可脱离 CAD 单测。
    /// </summary>
    internal static class SettingsPackage
    {
        /// <summary>包的默认文件名（放在 DLL 所在目录）。导出对话框默认就用它；
        /// 用户改名了也认 —— 目录里只有一个 <c>*.cspkg</c> 时照样自动加载。</summary>
        public const string PackageFileName = "人员设置.cspkg";

        private const string ListEntry = "list";
        private const string SignaturePrefix = "signatures/";
        private const int KeySize = 32;
        private const int IvSize = 16;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CSPKG001");

        // ------------------------------------------------------------------ 找包

        /// <summary>在指定目录里找配置包：优先固定名 <c>人员设置.cspkg</c>；没有的话，
        /// 目录里**只有一个** <c>*.cspkg</c> 时也认它（用户改了名照样能用）。
        /// 找不到 / 目录不存在 → null。</summary>
        public static string Find(string profileDirectory)
        {
            if (string.IsNullOrEmpty(profileDirectory)) return null;
            try
            {
                string primary = Path.Combine(profileDirectory, PackageFileName);
                if (File.Exists(primary)) return primary;
                if (!Directory.Exists(profileDirectory)) return null;
                string[] found = Directory.GetFiles(profileDirectory, "*.cspkg");
                return found.Length == 1 ? found[0] : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ 导出

        /// <summary>导出配置包。名单取<paramref name="people"/>（调用方应先把面板里正在编辑的
        /// 内容收进来），笔迹按每个岗位「签名文件 / 姓名.dwg」的规则去取
        /// （<see cref="SignatureVault.ReadSignatureBytes"/>：加密库优先、明文回落）。
        ///
        /// **包里所有笔迹都用包自己的新密钥重新封一遍**（库里的原件是用库密钥封的），
        /// 于是摊开时只需要包里那一把密钥，不必关心对方库里的密钥是哪一把。</summary>
        public static PackageExport Export(string packagePath, Dictionary<string, PersonEntry> people, string signatureDirectory)
        {
            PackageExport summary = new PackageExport { Path = packagePath };

            byte[] key = new byte[KeySize];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(key);

            Dictionary<string, byte[]> entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            entries[ListEntry] = Encoding.UTF8.GetBytes(PeopleConfig.ToText(people));

            foreach (string role in PeopleConfig.Roles)
            {
                PersonEntry entry;
                if (people == null || !people.TryGetValue(role, out entry) || entry == null) continue;
                string name = (entry.Name ?? "").Trim();
                if (name.Length == 0) continue;                     // 该岗位不签名
                summary.Names.Add(name);

                string fileName = string.IsNullOrWhiteSpace(entry.SignatureFile)
                    ? name + ".dwg"
                    : entry.SignatureFile.Trim();

                string entryName = SignaturePrefix + fileName;
                if (entries.ContainsKey(entryName)) { summary.Included.Add(fileName); continue; }   // 同一个人的笔迹只存一份

                byte[] dwg = SignatureVault.ReadSignatureBytes(signatureDirectory, fileName);
                if (dwg == null) { summary.Missing.Add(fileName); continue; }

                entries[entryName] = SignatureVault.EncodeEnvelope(key, dwg);
                summary.Included.Add(fileName);
            }

            Write(packagePath, key, entries);
            return summary;
        }

        // ------------------------------------------------------------------ 自动应用

        /// <summary>按 DLL 旁的配置包填人员设置。**没有包 → 直接返回 null（只花一次 File.Exists）**，
        /// 所以挂在每次读名单的路径上也不会拖慢签名。
        ///
        /// 清单 <see cref="PackageResult.Message"/>：只在自己**确实做了事**（写了名单 / 补了笔迹）
        /// 或包**读不出来**时才非空 —— 正常情况下第二次开始就是静默的。</summary>
        public static PackageResult ApplyIfNeeded(string profileDirectory, string settingsDirectory, string signatureDirectory)
        {
            string path = Find(profileDirectory);
            if (path == null) return null;

            PackageResult result = new PackageResult { Path = path };
            PackageContents content;
            try
            {
                content = Read(path);
            }
            catch (System.Exception ex)
            {
                result.Message = "检测到 DLL 旁的「" + Path.GetFileName(path) + "」，但读不出来（"
                    + ex.Message + "），本次未使用它。";
                return result;
            }

            // ---- 1) 名单：只在"本机还没配过"时才按包填 ----
            string packList = content.Text(ListEntry);
            if (packList != null && ShouldApplyList(settingsDirectory, packList))
            {
                try
                {
                    PeopleConfig.Save(settingsDirectory, PeopleConfig.Parse(packList));
                    result.ListApplied = true;
                }
                catch (System.Exception ex)
                {
                    result.Message = "配置包里的名单写不进去（" + ex.Message + "）。";
                }
            }

            // ---- 2) 笔迹：只补本地没有的，同名一律不覆盖 ----
            foreach (KeyValuePair<string, byte[]> pair in content.Entries)
            {
                if (!pair.Key.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string fileName = pair.Key.Substring(SignaturePrefix.Length);
                if (fileName.Length == 0) continue;
                try
                {
                    if (SignatureVault.Contains(signatureDirectory, fileName) || SignatureVault.WasDeleted(signatureDirectory, fileName)) continue;
                    byte[] dwg = SignatureVault.DecodeEnvelope(content.Key, pair.Value);
                    if (dwg == null) { result.Failed.Add(fileName); continue; }
                    SignatureVault.Save(signatureDirectory, fileName, dwg);
                    result.SignaturesAdded++;
                }
                catch (System.Exception)
                {
                    result.Failed.Add(fileName);
                }
            }

            if (!result.ListApplied && result.SignaturesAdded == 0 && result.Failed.Count == 0) return result;   // 无事发生：Message 留空

            StringBuilder message = new StringBuilder();
            message.Append("已按 DLL 旁的「").Append(Path.GetFileName(path)).Append("」填写人员设置：");
            if (result.ListApplied) message.Append("人员名单已写入；");
            if (result.SignaturesAdded > 0) message.Append("补入 ").Append(result.SignaturesAdded).Append(" 份签名笔迹；");
            if (result.Failed.Count > 0) message.Append("另有 ").Append(result.Failed.Count).Append(" 份笔迹取不出来（")
                .Append(string.Join("、", result.Failed.ToArray())).Append("）已跳过；");
            if (!result.ListApplied)
                message.Append("本机已有自己的人员名单，未覆盖（想用它请先删掉 DLL 旁的「人员名单.txt」再打开一次）；");
            result.Message = message.ToString().TrimEnd('；') + "。";
            return result;
        }

        /// <summary>「人员设置」对话框底部的一行状态：包在不在、按不按它走。没有包也给一句提示。</summary>
        public static string Describe(string profileDirectory, string settingsDirectory)
        {
            string path = Find(profileDirectory);
            if (path == null)
                return "人员设置包：无（点「导出人员设置」可以做一个，放在 DLL 旁边就会自动生效）";
            try
            {
                PackageContents content = Read(path);
                string packList = content.Text(ListEntry);
                string localText = ReadLocalList(settingsDirectory);
                string verdict;
                if (localText == null || PeopleConfig.IsFactoryDefaults(localText))
                    verdict = "会作为默认配置生效";
                else if (packList != null && PeopleConfig.SamePeople(localText, packList))
                    verdict = "本机名单与它一致";
                else
                    verdict = "本机已有自己的人员名单，不会覆盖它";
                return "人员设置包：" + Path.GetFileName(path) + "（含 " + content.SignatureCount + " 份笔迹；" + verdict + "）";
            }
            catch (System.Exception ex)
            {
                return "人员设置包：" + Path.GetFileName(path) + "（读不出来：" + ex.Message + "）";
            }
        }

        /// <summary>该不该按包里的名单填：本地没有名单 → 该；本地已经是包里那一份 → 不必；
        /// 本地还是出厂示例姓名（= 没配过）→ 该；否则（本机自己配过）→ 不覆盖。</summary>
        private static bool ShouldApplyList(string settingsDirectory, string packList)
        {
            string localText = ReadLocalList(settingsDirectory);
            if (localText == null) return true;                                    // 还没有名单
            if (PeopleConfig.SamePeople(localText, packList)) return false;        // 已经就是包里的
            return PeopleConfig.IsFactoryDefaults(localText);                      // 没配过才用包
        }

        /// <summary>读本机的人员名单文本；没有 / 读不出来 → null。</summary>
        private static string ReadLocalList(string settingsDirectory)
        {
            try
            {
                string path = PeopleConfig.ConfigPath(settingsDirectory);
                return File.Exists(path) ? PeopleConfig.ReadTextRobust(path) : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ 包格式

        /// <summary>解出来的包内容。</summary>
        internal sealed class PackageContents
        {
            /// <summary>包里的密钥（32 字节）——解 <c>signatures/*</c> 的信封用它。</summary>
            public byte[] Key;

            /// <summary>条目名 → 内容字节。</summary>
            public Dictionary<string, byte[]> Entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            /// <summary>笔迹条目数。</summary>
            public int SignatureCount
            {
                get
                {
                    int count = 0;
                    foreach (string name in Entries.Keys)
                        if (name.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase)) count++;
                    return count;
                }
            }

            /// <summary>取一个文本条目（UTF-8）；没有 → null。</summary>
            public string Text(string entryName)
            {
                byte[] data;
                if (!Entries.TryGetValue(entryName, out data)) return null;
                return Encoding.UTF8.GetString(data);
            }
        }

        /// <summary>写一个配置包。</summary>
        public static void Write(string path, byte[] key, Dictionary<string, byte[]> entries)
        {
            if (key == null || key.Length != KeySize) throw new ArgumentException("包的密钥必须是 32 字节");
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);

            byte[] payload = PackEntries(entries);
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.GenerateIV();
                using (ICryptoTransform enc = aes.CreateEncryptor())
                {
                    byte[] cipher = enc.TransformFinalBlock(payload, 0, payload.Length);
                    byte[] result = new byte[Magic.Length + KeySize + IvSize + cipher.Length];
                    int at = 0;
                    Buffer.BlockCopy(Magic, 0, result, at, Magic.Length); at += Magic.Length;
                    Buffer.BlockCopy(key, 0, result, at, KeySize); at += KeySize;
                    Buffer.BlockCopy(aes.IV, 0, result, at, IvSize); at += IvSize;
                    Buffer.BlockCopy(cipher, 0, result, at, cipher.Length);
                    WriteNormal(path, result);
                }
            }
        }

        /// <summary>读一个配置包；不是本格式 / 被改坏 → 抛（调用方负责给出人话提示）。</summary>
        public static PackageContents Read(string path)
        {
            byte[] all = File.ReadAllBytes(path);
            int head = Magic.Length + KeySize + IvSize;
            if (all.Length < head + 16) throw new InvalidDataException("文件太小，不是人员设置包");
            for (int i = 0; i < Magic.Length; i++)
                if (all[i] != Magic[i]) throw new InvalidDataException("文件头不对，不是人员设置包");

            byte[] key = new byte[KeySize];
            Buffer.BlockCopy(all, Magic.Length, key, 0, KeySize);
            byte[] iv = new byte[IvSize];
            Buffer.BlockCopy(all, Magic.Length + KeySize, iv, 0, IvSize);

            byte[] plain;
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
                        plain = dec.TransformFinalBlock(all, head, all.Length - head);
                }
                catch (CryptographicException ex)
                {
                    throw new InvalidDataException("内容损坏或不是人员设置包", ex);
                }
            }
            return new PackageContents { Key = key, Entries = UnpackEntries(plain) };
        }

        /// <summary>覆盖写一个普通文件：先摘掉隐藏属性，否则 Windows 会拒绝（本机系统级行为）。</summary>
        private static void WriteNormal(string path, byte[] content)
        {
            try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); }
            catch (System.Exception) { }
            File.WriteAllBytes(path, content);
        }

        private static byte[] PackEntries(Dictionary<string, byte[]> entries)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(entries == null ? 0 : entries.Count);
                if (entries != null)
                {
                    foreach (KeyValuePair<string, byte[]> pair in entries)
                    {
                        byte[] name = Encoding.UTF8.GetBytes(pair.Key);
                        byte[] data = pair.Value ?? new byte[0];
                        writer.Write(name.Length); writer.Write(name);
                        writer.Write(data.Length); writer.Write(data);
                    }
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static Dictionary<string, byte[]> UnpackEntries(byte[] payload)
        {
            Dictionary<string, byte[]> entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using (MemoryStream stream = new MemoryStream(payload))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                int count = reader.ReadInt32();
                for (int i = 0; i < count && stream.Position < stream.Length; i++)
                {
                    int nameLength = reader.ReadInt32();
                    string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
                    int dataLength = reader.ReadInt32();
                    entries[name] = reader.ReadBytes(dataLength);
                }
            }
            return entries;
        }
    }
}
