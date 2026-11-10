using System;
using System.IO;
using System.Collections.Generic;
using CadSignature;
internal static class CoverCalibrationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "CoverCalibration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(FrameOffsetConfig.LoadCoverBlockName(root) == "", "Empty config failed");
            FrameOffsetConfig.Save(root, "A3", 297, new double[] { 1, 2, 3, 4 });
            FrameOffsetConfig.Save(root, "封面", 297, new double[] { 10, 20, 30, 40 });
            FrameOffsetConfig.SaveCoverBlockName(root, "专用封面图框");
            Check(FrameOffsetConfig.Load(root, "A3")[1] == 1 && FrameOffsetConfig.Load(root, "封面")[1] == 10, "Independent offsets collided");
            var data = new Dictionary<string, double[]> { { "A3", FrameOffsetConfig.Load(root, "A3") }, { "封面", FrameOffsetConfig.Load(root, "封面") } };
            Check(FrameOffsetConfig.MatchSlot(root, "专用封面图框", 297, data) == "封面", "Cover was confused with same-size A3");
            Check(FrameOffsetConfig.MatchSlot(root, "普通A3图框", 297, data) == "A3", "Normal drawing used cover calibration");
            Check(FrameOffsetConfig.MatchSlot(root, "其他封面图框", 297, data) == null, "Uncalibrated cover used another cover or A3 calibration");
            FrameOffsetConfig.Remove(root, "封面");
            Check(FrameOffsetConfig.LoadCoverBlockName(root) == "" && FrameOffsetConfig.Load(root, "封面") == null && FrameOffsetConfig.Load(root, "A3") != null, "Clearing cover affected standard calibration");
            Console.WriteLine("PASS: independent cover persistence; same-size A3 isolation; uncalibrated cover rejection; cover-only clearing");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message); return 1; }
        finally { foreach (string path in Directory.GetFiles(root)) File.Delete(path); Directory.Delete(root); }
    }
}
