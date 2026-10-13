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
using DbPlotType = Teigha.DatabaseServices.PlotType;
using CadApp = HostMgd.ApplicationServices.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using DbPlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#endif



namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    
    private static void PlotDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, AppSettings settings, Document? plotDocument)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            throw new InvalidOperationException("Не найдено доступное устройство вывода.");
        }

        WaitForPlotIdle();

        styleSheet = PlotStyleManager.ResolveJobStyle(job, styleSheet);

        var oldDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = FindLayoutForJob(tr, db, job);
            var window = GetPlotWindow(job, plotDocument);
            using var plot = CreateValidatedPlot(
                layout,
                job,
                window,
                deviceName,
                styleSheet,
                settings.HideFrameBoundaryWhenPlotting,
                settings.PlotTransparency,
                settings.PlotObjectLineweights);

            PrepareOutputFile(job.OutputPath);
            RunPlot(plot.Info, documentName, job.OutputPath, job.DrawingNumber);
            tr.Commit();
            WaitForPlotIdle();
            ValidatePlotOutput(job.OutputPath);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldDatabase;
        }
    }

    
    private static ValidatedPlot CreateValidatedPlot(
        Layout layout,
        PlotJob job,
        Extents2d window,
        string deviceName,
        string styleSheet,
        bool hideOuterFrame,
        bool plotTransparency,
        bool plotObjectLineweights)
    {
        try
        {
            return CreateValidatedPlotCore(
                layout, job, window, deviceName, styleSheet, hideOuterFrame, plotTransparency, plotObjectLineweights);
        }
        catch (CachedMediaCatalogException)
        {

            InvalidateMediaCatalog(deviceName);
            return CreateValidatedPlotCore(
                layout, job, window, deviceName, styleSheet, hideOuterFrame, plotTransparency, plotObjectLineweights);
        }
    }

    
    private static ValidatedPlot CreateValidatedPlotCore(
        Layout layout,
        PlotJob job,
        Extents2d window,
        string deviceName,
        string styleSheet,
        bool hideOuterFrame,
        bool plotTransparency,
        bool plotObjectLineweights)
    {
        var validator = PlotSettingsValidator.Current;
        var media = ChooseMedia(validator, layout, deviceName, job, window, out var usedCachedCatalog);
        media.FromCachedCatalog = usedCachedCatalog;
        var errors = new List<string>();

        var preferredRotation = ResolvePlotRotation(deviceName, media.PreferredRotation, job, window);
        foreach (var rotation in RotationOrder(preferredRotation))
        {
            var settings = new PlotSettings(layout.ModelType);
            try
            {
                settings.CopyFrom(layout);
                ConfigurePlotSettings(
                    validator,
                    settings,
                    deviceName,
                    styleSheet,
                    media,
                    rotation,
                    window,
                    job,
                    hideOuterFrame,
                    plotTransparency,
                    plotObjectLineweights);

                var info = new PlotInfo
                {
                    Layout = layout.ObjectId,
                    OverrideSettings = settings
                };

                new PlotInfoValidator
                {
                    MediaMatchingPolicy = MatchingPolicy.MatchEnabled
                }.Validate(info);



                if (job.RequireExactPaperSize
                    && job.UseExactWindowScale
                    && settings.PlotPaperUnits == PlotPaperUnit.Inches)
                {
                    ConfigurePlotScale(validator, settings, window, job, deviceName, hideOuterFrame);
                    ResetAndCenterPlot(validator, settings);
                    new PlotInfoValidator
                    {
                        MediaMatchingPolicy = MatchingPolicy.MatchEnabled
                    }.Validate(info);
                }

                return new ValidatedPlot
                {
                    Info = info,
                    Settings = settings,
                    Media = media,
                    Rotation = rotation
                };
            }
            catch (Exception ex)
            {
                errors.Add($"{media.Name}/{rotation}: {ex.Message}");
                settings.Dispose();
            }
        }

        var failure = new InvalidOperationException(
            "AutoCAD отклонил текущие параметры печати."
            + $" Лист={job.DrawingNumber}_{job.Title};"
            + $" Бумага={job.PaperWidthMm:0.##}x{job.PaperHeightMm:0.##} мм;"
            + $" Окно=({window.MinPoint.X:0.###},{window.MinPoint.Y:0.###})-({window.MaxPoint.X:0.###},{window.MaxPoint.Y:0.###});"
            + " Результаты попыток=" + string.Join(" | ", errors));
        if (media.FromCachedCatalog)
        {
            throw new CachedMediaCatalogException(failure);
        }

        throw failure;
    }

    
    private static void ConfigurePlotSettings(
        PlotSettingsValidator validator,
        PlotSettings settings,
        string deviceName,
        string styleSheet,
        MediaChoice media,
        PlotRotation rotation,
        Extents2d window,
        PlotJob job,
        bool hideOuterFrame,
        bool plotTransparency,
        bool plotObjectLineweights)
    {
        try
        {
            validator.SetPlotConfigurationName(settings, deviceName, media.UseClosestBySize ? null : media.Name);
        }
        catch
        {
            validator.SetPlotConfigurationName(settings, deviceName, null);
        }

        validator.RefreshLists(settings);
        var paperUnit = IsRasterPlotDevice(deviceName)
            ? PlotPaperUnit.Pixels
            : PlotPaperUnit.Millimeters;

        validator.SetPlotPaperUnits(settings, paperUnit);
        if (media.UseClosestBySize)
        {
            validator.SetClosestMediaName(settings, media.WidthMm, media.HeightMm, PlotPaperUnit.Millimeters, false);
        }
        else
        {
            validator.SetCanonicalMediaName(settings, media.Name);
        }

        EnsureRequiredMediaSize(settings, media, deviceName);


        validator.SetPlotWindowArea(settings, window);
        validator.SetPlotType(settings, DbPlotType.Window);
        ConfigurePlotScale(validator, settings, window, job, deviceName, hideOuterFrame);
        validator.SetPlotRotation(settings, rotation);


        validator.SetPlotCentered(settings, true);

        if (!string.IsNullOrWhiteSpace(styleSheet))
        {
            validator.SetCurrentStyleSheet(settings, styleSheet);
        }


        if (hideOuterFrame)
        {
            TryApplyHiddenFrameWindow(validator, settings, window, job, deviceName);
        }

        settings.PlotTransparency = plotTransparency;

        settings.PrintLineweights = plotObjectLineweights;
    }

    
    private static void RunPlot(PlotInfo info, string documentName, string outputPath, string sheetName)
    {
        using var engine = PlotFactory.CreatePublishEngine();

        PlotProgressDialog? progress = BatchPlotHostProgress.SuppressEnginePlotDialog
            ? null
            : new PlotProgressDialog(false, 1, true);

        var plotStarted = false;
        var documentStarted = false;
        var sheetStarted = false;
        var pageStarted = false;
        var graphicsStarted = false;

        try
        {
            if (progress != null)
            {
                progress.set_PlotMsgString(PlotMessageIndex.DialogTitle, "Пакетная печать");
                progress.set_PlotMsgString(PlotMessageIndex.SheetProgressCaption, sheetName);
                progress.LowerPlotProgressRange = 0;
                progress.UpperPlotProgressRange = 100;
                progress.PlotProgressPos = 0;
                progress.OnBeginPlot();
                progress.IsVisible = true;
            }

            engine.BeginPlot(progress, null);
            plotStarted = true;
            engine.BeginDocument(info, documentName, null, 1, true, outputPath);
            documentStarted = true;
            progress?.OnBeginSheet();
            sheetStarted = progress != null;

#if NANOCAD
            // nanoCAD: PlotPageInfo does not implement IDisposable.
            var pageInfo = new PlotPageInfo();
#else
            using var pageInfo = new PlotPageInfo();
#endif
            engine.BeginPage(pageInfo, info, true, null);
            pageStarted = true;
            engine.BeginGenerateGraphics(null);
            graphicsStarted = true;
            engine.EndGenerateGraphics(null);
            graphicsStarted = false;
            engine.EndPage(null);
            pageStarted = false;

            if (progress != null)
            {
                progress.OnEndSheet();
                sheetStarted = false;
            }

            engine.EndDocument(null);
            documentStarted = false;
            if (progress != null)
            {
                progress.PlotProgressPos = 100;
                progress.OnEndPlot();
            }

            engine.EndPlot(null);
            plotStarted = false;
        }
        finally
        {
            if (graphicsStarted)
            {
                TryPlotCleanup(() => engine.EndGenerateGraphics(null));
            }

            if (pageStarted)
            {
                TryPlotCleanup(() => engine.EndPage(null));
            }

            if (sheetStarted && progress != null)
            {
                TryPlotCleanup(progress.OnEndSheet);
            }

            if (documentStarted)
            {
                TryPlotCleanup(() => engine.EndDocument(null));
            }

            if (plotStarted)
            {
                if (progress != null)
                {
                    TryPlotCleanup(progress.OnEndPlot);
                }

                TryPlotCleanup(() => engine.EndPlot(null));
            }

            progress?.Dispose();
        }
    }

    
    private static void PreviewDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, Document plotDocument)
    {
        WaitForPlotIdle();

        styleSheet = PlotStyleManager.ResolveJobStyle(job, styleSheet);

        var oldDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var settings = AppSettingsStore.Load();
            var layout = FindLayoutForJob(tr, db, job);
            var window = GetPlotWindow(job, plotDocument);
            using var plot = CreateValidatedPlot(
                layout,
                job,
                window,
                deviceName,
                styleSheet,
                settings.HideFrameBoundaryWhenPlotting,
                settings.PlotTransparency,
                settings.PlotObjectLineweights);
            RunPreview(plot.Info, documentName);
            tr.Commit();
            WaitForPlotIdle();
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldDatabase;
        }
    }

    
    private static void RunPreview(PlotInfo plotInfo, string documentName)
    {
        using var engine = PlotFactory.CreatePreviewEngine((int)PreviewEngineFlags.Plot);
        var plotStarted = false;
        var documentStarted = false;
        var pageStarted = false;
        var graphicsStarted = false;

        try
        {
            engine.BeginPlot(null, null);
            plotStarted = true;
            engine.BeginDocument(plotInfo, documentName, null, 1, false, null);
            documentStarted = true;
#if NANOCAD
            // nanoCAD: PlotPageInfo does not implement IDisposable.
            var pageInfo = new PlotPageInfo();
#else
            using var pageInfo = new PlotPageInfo();
#endif
            engine.BeginPage(pageInfo, plotInfo, true, null);
            pageStarted = true;
            engine.BeginGenerateGraphics(null);
            graphicsStarted = true;
            engine.EndGenerateGraphics(null);
            graphicsStarted = false;
            engine.EndPage(null);
            pageStarted = false;
            engine.EndDocument(null);
            documentStarted = false;
            engine.EndPlot(null);
            plotStarted = false;
        }
        finally
        {
            if (graphicsStarted) TryPlotCleanup(() => engine.EndGenerateGraphics(null));
            if (pageStarted) TryPlotCleanup(() => engine.EndPage(null));
            if (documentStarted) TryPlotCleanup(() => engine.EndDocument(null));
            if (plotStarted) TryPlotCleanup(() => engine.EndPlot(null));
        }
    }
}
