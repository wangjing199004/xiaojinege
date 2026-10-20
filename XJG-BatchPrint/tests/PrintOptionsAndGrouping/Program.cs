using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZwcadBatchPlot;

internal static class Program
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    [STAThread]
    private static void Main()
    {
        var app = new Application();
        // 存储替身避免写入用户的实际 Settings.json；调用实际增量合并代码。
        var panelA = AppSettingsStore.Load();
        var panelB = AppSettingsStore.Load();
        var optionsA = new SharedPrintOptions(panelA);
        var optionsB = new SharedPrintOptions(panelB);
        panelA.LastOutputFormat = "PNG"; panelA.SavePathMode = 2;
        panelA.CustomOutputDirectory = @"D:\输出"; panelA.MergePdf = true;
        optionsA.Save(panelA);
        panelB.LastStyleSheet = "monochrome.ctb";
        optionsB.Save(panelB);
        optionsA.Save(panelA); // 未操作的旧面板退出不覆盖新 CTB。
        var saved = AppSettingsStore.Load();
        Check(saved.LastOutputFormat == "PNG" && saved.SavePathMode == 2 && saved.MergePdf
            && saved.CustomOutputDirectory == @"D:\输出" && saved.LastStyleSheet == "monochrome.ctb",
            "两个面板公共配置应按实际修改合并");
        var count = AppSettingsStore.SaveCount;
        optionsA.Save(panelA);
        Check(count == AppSettingsStore.SaveCount, "未修改配置不应重复写文件");
        panelB.PaperMarginMm = -5; panelB.ScanScope = 3;
        AppSettingsStore.FailSave = true;
        optionsB.Save(panelB);
        AppSettingsStore.FailSave = false;
        optionsB.Save(panelB);
        Check(AppSettingsStore.Load().PaperMarginMm == -5 && AppSettingsStore.Load().ScanScope == 3,
            "保存失败后下一次应重试");
        Console.WriteLine("PASS: shared options merge, stale panel close, no redundant write, failed-save retry");

        var rows = new BindingList<TestRow>
        {
            new() {SourceFile = @"D:\项目一\建筑.dwg", Title = "平面图"},
            new() {SourceFile = @"D:\项目一\建筑.dwg", Title = "立面图"},
            new() {SourceFile = @"D:\项目二\建筑.dwg", Title = "剖面图"}
        };
        var grid = new DataGrid { Width = 660, Height = 360, AutoGenerateColumns = false,
            CanUserAddRows = false, FontFamily = new FontFamily("Microsoft YaHei UI") };
        grid.Columns.Add(new DataGridTextColumn {Header = "图名", Binding = new Binding("Title"), Width = 400});
        using (var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "WpfUi.xaml")))
            grid.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        grid.GroupStyle.Add((GroupStyle)grid.FindResource("SourceFileGroupStyle"));
        SourceFileGrouping.Attach(grid, rows, "SourceFile");
        var view = (ListCollectionView)grid.ItemsSource;
        Check(view.Groups!.Count == 2, "同名但不同目录的 DWG 不得合并成一组");
        var first = (CollectionViewGroup)view.Groups[0];
        Check(first.ItemCount == 2, "同源图纸应在同一分组");
        var state = first.Name;
        state.GetType().GetProperty("IsExpanded")!.SetValue(state, false);
        view.Refresh();
        Check(view.Cast<TestRow>().Count() == 3 && rows.Count(r => r.Selected) == 3,
            "折叠不得丢失打印任务或修改勾选");
        Check(ReferenceEquals(((CollectionViewGroup)view.Groups[0]).Name, state)
            && !(bool)state.GetType().GetProperty("IsExpanded")!.GetValue(state)!,
            "刷新应保留折叠状态");
        rows.Add(new() {SourceFile = @"D:\项目三\结构.dwg", Title = "结构图"});
        Check(view.Groups.Count == 3, "新增扫描结果应刷新分组");
        rows.RemoveAt(3);
        Check(view.Groups.Count == 2, "删除任务后不保留空组");
        // 不打开窗口，仅布局并渲染实际分组模板，检查 +/- 与折叠内容。
        var preview = new StackPanel { Background = Brushes.White, Width = 660, Height = 360 };
        var groupStyle = (GroupStyle)grid.FindResource("SourceFileGroupStyle");
        foreach (CollectionViewGroup group in view.Groups)
        {
            var container = new GroupItem { DataContext = group, Style = groupStyle.ContainerStyle };
            preview.Children.Add(container);
            container.ApplyTemplate();
        }
        preview.Measure(new Size(660, 360)); preview.Arrange(new Rect(0, 0, 660, 360)); preview.UpdateLayout();
        var buttons = Descendants(preview).OfType<ToggleButton>().Where(b => b.Width == 23).ToList();
        Check(buttons.Count == 2 && buttons.Any(b => b.Content?.ToString() == "+")
            && buttons.Any(b => b.Content?.ToString() == "−"), "分组应显示实际展开/折叠按钮");
        var image = new RenderTargetBitmap(660, 360, 96, 96, PixelFormats.Pbgra32);
        image.Render(preview);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "group-preview.png"))) encoder.Save(output);
        Console.WriteLine("PASS: real WPF grouping, same-name DWGs, collapsed task retention, refresh state, add/remove, +/- template render");
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    public sealed class TestRow
    {
        public string SourceFile {get; set;} = "";
        public string Title {get; set;} = "";
        public bool Selected {get; set;} = true;
    }
}

namespace ZwcadBatchPlot
{
    // 仅替代存储和宿主模型；被测合并逻辑及 WPF 视图/模板来自生产源码。
    public sealed class AppSettings
    {
        public string LastOutputFormat {get;set;} = "PDF";
        public int SavePathMode {get;set;}
        public string CustomOutputDirectory {get;set;} = "";
        public string LastPlotDevice {get;set;} = "";
        public string LastStyleSheet {get;set;} = "";
        public bool MergePdf {get;set;}
        public bool LeavePaperMargin {get;set;}
        public double PaperMarginMm {get;set;} = 1;
        public int ScanScope {get;set;}
        public bool RecognizeTitleByDominantFrameMode {get;set;}
    }
    public static class AppSettingsStore
    {
        private static string _json = "{}";
        public static int SaveCount;
        public static bool FailSave;
        public static AppSettings Load() => JsonSerializer.Deserialize<AppSettings>(_json)!;
        public static bool TrySave(AppSettings settings, out Exception? error)
        {
            error = null; if (FailSave) return false;
            _json = JsonSerializer.Serialize(settings); SaveCount++; return true;
        }
    }
}
