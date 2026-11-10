using System;
using System.Windows;
using System.Windows.Controls;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 主面板：把批量打印、通用批量、图框库整合到一个窗口，用 TabControl 分页。
/// 非模态单例，由 BatchPlotCommands 通过 CadDialog.ShowModeless 显示。
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _environmentReload;
    private readonly UserControl[] _panels;
    private readonly BatchPlotControl _batch;
    private readonly RectangleBatchPlotControl _rect;
    private readonly TitleBlockLibraryControl _library;
    private readonly SettingsControl _settings;

    public MainWindow(Document document)
    {
        InitializeComponent();
        Title = "出图中心";
        CadPickTrace.AttachWindow(this);

        _batch = new BatchPlotControl(document);
        _rect = new RectangleBatchPlotControl(document);
        var single = new SinglePlotControl(document);
        var attributes = new FrameToolsControl();
        var signatures = new FrameToolsControl(signature: true);
        _library = new TitleBlockLibraryControl();
        _settings = new SettingsControl();
        _settings.EnvironmentImported += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            // 导入已落盘，旧打印页不得在关闭时把旧选项写回；重建所有页读取新环境。
            _environmentReload = true;
            var current = CadApp.DocumentManager.MdiActiveDocument;
            BatchPlotCommands.CloseForReload();
            if (current != null && !current.IsDisposed) current.SendStringToExecute("ZBP_SETTINGS ", true, false, false);
        }));

        _panels = new UserControl[] { attributes, signatures, _batch, _rect, single, _library, _settings };

        _tabs.Items.Add(new TabItem { Header = "属性图框", Content = attributes });
        _tabs.Items.Add(new TabItem { Header = "图框签名", Content = signatures });
        _tabs.Items.Add(new TabItem { Header = "图框块打印", Content = _batch });
        _tabs.Items.Add(new TabItem { Header = "通用批量", Content = _rect });
        _tabs.Items.Add(new TabItem { Header = "单张打印", Content = single });
        _tabs.Items.Add(new TabItem { Header = "图框库", Content = _library });
        _tabs.Items.Add(new TabItem { Header = "设置", Content = _settings });

        // 默认选最前面的属性图框 Tab
        _tabs.SelectedIndex = 0;
        _tabs.SelectionChanged += OnTabsSelectionChanged;

        Closed += (_, _) => ShutdownPanels();
    }

    /// <summary>切到「设置」Tab 并打开其内部子标签页（0=常规 1=文件名 2=图纸目录 3=比例设置 4=快捷键）。</summary>
    public void ShowSettings(int settingsTabIndex)
    {
        SelectTab(4);
        _settings.SelectSettingsTab(settingsTabIndex);
    }

    /// <summary>
    /// 切回「图框块打印」Tab 时：图框库变更则重扫，设置保存过则刷新清单。
    /// 切回「通用批量」Tab 时：设置保存过则同步并刷新清单。
    /// </summary>
    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (_tabs.SelectedIndex)
        {
            case 2:
                if (_library.LibraryChanged)
                {
                    _library.ClearLibraryChanged();
                    _batch.RefreshAfterLibraryChange();
                }
                if (_settings.SettingsChanged)
                {
                    _settings.ClearSettingsChanged();
                    _batch.RefreshAfterSettingsChange();
                }
                break;
            case 3:
                if (_settings.SettingsChanged)
                {
                    _settings.ClearSettingsChanged();
                    _rect.RefreshAfterSettingsChange();
                }
                break;
        }
    }

    /// <summary>切到指定 Tab（0=图框块打印 1=通用批量 2=单张打印 3=图框库 4=设置）。</summary>
    public void SelectTab(int index)
    {
        // 保留打印菜单命令的原索引；-2=属性图框，-1=图框签名。
        var physicalIndex = index + 2;
        if (physicalIndex >= 0 && physicalIndex < _tabs.Items.Count)
            _tabs.SelectedIndex = physicalIndex;
    }

    /// <summary>关闭时清理各面板的临时资源（红框/序号/未保存提示）。</summary>
    internal bool IsBusy => TitleAttributeBatch.Core.Entry.IsBusy || _batch.IsBusy || _rect.IsBusy;

    private void ShutdownPanels()
    {
        foreach (var p in _panels)
        {
            try
            {
                if (p is FrameToolsControl tools) tools.Shutdown();
                else if (p is BatchPlotControl b) b.Shutdown(persistSettings: !_environmentReload);
                else if (p is RectangleBatchPlotControl r) r.Shutdown(persistSettings: !_environmentReload);
                else if (p is TitleBlockLibraryControl t) t.Shutdown(persistChanges: !_environmentReload);
                else if (p is SinglePlotControl s) s.Shutdown();
                else if (p is SettingsControl st && !_environmentReload) st.Shutdown();
            }
            catch { }
        }
        // 两个WinForms宿主先解除父子关系，再统一释放共用的属性/签名状态。
        TitleAttributeBatch.Core.Entry.Shutdown();
    }
}
