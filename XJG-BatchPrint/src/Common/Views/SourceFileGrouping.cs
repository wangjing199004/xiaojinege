using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace ZwcadBatchPlot;

/// <summary>只对显示视图分组，源任务和打印顺序不受展开状态影响。</summary>
internal sealed class SourceFileGrouping : IValueConverter
{
    private readonly Dictionary<string, FileGroup> _groups = new(StringComparer.OrdinalIgnoreCase);

    public static void Attach(DataGrid grid, IList rows, string sourceProperty)
    {
        var grouping = new SourceFileGrouping();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(sourceProperty, grouping));
        grid.ItemsSource = view;
        grid.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right) return;
            var node = e.OriginalSource as DependencyObject;
            while (node != null)
            {
                if (node is DataGridRow) { grid.ClearValue(SelectedSourceProperty); return; }
                if (node is ToggleButton) return; // +/- never changes selection.
                if (node is GroupItem item && item.DataContext is CollectionViewGroup group)
                {
                    if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) return;
                    grid.SelectedItems.Clear();
                    foreach (var row in group.Items) grid.SelectedItems.Add(row);
                    if (group.Items.Count > 0) grid.CurrentCell = new DataGridCellInfo(group.Items[0], grid.Columns[0]);
                    var fileGroup = (FileGroup)group.Name;
                    grid.SetValue(SelectedSourceProperty, fileGroup.FullPath);
                    if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
                        fileGroup.IsExpanded = !fileGroup.IsExpanded;
                    grid.Focus();
                    e.Handled = e.ChangedButton == MouseButton.Left;
                    return;
                }
                node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
        };
        if (rows is IBindingList bindingList)
        {
            bindingList.ListChanged += (_, e) =>
            {
                if (e.ListChangedType == ListChangedType.ItemChanged) return;
                // 提交编辑后刷新，避免删除/扫描后保留旧分组。
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                view.Refresh();
            };
        }
    }

    private static readonly DependencyProperty SelectedSourceProperty = DependencyProperty.RegisterAttached(
        "SelectedSource", typeof(string), typeof(SourceFileGrouping), new PropertyMetadata(null));
    internal static string? SelectedSource(DataGrid grid) => grid.GetValue(SelectedSourceProperty) as string;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string ?? "";
        if (!_groups.TryGetValue(path, out var group))
        {
            group = new FileGroup(path);
            _groups.Add(path, group);
        }
        return group;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private sealed class FileGroup : INotifyPropertyChanged
    {
        private bool _isExpanded = true;
        public FileGroup(string path) { FullPath = path; }
        public string FullPath { get; }
        public string DisplayName => string.IsNullOrWhiteSpace(FullPath) ? "当前图纸" : Path.GetFileName(FullPath);
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
