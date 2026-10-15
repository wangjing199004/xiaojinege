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
    private readonly UserControl[] _panels;
    private readonly BatchPlotControl _batch;
    private readonly RectangleBatchPlotControl _rect;
    private readonly TitleBlockLibraryControl _library;
    private readonly SettingsControl _settings;

    public MainWindow(Document document)
    {
        InitializeComponent();
        Title = "XJG批量打印";
        CadPickTrace.AttachWindow(this);

        _batch = new BatchPlotControl(document);
        _rect = new RectangleBatchPlotControl(document);
        var single = new SinglePlotControl(document);
        _library = new TitleBlockLibraryControl();
        _settings = new SettingsControl();

        _panels = new UserControl[] { _batch, _rect, single, _library, _settings };

        _tabs.Items.Add(new TabItem { Header = "图框块打印", Content = _batch });
        _tabs.Items.Add(new TabItem { Header = "通用批量", Content = _rect });
        _tabs.Items.Add(new TabItem { Header = "单张打印", Content = single });
        _tabs.Items.Add(new TabItem { Header = "图框库", Content = _library });
        _tabs.Items.Add(new TabItem { Header = "设置", Content = _settings });

        // 默认选「批量打印」Tab
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
            case 0:
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
            case 1:
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
        if (index >= 0 && index < _tabs.Items.Count)
            _tabs.SelectedIndex = index;
    }

    /// <summary>关闭时清理各面板的临时资源（红框/序号/未保存提示）。</summary>
    private void ShutdownPanels()
    {
        foreach (var p in _panels)
        {
            try
            {
                if (p is BatchPlotControl b) b.Shutdown();
                else if (p is RectangleBatchPlotControl r) r.Shutdown();
                else if (p is TitleBlockLibraryControl t) t.Shutdown();
                else if (p is SinglePlotControl s) s.Shutdown();
                else if (p is SettingsControl st) st.Shutdown();
            }
            catch { }
        }
    }
}
