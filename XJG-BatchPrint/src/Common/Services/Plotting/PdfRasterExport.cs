using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
#else
using ZwSoft.ZwCAD.ApplicationServices;
#endif

namespace ZwcadBatchPlot;

/**
 * @file PdfRasterExport.cs
 * @description PNG/JPG 输出：先用插件 PDF 绘图仪写出临时 PDF，再按设置中的 DPI 转成图片。
 *
 * 主要功能：
 * - PlotMany：无栅格作业时原样转发 CAD 打印；PNG/JPG 则改走临时 PDF 再转图
 * - 转图分辨率、JPG 质量从 AppSettings 读取，供设置界面接入
 * - CAD NETLOAD 前先 LoadLibrary 插件目录中的 pdfium.dll，避免搜到宿主 CAD 目录
 *
 * 注意：进度回调里临时把 OutputPath 还原成最终 png/jpg 名；失败不保留半截图和临时 PDF。
 */
public static class RasterExportSettings
{
    /// <summary>PNG/JPG 转图默认分辨率（DPI）。</summary>
    public const int DefaultDpi = 150;

    /// <summary>允许设置的最低 DPI。低于该值时回落到 <see cref="DefaultDpi"/>。</summary>
    public const int MinDpi = 72;

    /// <summary>允许设置的最高 DPI。高于该值时回落到 <see cref="DefaultDpi"/>。</summary>
    public const int MaxDpi = 600;

    /// <summary>JPG 默认质量（1–100）。</summary>
    public const int DefaultJpegQuality = 90;

    /// <summary>允许设置的最低 JPG 质量。</summary>
    public const int MinJpegQuality = 1;

    /// <summary>允许设置的最高 JPG 质量。</summary>
    public const int MaxJpegQuality = 100;

    /// <summary>
    /// 把配置中的 DPI 规范到合法范围。设置界面和 Settings.json 都写 <c>RasterExportDpi</c>。
    /// </summary>
    /// <param name="dpi">配置值；0 或越界表示未设置/非法。</param>
    /// <returns>用于本次转图的 DPI。</returns>
    public static int NormalizeDpi(int dpi)
    {
        return dpi < MinDpi || dpi > MaxDpi ? DefaultDpi : dpi;
    }

    /// <summary>
    /// 把配置中的 JPG 质量规范到 1–100。界面尚未暴露时，Settings.json 仍可直接写 <c>RasterExportJpegQuality</c>。
    /// </summary>
    /// <param name="quality">配置值；越界表示未设置/非法。</param>
    /// <returns>用于本次 JPG 编码的质量。</returns>
    public static int NormalizeJpegQuality(int quality)
    {
        return quality < MinJpegQuality || quality > MaxJpegQuality ? DefaultJpegQuality : quality;
    }
}

/// <summary>
/// 将 CAD 打印得到的临时 PDF 转为 PNG/JPG。打印管道本身始终使用 PDF 绘图仪。
/// </summary>
public static class PdfRasterExport
{
    private static readonly object PdfiumLock = new();
    private static bool _pdfiumLoaded;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    /// <summary>
    /// 批量出图入口。目标为 PNG/JPG 时先出临时 PDF 再按设置 DPI 转图；其它格式原样交给 <see cref="PlotterService.PlotMany"/>。
    /// </summary>
    public static List<PlotterService.PlotJobResult> PlotMany(
        IReadOnlyList<PlotJob> jobs,
        string deviceName,
        string styleSheet,
        Document currentDocument,
        AppSettings settings,
        Action<PlotJob>? beforeJob = null,
        CancellationToken cancellationToken = default)
    {
        if (jobs == null || jobs.Count == 0)
        {
            return PlotterService.PlotMany(
                jobs ?? Array.Empty<PlotJob>(),
                deviceName,
                styleSheet,
                currentDocument,
                settings,
                beforeJob,
                cancellationToken);
        }

        var originalPaths = jobs.ToDictionary(job => job, job => job.OutputPath ?? "");
        var rasterCount = originalPaths.Values.Count(IsRasterOutputPath);
        if (rasterCount == 0)
        {
            return PlotterService.PlotMany(jobs, deviceName, styleSheet, currentDocument, settings, beforeJob, cancellationToken);
        }

        if (rasterCount != jobs.Count)
        {
            throw new InvalidOperationException("PNG/JPG 转图不能与其它输出格式混在同一批打印。");
        }

        EnsurePdfiumLoaded();
        var dpi = RasterExportSettings.NormalizeDpi(settings.RasterExportDpi);
        var jpegQuality = RasterExportSettings.NormalizeJpegQuality(settings.RasterExportJpegQuality);
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ZwcadBatchPlot",
            "Raster_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var tempPaths = new Dictionary<PlotJob, string>();
        var index = 0;
        foreach (var job in jobs)
        {
            index++;
            var tempPath = Path.Combine(tempDirectory, index.ToString("D5") + ".pdf");
            tempPaths[job] = tempPath;
            job.OutputPath = tempPath;
        }

        try
        {
            Action<PlotJob>? wrappedBeforeJob = beforeJob == null
                ? null
                : job =>
                {
                    var tempPath = job.OutputPath;
                    if (originalPaths.TryGetValue(job, out var originalPath))
                    {
                        job.OutputPath = originalPath;
                    }

                    try
                    {
                        beforeJob(job);
                    }
                    finally
                    {
                        job.OutputPath = tempPath;
                    }
                };

            var results = PlotterService.PlotMany(
                jobs,
                AcadPlotterInstaller.PreferredPdfPlotter,
                styleSheet,
                currentDocument,
                settings,
                wrappedBeforeJob,
                cancellationToken);

            foreach (var result in results)
            {
                var imagePath = originalPaths[result.Job];
                var pdfPath = tempPaths[result.Job];
                try
                {
                    if (result.Succeeded)
                    {
                        ConvertPdfToImage(pdfPath, imagePath, dpi, jpegQuality);
                    }
                }
                catch (Exception ex)
                {
                    result.Error = ex;
                    TryDeleteFile(imagePath);
                }
                finally
                {
                    TryDeleteFile(pdfPath);
                    result.Job.OutputPath = imagePath;
                }
            }

            return results;
        }
        catch
        {
            foreach (var job in jobs)
            {
                if (originalPaths.TryGetValue(job, out var originalPath))
                {
                    job.OutputPath = originalPath;
                }
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>最终输出路径是否为 PNG/JPG。</summary>
    public static bool IsRasterOutputPath(string? path)
    {
        var extension = Path.GetExtension(path ?? "");
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CAD 宿主进程默认在 exe 目录搜 pdfium；NETLOAD 插件必须先按完整路径加载插件旁的原生库。
    /// </summary>
    private static void EnsurePdfiumLoaded()
    {
        if (_pdfiumLoaded)
        {
            return;
        }

        lock (PdfiumLock)
        {
            if (_pdfiumLoaded)
            {
                return;
            }

            var pluginDirectory = Path.GetDirectoryName(typeof(PdfRasterExport).Assembly.Location) ?? "";
            var candidates = new[]
            {
                Path.Combine(pluginDirectory, "pdfium.dll"),
                Path.Combine(pluginDirectory, "runtimes", "win-x64", "native", "pdfium.dll")
            };
            var loaded = false;
            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var handle = LoadLibrary(candidate);
                if (handle != IntPtr.Zero)
                {
                    loaded = true;
                    break;
                }
            }

            if (!loaded)
            {
                throw new FileNotFoundException(
                    "未找到或无法加载 pdfium.dll，无法将 PDF 转为 PNG/JPG。请把插件目录中的 pdfium.dll 与主 DLL 放在一起后重试。",
                    candidates[0]);
            }

            _pdfiumLoaded = true;
        }
    }

    /// <summary>
    /// 把临时 PDF 首页渲染为位图并写入最终 png/jpg。先写旁路临时文件，成功后再替换目标，避免留下半截图。
    /// </summary>
    /// <param name="pdfPath">CAD 刚写出的临时 PDF。</param>
    /// <param name="imagePath">用户看到的 png/jpg 路径。</param>
    /// <param name="dpi">本次转图像素密度，来自设置而不是写死值。</param>
    /// <param name="jpegQuality">JPG 编码质量，来自设置。</param>
    private static void ConvertPdfToImage(string pdfPath, string imagePath, int dpi, int jpegQuality)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            throw new FileNotFoundException("临时 PDF 不存在，无法转为图片。", pdfPath);
        }

        var pdfBytes = File.ReadAllBytes(pdfPath);
        if (pdfBytes.Length == 0)
        {
            throw new InvalidOperationException("临时 PDF 为空，无法转为图片。");
        }

        var directory = Path.GetDirectoryName(imagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var extension = Path.GetExtension(imagePath);
        var isJpeg = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                      || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        var stagingPath = imagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(dpi / 72.0)))
            {
                if (docReader.GetPageCount() < 1)
                {
                    throw new InvalidOperationException("临时 PDF 没有可转换的页面。");
                }

                using var pageReader = docReader.GetPageReader(0);
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();
                if (width <= 0 || height <= 0)
                {
                    throw new InvalidOperationException("PDF 页面尺寸无效，无法转为图片。");
                }

                var rawBytes = pageReader.GetImage(new NaiveTransparencyRemover(255, 255, 255));
                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                CopyBgra(bitmap, rawBytes);
                bitmap.SetResolution(dpi, dpi);
                if (isJpeg)
                {
                    SaveJpeg(bitmap, stagingPath, jpegQuality);
                }
                else
                {
                    bitmap.Save(stagingPath, ImageFormat.Png);
                }
            }

            if (new FileInfo(stagingPath).Length <= 0)
            {
                throw new InvalidOperationException("转图结果为空。");
            }

            if (File.Exists(imagePath))
            {
                File.Delete(imagePath);
            }

            File.Move(stagingPath, imagePath);
        }
        finally
        {
            TryDeleteFile(stagingPath);
        }
    }

    /// <summary>把 Docnet 返回的 BGRA 像素写入 32 位位图，按行复制以兼容 stride 填充。</summary>
    private static void CopyBgra(Bitmap bitmap, byte[] bgra)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var widthBytes = bitmap.Width * 4;
            if (bgra == null || bgra.Length < widthBytes * bitmap.Height)
            {
                throw new InvalidOperationException("PDF 渲染像素数据不完整。");
            }

            if (data.Stride == widthBytes)
            {
                Marshal.Copy(bgra, 0, data.Scan0, widthBytes * bitmap.Height);
                return;
            }

            for (var y = 0; y < bitmap.Height; y++)
            {
                var destination = new IntPtr(data.Scan0.ToInt64() + (y * (long)data.Stride));
                Marshal.Copy(bgra, y * widthBytes, destination, widthBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>按设置中的质量写出 JPG。</summary>
    private static void SaveJpeg(Bitmap bitmap, string path, int quality)
    {
        var encoder = FindJpegEncoder();
        if (encoder == null)
        {
            bitmap.Save(path, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        bitmap.Save(path, encoder, parameters);
    }

    private static ImageCodecInfo? FindJpegEncoder()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
            {
                return codec;
            }
        }

        return null;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}
