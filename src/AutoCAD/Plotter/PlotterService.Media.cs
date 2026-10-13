using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using HostMgd.PlottingServices;
using CadApp = HostMgd.ApplicationServices.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#endif



namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    
    private static MediaChoice ChooseMedia(
        PlotSettingsValidator validator,
        Layout layout,
        string deviceName,
        PlotJob job,
        Extents2d window,
        out bool usedCachedCatalog)
    {
        var catalog = GetMediaCatalog(
            validator,
            layout,
            deviceName,

            forceDeviceReload: job.CustomPaperWasAdded,
            out usedCachedCatalog);
        var names = catalog.Select(x => x.Name).ToList();
        if (names.Count == 0)
        {
            throw new InvalidOperationException($"У плоттера нет доступных форматов бумаги: {deviceName}");
        }


        var targetWidth = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm
            : job.PaperWidthMm > 0 ? job.PaperWidthMm : Math.Abs(job.MaxX - job.MinX);
        var targetHeight = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm
            : job.PaperHeightMm > 0 ? job.PaperHeightMm : Math.Abs(job.MaxY - job.MinY);
        var isRaster = IsRasterPlotDevice(deviceName);
        if (isRaster)
        {

            var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
            var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
            RasterPlotOrientation.GetDcsOrientedPaperSize(
                job, windowWidth, windowHeight, out targetWidth, out targetHeight);
        }

        var choices = catalog.Select(item =>
        {
            var directError = DirectSizeError(item.WidthMm, item.HeightMm, targetWidth, targetHeight);
            var rotatedError = DirectSizeError(item.WidthMm, item.HeightMm, targetHeight, targetWidth);
            return new MediaChoice
            {
                Name = item.Name,
                WidthMm = item.WidthMm,
                HeightMm = item.HeightMm,
                Error = Math.Min(directError, rotatedError),
                IsFullBleed = item.IsFullBleed,
                PreferredRotation = rotatedError < directError ? PlotRotation.Degrees090 : PlotRotation.Degrees000
            };
        }).ToList();


        var matchTolerance = job.RequireExactPaperSize ? ExactMediaToleranceMm : MediaMatchToleranceMm;
        var exact = PickPreferredMedia(choices.Where(x => x.Error <= matchTolerance));
        if (exact != null && (job.RequireExactPaperSize || IsLongPaperName(job.PaperName ?? "")))
        {
            exact.RequiresExactSize = true;
            exact.SizeToleranceMm = matchTolerance;
            return exact;
        }

        if (job.RequireExactPaperSize)
        {
            if (isRaster)
            {
                return BestRasterMedia(choices, targetWidth, targetHeight)
                       ?? throw new InvalidOperationException(
                           $"В устройстве {deviceName} AutoCAD отсутствует точная пользовательская бумага {targetWidth:0.######} x {targetHeight:0.######} мм; "
                           + "печать остановлена без замены на близкую бумагу или бумагу с тем же именем.");
            }

            var nearest = string.Join(", ", choices
                .OrderBy(x => x.Error)
                .Take(5)
                .Select(x => $"{x.Name}[{x.WidthMm:0.######}x{x.HeightMm:0.######}, ошибка {x.Error:0.######}]")
                .ToArray());
            throw new InvalidOperationException(
                $"В устройстве {deviceName} AutoCAD отсутствует точная пользовательская бумага {targetWidth:0.######} x {targetHeight:0.######} мм; "
                + $"печать остановлена без замены на близкую бумагу или бумагу с тем же именем. Форматов={choices.Count}; ближайший={nearest}");
        }

        var named = BestNamedMedia(choices, job);
        if (named != null)
        {
            named.RequiresExactSize = IsLongPaperName(job.PaperName ?? "");
            return named;
        }

        if (exact != null)
        {
            return exact;
        }

        if (isRaster)
        {
            return BestRasterMedia(choices, targetWidth, targetHeight)
                   ?? throw new InvalidOperationException($"У растрового устройства нет доступного пиксельного формата: {deviceName}");
        }

        if (IsLongPaperName(job.PaperName ?? "") && targetWidth > 0 && targetHeight > 0)
        {
            return new MediaChoice
            {
                Name = $"По размеру {targetWidth:0.##} x {targetHeight:0.##} мм",
                WidthMm = targetWidth,
                HeightMm = targetHeight,
                Error = 0,
                UseClosestBySize = true,
                RequiresExactSize = true,

                PreferredRotation = PlotRotation.Degrees000
            };
        }

        var closest = PickPreferredMedia(choices);
        if (closest != null)
        {
            return closest;
        }

        var fallbackName = BestMediaNameByText(names, job) ?? names[0];
        return new MediaChoice
        {
            Name = fallbackName,
            PreferredRotation = PlotRotation.Degrees000
        };
    }

    
    private static MediaChoice? PickPreferredMedia(IEnumerable<MediaChoice> choices)
    {
        return choices
            .OrderBy(x => x.Error)
            .ThenBy(x => x.IsFullBleed ? 0 : 1)
            .ThenBy(x => x.PreferredRotation == PlotRotation.Degrees000 ? 0 : 1)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    
    private static IReadOnlyList<MediaCatalogItem> GetMediaCatalog(
        PlotSettingsValidator validator,
        Layout layout,
        string deviceName,
        bool forceDeviceReload,
        out bool usedCache)
    {
        if (forceDeviceReload)
        {
            InvalidateMediaCatalog(deviceName);
            try
            {

                PlotConfigManager.RefreshList(RefreshCode.RefreshPC3DevicesList);
                PlotConfigManager.SetCurrentConfig(Path.GetFileName(deviceName)).RefreshMediaNameList();
            }
            catch
            {

            }
        }

        var cacheKey = BuildMediaCatalogCacheKey(deviceName, layout.ModelType);
        lock (MediaCatalogCacheLock)
        {
            if (MediaCatalogCache.TryGetValue(cacheKey, out var cached))
            {
                usedCache = true;
                return cached;
            }
        }

        usedCache = false;
        using var settings = new PlotSettings(layout.ModelType);
        settings.CopyFrom(layout);
        if (forceDeviceReload)
        {
            try
            {
                validator.SetPlotConfigurationName(settings, "None", null);
                validator.RefreshLists(settings);
            }
            catch
            {

            }
        }

        validator.SetPlotConfigurationName(settings, deviceName, null);
        validator.RefreshLists(settings);
        var isRaster = IsRasterPlotDevice(deviceName);
        var paperUnit = isRaster ? PlotPaperUnit.Pixels : PlotPaperUnit.Millimeters;
        var dpi = isRaster ? AcadPlotterInstaller.GetRasterDpi(deviceName) : (X: 100d, Y: 100d);
        validator.SetPlotPaperUnits(settings, paperUnit);

        var catalog = new List<MediaCatalogItem>();
        foreach (var name in validator.GetCanonicalMediaNameList(settings).Cast<string>())
        {
            var size = GetMediaSize(validator, settings, name, paperUnit);
            if (size == null)
            {
                continue;
            }

            var widthMm = isRaster ? PixelsToMillimeters(size.Value.Width, dpi.X) : size.Value.Width;
            var heightMm = isRaster ? PixelsToMillimeters(size.Value.Height, dpi.Y) : size.Value.Height;

            catalog.Add(new MediaCatalogItem
            {
                Name = name,
                WidthMm = widthMm,
                HeightMm = heightMm,
                IsFullBleed = IsFullBleedMedia(name)
            });
        }

        lock (MediaCatalogCacheLock)
        {

            MediaCatalogCache[cacheKey] = catalog;
        }

        return catalog;
    }

    
    private static string BuildMediaCatalogCacheKey(string deviceName, bool modelType)
    {
        var plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();


        var devicePath = AcadPlotterInstaller.ResolveActivePlotterPath(deviceName);
        if (string.IsNullOrWhiteSpace(devicePath) && !string.IsNullOrWhiteSpace(plottersDirectory))
            devicePath = Path.Combine(plottersDirectory, deviceName);

        var pmpPath = AcadPlotterInstaller.ReadAttachedPmpPath(devicePath);
        if (string.IsNullOrWhiteSpace(pmpPath) && !string.IsNullOrWhiteSpace(plottersDirectory))
        {
            pmpPath = Path.Combine(
                plottersDirectory,
                "PMP Files",
                Path.GetFileNameWithoutExtension(deviceName) + ".pmp");
        }

        return string.Join("|", deviceName, modelType ? "M" : "P", GetFileFingerprint(devicePath), GetFileFingerprint(pmpPath));
    }

    
    private static string GetFileFingerprint(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch
        {
            return "unavailable";
        }
    }

    
    private static void InvalidateMediaCatalog(string deviceName)
    {
        lock (MediaCatalogCacheLock)
        {
            var prefix = deviceName + "|";
            foreach (var key in MediaCatalogCache.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                MediaCatalogCache.Remove(key);
            }
        }
    }

    
    private static MediaChoice? BestNamedMedia(IEnumerable<MediaChoice> choices, PlotJob job)
    {
        var paper = job.PaperName ?? "";
        var basePaper = GetBasePaperName(paper);
        return PickPreferredMedia(
            choices.Where(x => MediaNameMatchesPaper(x.Name, paper, basePaper)));
    }

    
    private static MediaChoice? BestRasterMedia(
        IEnumerable<MediaChoice> choices,
        double targetWidth,
        double targetHeight)
    {
        if (targetWidth <= 0d || targetHeight <= 0d)
        {
            return null;
        }

        var targetAspect = Math.Max(targetWidth, targetHeight) / Math.Min(targetWidth, targetHeight);

        return choices
            .Where(choice => choice.WidthMm > 0d && choice.HeightMm > 0d)
            .OrderBy(choice => Math.Abs(Math.Log(
                (Math.Max(choice.WidthMm, choice.HeightMm)
                 / Math.Min(choice.WidthMm, choice.HeightMm)) / targetAspect)))
            .ThenBy(choice => (choice.WidthMm >= choice.HeightMm) == (targetWidth >= targetHeight) ? 0 : 1)
            .ThenByDescending(choice => choice.WidthMm * choice.HeightMm)
            .Select(choice =>
            {
                choice.PreferredRotation = (choice.WidthMm >= choice.HeightMm) != (targetWidth >= targetHeight)
                    ? PlotRotation.Degrees090
                    : PlotRotation.Degrees000;
                return choice;
            })
            .FirstOrDefault();
    }

    
    private static bool IsRasterPlotDevice(string deviceName)
    {
        return deviceName.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    
    private static void EnsureRequiredMediaSize(PlotSettings settings, MediaChoice media, string deviceName)
    {
        if (!media.RequiresExactSize)
        {
            return;
        }

        var size = GetPlotPaperSizeMm(settings, deviceName);
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        var directError = DirectSizeError(size.X, size.Y, media.WidthMm, media.HeightMm);
        var rotatedError = DirectSizeError(size.X, size.Y, media.HeightMm, media.WidthMm);
        var error = Math.Min(directError, rotatedError);
        if (error <= media.SizeToleranceMm)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Устройство AutoCAD не содержит подходящей бумаги. Требуется {media.WidthMm:0.##} x {media.HeightMm:0.##} мм, "
            + $"получено {size.X:0.##} x {size.Y:0.##} мм. Добавьте соответствующую удлинённую бумагу в выбранный PC3 или используйте устройство с пользовательскими форматами.");
    }

    
    private static string? BestMediaNameByText(IEnumerable<string> names, PlotJob job)
    {
        var paper = job.PaperName ?? "";
        var basePaper = GetBasePaperName(paper);
        return names
            .Where(x => MediaNameMatchesPaper(x, paper, basePaper))
            .OrderBy(x => IsFullBleedMedia(x) ? 0 : 1)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    
    private static bool MediaNameMatchesPaper(string mediaName, string paper, string basePaper)
    {
        if (IsLongPaperName(paper))
        {
            return (!string.IsNullOrWhiteSpace(paper)
                    && mediaName.IndexOf(paper, StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrWhiteSpace(basePaper)
                    && mediaName.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0
                    && IsLongMediaName(mediaName))
                || (!string.IsNullOrWhiteSpace(basePaper)
                    && mediaName.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0
                    && IsLongMediaName(mediaName));
        }

        return (!string.IsNullOrWhiteSpace(paper)
                && mediaName.IndexOf(paper, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(basePaper)
                && mediaName.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(basePaper)
                && mediaName.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0);
    }

    
    private static bool IsLongPaperName(string paperName)
    {
        return paperName.IndexOf('+') > 0;
    }

    
    private static string GetBasePaperName(string paperName)
    {
        var plusIndex = paperName.IndexOf('+');
        return plusIndex > 0 ? paperName.Substring(0, plusIndex) : paperName;
    }

    
    private static bool IsLongMediaName(string mediaName)
    {
        return mediaName.IndexOf("+", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("long", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("extend", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("extended", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("\u52A0\u957F", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    
    private static bool IsFullBleedMedia(string mediaName)
    {
        return mediaName.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("full bleed", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("\u65E0\u8FB9\u8DDD", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("\u6EE1\u5E45", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    
    private static IEnumerable<PlotRotation> RotationOrder(PlotRotation preferred)
    {
        yield return preferred;

        foreach (var rotation in new[]
        {
            PlotRotation.Degrees000,
            PlotRotation.Degrees090,
            PlotRotation.Degrees270,
            PlotRotation.Degrees180
        })
        {
            if (rotation != preferred)
            {
                yield return rotation;
            }
        }
    }

    
    private static (double Width, double Height)? GetMediaSize(
        PlotSettingsValidator validator,
        PlotSettings settings,
        string mediaName,
        PlotPaperUnit paperUnit)
    {
        try
        {
            validator.SetCanonicalMediaName(settings, mediaName);
            validator.SetPlotPaperUnits(settings, paperUnit);
            var size = settings.PlotPaperSize;
            if (size.X > 0 && size.Y > 0)
            {
                return (size.X, size.Y);
            }
        }
        catch
        {
        }

        return TryParseMediaSize(mediaName);
    }

    
    private static Point2d GetPlotPaperSizeMm(PlotSettings settings, string deviceName)
    {
        var size = settings.PlotPaperSize;
        if (!IsRasterPlotDevice(deviceName))
        {
            return size;
        }

        var dpi = AcadPlotterInstaller.GetRasterDpi(deviceName);
        return new Point2d(
            PixelsToMillimeters(size.X, dpi.X),
            PixelsToMillimeters(size.Y, dpi.Y));
    }

    
    private static double PixelsToMillimeters(double pixels, double dpi)
    {
        return pixels * 25.4d / (dpi > 0d ? dpi : 100d);
    }

    
    private static double DirectSizeError(double mediaWidth, double mediaHeight, double targetWidth, double targetHeight)
    {
        return Math.Max(Math.Abs(mediaWidth - targetWidth), Math.Abs(mediaHeight - targetHeight));
    }

    
    private static (double Width, double Height)? TryParseMediaSize(string name)
    {
        var match = Regex.Match(
            name,
            @"(?<w>\d+(?:\.\d+)?)\s*[_-]?\s*(?:x|X|\u00D7)\s*[_-]?\s*(?<h>\d+(?:\.\d+)?)\s*[_-]?\s*(?<unit>MM|MILLIMETERS?|IN|INCH(?:ES)?)?",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var width = double.Parse(match.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var height = double.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        if (unit is "IN" or "INCH" or "INCHES")
        {
            width *= 25.4;
            height *= 25.4;
        }

        return (width, height);
    }
}
