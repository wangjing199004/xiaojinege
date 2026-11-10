using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace GreenTest
{
    /// <summary>
    /// 脱离 AutoCAD 实测「单文件绿色版」的加密缓存流程：
    /// 直接反射调用真实绿色版 DLL 里的 CadSignature.GreenStore.Unlock / Seal，
    /// 再拿装机包原件逐字节回比，并检查落盘的东西到底是不是密文。
    /// GreenStore / GreenArchive 都只用 BCL，不需要 CAD。
    ///
    /// 5.25.0 起**同一个测试程序跑两种 DLL**，靠 DLL 里有没有内嵌资源自动切换：
    ///   * 干净版（出厂默认，GreenIncludeAll=false）—— 一个字节配置都不带，
    ///     首次解锁只建出空的工作目录，核心回内置默认名单（张三/李四…）；
    ///   * 全内嵌版（-p:GreenIncludeAll=true）—— 配置 + 加密素材库都烤进去，
    ///     首次解锁摊开的内容要和装机包原件逐字节一致。
    /// </summary>
    internal static class Program
    {
        private static int _pass, _fail;
        private static bool _full;

        private static MethodInfo _unlock, _seal;
        private static PropertyInfo _notice;

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            try { return Run(args); }
            catch (System.Exception error)
            {
                // 半路抛异常时（比如工作目录没建出来，后面写文件就炸）也要把话说明白，
                // 不要只留一个没有上下文的退出码。真正的线索仍然是上面的 [FAIL] 行。
                Console.WriteLine();
                Console.WriteLine("[EXCEPTION] 测试中途抛异常：");
                Console.WriteLine("  " + error.GetType().Name + ": " + error.Message);
                Console.WriteLine();
                Console.WriteLine("===== 通过 " + _pass + " 项，失败 " + _fail + " 项（未跑完）=====");
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("用法: greentest <绿色版DLL> <装机包根> <工作目录>");
                return 2;
            }
            string greenDll = args[0];
            string bundle = args[1];
            string work = args[2];

            string configDir = Path.Combine(bundle, "Contents", "Windows");
            string vaultDir = Path.Combine(bundle, "Signatures", ".db");

            Console.WriteLine("绿色版 DLL : " + greenDll);
            Console.WriteLine("装机包     : " + bundle);
            Console.WriteLine("工作目录   : " + work);

            Assembly green = Assembly.LoadFrom(greenDll);
            _full = HasEmbeddedContent(green);
            Console.WriteLine("内嵌内容   : " + (_full ? "有（全内嵌版：配置 + 素材库）" : "无（干净版：出厂默认，不带任何配置）"));
            Console.WriteLine();

            Type store = green.GetType("CadSignature.GreenStore", true);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _unlock = store.GetMethod("Unlock", flags, null, new[] { typeof(string) }, null);
            _seal = store.GetMethod("Seal", flags, null, new[] { typeof(string) }, null);
            _notice = store.GetProperty("LastNotice", flags);
            if (_unlock == null || _seal == null || _notice == null)
            {
                Console.WriteLine("GreenStore 的 Unlock/Seal/LastNotice 对不上，绿色版 DLL 是不是旧版？");
                return 2;
            }

            string sessionName = Constant(store, "SessionFolderName", flags);
            string cacheName = Constant(store, "CacheFileName", flags);
            string keyName = Constant(store, "KeyFileName", flags);

            string session = Path.Combine(work, sessionName);
            string cache = Path.Combine(work, cacheName);
            string key = Path.Combine(work, keyName);

            string[] sourceVault = Directory.Exists(vaultDir) ? Directory.GetFiles(vaultDir) : new string[0];

            // ---------------------------------------------------------- 0. DLL 本身
            Console.WriteLine("== 0. DLL 文件本身 ==");
            byte[] dllBytes = File.ReadAllBytes(greenDll);
            bool anyNameInDll = false;
            foreach (string name in RealNames(configDir)) if (Contains(dllBytes, name)) anyNameInDll = true;
            bool anyConfigRes = false;
            foreach (string r in green.GetManifestResourceNames())
                if (r.StartsWith("CadSignature.Embedded.Config.", StringComparison.Ordinal)) anyConfigRes = true;
            if (_full)
            {
                CheckTrue("内嵌了人员名单资源", anyConfigRes);
                // 全内嵌版把明文 人员名单.txt 也烤进去了 —— 这正是它**不能发出去**的原因，
                // 所以这里反过来断言"看得见"，免得哪天误以为它也是干净的。
                CheckTrue("DLL 里看得到明文姓名（全内嵌版就是这个代价，**别发给别人**）", anyNameInDll);
            }
            else
            {
                CheckTrue("**没有**内嵌任何配置文件资源（干净版）", !anyConfigRes);
                CheckTrue("DLL 里搜不到任何真实姓名字节（干净版可以直接发给别人）", !anyNameInDll);
            }

            // ---------------------------------------------------------- 1. 首次解锁
            Console.WriteLine();
            Console.WriteLine("== 1. 首次解锁（无缓存）==");
            string up = Unlock(work);
            CheckTrue("返回的是工作目录 " + sessionName, string.Equals(up, session, StringComparison.OrdinalIgnoreCase));
            CheckTrue("工作目录建出来了", Directory.Exists(session));
            CheckTrue("工作目录是隐藏的", HasHidden(session));
            if (_full)
            {
                CheckTrue("生成 人员名单.txt", File.Exists(Path.Combine(session, "人员名单.txt")));
                CheckTrue("生成 图框定位.txt", File.Exists(Path.Combine(session, "图框定位.txt")));
                CheckTrue("未生成 签名文件夹.txt（装机包本来没有）", !File.Exists(Path.Combine(session, "签名文件夹.txt")));
                int vaultCount = CountFiles(Path.Combine(session, "Signatures", ".db"));
                CheckTrue("素材库 " + sourceVault.Length + " 个文件全摊开（实际 " + vaultCount + "）", vaultCount == sourceVault.Length);
                CheckTrue("摊开内容与装机包原件逐字节一致",
                          SameBytes(Path.Combine(configDir, "人员名单.txt"), Path.Combine(session, "人员名单.txt")) &&
                          SameBytes(Path.Combine(configDir, "图框定位.txt"), Path.Combine(session, "图框定位.txt")) &&
                          AllVaultSame(vaultDir, Path.Combine(session, "Signatures", ".db")));
            }
            else
            {
                CheckTrue("干净版：一个文件都没摊开（人员名单.txt 不存在 → 核心回内置默认张三/李四…）",
                          !File.Exists(Path.Combine(session, "人员名单.txt")));
                CheckTrue("干净版：没有 图框定位.txt（图幅标定从零开始）",
                          !File.Exists(Path.Combine(session, "图框定位.txt")));
                CheckTrue("干净版：没有 Signatures 文件夹（笔迹库为空）",
                          !Directory.Exists(Path.Combine(session, "Signatures")));
                CheckTrue("干净版：工作目录里确实空无一物",
                          Directory.GetFileSystemEntries(session).Length == 0);
            }

            // ---------------------------------------------------------- 2. 封存
            Console.WriteLine();
            Console.WriteLine("== 2. 封存：写回加密缓存 + 删掉明文 ==");
            Seal(work);
            CheckTrue("生成 " + cacheName, File.Exists(cache));
            CheckTrue("生成 " + keyName, File.Exists(key));
            CheckTrue("明文工作目录已删除", !Directory.Exists(session));
            CheckTrue(cacheName + " 是隐藏的", HasHidden(cache));
            CheckTrue(keyName + " 是隐藏的", HasHidden(key));
            CheckTrue("密钥正好 32 字节", new FileInfo(key).Length == 32);

            List<string> leftovers = PlainLookingEntries(work);
            CheckTrue("DLL 旁边**没有明文残留**（实际：" + (leftovers.Count == 0 ? "无" : string.Join(", ", leftovers.ToArray())) + "）",
                      leftovers.Count == 0);

            // ---------------------------------------------------------- 3. 缓存确实是密文
            Console.WriteLine();
            Console.WriteLine("== 3. 缓存里搜不到明文 ==");
            byte[] cacheBytes = File.ReadAllBytes(cache);
            CheckTrue("找不到「项目负责人」（UTF-8）", !Contains(cacheBytes, "项目负责人"));
            CheckTrue("找不到「人员名单」（UTF-8）", !Contains(cacheBytes, "人员名单"));
            bool anyRealName = false;
            foreach (string name in RealNames(configDir))
                if (Contains(cacheBytes, name)) anyRealName = true;
            CheckTrue("找不到任何真实姓名字节", !anyRealName);
            CheckTrue("找不到 DWG 文件头（素材库也加密了）", !Contains(cacheBytes, "AC10"));
            CheckTrue("缓存头是 CDCACHE1 魔数", Contains(cacheBytes, "CDCACHE1"));

            // ---------------------------------------------------------- 4. 二次解锁
            Console.WriteLine();
            Console.WriteLine("== 4. 再解锁：内容原样回来 ==");
            Unlock(work);
            CheckTrue("工作目录回来了", Directory.Exists(session));
            if (_full)
            {
                CheckTrue("人员名单.txt 回到工作目录", File.Exists(Path.Combine(session, "人员名单.txt")));
                CheckTrue("内容与装机包原件一致",
                          SameBytes(Path.Combine(configDir, "人员名单.txt"), Path.Combine(session, "人员名单.txt")));
                CheckTrue("素材库 " + sourceVault.Length + " 个文件全回来", CountFiles(Path.Combine(session, "Signatures", ".db")) == sourceVault.Length);
            }
            else
            {
                CheckTrue("干净版：解出来仍然是空的（核心继续用内置默认名单）",
                          !File.Exists(Path.Combine(session, "人员名单.txt")));
            }

            // ---------------------------------------------------------- 5. 改动能持久
            Console.WriteLine();
            Console.WriteLine("== 5. 改动（改名单 / 加一条标定）==");
            File.WriteAllText(Path.Combine(session, "人员名单.txt"), "# 改过的\r\n设计=测试员|测试员.dwg\r\n", System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(session, "图框定位.txt"), "A1@RB=1,2,3,4,5\r\n", System.Text.Encoding.UTF8);
            Seal(work);
            Unlock(work);
            CheckTrue("改过的名单还在", File.ReadAllText(Path.Combine(session, "人员名单.txt")).Contains("测试员"));
            CheckTrue("改过的标定还在", File.ReadAllText(Path.Combine(session, "图框定位.txt")).Contains("A1@RB=1,2,3,4,5"));
            Seal(work);

            // ---------------------------------------------------------- 6. 密钥随包走
            Console.WriteLine();
            Console.WriteLine("== 6. 换一台电脑：拷 cache+key 即可接着用 ==");
            string moved = work + "-moved";
            if (Directory.Exists(moved)) Directory.Delete(moved, true);
            Directory.CreateDirectory(moved);
            File.Copy(cache, Path.Combine(moved, cacheName), true);
            File.SetAttributes(Path.Combine(moved, cacheName), FileAttributes.Normal);
            File.Copy(key, Path.Combine(moved, keyName), true);
            Unlock(moved);
            CheckTrue("新目录里解出来的名单含刚才的改动",
                      File.ReadAllText(Path.Combine(moved, sessionName, "人员名单.txt")).Contains("测试员"));
            if (_full)
                CheckTrue("新目录里素材库也在", CountFiles(Path.Combine(moved, sessionName, "Signatures", ".db")) > 0);
            Seal(moved);
            Directory.Delete(moved, true);

            // ---------------------------------------------------------- 7. 密钥丢了
            Console.WriteLine();
            Console.WriteLine("== 7. 密钥被删：不覆盖缓存，改名留证后重来 ==");
            File.SetAttributes(key, FileAttributes.Normal);
            File.Delete(key);
            Unlock(work);
            CheckTrue("工作目录仍然可用", Directory.Exists(session));
            if (_full) CheckTrue("能用 DLL 自带内容继续", File.Exists(Path.Combine(session, "人员名单.txt")));
            CheckTrue("原缓存被改名留证（没被覆盖）", File.Exists(cache + ".bak"));
            CheckTrue("有提示语告诉用户", !string.IsNullOrEmpty(Notice()));
            Seal(work);

            // ---------------------------------------------------------- 8. 缓存坏了
            Console.WriteLine();
            Console.WriteLine("== 8. 缓存被改坏：同样留证重来 ==");
            byte[] broken = File.ReadAllBytes(cache);
            broken[broken.Length - 5] ^= 0xFF;
            File.SetAttributes(cache, FileAttributes.Normal);
            File.WriteAllBytes(cache, broken);
            Unlock(work);
            CheckTrue("工作目录仍然可用", Directory.Exists(session));
            if (_full) CheckTrue("能用 DLL 自带内容继续", File.Exists(Path.Combine(session, "人员名单.txt")));
            CheckTrue("坏缓存改名留证", File.Exists(cache + ".bak"));
            CheckTrue("有提示语告诉用户", !string.IsNullOrEmpty(Notice()));
            Seal(work);

            // ---------------------------------------------------------- 9. 残留工作目录
            Console.WriteLine();
            Console.WriteLine("== 9. 上次没收尾留下的 .session 优先级最高 ==");
            string stale = Path.Combine(session, "人员名单.txt");
            Directory.CreateDirectory(session);
            File.WriteAllText(stale, "残留标记=1\r\n", System.Text.Encoding.UTF8);
            Unlock(work);
            CheckTrue("沿用了残留工作目录", File.ReadAllText(stale).Contains("残留标记"));
            Seal(work);

            // ---------------------------------------------------------- 10. 异常路径
            Console.WriteLine();
            Console.WriteLine("== 10. 异常路径不抛 ==");
            CheckTrue("传空串不抛", TryUnlock(""));
            CheckTrue("传不存在的盘符不抛", TryUnlock(@"Z:\definitely\not\here"));

            Console.WriteLine();
            Console.WriteLine("===== 通过 " + _pass + " 项，失败 " + _fail + " 项 =====");
            return _fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ 反射小工具

        /// <summary>这个 DLL 里有没有内嵌的配置/素材库 —— 有 = 全内嵌版，没有 = 干净版。</summary>
        private static bool HasEmbeddedContent(Assembly assembly)
        {
            foreach (string resource in assembly.GetManifestResourceNames())
                if (resource.StartsWith("CadSignature.Embedded.", StringComparison.Ordinal)) return true;
            return false;
        }

        private static string Constant(Type type, string name, BindingFlags flags)
        {
            FieldInfo field = type.GetField(name, flags | BindingFlags.FlattenHierarchy);
            // 用 GetValue 而不是 GetRawConstantValue：GreenStore 的 CacheFileName / KeyFileName
            // 自 5.25.1 起是 static readonly（名字跟着 DLL 文件名走，见 SelfBaseName），
            // raw 常量读法对静态字段直接抛 InvalidOperationException。
            return (string)field.GetValue(null);
        }

        private static string Unlock(string dir) { return (string)_unlock.Invoke(null, new object[] { dir }); }
        private static void Seal(string dir) { _seal.Invoke(null, new object[] { dir }); }
        private static string Notice() { return (string)_notice.GetValue(null, null); }

        private static bool TryUnlock(string dir)
        {
            try { _unlock.Invoke(null, new object[] { dir }); return true; }
            catch (TargetInvocationException error)
            {
                if (error.InnerException != null) Console.WriteLine("        （内层异常：" + error.InnerException.Message + "）");
                return false;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ 断言

        private static bool SameBytes(string a, string b)
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            byte[] x = File.ReadAllBytes(a), y = File.ReadAllBytes(b);
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        private static bool AllVaultSame(string from, string to)
        {
            foreach (string file in Directory.GetFiles(from))
                if (!SameBytes(file, Path.Combine(to, Path.GetFileName(file)))) return false;
            return true;
        }

        private static bool Contains(byte[] haystack, string text)
        {
            byte[] needle = System.Text.Encoding.UTF8.GetBytes(text);
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        private static int CountFiles(string folder)
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0;
        }

        private static bool HasHidden(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.Hidden) != 0; }
            catch { return false; }
        }

        /// <summary>工作目录下次级里"看起来是明文"的项（插件自己生成的 CadSignature.* 与 .session 除外）。</summary>
        private static List<string> PlainLookingEntries(string root)
        {
            List<string> found = new List<string>();
            if (!Directory.Exists(root)) return found;
            foreach (string entry in Directory.GetFileSystemEntries(root))
            {
                string name = Path.GetFileName(entry);
                // 允许的两类：加密缓存与它的密钥。名字 = 「DLL 文件名 + .cache / .key」，
                // 多版本绿色版下叫 CadSignature-ACAD2024.cache —— 别写死 "CadSignature." 前缀
                // （5.25.1 起缓存改跟着 DLL 文件名走，写死前缀会把新名字误判成明文残留）。
                string ext = Path.GetExtension(name);
                if (ext.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".key", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(".session", StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(name);
            }
            return found;
        }

        /// <summary>从装机包的人员名单里抠出真实姓名，用来验证 DLL / 缓存里搜不到它们。</summary>
        private static List<string> RealNames(string configDir)
        {
            List<string> names = new List<string>();
            string path = Path.Combine(configDir, "人员名单.txt");
            if (!File.Exists(path)) return names;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string value = line.Substring(equals + 1).Trim();
                int bar = value.IndexOf('|');
                string name = (bar >= 0 ? value.Substring(0, bar) : value).Trim();
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }

        private static void CheckTrue(string what, bool ok)
        {
            if (ok) { _pass++; Console.WriteLine("  [OK]   " + what); }
            else { _fail++; Console.WriteLine("  [FAIL] " + what); }
        }
    }
}
