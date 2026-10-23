using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZwcadBatchPlot;

internal sealed class StandardFramePickerDialog : Window
{
    private readonly ListBox _list;
    public StandardFrameCatalog.Item? SelectedTemplate => _list.SelectedItem as StandardFrameCatalog.Item;

    public StandardFramePickerDialog()
    {
        Title = "插入标准图框";
        Width = 380;
        Height = 370;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 12;
        var panel = new DockPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "选择图框后，移动鼠标预览，按图框中心对位。",
            Margin = new Thickness(0, 0, 0, 10)
        });
        DockPanel.SetDock(panel.Children[0], Dock.Top);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var insert = new Button { Content = "插入", IsDefault = true, MinWidth = 76, Padding = new Thickness(8, 4, 8, 4) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 76,
            Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(insert);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        _list = new ListBox { ItemsSource = StandardFrameCatalog.Items.Where(item => item.Id != "a3-directory" && item.Id != "a4-directory").ToList(), DisplayMemberPath = "Name", SelectedIndex = 0 };
        _list.ItemContainerStyle = new Style(typeof(ListBoxItem));
        _list.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        panel.Children.Add(_list);
        insert.Click += (_, _) => { if (SelectedTemplate != null) DialogResult = true; };
        _list.MouseDoubleClick += (_, _) => { if (SelectedTemplate != null) DialogResult = true; };
        Content = panel;
    }
}
