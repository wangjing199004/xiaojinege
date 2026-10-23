using System;
using System.Collections.Generic;

namespace ZwcadBatchPlot;

internal static class HpDirectoryPlan
{
    internal sealed class Page
    {
        public Page(int offset, int count, int capacity) { Offset = offset; Count = count; Capacity = capacity; }
        public int Offset { get; }
        public int Count { get; }
        public int Capacity { get; }
        public string Paper => Capacity == 27 ? "A4" : "A3";
    }

    public static IReadOnlyList<Page> Create(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "没有需要导出的图纸。");
        var capacity = count <= 27 ? 27 : 59;
        var pages = new List<Page>();
        for (var offset = 0; offset < count; offset += capacity)
            pages.Add(new Page(offset, Math.Min(capacity, count - offset), capacity));
        return pages;
    }
}
