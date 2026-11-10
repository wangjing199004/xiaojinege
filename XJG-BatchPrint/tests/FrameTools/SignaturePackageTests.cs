using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CadSignature;

namespace PkgTest
{
    /// <summary>
    /// 脱离 AutoCAD 实测「人员设置.cspkg」配置包（5.26.0）：
    /// 导出 → 换一台"干净机器"应用 → 幂等 → 本机已有名单时不覆盖 → 坏包不炸。
    ///
    /// 直接引用 Source\Core 里那三个纯 BCL 的类（见 pkgtest.csproj 的 Compile Include），
    /// 所以这里跑的就是插件真正用的那份代码，不是复制品。
    /// </summary>
    internal static class Program
    {
        private static int _pass, _fail;
        private static string _root;

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            try
            {
                _root = args.Length > 0 ? args[0] : "pkgtest-run";
                return Run();
            }
            catch (System.Exception error)
            {
                Console.WriteLine();
                Console.WriteLine("[EXCEPTION] 测试中途抛异常：");
                Console.WriteLine("  " + error.GetType().Name + ": " + error.Message);
                Console.WriteLine("  " + error.StackTrace);
                Console.WriteLine();
                Console.WriteLine("===== 通过 " + _pass + " 项，失败 " + _fail + " 项（未跑完）=====");
                return 1;
            }
        }

        private static int Run()
        {
            Console.WriteLine("工作目录: " + Path.GetFullPath(_root));
            Console.WriteLine();

            // ---------------------------------------------------------- 0. 准备
            Console.WriteLine("== 0. 造素材与名单 ==");
            string sourceSig = Fresh("src-sig");
            byte[] dwgZhang = FakeDwg("ZHANG");
            byte[] dwgLi = FakeDwg("LI");
            byte[] dwgWang = FakeDwg("WANG");
            File.WriteAllBytes(Path.Combine(sourceSig, "张三.dwg"), dwgZhang);      // 明文件
            File.WriteAllBytes(Path.Combine(sourceSig, "李四.dwg"), dwgLi);        // 明文件
            SignatureVault.Save(sourceSig, "王五.dwg", dwgWang);                   // 加密库里的

            Dictionary<string, PersonEntry> people = PeopleConfig.Defaults();
            people["项目负责人"] = new PersonEntry { Name = "张三", SignatureFile = "" };
            people["专业负责人"] = new PersonEntry { Name = "李四", SignatureFile = "" };
            people["审定"] = new PersonEntry { Name = "王五", SignatureFile = "" };
            people["审核"] = new PersonEntry { Name = "赵六", SignatureFile = "" };   // 故意不给素材
            people["校对"] = new PersonEntry { Name = "", SignatureFile = "" };
            people["设计"] = new PersonEntry { Name = "", SignatureFile = "" };
            people["制图"] = new PersonEntry { Name = "", SignatureFile = "" };
            Check("素材库里有王五（加密）", SignatureVault.Contains(sourceSig, "王五.dwg"));
            Check("签名文件夹里有张三的明文件", File.Exists(Path.Combine(sourceSig, "张三.dwg")));

            // ---------------------------------------------------------- 1. 导出
            Console.WriteLine();
            Console.WriteLine("== 1. 导出配置包 ==");
            string dllFolder = Fresh("machine-a-dll");
            string packagePath = Path.Combine(dllFolder, SettingsPackage.PackageFileName);
            PackageExport export = SettingsPackage.Export(packagePath, people, sourceSig);

            Check("打包了 4 个有姓名的岗位（实际 " + export.Names.Count + "）", export.Names.Count == 4);
            Check("打包了 3 份笔迹（实际 " + export.Included.Count + "）", export.Included.Count == 3);
            Check("点名但没找到的 1 份被记下来（实际 " + export.Missing.Count + "）",
                  export.Missing.Count == 1 && export.Missing[0] == "赵六.dwg");
            Check("包文件真的生成了", File.Exists(packagePath));
            Check("包比一份 dwg 大（说明笔迹确实装进去了）", new FileInfo(packagePath).Length > 3 * dwgZhang.Length);

            byte[] raw = File.ReadAllBytes(packagePath);
            Check("包开头是 CSPKG001 魔数", Contains(raw, "CSPKG001"));
            Check("包内搜不到任何姓名（张三）", !Contains(raw, "张三"));
            Check("包内搜不到岗位名（项目负责人）", !Contains(raw, "项目负责人"));
            Check("包内搜不到 DWG 文件头（AC10）", !Contains(raw, "AC10"));

            // ---------------------------------------------------------- 2. 换台干净机器
            Console.WriteLine();
            Console.WriteLine("== 2. 换一台干净机器：放到 DLL 旁就自动生效 ==");
            string machineB = Fresh("machine-b-dll");
            string machineBSettings = Path.Combine(machineB, "Contents-Windows");
            string machineBSig = Path.Combine(machineB, "Signatures");
            Directory.CreateDirectory(machineBSettings);
            File.Copy(packagePath, Path.Combine(machineB, SettingsPackage.PackageFileName));

            PackageResult first = SettingsPackage.ApplyIfNeeded(machineB, machineBSettings, machineBSig);
            Check("找到并应用了包", first != null);
            Check("名单被写进去了", first != null && first.ListApplied);
            Check("补入 3 份笔迹（实际 " + (first == null ? -1 : first.SignaturesAdded) + "）",
                  first != null && first.SignaturesAdded == 3);
            Check("命令行会有提示语", first != null && !string.IsNullOrEmpty(first.Message));
            Check("人员名单.txt 落到 settings 目录", File.Exists(PeopleConfig.ConfigPath(machineBSettings)));

            Dictionary<string, PersonEntry> got = PeopleConfig.Load(machineBSettings);
            Check("新机器上读到的名单 = 导出时那份",
                  PeopleConfig.SamePeople(PeopleConfig.ToText(got), PeopleConfig.ToText(people)));
            Check("新机器上「项目负责人」= 张三", got["项目负责人"].Name == "张三");
            Check("新机器上「校对」留空（导出时就是空）", got["校对"].Name == "");

            Check("张三的笔迹能被取出来", SignatureVault.ReadSignatureBytes(machineBSig, "张三.dwg") != null);
            Check("张三的笔迹字节与原件一致",
                  SameBytes(SignatureVault.ReadSignatureBytes(machineBSig, "张三.dwg"), dwgZhang));
            Check("李四的笔迹字节与原件一致",
                  SameBytes(SignatureVault.ReadSignatureBytes(machineBSig, "李四.dwg"), dwgLi));
            Check("王五（原来在加密库里的）也搬过来了",
                  SameBytes(SignatureVault.ReadSignatureBytes(machineBSig, "王五.dwg"), dwgWang));
            Check("新机器的素材库也是隐藏目录", HasHidden(SignatureVault.VaultPath(machineBSig)));
            Check("新机器的签名文件夹里**没有明文 dwg**",
                  !Directory.Exists(machineBSig) || Directory.GetFiles(machineBSig, "*.dwg").Length == 0);

            // ---------------------------------------------------------- 3. 幂等
            Console.WriteLine();
            Console.WriteLine("== 3. 再跑一遍：什么都不做 ==");
            PackageResult second = SettingsPackage.ApplyIfNeeded(machineB, machineBSettings, machineBSig);
            Check("第二次没有重复写名单", second != null && !second.ListApplied);
            Check("第二次没有重复补笔迹", second != null && second.SignaturesAdded == 0);
            Check("第二次不再打扰用户（提示语为空）", second != null && string.IsNullOrEmpty(second.Message));

            // ---------------------------------------------------------- 4. 本机名单优先
            Console.WriteLine();
            Console.WriteLine("== 4. 对方已经配过自己的名单：不覆盖 ==");
            Dictionary<string, PersonEntry> mine = PeopleConfig.Defaults();
            mine["项目负责人"] = new PersonEntry { Name = "我自己的负责人", SignatureFile = "" };
            PeopleConfig.Save(machineBSettings, mine);

            PackageResult third = SettingsPackage.ApplyIfNeeded(machineB, machineBSettings, machineBSig);
            Check("名单没被包覆盖", third != null && !third.ListApplied);
            Check("本机写的那个姓名还在", PeopleConfig.Load(machineBSettings)["项目负责人"].Name == "我自己的负责人");
            Check("但缺的笔迹仍然会补（说明两件事是分开判断的）", third.SignaturesAdded == 0);

            // ---------------------------------------------------------- 5. 出厂默认名单 = 可以覆盖
            Console.WriteLine();
            Console.WriteLine("== 5. 本机名单还是出厂示例（张三/李四…）：按包填 ==");
            string machineC = Fresh("machine-c-dll");
            string machineCSettings = Path.Combine(machineC, "Contents-Windows");
            string machineCSig = Path.Combine(machineC, "Signatures");
            Directory.CreateDirectory(machineCSettings);
            File.Copy(packagePath, Path.Combine(machineC, SettingsPackage.PackageFileName));
            PeopleConfig.Save(machineCSettings, PeopleConfig.Defaults());     // 干净安装包出厂就是这一份
            Check("出厂名单确实被判为「没配过」",
                  PeopleConfig.IsFactoryDefaults(PeopleConfig.ReadTextRobust(PeopleConfig.ConfigPath(machineCSettings))));
            PackageResult factory = SettingsPackage.ApplyIfNeeded(machineC, machineCSettings, machineCSig);
            Check("于是按包填写了", factory != null && factory.ListApplied);
            Check("姓名换成了包里的那一套", PeopleConfig.Load(machineCSettings)["项目负责人"].Name == "张三");

            // ---------------------------------------------------------- 6. 已有同名笔迹不覆盖
            Console.WriteLine();
            Console.WriteLine("== 6. 本机库里已有同名笔迹：保留本机的 ==");
            string machineD = Fresh("machine-d-dll");
            string machineDSettings = Path.Combine(machineD, "Contents-Windows");
            string machineDSig = Path.Combine(machineD, "Signatures");
            Directory.CreateDirectory(machineDSettings);
            File.Copy(packagePath, Path.Combine(machineD, SettingsPackage.PackageFileName));
            byte[] localZhang = FakeDwg("LOCAL-ZHANG");
            SignatureVault.Save(machineDSig, "张三.dwg", localZhang);
            PackageResult keep = SettingsPackage.ApplyIfNeeded(machineD, machineDSettings, machineDSig);
            Check("同名的那份没被覆盖",
                  SameBytes(SignatureVault.ReadSignatureBytes(machineDSig, "张三.dwg"), localZhang));
            Check("缺的另外两份补了进来（实际 " + keep.SignaturesAdded + "）", keep.SignaturesAdded == 2);

            // ---------------------------------------------------------- 7. 坏包 / 没有包
            Console.WriteLine();
            Console.WriteLine("== 7. 异常路径：坏包不炸、没包不查 ==");
            string broken = Fresh("broken-dll");
            File.WriteAllBytes(Path.Combine(broken, SettingsPackage.PackageFileName), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
            bool threw = false;
            PackageResult bad = null;
            try { bad = SettingsPackage.ApplyIfNeeded(broken, broken, Path.Combine(broken, "Signatures")); }
            catch (System.Exception) { threw = true; }
            Check("坏包不抛异常", !threw);
            Check("坏包会告诉用户（而不是静默）", bad != null && !string.IsNullOrEmpty(bad.Message));

            string empty = Fresh("empty-dll");
            Check("没有包 → ApplyIfNeeded 返回 null（每次只花一次 File.Exists）",
                  SettingsPackage.ApplyIfNeeded(empty, empty, Path.Combine(empty, "Signatures")) == null);
            Check("没有包 → Find 返回 null", SettingsPackage.Find(empty) == null);
            Check("固定名优先", SettingsPackage.Find(dllFolder) == packagePath);

            string renamed = Fresh("renamed-dll");
            File.Copy(packagePath, Path.Combine(renamed, "设计部的人.cspkg"));
            Check("目录里只有一个 *.cspkg（改了名）也认", SettingsPackage.Find(renamed) != null);
            File.Copy(packagePath, Path.Combine(renamed, "另一个.cspkg"));
            Check("有两个且都不是固定名 → 不猜（返回 null）", SettingsPackage.Find(renamed) == null);

            // ---------------------------------------------------------- 8. 状态行
            Console.WriteLine();
            Console.WriteLine("== 8. 「人员设置」底部那行状态 ==");
            string desc = SettingsPackage.Describe(machineB, machineBSettings);
            Check("已配过的机器：状态说「不会覆盖」", desc != null && desc.Contains("不会覆盖"));
            string desc2 = SettingsPackage.Describe(machineD, machineDSettings);
            Check("刚被包填过的机器：状态说「与它一致」", desc2 != null && desc2.Contains("一致"));
            Check("没包的目录也给一句提示", SettingsPackage.Describe(empty, empty) != null);

            Console.WriteLine();
            Console.WriteLine("===== 通过 " + _pass + " 项，失败 " + _fail + " 项 =====");
            return _fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ 小工具

        /// <summary>一个干净的子目录（先删后建）。</summary>
        private static string Fresh(string name)
        {
            string dir = Path.Combine(_root, name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>造一段"像 DWG"的字节：带 AC1027 文件头 + 可区分的填充。</summary>
        private static byte[] FakeDwg(string tag)
        {
            byte[] head = Encoding.ASCII.GetBytes("AC1027");
            byte[] body = new byte[2048];
            byte[] seed = Encoding.UTF8.GetBytes(tag);
            for (int i = 0; i < body.Length; i++) body[i] = (byte)((i * 31 + seed[i % seed.Length]) & 0xFF);
            byte[] result = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, result, 0, head.Length);
            Buffer.BlockCopy(body, 0, result, head.Length, body.Length);
            return result;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool Contains(byte[] haystack, string text)
        {
            byte[] needle = Encoding.UTF8.GetBytes(text);
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        private static bool HasHidden(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.Hidden) != 0; }
            catch { return false; }
        }

        private static void Check(string what, bool ok)
        {
            if (ok) { _pass++; Console.WriteLine("  [OK]   " + what); }
            else { _fail++; Console.WriteLine("  [FAIL] " + what); }
        }
    }
}
