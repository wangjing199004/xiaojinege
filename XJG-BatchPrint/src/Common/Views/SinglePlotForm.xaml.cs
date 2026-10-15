using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed partial class SinglePlotControl : UserControl
{
    /// <summary>留白下拉项，语义与 BatchPlotForm 的 MarginOption 一致（正值=扩大纸张，负值=缩比例）。</summary>
    private sealed class MarginOption
    {
        public double Value { get; set; }
        public override string ToString() => Value > 0
            ? $"+ {Value:0.#} mm"
            : $"- {Math.Abs(Value):0.#} mm";
    }

    // _paperCombo/_outputPath/_areaLabel/_scaleLabel/_styleCombo/_styleSettingsButton/
    // _leaveMargin/_marginInput 由 XAML 的 x:Name 生成。
    private readonly Document _document;
    private readonly AppSettings _settings;

    private IReadOnlyList<PaperDetection> _candidates = new PaperDetection[0];
    private double _width;
    private double _height;
    private double _minX;
    private double _minY;
    private double _maxX;
    private double _maxY;
    private bool _isArbitraryPaper;
    private string? _customPmpPath;
    private PmpCustomPaper.Registration? _customPaperRegistration;
    private bool _forceCustomPaperReload;
    private readonly List<PmpCustomPaper.Registration> _preparedPapers = new();
    private string _deviceName = "";
    private string _defaultStyleSheet = "";

    public SinglePlotControl(Document document)
    {
        _document = document;
        _settings = AppSettingsStore.Load();

        InitializeComponent();

        InitMarginCombo(_marginInput, 72, 1.0);
        _marginInput.IsEnabled = false;
        _leaveMargin.Checked += (_, _) => _marginInput.IsEnabled = _leaveMargin.IsChecked == true;
        _leaveMargin.Unchecked += (_, _) => _marginInput.IsEnabled = _leaveMargin.IsChecked == true;

        LoadStyles();

        // 未框选前禁用打印/预览；框选并识别到纸张后启用。
        _printButton.IsEnabled = false;
        _previewButton.IsEnabled = false;
        _areaLabel.Text = "尚未框选图纸外框，请点击「框选外框」。";
    }

    /// <summary>主面板关闭时调用：清理临时注册的任意纸张。</summary>
    public void Shutdown()
    {
        CleanupCustomPaper();
    }

    public PaperDetection? SelectedPaper
    {
        get
        {
            if (_candidates.Count == 0)
            {
                return null;
            }

            if (_paperCombo.SelectedIndex >= 0 && _paperCombo.SelectedIndex < _candidates.Count)
            {
                return _candidates[_paperCombo.SelectedIndex];
            }

            return _candidates[0];
        }
    }

    private string OutputPath => _outputPath.Text;
    private string SelectedStyle => _styleCombo.SelectedItem?.ToString() ?? "";
    private bool LeavePaperMargin => _leaveMargin.IsChecked == true;
    private double PaperMarginMm => ReadMarginValue(_marginInput);

    private void LoadStyles()
    {
        var styles = PlotStyleManager.GetAvailableCtbStyles();
        var (deviceName, defaultStyleSheet) = ResolveSinglePlotOptions(_settings, styles);
        _deviceName = deviceName;
        _defaultStyleSheet = defaultStyleSheet;

        foreach (var style in styles)
        {
            _styleCombo.Items.Add(style);
        }
        PlotStyleManager.RestoreSavedStyle(_styleCombo, defaultStyleSheet);
        _styleSettingsButton.IsEnabled = _styleCombo.SelectedIndex >= 0;
        _styleCombo.SelectionChanged += (_, _) =>
            _styleSettingsButton.IsEnabled = _styleCombo.SelectedIndex >= 0;
    }

    private void PickFrameButton_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window == null) return;
        var picked = false;
        try
        {
            PendingLibraryInput.Queue(window, () =>
            {
                var status = "cancelled";
                CadPickDiagnostics.LogPickBegin("single-print:frame");
                using var trace = new CadPickTrace(_document.Editor);
                CadPickDiagnostics.LogPickReady("single-print:frame");
                try
                {
                    if (!TryPickFrame(out var width, out var height,
                            out var minX, out var minY, out var maxX, out var maxY)) return;
                    _width = width;
                    _height = height;
                    _minX = minX;
                    _minY = minY;
                    _maxX = maxX;
                    _maxY = maxY;
                    picked = true;
                    status = "ok";
                }
                finally { CadPickDiagnostics.LogPickEnd("single-print:frame", status); }
            }, afterRestore: () =>
            {
                // 原面板恢复后再填纸张；任意比例对话框需要可见的属主。
                if (picked) DetectAndFill();
            }, expectedDocument: _document, commandName: "ZBP_SINGLE_FRAME_INPUT");
        }
        catch (System.Exception ex)
        {
            CadPickDiagnostics.Log("single-frame-queue-error", ex.ToString());
            MessageBox.Show("无法开始框选外框: " + ex.Message, "单张打印",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>回 CAD 框选图纸外框，返回 false 表示取消或无效。</summary>
    private bool TryPickFrame(out double width, out double height, out double minX, out double minY, out double maxX, out double maxY)
    {
        width = height = minX = minY = maxX = maxY = 0;
        var editor = _document.Editor;

        var first = editor.GetPoint(new PromptPointOptions("\n选择图纸外框第一个角点: "));
        if (first.Status != PromptStatus.OK)
        {
            return false;
        }

        CadPickDiagnostics.LogPoint("single-print:frame", "firstReturnedUcs", first.Value.X, first.Value.Y, first.Value.Z);
        var second = editor.GetCorner(new PromptCornerOptions("\n选择图纸外框对角点: ", first.Value));
        if (second.Status != PromptStatus.OK)
        {
            return false;
        }

        CadPickDiagnostics.LogPoint("single-print:frame", "secondReturnedUcs", second.Value.X, second.Value.Y, second.Value.Z);
        var ucsP1 = first.Value;
        var ucsP2 = second.Value;

        // 构建 UCS → DCS 变换矩阵，等价于 acedTrans(point, UCS, DCS)
        var ucsToDcs = BatchPlotCommands.BuildUcsToDcsMatrix(editor);

        // UCS 矩形四个角点一步到位变换到 DCS，不在中间环节取包围盒
        var corners = new[]
        {
            new Point3d(ucsP1.X, ucsP1.Y, 0).TransformBy(ucsToDcs),
            new Point3d(ucsP2.X, ucsP1.Y, 0).TransformBy(ucsToDcs),
            new Point3d(ucsP1.X, ucsP2.Y, 0).TransformBy(ucsToDcs),
            new Point3d(ucsP2.X, ucsP2.Y, 0).TransformBy(ucsToDcs)
        };

        minX = corners.Min(p => p.X);
        minY = corners.Min(p => p.Y);
        maxX = corners.Max(p => p.X);
        maxY = corners.Max(p => p.Y);
        width = maxX - minX;
        height = maxY - minY;
        if (width <= 1e-6 || height <= 1e-6)
        {
            MessageBox.Show("选择的图纸外框宽度或高度无效。", "单张打印", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    /// <summary>根据框选尺寸识别纸张（必要时弹自定义比例），填充界面。</summary>
    private void DetectAndFill()
    {
        // 清理上一次的临时任意纸张注册，避免残留。
        CleanupCustomPaper();

        try
        {
            // 只在 LA_pdf 缺失时安装。
            var plottersDir = AcadPlotterInstaller.GetPlottersDirectory();
            var installedPlotter = Path.Combine(plottersDir, AcadPlotterInstaller.PreferredPdfPlotter);
            var installedPmp = Path.Combine(plottersDir, "PMP Files", "LA_pdf.pmp");
            if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
            {
                var installResult = AcadPlotterInstaller.InstallBundledPlotter();
                if (!installResult.Installed)
                {
                    throw new InvalidOperationException("LA_pdf 打印机配置不完整: " + installResult.Message);
                }

                plottersDir = AcadPlotterInstaller.GetPlottersDirectory();
                installedPlotter = Path.Combine(plottersDir, AcadPlotterInstaller.PreferredPdfPlotter);
                installedPmp = Path.Combine(plottersDir, "PMP Files", "LA_pdf.pmp");
                if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
                {
                    throw new InvalidOperationException("LA_pdf 打印机安装后仍未找到 PC3/PC5 或 PMP 文件。");
                }
            }

            _isArbitraryPaper = false;
            var candidates = PaperSizeDetector.DetectCandidates(_width, _height);
            if (candidates.Count == 0)
            {
                _isArbitraryPaper = true;
                var guessedScale = PaperSizeDetector.GuessScale(_width, _height);
                var scaleForm = new CustomScaleForm(_width, _height, guessedScale);
                if (CadDialog.ShowModal(scaleForm, Window.GetWindow(this)) != true)
                {
                    return;
                }

                if (scaleForm.SelectedStandardPaper != null)
                {
                    _isArbitraryPaper = false;
                    candidates = new List<PaperDetection> { scaleForm.SelectedStandardPaper };
                }
                else
                {
                    var scale = scaleForm.SelectedScale;
                    var paperW = scaleForm.PaperWidthMm;
                    var paperH = scaleForm.PaperHeightMm;

                    try
                    {
                        _customPmpPath = Path.Combine(plottersDir, "PMP Files", "LA_pdf.pmp");
                        if (!File.Exists(_customPmpPath))
                        {
                            throw new FileNotFoundException("LA_pdf.pmp 不存在，无法注册任意纸张。", _customPmpPath);
                        }

                        _customPaperRegistration = PmpCustomPaper.RegisterCustomPaper(_customPmpPath, paperW, paperH)
                            ?? throw new InvalidOperationException("LA_pdf.pmp 注册任意纸张失败。");
                        _forceCustomPaperReload = _customPaperRegistration.WasAdded;
#if AUTOCAD
                        var attachment = AcadPlotterInstaller.EnsureActivePdfPmpAttachment(
                            installedPlotter,
                            _customPmpPath,
                            forceRewrite: _customPaperRegistration.WasAdded);
                        if (!attachment.Success)
                        {
                            throw new InvalidOperationException("LA_pdf.pc3 关联当前 PMP 失败：" + attachment.Message);
                        }
                        _forceCustomPaperReload |= attachment.Changed;
#endif
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("任意纸张注册失败，已停止打印，避免回退到错误纸张。", ex);
                    }

                    candidates = new List<PaperDetection>
                    {
                        new()
                        {
                            PaperName = _customPaperRegistration.PaperName,
                            PaperWidthMm = paperW,
                            PaperHeightMm = paperH,
                            ScaleValue = scale,
                            ScaleText = PaperSizeDetector.ToScaleText(scale),
                            Note = $"自定义纸张 {paperW:0.##} x {paperH:0.##} mm"
                        }
                    };
                }
            }

            _candidates = candidates;
            FillCandidates(candidates);
        }
        catch (Exception ex)
        {
            _document.Editor.WriteMessage("\n单张打印识别失败: " + ex.Message);
            MessageBox.Show("单张打印识别失败: " + ex.Message, "单张打印", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FillCandidates(IReadOnlyList<PaperDetection> candidates)
    {
        _paperCombo.Items.Clear();
        foreach (var candidate in candidates)
        {
            _paperCombo.Items.Add(
                $"{candidate.PaperName}    {candidate.ScaleText}    {candidate.PaperWidthMm:0.##} × {candidate.PaperHeightMm:0.##} mm");
        }
        _paperCombo.SelectedIndex = 0;

        // 比例显示随纸张切换更新
        _scaleLabel.Text = $"预估比例: {candidates[0].ScaleText}";
        _paperCombo.SelectionChanged -= PaperCombo_SelectionChanged;
        _paperCombo.SelectionChanged += PaperCombo_SelectionChanged;

        _areaLabel.Text = $"打印区域: {_width:0.##} × {_height:0.##}（图纸单位）";
        _outputPath.Text = BuildDefaultPath(GetSourceFile());

        _printButton.IsEnabled = true;
        _previewButton.IsEnabled = true;
    }

    private void PaperCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var paper = SelectedPaper;
        if (paper != null)
        {
            _scaleLabel.Text = $"预估比例: {paper.ScaleText}";
        }
    }

    private string GetSourceFile()
    {
        return string.IsNullOrWhiteSpace(_document.Database.Filename)
            ? _document.Name
            : _document.Database.Filename;
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        ExecutePlot(isPreview: true);
    }

    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        ExecutePlot(isPreview: false);
    }

    private void ExecutePlot(bool isPreview)
    {
        if (!isPreview)
        {
            ExecutePlotCore(isPreview: false);
            return;
        }

        if (SelectedPaper == null) return;
        var window = Window.GetWindow(this);
        if (window == null) return;
        try
        {
            // 原生预览循环必须由文档命令启动，期间主面板隐藏，退出后统一恢复。
            PendingLibraryInput.Queue(window, () => ExecutePlotCore(isPreview: true),
                expectedDocument: _document, commandName: "ZBP_SINGLE_PREVIEW");
        }
        catch (Exception ex)
        {
            CadPickDiagnostics.Log("preview-queue-error", ex.ToString());
            MessageBox.Show("无法开始打印预览: " + ex.Message, "单张打印",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExecutePlotCore(bool isPreview)
    {
        var paper = SelectedPaper;
        if (paper == null)
        {
            return;
        }

        var editor = _document.Editor;
        var sourceFile = GetSourceFile();
        var outputPath = OutputPath;
        var styleSheet = SelectedStyle;
        var layoutName = LayoutManager.Current.CurrentLayout;
        var isPaperSpace = !_document.Database.TileMode;
        var baseName = Path.GetFileNameWithoutExtension(sourceFile);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Drawing";
        }

        var job = new PlotJob
        {
            IsManualWindow = true,
            IsDcsWindow = true,
            SourceFile = sourceFile,
            SpaceName = layoutName,
            IsPaperSpace = isPaperSpace,
            DrawingNumber = baseName,
            Title = baseName,
            PaperName = paper.PaperName,
            ScaleText = paper.ScaleText,
            SizeText = $"{_width:0.##} x {_height:0.##}",
            PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
            DetectionNote = "单张打印：用户框选图纸外框",
            PaperWidthMm = paper.PaperWidthMm,
            PaperHeightMm = paper.PaperHeightMm,
            MinX = _minX,
            MinY = _minY,
            MaxX = _maxX,
            MaxY = _maxY,
            OutputPath = outputPath,
            LeavePaperMargin = LeavePaperMargin,
            PaperMarginMm = PaperMarginMm,
            RequireExactPaperSize = _isArbitraryPaper,
            UseExactWindowScale = _isArbitraryPaper,
            DetectedRequiresCustomPaperRegistration = _isArbitraryPaper,
            CustomPaperWasAdded = _forceCustomPaperReload
        };

        try
        {
            // 单张与批打共用纸张准备：正值留白必须使用扩大的物理纸张。
            var preparation = CustomPaperBatchPreparer.Prepare(new[] { job }, _deviceName);
            _preparedPapers.AddRange(preparation.Registrations.Where(registration => registration.WasAdded));
            job.CustomPaperWasAdded |= _forceCustomPaperReload;
            if (job.LeavePaperMargin && job.PaperMarginMm > 0 && job.EffectivePaperWidthMm <= 0)
                throw new InvalidOperationException("当前打印机不支持扩大纸张留白，请使用 LA_pdf 打印机。");
            CadPickDiagnostics.Log("single-paper", $"preview={isPreview} marginEnabled={job.LeavePaperMargin} marginMm={job.PaperMarginMm} paper={job.PaperWidthMm}x{job.PaperHeightMm} effective={job.EffectivePaperWidthMm}x{job.EffectivePaperHeightMm}");
            SaveLastPlotOptions(_settings, _deviceName, styleSheet);
            if (isPreview)
            {
                CadPickDiagnostics.Log("preview", "ENTER single preview");
                CadPickDiagnostics.Dump("single-preview-enter");
                PlotterService.Preview(job, _deviceName, styleSheet, _document);
                CadPickDiagnostics.Log("preview", "RETURN single preview");
                editor.WriteMessage("\n单张打印预览已结束。");
            }
            else
            {
                PlotterService.Plot(job, _deviceName, styleSheet, _document, _settings);
                editor.WriteMessage($"\n单张打印完成: {outputPath}");
                RevealFileInExplorer(outputPath);
                MessageBox.Show(
                    $"单张打印完成。\n纸张: {paper.PaperName} {paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm\n文件: {outputPath}",
                    "单张打印",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            CadPickDiagnostics.Log(isPreview ? "preview-error" : "print-error", ex.ToString());
            editor.WriteMessage("\n单张打印失败: " + ex.Message);
            MessageBox.Show("单张打印失败: " + ex.Message, "单张打印", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CleanupCustomPaper();
            if (isPreview) CadPickDiagnostics.Log("preview", "CLEANUP single preview");
        }
    }

    private void CleanupCustomPaper()
    {
        if (_preparedPapers.Count > 0)
        {
            var pmpPath = Path.Combine(AcadPlotterInstaller.GetPlottersDirectory(), "PMP Files", "LA_pdf.pmp");
            foreach (var registration in _preparedPapers)
                PmpCustomPaper.RemoveCustomPaper(pmpPath, registration.PaperName);
            _preparedPapers.Clear();
        }
        if (_customPmpPath != null && _customPaperRegistration?.WasAdded == true)
        {
            PmpCustomPaper.RemoveCustomPaper(_customPmpPath, _customPaperRegistration.PaperName);
        }

        _customPmpPath = null;
        _customPaperRegistration = null;
        _forceCustomPaperReload = false;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "pdf",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            FileName = Path.GetFileName(_outputPath.Text),
            InitialDirectory = Path.GetDirectoryName(_outputPath.Text),
            OverwritePrompt = true,
            Title = "选择 PDF 保存位置"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _outputPath.Text = dialog.FileName;
        }
    }

    private void StyleSettingsButton_Click(object sender, RoutedEventArgs e)
        => PlotStyleManager.EditSelectedStyle(Window.GetWindow(this), _styleCombo.SelectedItem?.ToString());

    private void LeaveMargin_CheckChanged(object sender, RoutedEventArgs e)
        => _marginInput.IsEnabled = _leaveMargin.IsChecked == true;

    /// <summary>初始化留白下拉列表，语义与 BatchPlotForm.InitMarginCombo 一致。</summary>
    private static void InitMarginCombo(ComboBox combo, int width, double savedValue)
    {
        combo.Width = width;
        combo.Items.Clear();
        for (var n = 1; n <= 10; n++)
        {
            combo.Items.Add(new MarginOption { Value = n });
            combo.Items.Add(new MarginOption { Value = -n });
        }

        var bestIdx = 0;
        var bestDiff = double.MaxValue;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is MarginOption opt)
            {
                var diff = Math.Abs(opt.Value - savedValue);
                if (diff < bestDiff) { bestDiff = diff; bestIdx = i; }
            }
        }
        combo.SelectedIndex = bestIdx;
    }

    /// <summary>读取留白下拉列表的选中值（毫米）。</summary>
    private static double ReadMarginValue(ComboBox combo)
        => combo.SelectedItem is MarginOption opt ? opt.Value : 1.0;

    private static string BuildDefaultPath(string sourceFile)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFile);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Drawing";
        }

        var directory = Path.GetDirectoryName(sourceFile);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return Path.Combine(directory, baseName + ".pdf");
    }

    /// <summary>选择单张打印使用的 PDF 打印机和打印样式表。</summary>
    private static (string DeviceName, string StyleSheet) ResolveSinglePlotOptions(
        AppSettings settings,
        IReadOnlyList<string> styles)
    {
        using var plotSettings = new PlotSettings(true);
        var validator = PlotSettingsValidator.Current;
        var devices = validator.GetPlotDeviceList()
            .Cast<object>()
            .Select(value => value?.ToString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var device = FindPlotOption(devices, AcadPlotterInstaller.PreferredPdfPlotter)
            ?? FindPlotOption(devices, settings.LastPlotDevice)
            ?? devices.FirstOrDefault(value => value.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? throw new InvalidOperationException("没有找到可用的 PDF 打印机。");

        var style = PlotStyleManager.ResolvePreferredStyle(styles, settings.LastStyleSheet);
        return (device, style);
    }

    private static void SaveLastPlotOptions(AppSettings settings, string deviceName, string styleSheet)
    {
        var style = PlotStyleManager.NormalizeStyleName(styleSheet);
        settings.LastPlotDevice = deviceName;
        if (!string.IsNullOrEmpty(style))
        {
            settings.LastStyleSheet = style;
        }
        AppSettingsStore.TrySave(settings, out _);
    }

    private static string? FindPlotOption(IEnumerable<string> values, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return null;
        }

        return values.FirstOrDefault(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            ?? values.FirstOrDefault(value => value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static void RevealFileInExplorer(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{Path.GetFullPath(filePath)}\"",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}
