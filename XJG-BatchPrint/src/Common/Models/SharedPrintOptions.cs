using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ZwcadBatchPlot;

/// <summary>仅合并本面板实际修改的公共选项，保留其他面板和设置页的新配置。</summary>
internal sealed class SharedPrintOptions
{
    private static readonly PropertyInfo[] Properties = new[]
    {
        nameof(AppSettings.LastOutputFormat), nameof(AppSettings.SavePathMode),
        nameof(AppSettings.CustomOutputDirectory), nameof(AppSettings.LastPlotDevice),
        nameof(AppSettings.LastStyleSheet), nameof(AppSettings.MergePdf),
        nameof(AppSettings.LeavePaperMargin), nameof(AppSettings.PaperMarginMm),
        nameof(AppSettings.ScanScope), nameof(AppSettings.RecognizeTitleByDominantFrameMode)
    }.Select(name => typeof(AppSettings).GetProperty(name)!).ToArray();
    private readonly Dictionary<string, object?> _previous = new();

    public SharedPrintOptions(AppSettings settings) => Remember(settings);

    private void Remember(AppSettings settings)
    {
        foreach (var property in Properties) _previous[property.Name] = property.GetValue(settings);
    }

    public void Save(AppSettings settings)
    {
        var changed = Properties.Where(p => !Equals(_previous[p.Name], p.GetValue(settings))).ToArray();
        if (changed.Length == 0) return;
        var latest = AppSettingsStore.Load();
        foreach (var property in changed) property.SetValue(latest, property.GetValue(settings));
        if (AppSettingsStore.TrySave(latest, out _)) Remember(settings);
    }
}
