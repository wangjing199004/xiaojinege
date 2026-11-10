using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
namespace ZwcadBatchPlot;
internal sealed class FrameToolsControl : UserControl
{
    private readonly WindowsFormsHost _host = new WindowsFormsHost();
    internal FrameToolsControl(bool signature = false)
    {
        _host.Child = signature ? TitleAttributeBatch.Core.Entry.CreateSignatureView() : TitleAttributeBatch.Core.Entry.CreateAttributeView();
        Content = _host;
        Loaded += (_, _) => TitleAttributeBatch.Core.Entry.OwnerWindow = Window.GetWindow(this);
    }
    internal void Shutdown()
    {
        _host.Child = null;
        _host.Dispose();
    }
}
