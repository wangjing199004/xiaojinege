using System;

namespace ZwcadBatchPlot;

/// <summary>矩形图框扫描进度快照。</summary>
public sealed class RectangleScanProgress
{
    /// <summary>进度条标题，例如「正在识别图框…」。</summary>
    public string Title { get; set; } = "正在识别图框…";

    /// <summary>当前阶段说明。</summary>
    public string Detail { get; set; } = "";

    /// <summary>已完成量；与 <see cref="Total"/> 同时大于 0 时显示确定进度。</summary>
    public int Current { get; set; }

    /// <summary>总量；为 0 时进度条不确定（滚动）。</summary>
    public int Total { get; set; }
}
