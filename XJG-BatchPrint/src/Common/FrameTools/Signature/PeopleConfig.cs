#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CadSignature
{
    /// <summary>一个岗位的人员参数：签什么姓名、用哪份签名笔迹文件。</summary>
    internal class PersonEntry
    {
        /// <summary>姓名；留空表示该岗位不签名。</summary>
        public string Name = "";

        /// <summary>签名笔迹文件名（可选）。留空 = 自动用 Signatures 文件夹里的「姓名.dwg」。</summary>
        public string SignatureFile = "";
    }

    /// <summary>
    /// 人员配置（5.9.0）：把「岗位 → 姓名 → 签名笔迹」从代码里抽出来，改成可修改的参数。
    ///
    /// 配置文件「人员名单.txt」与核心 DLL 同目录（装机包里就是 Contents\Windows），
    /// 一键编译脚本只覆盖 DLL，不会碰这个文件，所以改名单不需要重新编译。
    /// 文件不存在时用内置默认名单 —— 行为与 5.8.0 完全一致。
    ///
    /// 文件格式（UTF-8，# 开头是注释）：
    ///   岗位=姓名                  —— 正常写法
    ///   岗位=姓名|笔迹文件.dwg      —— 指定签名笔迹文件（相对 Signatures 文件夹）
    ///   岗位=                      —— 该岗位不签名
    ///
    /// 面板「人员设置」保存后立即生效：下一次「一键签名」就按新名单执行，
    /// 不需要重新编译，也不需要重启 AutoCAD。
    ///
    /// 5.26.0：解析与序列化抽成 <see cref="Parse"/> / <see cref="ToText"/>，
    /// 让「人员设置.cspkg」配置包（<see cref="SettingsPackage"/>）复用同一套格式，
    /// 不必再写第二份解析代码（两份迟早会走偏）。
    /// </summary>
    internal static class PeopleConfig
    {
        /// <summary>配置文件名（存放在核心 DLL 同目录）。</summary>
        public const string FileName = "人员名单.txt";

        /// <summary>签字栏 7 行的岗位名，顺序即从上到下。
        /// 岗位名要和定位点 XData 里存的岗位对得上，所以固定不动；换人改「姓名」即可。</summary>
        public static readonly string[] Roles =
        {
            "项目负责人", "专业负责人", "审定", "审核", "校对", "设计", "制图"
        };

        /// <summary>配置文件完整路径。参数可以是**目录**（核心 DLL 所在目录，常规用法），
        /// 也可以直接是**配置文件本身的完整路径** —— 后者是 5.13.3 补的容错：
        /// <c>Load</c>/<c>Save</c> 的参数语义是「目录」，但调用方很容易顺手把
        /// <c>ConfigPath()</c> 的结果再传回来（那样会在文件名后面又拼一次文件名，
        /// 变成 <c>…\人员名单.txt\人员名单.txt</c>，文件永远找不到、静默回退默认名单）。
        /// 这里识别出「已经指向本文件」就直接返回，两个语义都成立。
        ///
        /// 5.13.2 起保证是**绝对路径**：directory 为空或是相对路径时，按当前工作目录补全，
        /// 避免把名单悄悄写到 AutoCAD 的启动目录里（那样用户根本找不到这个文件）。</summary>
        public static string ConfigPath(string directory)
        {
            string folder = directory == null ? "" : directory.Trim();
            if (folder.Length == 0) folder = ".";

            // 已经是本配置文件本身 → 直接用（不要再拼一次文件名）
            string name = Path.GetFileName(folder);
            if (string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
            {
                try { return Path.GetFullPath(folder); }
                catch (System.Exception) { return folder; }
            }

            string path = Path.Combine(folder, FileName);
            try { return Path.GetFullPath(path); }
            catch (System.Exception) { return path; }
        }

        /// <summary>读取人员配置；文件不存在、某行写错或岗位名对不上时，该岗位回退到内置默认值。</summary>
        public static Dictionary<string, PersonEntry> Load(string directory)
        {
            string path = ConfigPath(directory);
            if (!File.Exists(path)) return Defaults();
            return Parse(ReadTextRobust(path));
        }

        /// <summary>把一段「人员名单.txt」正文解析成人员配置（5.26.0 从 <see cref="Load"/> 抽出）。
        /// 基准是内置默认名单：没写到的岗位保持默认值，写错的行按「不认识的岗位」忽略。
        /// 「人员设置.cspkg」里存的名单文本走的是同一个函数，所以包与文件的行为必然一致。</summary>
        public static Dictionary<string, PersonEntry> Parse(string text)
        {
            Dictionary<string, PersonEntry> people = Defaults();
            if (string.IsNullOrEmpty(text)) return people;
            foreach (string rawLine in SplitLines(text))
            {
                string line = rawLine == null ? "" : rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string role = line.Substring(0, equals).Trim();
                if (Array.IndexOf(Roles, role) < 0) continue;   // 不认识的岗位名：忽略该行
                string value = line.Substring(equals + 1).Trim();
                PersonEntry entry = new PersonEntry();
                int bar = value.IndexOf('|');
                if (bar >= 0)
                {
                    entry.Name = value.Substring(0, bar).Trim();
                    entry.SignatureFile = value.Substring(bar + 1).Trim();
                }
                else
                {
                    entry.Name = value;
                }
                people[role] = entry;
            }
            return people;
        }

        /// <summary>把人员配置序列化成「人员名单.txt」的正文（5.26.0 从 <see cref="Save"/> 抽出）。
        /// 包里的名单条目用的就是它 —— 落地时直接写出即可，与面板保存的结果逐字节同源。</summary>
        public static string ToText(Dictionary<string, PersonEntry> people)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("# A1 图框签字 · 人员名单（面板「人员设置」保存生成，也可以直接用记事本改）");
            text.AppendLine("# 每行：岗位=姓名。姓名留空 = 该行不签名；# 开头是注释。");
            text.AppendLine("# 签名笔迹默认在 Signatures 文件夹找「姓名.dwg」；要换笔迹就写成：岗位=姓名|笔迹文件.dwg");
            foreach (string role in Roles)
            {
                PersonEntry entry;
                if (people == null || !people.TryGetValue(role, out entry)) entry = new PersonEntry();
                string line = role + "=" + (entry.Name == null ? "" : entry.Name.Trim());
                if (!string.IsNullOrWhiteSpace(entry.SignatureFile)) line += "|" + entry.SignatureFile.Trim();
                text.AppendLine(line);
            }
            return text.ToString();
        }

        /// <summary>保存人员配置（面板「人员设置」调用）。写 UTF-8（带 BOM），记事本打开不乱码。
        /// 5.13.2 起：目录不存在会先建出来；写完后**回读校验**，确认文件真的落地且能读回写入的
        /// 内容 —— 写到别处 / 权限不足 / 被同步软件拦截都会在这里抛异常，由界面弹窗告诉用户，
        /// 而不是「提示已保存、实际没存上」。</summary>
        public static void Save(string directory, Dictionary<string, PersonEntry> people)
        {
            string path = ConfigPath(directory);
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(path, ToText(people), new UTF8Encoding(true));

            // 回读校验：确认真的写到了 path，而且能解析出内容
            if (!File.Exists(path))
                throw new IOException("保存后找不到配置文件：" + path);
            Dictionary<string, PersonEntry> check = Load(directory);
            foreach (string role in Roles)
            {
                PersonEntry expected, actual;
                if (people != null && people.TryGetValue(role, out expected) && expected != null)
                {
                    if (check.TryGetValue(role, out actual))
                    {
                        string want = expected.Name == null ? "" : expected.Name.Trim();
                        if (string.Equals(want, actual.Name, StringComparison.Ordinal)) continue;
                    }
                    throw new IOException("写入后校验失败（岗位「" + role + "」内容对不上）：" + path);
                }
            }
        }

        /// <summary>内置默认名单（**示例姓名**，5.24.2 起）：配置文件缺失时生效。
        /// 原来这里写的是真实人员姓名，干净安装包分发出去会被误认成"已经配好了"，也把无关人员姓名
        /// 带给了别人；改成张三/李四… 这种一眼就知道要替换的示例，签名文件列同样给示例
        /// 「姓名.dwg」（与留空时的自动规则一致，看得见更容易理解格式）。
        /// 装好后在面板「人员设置」里改成自己的人员，保存即生效。</summary>
        public static Dictionary<string, PersonEntry> Defaults()
        {
            Dictionary<string, PersonEntry> people = new Dictionary<string, PersonEntry>();
            Add(people, "项目负责人", "张三", "张三.dwg");
            Add(people, "专业负责人", "李四", "李四.dwg");
            Add(people, "审定", "王五", "王五.dwg");
            Add(people, "审核", "赵六", "赵六.dwg");
            Add(people, "校对", "钱七", "钱七.dwg");
            Add(people, "设计", "孙八", "孙八.dwg");
            Add(people, "制图", "周九", "周九.dwg");
            return people;
        }

        /// <summary>一段名单文本是不是**出厂默认**（= 用户还没配过自己的名单）。
        /// 5.26.0 新增：只按「姓名」判断 —— 干净安装/绿色版出厂状态下，名单文件里就是
        /// 张三/李四… 这套示例，「人员设置.cspkg」正是靠这一条决定「该不该按包填写」。
        /// 用户一旦改过任何一个人的姓名（哪怕改成空），就不再算默认，本地永远优先。</summary>
        public static bool IsFactoryDefaults(string text)
        {
            Dictionary<string, PersonEntry> parsed = Parse(text);
            Dictionary<string, PersonEntry> standard = Defaults();
            foreach (string role in Roles)
            {
                PersonEntry entry;
                string name = parsed.TryGetValue(role, out entry) && entry != null
                    ? (entry.Name ?? "").Trim()
                    : "";
                if (!string.Equals(name, standard[role].Name, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>两份名单文本是否**指向同一套配置**（逐岗位比姓名 + 笔迹文件名）。
        /// 用于「包已经应用过了就别再写一遍」的幂等判断 —— 不能按文件时间判断，
        /// 绿色版每次命令都把配置从缓存摊到新的工作目录，时间戳永远是"刚刚"。</summary>
        public static bool SamePeople(string textA, string textB)
        {
            Dictionary<string, PersonEntry> a = Parse(textA);
            Dictionary<string, PersonEntry> b = Parse(textB);
            foreach (string role in Roles)
            {
                PersonEntry x, y;
                string nameA = a.TryGetValue(role, out x) && x != null ? (x.Name ?? "").Trim() : "";
                string nameB = b.TryGetValue(role, out y) && y != null ? (y.Name ?? "").Trim() : "";
                if (!string.Equals(nameA, nameB, StringComparison.Ordinal)) return false;
                string fileA = x == null ? "" : (x.SignatureFile ?? "").Trim();
                string fileB = y == null ? "" : (y.SignatureFile ?? "").Trim();
                if (!string.Equals(fileA, fileB, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static void Add(Dictionary<string, PersonEntry> people, string role, string name, string file)
        {
            people[role] = new PersonEntry { Name = name, SignatureFile = file };
        }

        /// <summary>按行读文件，编码自适应：优先按 UTF-8 严格解码，遇到 GBK 等本地编码（比如
        /// 用老版记事本另存为 ANSI）时退回系统默认编码，避免中文岗位名变成乱码导致匹配不上。</summary>
        internal static IEnumerable<string> ReadLinesRobust(string path)
        {
            return SplitLines(ReadTextRobust(path));
        }

        /// <summary>读文本（编码自适应），见 <see cref="ReadLinesRobust"/> 的说明。</summary>
        internal static string ReadTextRobust(string path)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default.GetString(File.ReadAllBytes(path));
            }
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
    }

    /// <summary>
    /// 默认签名文件夹设置（5.12.0）：面板「设置默认文件夹」按钮写入。
    /// 配置文件「签名文件夹.txt」与核心 DLL 同目录，内容一行 = 文件夹绝对路径
    /// （# 开头的行是注释）。设置后「一键签名」与「选取…」都用这个文件夹；
    /// 删除该文件即恢复自动定位（从核心 DLL 往上找 Signatures 文件夹）。
    ///
    /// 5.26.0：面板上的「设置默认文件夹」按钮已撤（改用「导出人员设置」配置包带走全部设置），
    /// 这里**只保留读写能力**：老用户之前设过的文件夹继续生效，也可以直接编辑这个文本文件。
    /// </summary>
    internal static class SignatureSettings
    {
        /// <summary>设置文件名（存放在核心 DLL 同目录）。</summary>
        public const string FileName = "签名文件夹.txt";

        /// <summary>读取用户设置的默认签名文件夹；没设置或读不出来时返回 null（回自动定位）。</summary>
        public static string LoadCustomDirectory(string baseDirectory)
        {
            try
            {
                string path = Path.Combine(baseDirectory ?? "", FileName);
                if (!File.Exists(path)) return null;
                foreach (string rawLine in PeopleConfig.ReadLinesRobust(path))
                {
                    string line = rawLine == null ? "" : rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    return line;    // 第一条非注释行 = 路径
                }
                return null;
            }
            catch (System.Exception)
            {
                return null;        // 设置文件坏了不致命，回自动定位
            }
        }

        /// <summary>保存用户设置的默认签名文件夹（UTF-8 带 BOM，记事本打开不乱码）。</summary>
        public static void SaveCustomDirectory(string baseDirectory, string folderPath)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("# 默认签名文件夹：存放签名 .dwg 笔迹。也可以直接改这一行。");
            text.AppendLine("# 删除本文件即恢复自动定位（从核心 DLL 所在目录往上找 Signatures 文件夹）。");
            text.AppendLine((folderPath == null ? "" : folderPath.Trim()));
            File.WriteAllText(Path.Combine(baseDirectory ?? "", FileName), text.ToString(), new UTF8Encoding(true));
        }
    }
}
