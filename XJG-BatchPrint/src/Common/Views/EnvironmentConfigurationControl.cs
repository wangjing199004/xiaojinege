using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace ZwcadBatchPlot;
internal sealed class EnvironmentConfigurationControl : UserControl
{
    private readonly Dictionary<EnvironmentComponents, CheckBox> choices = new();
    internal event Action? ExportRequested;
    internal event Action? ImportRequested;
    internal EnvironmentComponents Selected => GetSelected();
    private EnvironmentComponents GetSelected() { var value = EnvironmentComponents.None; foreach (var choice in choices) if (choice.Value.IsChecked == true) value |= choice.Key; return value; }
    internal EnvironmentConfigurationControl()
    {
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "配置管理", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        content.Children.Add(new TextBlock { Text = "勾选需要导出或导入的配置类型。未勾选的配置保持原样。导出前请先保存图框库及人员设置的修改。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        AddChoice(content, EnvironmentComponents.Settings, "插件设置与快捷键", "打印、保存路径、目录、比例、排序及命令别名等设置");
        AddChoice(content, EnvironmentComponents.Library, "图框库", "图框识别字段、打印范围、图幅及纸张映射");
        AddChoice(content, EnvironmentComponents.Calibration, "图框与封面标定", "各图幅的签名位置、距离和封面定位");
        AddChoice(content, EnvironmentComponents.Signatures, "人员名单与签名素材", "岗位姓名、签名文件关联及加密笔迹，换机自动重定位");
        var selection = new WrapPanel { Margin = new Thickness(0, 8, 0, 18) };
        AddButton(selection, "全选", () => { foreach (var item in choices.Values) item.IsChecked = true; });
        AddButton(selection, "取消全选", () => { foreach (var item in choices.Values) item.IsChecked = false; }); content.Children.Add(selection);
        var actions = new WrapPanel();
        AddButton(actions, "导出所选配置", () => ExportRequested?.Invoke());
        AddButton(actions, "导入所选配置", () => ImportRequested?.Invoke()); content.Children.Add(actions);
        content.Children.Add(new TextBlock { Text = "导出为单个 .dcenv 文件，可在另一设备导入。兼容旧版整包、图框库 JSON 和人员设置 .cspkg。导入前自动备份，确认后重建面板并清空临时列表及未保存编辑。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) });
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
    }
    private void AddChoice(Panel parent, EnvironmentComponents component, string title, string detail)
    {
        var label = new StackPanel(); label.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        label.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var box = new CheckBox { IsChecked = true, Content = label, Margin = new Thickness(0, 0, 0, 16), VerticalContentAlignment = VerticalAlignment.Top };
        choices.Add(component, box); parent.Children.Add(box);
    }
    private static void AddButton(Panel parent, string title, Action click)
    {
        var button = new Button { Content = title, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        button.SetResourceReference(StyleProperty, "PluginButtonStyle"); button.Click += (_, _) => click(); parent.Children.Add(button);
    }
}
