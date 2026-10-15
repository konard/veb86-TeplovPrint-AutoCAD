#if NANOCAD
using System;
using System.Linq;
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace ZwcadBatchPlot;

/// <summary>
/// nanoCAD 24 print route (issue #9). 1.0.3 wrote the job settings into the database-resident layout
/// (Layout.UpgradeOpen + native PlotSettingsValidator) and plotted through the managed PlotEngine: nanoCAD closed
/// without any message right after the progress window appeared, and its managed PlotEngine is empty anyway.
/// Here the database is only read (layout, plot window, media catalog of the plotter), and the page setup is written
/// and printed through nanoCAD's COM API (<see cref="NanoCadComPlot"/>). The page setup of the layout is restored afterwards.
/// </summary>
public static partial class PlotterService
{
    private static void PlotDatabaseThroughCom(
        Database db,
        PlotJob job,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Document? plotDocument)
    {
        var comDocument = NanoCadComPlot.GetComDocument(plotDocument);
        var setup = ResolveComPageSetup(db, job, deviceName, styleSheet, settings, plotDocument);
        var snapshot = NanoCadComPlot.CaptureLayout(comDocument, setup.LayoutName);
        try
        {
            NanoCadComPlot.ApplyPageSetup(comDocument, setup);
            PrepareOutputFile(job.OutputPath);
            NanoCadComPlot.PlotToFile(comDocument, deviceName, job.OutputPath);
        }
        finally
        {
            snapshot.Restore();
        }

        WaitForPlotIdle();
        if (!NanoCadPlotOutput.TryMoveToRequestedPath(job.OutputPath))
        {
            PrintSessionLog.Write("WARN", "Файл печати не найден после Plot.PlotToFile: " + job.OutputPath);
        }
    }

    private static void PreviewDatabaseThroughCom(
        Database db,
        PlotJob job,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Document plotDocument)
    {
        var comDocument = NanoCadComPlot.GetComDocument(plotDocument);
        var setup = ResolveComPageSetup(db, job, deviceName, styleSheet, settings, plotDocument);
        var snapshot = NanoCadComPlot.CaptureLayout(comDocument, setup.LayoutName);
        try
        {
            NanoCadComPlot.ApplyPageSetup(comDocument, setup);
            NanoCadComPlot.DisplayPlotPreview(comDocument);
        }
        finally
        {
            snapshot.Restore();
        }
    }

    /// <summary>
    /// Page setup of the job with the same media, rotation and scale rules as the AutoCAD route. Read-only for the
    /// drawing: the media catalog is read through a PlotSettings that is not in the database (works in nanoCAD 24, issue #7).
    /// </summary>
    private static NanoCadComPageSetup ResolveComPageSetup(
        Database db,
        PlotJob job,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Document? plotDocument)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var layout = FindLayoutForJob(tr, db, job);
        var window = GetPlotWindow(job, plotDocument);
        PrintSessionLog.Info(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "Лист «{0}» (модель={1}); окно печати ({2:0.###},{3:0.###})-({4:0.###},{5:0.###})",
            layout.LayoutName,
            layout.ModelType,
            window.MinPoint.X,
            window.MinPoint.Y,
            window.MaxPoint.X,
            window.MaxPoint.Y));

        var validator = PlotSettingsValidator.Current;
        MediaChoice media = null!;
        PlotStepException.Run("Выбор формата бумаги (каталог плоттера)", () =>
        {
            media = ChooseMedia(validator, layout, deviceName, job, window, out _);
            if (media.UseClosestBySize)
            {
                media = ClosestCatalogMedia(validator, layout, deviceName, media);
            }
        });

        var rotation = ResolvePlotRotation(deviceName, media.PreferredRotation, job, window);
        var isRaster = IsRasterPlotDevice(deviceName);
        var paperUnits = isRaster ? NanoCadPageSetup.PaperUnitsPixels : NanoCadPageSetup.PaperUnitsMillimeters;
        var dpi = isRaster ? Math.Max(AcadPlotterInstaller.GetRasterDpi(deviceName).X, AcadPlotterInstaller.GetRasterDpi(deviceName).Y) : 0d;

        var mediaWidth = media.WidthMm > 0d ? media.WidthMm : job.EffectivePaperWidthMm > 0d ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var mediaHeight = media.HeightMm > 0d ? media.HeightMm : job.EffectivePaperHeightMm > 0d ? job.EffectivePaperHeightMm : job.PaperHeightMm;
        var hideOuterFrame = settings.HideFrameBoundaryWhenPlotting;
        var millimetersPerUnit = NanoCadPageSetup.ResolveMillimetersPerDrawingUnit(
            job.LeavePaperMargin,
            job.UseExactWindowScale || hideOuterFrame,
            job.PaperMarginMm,
            job.PaperWidthMm,
            job.PaperHeightMm,
            mediaWidth,
            mediaHeight,
            window.MaxPoint.X - window.MinPoint.X,
            window.MaxPoint.Y - window.MinPoint.Y);

        var plotWindow = window;
        if (hideOuterFrame
            && millimetersPerUnit is double scale
            && PlotWindowInset.TryInsetByPaperMillimeters(window, scale, PlotWindowInset.PaperInsetMm, out var insetWindow))
        {
            plotWindow = insetWindow;
        }

        tr.Commit();
        return new NanoCadComPageSetup
        {
            LayoutName = layout.LayoutName,
            DeviceName = deviceName,
            MediaName = media.Name,
            PaperUnits = paperUnits,
            WindowMinX = plotWindow.MinPoint.X,
            WindowMinY = plotWindow.MinPoint.Y,
            WindowMaxX = plotWindow.MaxPoint.X,
            WindowMaxY = plotWindow.MaxPoint.Y,
            Rotation = (int)rotation,
            CustomScale = millimetersPerUnit == null ? null : NanoCadPageSetup.ToPaperUnits(millimetersPerUnit.Value, paperUnits, dpi),
            StyleSheet = styleSheet,
            PlotWithLineweights = settings.PlotObjectLineweights
        };
    }

    /// <summary>The COM layout takes only a media name of the plotter: the nearest one by size (no SetClosestMediaName in COM).</summary>
    private static MediaChoice ClosestCatalogMedia(PlotSettingsValidator validator, Layout layout, string deviceName, MediaChoice wanted)
    {
        var catalog = GetMediaCatalog(validator, layout, deviceName, forceDeviceReload: false, out _);
        var closest = catalog
            .Select(item => new
            {
                Item = item,
                Direct = DirectSizeError(item.WidthMm, item.HeightMm, wanted.WidthMm, wanted.HeightMm),
                Rotated = DirectSizeError(item.WidthMm, item.HeightMm, wanted.HeightMm, wanted.WidthMm)
            })
            .OrderBy(x => Math.Min(x.Direct, x.Rotated))
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"У плоттера нет доступных форматов бумаги: {deviceName}");

        return new MediaChoice
        {
            Name = closest.Item.Name,
            WidthMm = closest.Item.WidthMm,
            HeightMm = closest.Item.HeightMm,
            Error = Math.Min(closest.Direct, closest.Rotated),
            IsFullBleed = closest.Item.IsFullBleed,
            RequiresExactSize = wanted.RequiresExactSize,
            PreferredRotation = closest.Rotated < closest.Direct ? PlotRotation.Degrees090 : PlotRotation.Degrees000
        };
    }
}
#endif
