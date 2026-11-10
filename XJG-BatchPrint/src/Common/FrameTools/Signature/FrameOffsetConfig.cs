#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CadSignature
{
    /// <summary>
    /// 图幅标定（5.18.0）：分别固定 A0/A1/A2/A3 四种图框各自的签名位置。
    ///
    /// 每个图幅记一条：签字栏「姓名」列左上角与「签名」列右下角，相对**该图框实际内容
    /// 右下角**的偏移（块内坐标），外加**标定时该图幅的内容高度**。「图框签名」框选批量
    /// 签名时按每个图框实际内容的高度匹配图幅（差 ≤5% 取最接近者），套用对应偏移——
    /// 高度不受向右拉伸影响：A0/A1/A2/A3 四类高度互不相同、自动各归各类，而 A1+0.25/
    /// A1+0.5 等加长变体与 A1 高度相同，**同一类标定一次全类共用**（5.18.2 起按高度；
    /// 5.18.0/5.18.1 按宽度时每种拉伸量都要单独标定）。
    ///
    /// 锚点与偏移都在**块坐标系**里记（内容外包框并集在块内取，旋转/缩放由外层
    /// BlockTransform 处理），所以平移、缩放、旋转、动态拉伸都能对准。
    ///
    /// 配置文件「图框定位.txt」与核心 DLL 同目录（装机包里就是 Contents\Windows），
    /// 一键编译脚本只覆盖 DLL，不会碰这个文件。行格式（UTF-8，# 开头是注释）：
    ///   A1@RB=内容高,dx1,dy1,dx2,dy2
    /// （@RB = 锚定内容右下角；5.17.x 的旧格式行不再识别，保留在文件里无害）。
    ///
    /// 删除对应行（或整个文件）即可作废该图幅的标定（「图幅标定」对话框里有「清除」按钮）。
    /// </summary>
    internal static class FrameOffsetConfig
    {
        /// <summary>配置文件名（存放在核心 DLL 同目录）。</summary>
        public const string FileName = "图框定位.txt";

        /// <summary>四种图幅（也用作配置里的键名）。「图框签名」按内容高度匹配它们。</summary>
        public static readonly string[] Slots = { "A0", "A1", "A2", "A3", "封面" };
        public static string LoadCoverBlockName(string directory)
        {
            if (!File.Exists(ConfigPath(directory))) return "";
            var entries = ReadEntries(ConfigPath(directory), null);
            string value;
            if (!entries.TryGetValue("COVER_BLOCK", out value)) return "";
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); } catch { return ""; }
        }
        public static void SaveCoverBlockName(string directory, string name)
        {
            string path = ConfigPath(directory);
            var entries = ReadEntries(path, null);
            entries["COVER_BLOCK"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
            WriteEntries(path, entries);
        }
        public static string MatchSlot(string directory, string blockName, double height, Dictionary<string, double[]> calibrations)
        {
            string coverName = LoadCoverBlockName(directory);
            if (string.Equals(blockName, coverName, StringComparison.OrdinalIgnoreCase))
                return calibrations.ContainsKey("封面") ? "封面" : null;
            if (blockName.IndexOf("封面", StringComparison.Ordinal) >= 0) return null;
            string matched = null;
            double best = 0;
            foreach (var pair in calibrations)
            {
                if (pair.Key == "封面") continue;
                double diff = Math.Abs(height - pair.Value[0]) / Math.Max(1.0, pair.Value[0]);
                if (diff <= .05 && (matched == null || diff < best)) { matched = pair.Key; best = diff; }
            }
            return matched;
        }

        /// <summary>新格式的锚点后缀（@RB = 锚定 Right-Bottom，区别于 5.17.x 的旧格式行）。</summary>
        private const string AnchorSuffix = "@RB";

        /// <summary>配置文件完整路径。参数是**目录**（核心 DLL 所在目录）；
        /// 顺手把 ConfigPath 的结果再传进来也能识别（与 PeopleConfig.ConfigPath 同一套容错）。</summary>
        public static string ConfigPath(string baseDirectory)
        {
            string folder = baseDirectory == null ? "" : baseDirectory.Trim();
            if (folder.Length == 0) folder = ".";
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

        /// <summary>读某个图幅的标定；返回 5 个数（内容高, dx1, dy1, dx2, dy2），
        /// 没有记录或格式不对时返回 null（表示未标定）。</summary>
        public static double[] Load(string baseDirectory, string slot)
        {
            if (string.IsNullOrEmpty(slot)) return null;
            string key = slot + AnchorSuffix;
            string path = ConfigPath(baseDirectory);
            if (!File.Exists(path)) return null;
            try
            {
                foreach (string rawLine in PeopleConfig.ReadLinesRobust(path))
                {
                    string line = rawLine == null ? "" : rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    if (!string.Equals(line.Substring(0, equals).Trim(), key, StringComparison.Ordinal)) continue;
                    return ParseNumbers(line.Substring(equals + 1), 5);
                }
            }
            catch (System.Exception)
            {
                // 配置文件读不出来按「未标定」处理，重新标一次即可，不值得报错
            }
            return null;
        }

        /// <summary>保存（或覆盖）一个图幅的标定；其他行原样保留。
        /// 写完**回读校验**（与 PeopleConfig.Save 同规矩）：内容对不上就抛异常，
        /// 让调用方把真实路径报给用户，而不是「提示成功、实际没存上」。</summary>
        public static void Save(string baseDirectory, string slot, double localWidth, double[] offsets)
        {
            if (string.IsNullOrEmpty(slot) || offsets == null || offsets.Length != 4)
                throw new ArgumentException("标定数据不完整。");

            string path = ConfigPath(baseDirectory);
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);

            // 先读旧记录，替换该图幅（连同 5.17.x 可能留下的同键旧行），其余原样写回
            Dictionary<string, string> entries = ReadEntries(path, slot);
            StringBuilder line = new StringBuilder();
            line.Append(FormatNumber(localWidth));
            for (int i = 0; i < 4; i++) line.Append(',').Append(FormatNumber(offsets[i]));
            entries[slot + AnchorSuffix] = line.ToString();
            WriteEntries(path, entries);
            Verify(baseDirectory, slot, localWidth, offsets);
        }

        /// <summary>删除一个图幅的标定；返回是否真的删到了东西（本来就未标定时返回 false）。
        /// 注意：这里必须用 <c>ReadEntries(path, null)</c> 读**全量**条目 —— 若照 Save 的用法传 slot，
        /// 它会主动跳过该 slot 的行，导致 ContainsKey 恒为 false，「清除」按钮永远删不掉东西
        /// （5.24.1 由脱离 CAD 的单元测试发现并修正）。</summary>
        public static bool Remove(string baseDirectory, string slot)
        {
            string path = ConfigPath(baseDirectory);
            if (string.IsNullOrEmpty(slot) || !File.Exists(path)) return false;
            Dictionary<string, string> entries = ReadEntries(path, null);
            bool hadNew = entries.Remove(slot + AnchorSuffix);      // 当前格式
            bool hadOld = entries.Remove(slot);                     // 5.17.x 旧格式键，一并作废
            if (slot == "封面") hadNew = entries.Remove("COVER_BLOCK") || hadNew;
            if (!hadNew && !hadOld) return false;
            WriteEntries(path, entries);
            return true;
        }

        /// <summary>读出全部「键 → 值」记录。removeSlot 指定的图幅（含旧格式键）不进结果。</summary>
        private static Dictionary<string, string> ReadEntries(string path, string removeSlot)
        {
            Dictionary<string, string> entries = new Dictionary<string, string>();
            if (!File.Exists(path)) return entries;
            foreach (string rawLine in PeopleConfig.ReadLinesRobust(path))
            {
                string text = rawLine == null ? "" : rawLine.Trim();
                if (text.Length == 0 || text.StartsWith("#") || text.StartsWith(";")) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                string key = text.Substring(0, equals).Trim();
                if (key.Length == 0) continue;
                if (string.Equals(key, removeSlot, StringComparison.Ordinal)) continue;            // 旧格式键：作废
                if (string.Equals(key, removeSlot + AnchorSuffix, StringComparison.Ordinal)) continue;
                if (!entries.ContainsKey(key)) entries[key] = text.Substring(equals + 1).Trim();
            }
            return entries;
        }

        /// <summary>配置文件的三行说明头（WriteEntries 与 EnsureTemplate 共用一份，改文案不会漏）。</summary>
        private static void AppendHeader(StringBuilder text)
        {
            text.AppendLine("# A1 图框签字 · 图幅定位（「图幅标定」生成，也可以直接用记事本改）");
            text.AppendLine("# 封面@RB 独立记录六岗位笔迹区；COVER_BLOCK 为封面原始块名编码，不参与普通图幅高度匹配。");
            text.AppendLine("# 每行一个图幅：A0/A1/A2/A3@RB=内容高,姓名列左上角dx,dy,签名列右下角dx,dy（相对该图框内容右下角的偏移，块内坐标）");
            text.AppendLine("# 「图框签名」框选时按内容高度匹配图幅（±5%）；签字栏跟右缘走时，拉伸/平移/缩放/旋转都能对准。删除对应行即作废该图幅标定。");
        }

        /// <summary>文件不存在时写一份带注释的空模板（5.24.1），返回配置文件的完整路径。
        /// 为什么需要它：干净安装包按设计不含标定数据，装到新电脑后本文件原本要等**第一次
        /// 标定成功**才出现 —— 用户既不知道有这个文件、也不知道该放在哪。现在打开面板就会
        /// 把文件建好，提示里也能报出完整路径，照着做即可。
        /// 文件已存在则原样不动；写不出来（目录只读等）时静默跳过，绝不能因为它挡住面板打开。</summary>
        public static string EnsureTemplate(string baseDirectory)
        {
            string path = ConfigPath(baseDirectory);
            try
            {
                if (!File.Exists(path))
                {
                    string folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
                    StringBuilder text = new StringBuilder();
                    AppendHeader(text);
                    text.AppendLine("# 本机还没有标定任何图幅：打开面板 → 点「图框签字」右边的「>」→ 对 A0/A1/A2/A3 各标一次 → 点「确认」写入本文件。");
                    text.AppendLine("# 下面一行是格式示例（「#」开头的行只是说明，不会被读成标定；要手工改就把它复制一行、去掉 # 并填自己的数值）：");
                    text.AppendLine("# A1@RB=59400,-6100,11500,-1000,6600");
                    File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
                }
            }
            catch (System.Exception)
            {
                // 目录只读等异常：保持「未标定」的正常流程，不打断面板打开
            }
            return path;
        }

        private static void WriteEntries(string path, Dictionary<string, string> entries)
        {
            StringBuilder text = new StringBuilder();
            AppendHeader(text);
            foreach (KeyValuePair<string, string> entry in entries)
                text.AppendLine(entry.Key + "=" + entry.Value);
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        }

        /// <summary>写完回读校验：宽度与四个偏移都得原样读得回来。</summary>
        private static void Verify(string baseDirectory, string slot, double localWidth, double[] offsets)
        {
            double[] check = Load(baseDirectory, slot);
            if (check == null || Math.Abs(check[0] - localWidth) > 1e-6)
                throw new IOException("图幅定位写入后校验失败（图幅 " + slot + "）：" + ConfigPath(baseDirectory));
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(check[i + 1] - offsets[i]) > 1e-6)
                    throw new IOException("图幅定位写入后校验失败（图幅 " + slot + "）：" + ConfigPath(baseDirectory));
            }
        }

        /// <summary>把“n1,n2,…,n5”解析成 count 个数；格式不对返回 null（按未标定处理）。</summary>
        private static double[] ParseNumbers(string value, int count)
        {
            if (value == null) return null;
            string[] parts = value.Split(',');
            if (parts.Length != count) return null;
            double[] numbers = new double[count];
            for (int i = 0; i < count; i++)
            {
                double number;
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
                numbers[i] = number;
            }
            return numbers;
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
