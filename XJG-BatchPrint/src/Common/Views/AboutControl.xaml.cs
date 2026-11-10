using System;
using System.Windows;
using System.Windows.Controls;

namespace ZwcadBatchPlot;

public sealed partial class AboutControl : UserControl
{
    public event Action? OkRequested;

    public AboutControl()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => OkRequested?.Invoke();

    private sealed class AboutViewModel
    {
        public string VersionText => "V1.0";
    }
}
