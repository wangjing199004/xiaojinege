using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZwcadBatchPlot;

internal static class PaperAndLogRegression
{
    private static void Check(bool result) { if (!result) throw new Exception("paper/log regression failed"); }
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "XjgRegression_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "PMP Files"));
        AcadPlotterInstaller.Root = root;
        File.WriteAllText(Path.Combine(root, "LA_pdf"), "test");
        File.WriteAllText(Path.Combine(root, "PMP Files", "LA_pdf.pmp"), "test");
        var job = new PlotJob { PaperWidthMm = 841, PaperHeightMm = 594, LeavePaperMargin = true, PaperMarginMm = 5 };
        CustomPaperBatchPreparer.Prepare(new[] { job }, "LA_pdf");
        Check(job.EffectivePaperWidthMm == 851 && job.EffectivePaperHeightMm == 604);
        Check(PmpCustomPaper.Last[0].WidthMm == 851 && PmpCustomPaper.Last[0].HeightMm == 604 && job.RequireExactPaperSize);
        job.PaperMarginMm = -5;
        CustomPaperBatchPreparer.Prepare(new[] { job }, "LA_pdf");
        Check(job.EffectivePaperWidthMm == 0 && job.EffectivePaperHeightMm == 0 && !job.RequiresCustomPaperRegistration);
        job.LeavePaperMargin = false;
        job.DetectedRequiresCustomPaperRegistration = true;
        CustomPaperBatchPreparer.Prepare(new[] { job }, "LA_pdf");
        Check(PmpCustomPaper.Last[0].WidthMm == 841 && job.RequireExactPaperSize && job.UseExactWindowScale);
        var source = Path.Combine(root, "sample.log");
        var content = string.Concat(Enumerable.Repeat("操作日志\n", 10000));
        File.WriteAllText(source, content);
        Check(OperationLogStore.ListFiles(new[] { root, root }).Count == 1);
        Check(OperationLogStore.ReadTail(source, 1024).Contains("仅显示日志末尾"));
        var target = Path.Combine(root, "export.log");
        OperationLogStore.Export(source, target);
        Check(File.ReadAllText(target) == content);
        try { OperationLogStore.Export(source, source); throw new Exception("source overwritten"); }
        catch (InvalidOperationException) { }
        Console.WriteLine("PASS: expanded paper size/registration, negative margin reset, arbitrary paper, bounded log preview, full export, source protection");
    }
}

namespace ZwcadBatchPlot
{
    public sealed class PlotJob
    {
        public bool LeavePaperMargin, DetectedRequiresCustomPaperRegistration, RequiresCustomPaperRegistration,
            RequireExactPaperSize, UseExactWindowScale, CustomPaperWasAdded, IsPaperSpace;
        public double PaperMarginMm, PaperWidthMm, PaperHeightMm, EffectivePaperWidthMm, EffectivePaperHeightMm;
        public string PaperName = "A1";
    }
    public static class AcadPlotterInstaller
    {
        public static string Root = "";
        public const string PreferredPdfPlotter = "LA_pdf", PreferredDwfPlotter = "LA_dwf";
        public static string GetPlottersDirectory() => Root;
        public sealed class Result { public bool Installed, Written; public string DeviceName = "", Message = ""; }
        public static Result InstallDwfPlotter() => new();
        public static Result InstallBundledPlotter() => new();
        public static void RefreshPlotterDevicesIfNeeded(bool refresh) { }
    }
    public static class PmpCustomPaper
    {
        public sealed class Registration { public bool WasAdded; }
        public sealed class PaperRequest { public double WidthMm, HeightMm; }
        public static List<PaperRequest> Last = new();
        public static List<Registration> RegisterCustomPapers(string path, List<PaperRequest> requests)
        { Last = requests; return requests.Select(_ => new Registration()).ToList(); }
    }
}
