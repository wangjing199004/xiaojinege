using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ZwcadBatchPlot;

internal sealed class OperationLogControl : UserControl
{
    private readonly ListBox _files = new() { MinHeight = 70, DisplayMemberPath = "Label" };
    private readonly TextBox _preview = new()
    {
        IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"), FontSize = 11
    };
    private readonly TextBox _path = new() { IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _export;

    private sealed class Entry
    {
        internal FileInfo File = null!;
        public string Label => $"{File.LastWriteTime:MM-dd HH:mm:ss}  {File.Name}  ({File.Length / 1024d:0.#} KB)";
    }

    public OperationLogControl()
    {
        var root = new Grid { Margin = new Thickness(10) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(5), new GridLength(2, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        AddButton(buttons, "刷新", () => RefreshLogs());
        AddButton(buttons, "本次操作日志", () => RefreshLogs(CadPickDiagnostics.LogPath));
        _export = AddButton(buttons, "导出选中日志", ExportSelected);
        AddButton(buttons, "打开日志目录", OpenDirectory);
        root.Children.Add(buttons);
        Add(root, _path, 1);
        Add(root, _files, 2);
        Add(root, new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows }, 3);
        Add(root, _preview, 4);
        Add(root, _status, 5);
        Content = root;
        _files.SelectionChanged += (_, _) => ReadSelected();
        Loaded += (_, _) => RefreshLogs();
    }

    private static void Add(Grid grid, UIElement element, int row)
    { Grid.SetRow(element, row); grid.Children.Add(element); }

    private static Button AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
        return button;
    }

    internal void RefreshLogs(string? preferredPath = null)
    {
        try
        {
            CadPickDiagnostics.Log("logs", "查看操作日志");
            var selected = preferredPath ?? (_files.SelectedItem as Entry)?.File.FullName ?? CadPickDiagnostics.LogPath;
            var directories = new[] { BatchPlotLogger.LogDirectory,
                Path.GetDirectoryName(CadPickDiagnostics.LogPath) ?? "",
                Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Logs") };
            var entries = OperationLogStore.ListFiles(directories).Select(file => new Entry { File = file }).ToList();
            _files.ItemsSource = entries;
            _files.SelectedItem = entries.FirstOrDefault(entry => string.Equals(entry.File.FullName, selected, StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault();
            _status.Text = $"共 {entries.Count} 份日志。操作日志自动记录；打印等详细日志按常规页开关生成。刷新可查看最新内容。";
        }
        catch (Exception ex) { _status.Text = "读取日志列表失败：" + ex.Message; }
    }

    private void ReadSelected()
    {
        var entry = _files.SelectedItem as Entry;
        _export.IsEnabled = entry != null;
        _path.Text = entry?.File.FullName ?? "";
        try
        {
            _preview.Text = entry == null ? "暂无日志。" : OperationLogStore.ReadTail(entry.File.FullName);
            _preview.ScrollToEnd();
        }
        catch (Exception ex) { _preview.Text = "读取失败：" + ex.Message; }
    }

    private void ExportSelected()
    {
        if (_files.SelectedItem is not Entry entry) return;
        var picker = new SaveFileDialog
        {
            Title = "导出检修日志", FileName = entry.File.Name, Filter = "日志文件 (*.log)|*.log",
            DefaultExt = ".log", AddExtension = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            CadPickDiagnostics.Log("logs", "导出日志: " + entry.File.Name);
            OperationLogStore.Export(entry.File.FullName, picker.FileName);
            _status.Text = "已导出完整日志：" + picker.FileName;
        }
        catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message, "操作日志"); }
    }

    private void OpenDirectory()
    {
        try
        {
            var directory = Path.GetDirectoryName((_files.SelectedItem as Entry)?.File.FullName ?? CadPickDiagnostics.LogPath);
            if (directory == null) return;
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + directory + "\"", UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show("打开目录失败：" + ex.Message, "操作日志"); }
    }
}
