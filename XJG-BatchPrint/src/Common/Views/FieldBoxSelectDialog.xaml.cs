using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>编辑图框库记录时传入的已配置字段与纸张。</summary>
public sealed class FieldBoxSelectInitialState
{
   public LocalRectangle TitleRegion { get; set; } = new();
   public LocalRectangle DrawingNumberRegion { get; set; } = new();
   public LocalRectangle DateRegion { get; set; } = new();
   public LocalRectangle RevisionRegion { get; set; } = new();
   public LocalRectangle PhaseRegion { get; set; } = new();
   public LocalRectangle Info1Region { get; set; } = new();
   public LocalRectangle Info2Region { get; set; } = new();
   public string PaperName { get; set; } = "";
   public double PaperWidthMm { get; set; }
   public double PaperHeightMm { get; set; }
}

/// <summary>
/// 新增或编辑图框字段框选对话框。
/// 图名/图号为必选，日期/版次/设计阶段/信息1/信息2可选框选。
/// 每个已选字段在图中以红色临时矩形框 + 对角线 + 字段名文字标识。
/// 由 CAD 管理模态生命周期，框选期间可缩放/平移；调用方用
/// <c>ShowFieldDialog</c> 显示并等待，关闭后读 <see cref="Accepted"/>。
/// 使用 Accepted 承载确认结果。
/// </summary>
public sealed partial class FieldBoxSelectDialog : Window
{
   private readonly Editor _editor;
   private readonly Matrix3d _inverseBlockTransform;
   private readonly Matrix3d _blockTransform;
   private readonly TransientFrameMarkers _markers;

   // 存储每个已选字段的世界坐标角点，用于对话框重新显示时刷新全部临时标识。
   private readonly Dictionary<string, (Point3d Corner1, Point3d Corner2)> _fieldCorners = new(StringComparer.Ordinal);

   // 打印范围（外框）的世界坐标角点，随用户重新框选而更新。
   private (Point3d Corner1, Point3d Corner2) _printAreaCorners;

   /// <summary>打印范围（块局部坐标），对话框关闭后由调用方读取。</summary>
   public LocalRectangle ReferenceFrame { get; set; }

   public LocalRectangle TitleRegion { get; private set; } = new();
   public LocalRectangle DrawingNumberRegion { get; private set; } = new();
   public LocalRectangle DateRegion { get; private set; } = new();
   public LocalRectangle RevisionRegion { get; private set; } = new();
   public LocalRectangle PhaseRegion { get; private set; } = new();
   public LocalRectangle Info1Region { get; private set; } = new();
   public LocalRectangle Info2Region { get; private set; } = new();

   /// <summary>关闭后为 true 表示用户点了「确定」或「跳过」；取消为 false。</summary>
   public bool Accepted { get; private set; }

   // 纸张设置与矩形框批打共用同一候选识别策略；下拉项直接对应完整纸张结果，
   // 避免名称、物理尺寸和比例被分别修改后彼此不一致。
   private readonly PaperSizeDetector.DetectionOptions _paperDetectionOptions;
   private IReadOnlyList<PaperDetection> _paperOptions = Array.Empty<PaperDetection>();

   // 「套用已有图框」：仅列出与当前识别纸张尺寸兼容的库条目。
   private List<TitleBlockDefinition> _compatibleLibraryEntries = new();
   private bool _applyingLibraryTemplate;
   private bool _markersInitialized;

   private bool picking;
   private CadPickTrace? _pickTrace;
#if AUTOCAD
   private EditorUserInteraction? _cadPickUi;
#endif

   private void RequestPick(Action pick)
   {
       if (picking) return;
       CadPickDiagnostics.Log("action", "BEGIN " + pick.Method.Name);
       try { pick(); }
       catch (System.Exception ex)
       {
           CadPickDiagnostics.Log("pick-error", ex.ToString());
           MessageBox.Show(this, "框选失败: " + ex.Message, Title,
               MessageBoxButton.OK, MessageBoxImage.Warning);
       }
       finally { CadPickDiagnostics.Log("action", "END " + pick.Method.Name); }
   }

   private PaperDetection? SelectedPaper =>
       _paperName.SelectedIndex >= 0 && _paperName.SelectedIndex < _paperOptions.Count
           ? _paperOptions[_paperName.SelectedIndex]
           : null;

   public string PaperName => SelectedPaper?.PaperName ?? "";
   public double PaperWidthMm => SelectedPaper?.PaperWidthMm ?? 0d;
   public double PaperHeightMm => SelectedPaper?.PaperHeightMm ?? 0d;

   public FieldBoxSelectDialog(Editor editor, Matrix3d inverseBlockTransform,
       Matrix3d blockTransform, TransientFrameMarkers markers, LocalRectangle referenceFrame,
       IReadOnlyList<PaperDetection> paperOptions,
       PaperSizeDetector.DetectionOptions paperDetectionOptions,
       FieldBoxSelectInitialState? initialState = null)
   {
       InitializeComponent();
       CadPickTrace.AttachWindow(this);
       CadPickDiagnostics.Log("field-dialog", "inverse=" + inverseBlockTransform + " blockTransform=" + blockTransform);

       _editor = editor;
       _inverseBlockTransform = inverseBlockTransform;
       _blockTransform = blockTransform;
       _markers = markers;
       _paperDetectionOptions = paperDetectionOptions;
       ReferenceFrame = referenceFrame;

       // 用 4 个局部角点完整变换后取得世界包盒；不能只变换一对对角点，
       // 否则旋转块在窗口重新显示时会把初始红框替换成错误范围。
       var worldFrame = RectangleGeometry.TransformRectangle(referenceFrame, blockTransform);
       _printAreaCorners = (
           new Point3d(worldFrame.MinX, worldFrame.MinY, 0),
           new Point3d(worldFrame.MaxX, worldFrame.MaxY, 0));

       _libraryTemplateCombo.SelectionChanged += (_, _) => OnLibraryTemplateSelected();
       _paperName.SelectionChanged += (_, _) =>
       {
           if (!_applyingLibraryTemplate)
           {
               RefreshLibraryTemplateList();
           }
       };

       UpdatePrintAreaStatus();

       if (initialState != null)
       {
           ApplyInitialState(initialState);
       }

       // 纸张：默认按打印范围自动识别，重新框选打印范围时同步刷新，也可手动修改。
       ApplyPaperOptions(
           paperOptions,
           initialState?.PaperName,
           initialState?.PaperWidthMm ?? 0d,
           initialState?.PaperHeightMm ?? 0d);
       RefreshLibraryTemplateList();

       // 显式处理 Esc，与 Accepted 的结果语义保持一致。
       PreviewKeyDown += (_, e) =>
       {
           if (e.Key == System.Windows.Input.Key.Escape)
           {
               Close();
           }
       };

       // 兜底释放宿主交互对象；正常路径由 EndCadPick 完成。
       Closed += (_, _) =>
       {
           _pickTrace?.Dispose();
           _pickTrace = null;
           CadPickDiagnostics.Log("field-dialog", "CLOSED accepted=" + Accepted);
#if AUTOCAD
           var interaction = _cadPickUi;
           _cadPickUi = null;
           interaction?.Dispose();
#endif
       };

       // 首次显示时刷新红色临时框。只用 UpdateScreen，不做 Regen：
       // 整图重生成会在非模态 Show 期间拖住 UI 线程，复杂图面会出现白块对话框并长时间卡顿。
       // Transient DirectTopmost 标识经 UpdateScreen 即可看见。
       ContentRendered += (_, _) =>
       {
           if (_markersInitialized)
           {
               return;
           }

           _markersInitialized = true;
           RefreshAllMarkers();
           _markers.RefreshDisplay(regenerate: false);
       };
   }

   /// <summary>
   /// 按当前纸张物理尺寸筛选库中兼容条目并刷新下拉。
   /// 重新框选打印范围 / 更换纸张候选后应再次调用。
   /// </summary>
   private void RefreshLibraryTemplateList()
   {
       if (_applyingLibraryTemplate)
       {
           return;
       }

       var previousBlockName = (_libraryTemplateCombo.SelectedItem as LibraryTemplateItem)?.Definition?.BlockName;
       _compatibleLibraryEntries = LoadCompatibleLibraryEntries();

       _applyingLibraryTemplate = true;
       try
       {
           _libraryTemplateCombo.Items.Clear();
           _libraryTemplateCombo.Items.Add(new LibraryTemplateItem(null, "（不套用）"));
           foreach (var entry in _compatibleLibraryEntries)
           {
               _libraryTemplateCombo.Items.Add(new LibraryTemplateItem(entry, FormatLibraryEntryDisplay(entry)));
           }

           var restoreIndex = 0;
           if (!string.IsNullOrWhiteSpace(previousBlockName))
           {
               for (var i = 1; i < _libraryTemplateCombo.Items.Count; i++)
               {
                   if (_libraryTemplateCombo.Items[i] is LibraryTemplateItem item
                       && item.Definition != null
                       && string.Equals(item.Definition.BlockName, previousBlockName, StringComparison.OrdinalIgnoreCase))
                   {
                       restoreIndex = i;
                       break;
                   }
               }
           }

           _libraryTemplateCombo.SelectedIndex = _libraryTemplateCombo.Items.Count > 0 ? restoreIndex : -1;

           var hasCompatible = _compatibleLibraryEntries.Count > 0;
           _libraryTemplateCombo.IsEnabled = hasCompatible;
           _libraryTemplateHint.Text = hasCompatible
               ? $"已列出 {_compatibleLibraryEntries.Count} 个纸张兼容的已有图框，选择后立即套用字段与纸张。"
               : "无与当前纸张尺寸兼容的已有图框；请手动框选字段。";
           _libraryTemplateHint.Foreground = hasCompatible ? Brushes.DimGray : Brushes.DarkOrange;
       }
       finally
       {
           _applyingLibraryTemplate = false;
       }
   }

   private List<TitleBlockDefinition> LoadCompatibleLibraryEntries()
   {
       if (!TryGetCurrentPaperSizeMm(out var widthMm, out var heightMm))
       {
           return new List<TitleBlockDefinition>();
       }

       var toleranceMm = _paperDetectionOptions.LongPaperShortSideToleranceMm;
       try
       {
           var library = TitleBlockLibraryStore.Load();
           return library.Blocks
               .Where(b => b != null
                   && !string.IsNullOrWhiteSpace(b.BlockName)
                   && PaperSizeDetector.ArePhysicalSizesCompatible(
                       b.PaperWidthMm,
                       b.PaperHeightMm,
                       widthMm,
                       heightMm,
                       toleranceMm))
               .OrderBy(b => b.BlockName, StringComparer.OrdinalIgnoreCase)
               .ThenBy(b => b.PaperName, StringComparer.OrdinalIgnoreCase)
               .ToList();
       }
       catch
       {
           return new List<TitleBlockDefinition>();
       }
   }

   private bool TryGetCurrentPaperSizeMm(out double widthMm, out double heightMm)
   {
       widthMm = 0d;
       heightMm = 0d;
       var paper = SelectedPaper ?? (_paperOptions.Count > 0 ? _paperOptions[0] : null);
       if (paper == null || paper.PaperWidthMm <= 0d || paper.PaperHeightMm <= 0d)
       {
           return false;
       }

       widthMm = paper.PaperWidthMm;
       heightMm = paper.PaperHeightMm;
       return true;
   }

   private static string FormatLibraryEntryDisplay(TitleBlockDefinition entry)
   {
       var sizeText = entry.PaperWidthMm > 0d && entry.PaperHeightMm > 0d
           ? $"{entry.PaperWidthMm:0.##}×{entry.PaperHeightMm:0.##} mm"
           : "";
       if (string.IsNullOrWhiteSpace(entry.PaperName))
       {
           return string.IsNullOrWhiteSpace(sizeText)
               ? entry.BlockName
               : $"{entry.BlockName} · {sizeText}";
       }

       return string.IsNullOrWhiteSpace(sizeText)
           ? $"{entry.BlockName} · {entry.PaperName}"
           : $"{entry.BlockName} · {entry.PaperName} {sizeText}";
   }

   private void OnLibraryTemplateSelected()
   {
       if (_applyingLibraryTemplate)
       {
           return;
       }

       if (_libraryTemplateCombo.SelectedItem is not LibraryTemplateItem item || item.Definition == null)
       {
           return;
       }

       ApplyLibraryTemplate(item.Definition);
   }

   /// <summary>
   /// 将库条目的字段区域与纸张套用到当前新定义：相对坐标按源 CoordinateMode
   /// 还原到<strong>当前</strong> ReferenceFrame 的块局部绝对矩形，并刷新红框。
   /// </summary>
   private void ApplyLibraryTemplate(TitleBlockDefinition template)
   {
       _applyingLibraryTemplate = true;
       try
       {
           var frame = ReferenceFrame;
           var mode = template.CoordinateMode;

           ApplyConvertedField("图名", template.TitleRegion, mode, frame, r => TitleRegion = r, _titleStatus);
           ApplyConvertedField("图号", template.DrawingNumberRegion, mode, frame, r => DrawingNumberRegion = r, _numberStatus);
           ApplyConvertedField("日期", template.DateRegion, mode, frame, r => DateRegion = r, _dateStatus);
           ApplyConvertedField("版次", template.RevisionRegion, mode, frame, r => RevisionRegion = r, _revisionStatus);
           ApplyConvertedField("设计阶段", template.PhaseRegion, mode, frame, r => PhaseRegion = r, _phaseStatus);
           ApplyConvertedField("信息1", template.Info1Region, mode, frame, r => Info1Region = r, _info1Status);
           ApplyConvertedField("信息2", template.Info2Region, mode, frame, r => Info2Region = r, _info2Status);

           EnsureAndSelectPaperFromTemplate(template);
           RefreshAllMarkers();
           _libraryTemplateHint.Text = $"已套用：{FormatLibraryEntryDisplay(template)}（仍可手动改框选或纸张）";
           _libraryTemplateHint.Foreground = Brushes.Green;
       }
       finally
       {
           _applyingLibraryTemplate = false;
       }
   }

   private void ApplyConvertedField(
       string fieldName,
       LocalRectangle storedRegion,
       string? coordinateMode,
       LocalRectangle referenceFrame,
       Action<LocalRectangle> setRegion,
       TextBlock statusLabel)
   {
       LocalRectangle absolute;
       if (!storedRegion.HasArea())
       {
           absolute = new LocalRectangle();
       }
       else if (string.Equals(coordinateMode, "World", StringComparison.OrdinalIgnoreCase))
       {
           // World：库内为世界坐标，转到当前块局部（与编辑回显一致）。
           var p1 = new Point3d(storedRegion.MinX, storedRegion.MinY, 0).TransformBy(_inverseBlockTransform);
           var p2 = new Point3d(storedRegion.MaxX, storedRegion.MaxY, 0).TransformBy(_inverseBlockTransform);
           absolute = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
       }
       else
       {
           absolute = TitleBlockRegionConverter.FromStoredRelative(storedRegion, referenceFrame, coordinateMode);
       }

       setRegion(absolute);
       UpdateStatus(statusLabel, absolute);

       if (!absolute.HasArea())
       {
           _fieldCorners.Remove(fieldName);
           _markers.Remove(fieldName);
           return;
       }

       var worldRegion = RectangleGeometry.TransformRectangle(absolute, _blockTransform);
       _fieldCorners[fieldName] = (
           new Point3d(worldRegion.MinX, worldRegion.MinY, 0),
           new Point3d(worldRegion.MaxX, worldRegion.MaxY, 0));
   }

   private void EnsureAndSelectPaperFromTemplate(TitleBlockDefinition template)
   {
       if (string.IsNullOrWhiteSpace(template.PaperName)
           || template.PaperWidthMm <= 0d
           || template.PaperHeightMm <= 0d)
       {
           return;
       }

       var options = _paperOptions.ToList();
       var existingIndex = options.FindIndex(x =>
           string.Equals(x.PaperName, template.PaperName, StringComparison.OrdinalIgnoreCase)
           && Math.Abs(x.PaperWidthMm - template.PaperWidthMm) <= 0.01d
           && Math.Abs(x.PaperHeightMm - template.PaperHeightMm) <= 0.01d);

       if (existingIndex < 0)
       {
           var width = Math.Abs(_printAreaCorners.Corner2.X - _printAreaCorners.Corner1.X);
           var height = Math.Abs(_printAreaCorners.Corner2.Y - _printAreaCorners.Corner1.Y);
           var scaleX = template.PaperWidthMm > 0 ? width / template.PaperWidthMm : 0d;
           var scaleY = template.PaperHeightMm > 0 ? height / template.PaperHeightMm : 0d;
           var scale = scaleX > 0 && scaleY > 0 ? (scaleX + scaleY) / 2d : Math.Max(scaleX, scaleY);
           if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
           {
               scale = 1d;
           }

           options.Insert(0, new PaperDetection
           {
               PaperName = template.PaperName,
               PaperWidthMm = template.PaperWidthMm,
               PaperHeightMm = template.PaperHeightMm,
               ScaleValue = scale,
               ScaleText = scale >= 1d ? "1:" + scale.ToString("0.###") : (1d / scale).ToString("0.###") + ":1",
               IsLong = template.PaperName.IndexOf('+') > 0,
               RequiresCustomPaper = string.Equals(
                   template.PaperName,
                   PaperSizeDetector.CustomPaperName,
                   StringComparison.OrdinalIgnoreCase),
               Note = "来自已有图框套用"
           });
       }

       ApplyPaperOptions(options, template.PaperName, template.PaperWidthMm, template.PaperHeightMm);
   }

   private sealed class LibraryTemplateItem
   {
       public LibraryTemplateItem(TitleBlockDefinition? definition, string display)
       {
           Definition = definition;
           Display = display;
       }

       public TitleBlockDefinition? Definition { get; }
       public string Display { get; }
       public override string ToString() => Display;
   }

   private void ApplyInitialState(FieldBoxSelectInitialState state)
   {
       TitleRegion = state.TitleRegion;
       DrawingNumberRegion = state.DrawingNumberRegion;
       DateRegion = state.DateRegion;
       RevisionRegion = state.RevisionRegion;
       PhaseRegion = state.PhaseRegion;
       Info1Region = state.Info1Region;
       Info2Region = state.Info2Region;

       AddInitialField("图名", TitleRegion, _titleStatus);
       AddInitialField("图号", DrawingNumberRegion, _numberStatus);
       AddInitialField("日期", DateRegion, _dateStatus);
       AddInitialField("版次", RevisionRegion, _revisionStatus);
       AddInitialField("设计阶段", PhaseRegion, _phaseStatus);
       AddInitialField("信息1", Info1Region, _info1Status);
       AddInitialField("信息2", Info2Region, _info2Status);
   }

   private void AddInitialField(string fieldName, LocalRectangle region, TextBlock statusLabel)
   {
       UpdateStatus(statusLabel, region);
       if (!region.HasArea())
       {
           return;
       }

       // 库内字段已转换为当前块的局部坐标；变回 WCS 后建立首次显示的红色临时框。
       var worldRegion = RectangleGeometry.TransformRectangle(region, _blockTransform);
       _fieldCorners[fieldName] = (
           new Point3d(worldRegion.MinX, worldRegion.MinY, 0),
           new Point3d(worldRegion.MaxX, worldRegion.MaxY, 0));
   }

   /// <summary>
   /// 根据已存储的世界坐标角点，重新绘制所有已选字段及打印范围的临时红色标识。
   /// 批量写入后只刷屏一次，避免逐框 UpdateScreen 造成连续闪烁。
   /// </summary>
   private void RefreshAllMarkers()
   {
       // 外框标识：不显示文字标签，避免外框过大时文字遮挡图面。
       _markers.SetBox("外框", _printAreaCorners.Corner1, _printAreaCorners.Corner2, null, refresh: false);
       foreach (var kv in _fieldCorners)
       {
           _markers.SetBox(kv.Key, kv.Value.Corner1, kv.Value.Corner2, kv.Key, refresh: false);
       }

       _markers.RefreshDisplay();
   }

   /// <summary>
   /// 重新框选打印范围。用户可在 CAD 中手动框选以修正自动识别的外包框。
   /// </summary>
   private void SelectPrintArea()
   {
       double detectedWidth = 0;
       double detectedHeight = 0;
       var picked = false;
       var status = "cancelled";
       BeginCadPick("print-area");
       try
       {
           CadPickDiagnostics.Log("prompt", "GetPoint begin");
           var first = _editor.GetPoint(new PromptPointOptions("\n框选图框打印外边界第一个角点: "));
           if (first.Status != PromptStatus.OK)
           {
               status = "first:" + first.Status;
               return;
           }

           CadPickDiagnostics.LogPoint("print-area", "firstReturnedUcs", first.Value.X, first.Value.Y, first.Value.Z);

           CadPickDiagnostics.Dump("before-second-point");
           CadPickDiagnostics.Log("prompt", "GetCorner begin (document-context-v3)");
           var cornerOptions = new PromptCornerOptions("\n框选图框打印外边界对角点: ", first.Value);
           var second = _editor.GetCorner(cornerOptions);
           if (second.Status != PromptStatus.OK)
           {
               status = "second:" + second.Status;
               return;
           }

           // 更新世界坐标角点（用于外框标记显示）
           CadPickDiagnostics.LogPoint("print-area", "secondReturnedUcs", second.Value.X, second.Value.Y, second.Value.Z);
           _printAreaCorners = (first.Value, second.Value);

           // 转换为块局部坐标，更新 ReferenceFrame
           var p1 = first.Value.TransformBy(_inverseBlockTransform);
           var p2 = second.Value.TransformBy(_inverseBlockTransform);
           ReferenceFrame = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
           CadPickDiagnostics.Log("region", $"print-area local=({p1})-({p2})");

           // 立即绘制新的外框临时标识
           _markers.SetBox("外框", first.Value, second.Value, null);
           UpdatePrintAreaStatus();

           detectedWidth = Math.Abs(second.Value.X - first.Value.X);
           detectedHeight = Math.Abs(second.Value.Y - first.Value.Y);
           picked = true;
           status = "ok";
       }
       finally
       {
           EndCadPick("print-area", status);
       }

       if (!picked)
       {
           return;
       }

       // 恢复窗体后再做纸张识别/弹窗，避免透明禁用态下嵌套对话框异常。
       ApplyPaperOptions(ArbitraryPaperPicker.DetectCandidatesOrPrompt(
           detectedWidth,
           detectedHeight,
           _paperDetectionOptions,
           this));
       // 打印范围变更后，兼容库条目可能变化；若仍选中某模板则按新 ReferenceFrame 重算字段。
       RefreshLibraryTemplateList();
       if (_libraryTemplateCombo.SelectedItem is LibraryTemplateItem stillSelected
           && stillSelected.Definition != null)
       {
           ApplyLibraryTemplate(stillSelected.Definition);
       }
   }

   /// <summary>
   /// 更新打印范围状态标签：第一行宽×高（世界坐标单位），第二行提示可手动修正自动识别边框。
   /// </summary>
   private void UpdatePrintAreaStatus()
   {
       var w = Math.Abs(_printAreaCorners.Corner2.X - _printAreaCorners.Corner1.X);
       var h = Math.Abs(_printAreaCorners.Corner2.Y - _printAreaCorners.Corner1.Y);
       _printAreaStatus.Text = $"{(int)w} × {(int)h}";
       _printAreaStatus.Foreground = Brushes.Green;
   }

   private bool ValidateRequiredFields()
   {
       if (!TitleRegion.HasArea() || !DrawingNumberRegion.HasArea())
       {
           MessageBox.Show(
               "图名和图号为必选项，请先点击对应\"框选\"按钮完成框选。",
               Title, MessageBoxButton.OK, MessageBoxImage.Warning);
           return false;
       }

       if (string.IsNullOrWhiteSpace(PaperName))
       {
           MessageBox.Show(
               "纸张名称不能为空。",
               Title, MessageBoxButton.OK, MessageBoxImage.Warning);
           return false;
       }

       return true;
   }

   private void OnSelectPrintAreaClick(object sender, RoutedEventArgs e) => RequestPick(SelectPrintArea);

   private void SelectTitle() { if (TryBoxSelect("图名", out var r)) { TitleRegion = r; UpdateStatus(_titleStatus, TitleRegion); } }
   private void ClearTitle() { TitleRegion = new LocalRectangle(); _fieldCorners.Remove("图名"); _markers.Remove("图名"); UpdateStatus(_titleStatus, TitleRegion); }

   private void SelectNumber() { if (TryBoxSelect("图号", out var r)) { DrawingNumberRegion = r; UpdateStatus(_numberStatus, DrawingNumberRegion); } }
   private void ClearNumber() { DrawingNumberRegion = new LocalRectangle(); _fieldCorners.Remove("图号"); _markers.Remove("图号"); UpdateStatus(_numberStatus, DrawingNumberRegion); }

   private void SelectDate() { if (TryBoxSelect("日期", out var r)) { DateRegion = r; UpdateStatus(_dateStatus, DateRegion); } }
   private void ClearDate() { ClearOptionalField("日期", r => DateRegion = r, _dateStatus); }

   private void SelectRevision() { if (TryBoxSelect("版次", out var r)) { RevisionRegion = r; UpdateStatus(_revisionStatus, RevisionRegion); } }
   private void ClearRevision() { ClearOptionalField("版次", r => RevisionRegion = r, _revisionStatus); }

   private void SelectPhase() { if (TryBoxSelect("设计阶段", out var r)) { PhaseRegion = r; UpdateStatus(_phaseStatus, PhaseRegion); } }
   private void ClearPhase() { ClearOptionalField("设计阶段", r => PhaseRegion = r, _phaseStatus); }

   private void SelectInfo1() { if (TryBoxSelect("信息1", out var r)) { Info1Region = r; UpdateStatus(_info1Status, Info1Region); } }
   private void ClearInfo1() { ClearOptionalField("信息1", r => Info1Region = r, _info1Status); }

   private void SelectInfo2() { if (TryBoxSelect("信息2", out var r)) { Info2Region = r; UpdateStatus(_info2Status, Info2Region); } }
   private void ClearInfo2() { ClearOptionalField("信息2", r => Info2Region = r, _info2Status); }

   private void OnSelectTitleClick(object sender, RoutedEventArgs e) => RequestPick(SelectTitle);
   private void OnClearTitleClick(object sender, RoutedEventArgs e) => ClearTitle();
   private void OnSelectNumberClick(object sender, RoutedEventArgs e) => RequestPick(SelectNumber);
   private void OnClearNumberClick(object sender, RoutedEventArgs e) => ClearNumber();
   private void OnSelectDateClick(object sender, RoutedEventArgs e) => RequestPick(SelectDate);
   private void OnClearDateClick(object sender, RoutedEventArgs e) => ClearDate();
   private void OnSelectRevisionClick(object sender, RoutedEventArgs e) => RequestPick(SelectRevision);
   private void OnClearRevisionClick(object sender, RoutedEventArgs e) => ClearRevision();
   private void OnSelectPhaseClick(object sender, RoutedEventArgs e) => RequestPick(SelectPhase);
   private void OnClearPhaseClick(object sender, RoutedEventArgs e) => ClearPhase();
   private void OnSelectInfo1Click(object sender, RoutedEventArgs e) => RequestPick(SelectInfo1);
   private void OnClearInfo1Click(object sender, RoutedEventArgs e) => ClearInfo1();
   private void OnSelectInfo2Click(object sender, RoutedEventArgs e) => RequestPick(SelectInfo2);
   private void OnClearInfo2Click(object sender, RoutedEventArgs e) => ClearInfo2();

   private void ClearOptionalField(string fieldName, Action<LocalRectangle> setRegion, TextBlock statusLabel)
   {
       setRegion(new LocalRectangle());
       _fieldCorners.Remove(fieldName);
       _markers.Remove(fieldName);
       UpdateStatus(statusLabel, new LocalRectangle());
   }

   private bool TryBoxSelect(string fieldName, out LocalRectangle region)
   {
       region = new LocalRectangle();
       var status = "cancelled";
       BeginCadPick("field:" + fieldName);
       try
       {
           var firstPrompt = $"框选{fieldName}区域第一个角点，或右键跳过: ";
           var secondPrompt = $"框选{fieldName}区域对角点: ";

           CadPickDiagnostics.Log("prompt", "GetPoint begin");
           var first = _editor.GetPoint(new PromptPointOptions(firstPrompt));
           if (first.Status != PromptStatus.OK)
           {
               status = "first:" + first.Status;
               return false;
           }

           CadPickDiagnostics.LogPoint("field:" + fieldName, "firstReturnedUcs", first.Value.X, first.Value.Y, first.Value.Z);

           CadPickDiagnostics.Dump("before-second-point");
           CadPickDiagnostics.Log("prompt", "GetCorner begin (document-context-v3)");
           var cornerOptions = new PromptCornerOptions(secondPrompt, first.Value);
           var second = _editor.GetCorner(cornerOptions);
           if (second.Status != PromptStatus.OK)
           {
               status = "second:" + second.Status;
               return false;
           }

           CadPickDiagnostics.LogPoint("field:" + fieldName, "secondReturnedUcs", second.Value.X, second.Value.Y, second.Value.Z);

           var p1 = first.Value.TransformBy(_inverseBlockTransform);
           var p2 = second.Value.TransformBy(_inverseBlockTransform);
           region = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
           CadPickDiagnostics.Log("region", $"field={fieldName} local=({p1})-({p2}) width={region.MaxX-region.MinX} height={region.MaxY-region.MinY}");

           // 存储世界坐标角点，用于对话框重新显示时刷新临时标识。
           _fieldCorners[fieldName] = (first.Value, second.Value);

           // 世界坐标绘制红色临时标识（红色矩形框 + 对角线 + 居中字段名文字），
           // 重新框选时自动替换旧标识。
           _markers.SetBox(fieldName, first.Value, second.Value, fieldName);
           status = "ok";
           return true;
       }
       finally
       {
           EndCadPick("field:" + fieldName, status);
       }
   }

   /// <summary>成对进入/释放宿主交互状态，不用 DoEvents 重入消息循环。</summary>
   private void BeginCadPick(string what)
   {
       CadPickDiagnostics.LogPickBegin(what);
       CadPickDiagnostics.Dump("before-interaction:" + what);
       picking = true;
       try
       {
           CadPickDiagnostics.ReleaseMouseCapture(this);
#if AUTOCAD
           _cadPickUi = _editor.StartUserInteraction(
               new System.Windows.Interop.WindowInteropHelper(this).Handle);
#else
           Hide();
           CadWindowFocus.ActivateCadWindow();
#endif
           // 取点时撤下辅助图元，保持用户的对象捕捉设置。
           _markers.Suspend();
           _pickTrace = new CadPickTrace(_editor);
           CadPickDiagnostics.LogPickReady(what);
       }
       catch
       {
           EndCadPick(what, "begin-failed");
           throw;
       }
   }

   private void EndCadPick(string what, string status)
   {
       _pickTrace?.Dispose();
       _pickTrace = null;
       CadPickDiagnostics.Dump("before-restore:" + what);
       try { _markers.Resume(); }
       finally
       {
           try
           {
#if AUTOCAD
               // End() 不能替代 IDisposable 的释放。
               var interaction = _cadPickUi;
               _cadPickUi = null;
               interaction?.Dispose();
#else
               CadWindowFocus.RestoreDialog(this);
#endif
           }
           finally
           {
               picking = false;
               CadPickDiagnostics.LogPickEnd(what, status);
               CadPickDiagnostics.Dump("after-restore:" + what);
           }
       }
   }

   private static void UpdateStatus(TextBlock label, LocalRectangle region)
   {
       if (region.HasArea())
       {
           label.Text = $"已选择 ({region.MinX:0.###},{region.MinY:0.###})-({region.MaxX:0.###},{region.MaxY:0.###})";
           label.Foreground = Brushes.Green;
       }
       else
       {
           label.Text = "(未选择)";
           label.Foreground = Brushes.DimGray;
       }
   }

   private void OnOkClick(object sender, RoutedEventArgs e) => Confirm();

   private void OnSkipClick(object sender, RoutedEventArgs e) => Skip();

   private void OnCancelClick(object sender, RoutedEventArgs e)
   {
       CadPickDiagnostics.Log("action", "CANCEL");
       Close();
   }

   private void Confirm()
   {
       CadPickDiagnostics.Log("action", "CONFIRM title=" + TitleRegion.HasArea()
           + " number=" + DrawingNumberRegion.HasArea() + " paper=" + PaperName);
       if (!ValidateRequiredFields())
       {
           return;
       }

       Accepted = true;
       Close();
   }

   private void Skip()
   {
       CadPickDiagnostics.Log("action", "SKIP");
       // 跳过仅留空可选字段，图名/图号仍为必选。
       if (!ValidateRequiredFields())
       {
           return;
       }

       ClearOptionalField("日期", r => DateRegion = r, _dateStatus);
       ClearOptionalField("版次", r => RevisionRegion = r, _revisionStatus);
       ClearOptionalField("设计阶段", r => PhaseRegion = r, _phaseStatus);
       ClearOptionalField("信息1", r => Info1Region = r, _info1Status);
       ClearOptionalField("信息2", r => Info2Region = r, _info2Status);
       Accepted = true;
       Close();
   }

   /// <summary>
   /// 主动指定任意纸张：按当前打印范围宽高弹出比例对话框。
   /// 长宽比接近标准图幅或 1/8 模数加长图时可选择目标图幅（任意比例）；否则按输入比例生成自定义纸张。
   /// 生成的候选加入下拉并选中（同名同尺寸项去重替换）。
   /// </summary>
   private void PickArbitraryPaper()
   {
       var width = Math.Abs(_printAreaCorners.Corner2.X - _printAreaCorners.Corner1.X);
       var height = Math.Abs(_printAreaCorners.Corner2.Y - _printAreaCorners.Corner1.Y);
       if (width <= 1e-6 || height <= 1e-6)
       {
           return;
       }

       var guessedScale = PaperSizeDetector.GuessScale(width, height);
       var scaleForm = new CustomScaleForm(
           width,
           height,
           guessedScale,
           ArbitraryPaperPicker.HintText,
           allowAspectRatioPapers: !_paperDetectionOptions.IncludeGenericDynamicTitleBlockPaper);
       if (CadDialog.ShowModal(scaleForm, this) != true)
       {
           return;
       }

       var arbitrary = ArbitraryPaperPicker.CreatePaperFromScaleForm(scaleForm, width, height);

       // 同名同尺寸项去重替换，避免重复添加。
       var options = _paperOptions.ToList();
       var existingIndex = options.FindIndex(x =>
           string.Equals(x.PaperName, arbitrary.PaperName, StringComparison.OrdinalIgnoreCase)
           && Math.Abs(x.PaperWidthMm - arbitrary.PaperWidthMm) <= 0.01d
           && Math.Abs(x.PaperHeightMm - arbitrary.PaperHeightMm) <= 0.01d);
       if (existingIndex >= 0)
       {
           options[existingIndex] = arbitrary;
       }
       else
       {
           options.Add(arbitrary);
       }

       ApplyPaperOptions(options, arbitrary.PaperName, arbitrary.PaperWidthMm, arbitrary.PaperHeightMm);
       RefreshLibraryTemplateList();
   }

   private void OnPickArbitraryPaperClick(object sender, RoutedEventArgs e) => PickArbitraryPaper();

   private void ApplyPaperOptions(
       IReadOnlyList<PaperDetection> paperOptions,
       string? preferredPaperName = null,
       double preferredPaperWidthMm = 0d,
       double preferredPaperHeightMm = 0d)
   {
       if (paperOptions.Count == 0)
       {
           throw new ArgumentException("至少需要一个纸张候选项。", nameof(paperOptions));
       }

       _paperOptions = paperOptions;
       var suppressLibraryRefresh = _applyingLibraryTemplate;
       _applyingLibraryTemplate = true;
       try
       {
           _paperName.Items.Clear();
           foreach (var paper in _paperOptions)
           {
               _paperName.Items.Add(PaperSizeDetector.FormatOption(paper));
           }

           var preferredIndex = -1;
           if (!string.IsNullOrWhiteSpace(preferredPaperName))
           {
               for (var i = 0; i < _paperOptions.Count; i++)
               {
                   var paper = _paperOptions[i];
                   if (string.Equals(paper.PaperName, preferredPaperName, StringComparison.OrdinalIgnoreCase)
                       && Math.Abs(paper.PaperWidthMm - preferredPaperWidthMm) <= 0.01d
                       && Math.Abs(paper.PaperHeightMm - preferredPaperHeightMm) <= 0.01d)
                   {
                       preferredIndex = i;
                       break;
                   }
               }
           }

           _paperName.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
       }
       finally
       {
           _applyingLibraryTemplate = suppressLibraryRefresh;
       }
   }
}
