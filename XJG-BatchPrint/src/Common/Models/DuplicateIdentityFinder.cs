using System;
using System.Collections.Generic;
using System.Linq;

namespace ZwcadBatchPlot;

internal static class DuplicateIdentityFinder
{
    /// <summary>仅标记同一源文件内的重复值；空值忽略，大小写和首尾空白不敏感。</summary>
    public static HashSet<T> Find<T>(IEnumerable<T> rows, Func<T, string?> source, Func<T, string?> identity)
    {
        var duplicates = new HashSet<T>();
        foreach (var file in rows.GroupBy(row => (source(row) ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
        {
            foreach (var group in file.GroupBy(row => (identity(row) ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
            {
                if (group.Key.Length == 0 || group.Count() < 2) continue;
                foreach (var row in group) duplicates.Add(row);
            }
        }
        return duplicates;
    }
}
