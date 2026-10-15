using System;
using ZwcadBatchPlot;

internal static class DwgViewFitRegression
{
    private static void Check(bool result) { if (!result) throw new Exception("DWG view fit regression failed"); }
    internal static void Run()
    {
        var landscape = DwgViewFit.Calculate(100000, 600000, 184100, 659400, 16d / 9d);
        Check(landscape.CenterX == 142050 && landscape.CenterY == 629700);
        Check(landscape.Width >= 84100 * 1.09 && landscape.Height >= 59400 * 1.09);
        Check(Math.Abs(landscape.Width / landscape.Height - 16d / 9d) < 1e-10);
        var portrait = DwgViewFit.Calculate(-3000, -5000, -2406, -4159, 2);
        Check(portrait.CenterX == -2703 && portrait.CenterY == -4579.5);
        Check(portrait.Height > 841 && portrait.Width > 594);
        var wide = DwgViewFit.Calculate(0, 0, 10000, 100, 0.5);
        Check(wide.Width > 10000 && wide.Height > 100);
        var fallback = DwgViewFit.Calculate(0, 0, 100, 100, double.NaN);
        Check(Math.Abs(fallback.Width / fallback.Height - 16d / 9d) < 1e-10);
        try { DwgViewFit.Calculate(0, 0, 0, 100, 1); throw new Exception("zero width accepted"); }
        catch (InvalidOperationException) { }
        Console.WriteLine("PASS: saved DWG view center, large/negative coordinates, portrait/landscape fit, padding, aspect fallback, invalid bounds");
    }
}
