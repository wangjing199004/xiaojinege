using System;
using System.Collections.Generic;
using System.Linq;

namespace ZwcadBatchPlot;

internal static class StandardFrameCatalog
{
    internal sealed class Item
    {
        public Item(string id, string name, string resource) { Id = id; Name = name; Resource = resource; }
        public string Id { get; }
        public string Name { get; }
        public string Resource { get; }
    }
    public static IReadOnlyList<Item> Items { get; } = Array.AsReadOnly(new[]
    {
        new Item("a0-plus", "A0+图框", "ZwcadBatchPlot.Private.Std.1"),
        new Item("a1-plus", "A1+图框", "ZwcadBatchPlot.Private.Std.2"),
        new Item("a2-plus", "A2+图框", "ZwcadBatchPlot.Private.Std.3"),
        new Item("cover", "封面", "ZwcadBatchPlot.Private.Std.4"),
        new Item("seal", "目录签章框", "ZwcadBatchPlot.Private.Std.5"),
        new Item("a3-directory", "A3目录", "ZwcadBatchPlot.Private.Hp.a"),
        new Item("a4-directory", "A4目录", "ZwcadBatchPlot.Private.Hp.b")
    });
    public static Item Get(string id) => Items.FirstOrDefault(item => item.Id == id)
        ?? throw new ArgumentException("请选择有效的标准图框。", nameof(id));
}
