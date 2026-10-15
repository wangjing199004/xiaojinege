using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ZwcadBatchPlot;

internal static class OperationLogStore
{
    internal static IReadOnlyList<FileInfo> ListFiles(IEnumerable<string> directories)
    {
        var files = new List<FileInfo>();
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(directory))
                    files.AddRange(new DirectoryInfo(directory).GetFiles("*.log"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files.GroupBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).OrderByDescending(file => file.LastWriteTimeUtc).ToList();
    }

    internal static string ReadTail(string path, int maxBytes = 256 * 1024)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var truncated = stream.Length > maxBytes;
        if (truncated) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var text = reader.ReadToEnd();
        if (!truncated) return text;
        var newline = text.IndexOf('\n');
        if (newline >= 0) text = text.Substring(newline + 1);
        return "（仅显示日志末尾，导出会保留完整内容）" + Environment.NewLine + text;
    }

    internal static void Export(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择其他保存位置，不能覆盖正在查看的原始日志。");
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }
}
