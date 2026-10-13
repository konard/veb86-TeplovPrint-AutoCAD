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
#if NANOCAD
using HostMgd.ApplicationServices;
#elif AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
#else
using ZwSoft.ZwCAD.ApplicationServices;
#endif

namespace ZwcadBatchPlot;


public static class RasterExportSettings
{

    public const int DefaultDpi = 150;


    public const int MinDpi = 72;


    public const int MaxDpi = 600;


    public const int DefaultJpegQuality = 90;


    public const int MinJpegQuality = 1;


    public const int MaxJpegQuality = 100;

    /// <summary>

    /// </summary>


    public static int NormalizeDpi(int dpi)
    {
        return dpi < MinDpi || dpi > MaxDpi ? DefaultDpi : dpi;
    }

    /// <summary>

    /// </summary>


    public static int NormalizeJpegQuality(int quality)
    {
        return quality < MinJpegQuality || quality > MaxJpegQuality ? DefaultJpegQuality : quality;
    }
}

/// <summary>

/// </summary>
public static class PdfRasterExport
{
    private static readonly object PdfiumLock = new();
    private static bool _pdfiumLoaded;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    /// <summary>

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
            throw new InvalidOperationException("Преобразование в PNG/JPG нельзя смешивать с другими форматами в одном задании печати.");
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


    public static bool IsRasterOutputPath(string? path)
    {
        var extension = Path.GetExtension(path ?? "");
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>

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
                    "Файл pdfium.dll не найден или не загружается. Поместите pdfium.dll рядом с основной DLL плагина и повторите попытку.",
                    candidates[0]);
            }

            _pdfiumLoaded = true;
        }
    }

    /// <summary>

    /// </summary>




    private static void ConvertPdfToImage(string pdfPath, string imagePath, int dpi, int jpegQuality)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            throw new FileNotFoundException("Временный PDF отсутствует; преобразование в изображение невозможно.", pdfPath);
        }

        var pdfBytes = File.ReadAllBytes(pdfPath);
        if (pdfBytes.Length == 0)
        {
            throw new InvalidOperationException("Временный PDF пуст; преобразование в изображение невозможно.");
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
                    throw new InvalidOperationException("Во временном PDF нет страниц для преобразования.");
                }

                using var pageReader = docReader.GetPageReader(0);
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();
                if (width <= 0 || height <= 0)
                {
                    throw new InvalidOperationException("Размер страницы PDF недопустим; преобразование невозможно.");
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
                throw new InvalidOperationException("Результат преобразования изображения пуст.");
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


    private static void CopyBgra(Bitmap bitmap, byte[] bgra)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var widthBytes = bitmap.Width * 4;
            if (bgra == null || bgra.Length < widthBytes * bitmap.Height)
            {
                throw new InvalidOperationException("Данные пикселей при визуализации PDF неполны.");
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
