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
            OutputWithJobSettings(db, job, deviceName, styleSheet, settings, plotDocument, info =>
            {
                PrepareOutputFile(job.OutputPath);
                RunPlot(info, documentName, job.OutputPath, job.DrawingNumber);
            });
            WaitForPlotIdle();
            ValidatePlotOutput(job.OutputPath);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldDatabase;
        }
    }

    /// <summary>
    /// Builds the PlotInfo of the job and runs <paramref name="output"/> (plot or preview) with it.
    /// AutoCAD: the job settings are passed as PlotInfo.OverrideSettings, the layout is not modified.
    /// nanoCAD 24 rejected every override attempt with "External component has thrown an exception" (issue #7):
    /// its native PlotSettingsValidator is applied there to a PlotSettings that is not in the database.
    /// Then the settings are applied to the layout itself, as nanoCAD's own plotting API does, and the layout
    /// page setup is restored after the output.
    /// </summary>
    private static void OutputWithJobSettings(
        Database db,
        PlotJob job,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Document? plotDocument,
        Action<PlotInfo> output)
    {
#if NANOCAD
        if (!_nanoCadPlotsThroughLayout)
        {
            try
            {
                OutputWithOverrideSettings(db, job, deviceName, styleSheet, settings, plotDocument, output);
                return;
            }
            catch (PlotSettingsRejectedException overrideFailure)
            {
                TraceLog.Write("Override plot settings rejected, applying them to the layout: " + overrideFailure.Message);
                try
                {
                    OutputWithConfiguredLayout(db, job, deviceName, styleSheet, settings, plotDocument, output);
                }
                catch (PlotSettingsRejectedException layoutFailure)
                {
                    throw new PlotSettingsRejectedException(
                        overrideFailure.Message + " Печать с параметрами самого листа тоже не удалась: " + layoutFailure.Message,
                        layoutFailure);
                }

                _nanoCadPlotsThroughLayout = true;
                return;
            }
        }

        OutputWithConfiguredLayout(db, job, deviceName, styleSheet, settings, plotDocument, output);
#else
        OutputWithOverrideSettings(db, job, deviceName, styleSheet, settings, plotDocument, output);
#endif
    }

    
    private static void OutputWithOverrideSettings(
        Database db,
        PlotJob job,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Document? plotDocument,
        Action<PlotInfo> output)
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
            settings.PlotObjectLineweights,
            configureLayout: false);

        output(plot.Info);
        tr.Commit();
    }

#if NANOCAD
    /// <summary>
    /// The override settings were rejected by nanoCAD once; the next jobs go straight to the layout route
    /// instead of repeating four failing native calls per sheet.
    /// </summary>
    private static bool _nanoCadPlotsThroughLayout;

    /// <summary>
    /// nanoCAD (issue #7): applies the job settings to the database-resident layout, plots it without
    /// OverrideSettings and restores the original page setup of the layout afterwards.
    /// </summary>
    private static void OutputWithConfiguredLayout(
        Database db,
        PlotJob job,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Document? plotDocument,
        Action<PlotInfo> output)
    {
        ObjectId layoutId;
        PlotSettings original;
        PlotInfo info;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var layout = FindLayoutForJob(tr, db, job);
            layoutId = layout.ObjectId;
            original = new PlotSettings(layout.ModelType);
            original.CopyFrom(layout);
            var window = GetPlotWindow(job, plotDocument);
            layout.UpgradeOpen();
            try
            {
                using var plot = CreateValidatedPlot(
                    layout,
                    job,
                    window,
                    deviceName,
                    styleSheet,
                    settings.HideFrameBoundaryWhenPlotting,
                    settings.PlotTransparency,
                    settings.PlotObjectLineweights,
                    configureLayout: true);
                info = plot.Info;
            }
            catch
            {
                original.Dispose();
                throw;
            }

            // The plot engine reads the layout from the database: the configured page setup has to be committed.
            tr.Commit();
        }

        try
        {
            output(info);
        }
        finally
        {
            RestoreLayoutPlotSettings(db, layoutId, original);
        }
    }

    
    private static void RestoreLayoutPlotSettings(Database db, ObjectId layoutId, PlotSettings original)
    {
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
            layout.CopyFrom(original);
            tr.Commit();
        }
        catch (Exception ex)
        {
            TraceLog.Write("RestoreLayoutPlotSettings failed: " + ex);
        }
        finally
        {
            original.Dispose();
        }
    }
#endif

    
    private static ValidatedPlot CreateValidatedPlot(
        Layout layout,
        PlotJob job,
        Extents2d window,
        string deviceName,
        string styleSheet,
        bool hideOuterFrame,
        bool plotTransparency,
        bool plotObjectLineweights,
        bool configureLayout)
    {
        try
        {
            return CreateValidatedPlotCore(
                layout, job, window, deviceName, styleSheet, hideOuterFrame, plotTransparency, plotObjectLineweights, configureLayout);
        }
        catch (CachedMediaCatalogException)
        {

            InvalidateMediaCatalog(deviceName);
            return CreateValidatedPlotCore(
                layout, job, window, deviceName, styleSheet, hideOuterFrame, plotTransparency, plotObjectLineweights, configureLayout);
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
        bool plotObjectLineweights,
        bool configureLayout)
    {
        var validator = PlotSettingsValidator.Current;
        var media = ChooseMedia(validator, layout, deviceName, job, window, out var usedCachedCatalog);
        media.FromCachedCatalog = usedCachedCatalog;
        var errors = new List<string>();

        var preferredRotation = ResolvePlotRotation(deviceName, media.PreferredRotation, job, window);
        foreach (var rotation in RotationOrder(preferredRotation))
        {
            // configureLayout: the layout is open for write and is configured itself (nanoCAD, issue #7).
            var settings = configureLayout ? layout : new PlotSettings(layout.ModelType);
            try
            {
                if (!configureLayout)
                {
                    PlotStepException.Run("CopyFrom(layout)", () => settings.CopyFrom(layout));
                }

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
                    Layout = layout.ObjectId
                };
                if (!configureLayout)
                {
                    info.OverrideSettings = settings;
                }

                ValidatePlotInfo(info);



                if (job.RequireExactPaperSize
                    && job.UseExactWindowScale
                    && settings.PlotPaperUnits == PlotPaperUnit.Inches)
                {
                    ConfigurePlotScale(validator, settings, window, job, deviceName, hideOuterFrame);
                    ResetAndCenterPlot(validator, settings);
                    ValidatePlotInfo(info);
                }

                return new ValidatedPlot
                {
                    Info = info,
                    Settings = settings,
                    Media = media,
                    Rotation = rotation,
                    OwnsSettings = !configureLayout
                };
            }
            catch (Exception ex)
            {
                errors.Add($"{media.Name}/{rotation}: {ex.Message}");
                if (!configureLayout)
                {
                    settings.Dispose();
                }
            }
        }

        var failure = new PlotSettingsRejectedException(
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

    /// <summary>
    /// AutoCAD требует проверки PlotInfo перед печатью. В nanoCAD 24 PlotInfoValidator.Validate и
    /// MediaMatchingPolicy не реализованы: вызов только показывает модальное окно "Mgd / NOT_IMPLEMENTED" (issue #3),
    /// поэтому в nanoCAD проверка пропускается — параметры уже применены через PlotSettingsValidator.
    /// </summary>
    private static void ValidatePlotInfo(PlotInfo info)
    {
#if NANOCAD
        TraceLog.Write("ValidatePlotInfo: skipped, PlotInfoValidator.Validate is not implemented in nanoCAD");
#else
        new PlotInfoValidator
        {
            MediaMatchingPolicy = MatchingPolicy.MatchEnabled
        }.Validate(info);
#endif
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
        // Every call is labelled: nanoCAD 24 reports failures of its native plot code only as
        // "External component has thrown an exception" (issue #7), without saying which setting was refused.
        var mediaName = media.UseClosestBySize ? null : media.Name;
        PlotStepException.Run($"SetPlotConfigurationName({deviceName}, {mediaName ?? "<null>"})", () =>
        {
            try
            {
                validator.SetPlotConfigurationName(settings, deviceName, mediaName);
            }
            catch
            {
                validator.SetPlotConfigurationName(settings, deviceName, null);
            }
        });

        PlotStepException.Run("RefreshLists", () => validator.RefreshLists(settings));
        var paperUnit = IsRasterPlotDevice(deviceName)
            ? PlotPaperUnit.Pixels
            : PlotPaperUnit.Millimeters;

        PlotStepException.Run($"SetPlotPaperUnits({paperUnit})", () => validator.SetPlotPaperUnits(settings, paperUnit));
        if (media.UseClosestBySize)
        {
            PlotStepException.Run(
                $"SetClosestMediaName({media.WidthMm:0.##}x{media.HeightMm:0.##})",
                () => validator.SetClosestMediaName(settings, media.WidthMm, media.HeightMm, PlotPaperUnit.Millimeters, false));
        }
        else
        {
            PlotStepException.Run($"SetCanonicalMediaName({media.Name})", () => validator.SetCanonicalMediaName(settings, media.Name));
        }

        PlotStepException.Run("EnsureRequiredMediaSize", () => EnsureRequiredMediaSize(settings, media, deviceName));


        PlotStepException.Run("SetPlotWindowArea", () => validator.SetPlotWindowArea(settings, window));
        PlotStepException.Run("SetPlotType(Window)", () => validator.SetPlotType(settings, DbPlotType.Window));
        PlotStepException.Run("ConfigurePlotScale", () => ConfigurePlotScale(validator, settings, window, job, deviceName, hideOuterFrame));
        PlotStepException.Run($"SetPlotRotation({rotation})", () => validator.SetPlotRotation(settings, rotation));


        PlotStepException.Run("SetPlotCentered", () => validator.SetPlotCentered(settings, true));

        if (!string.IsNullOrWhiteSpace(styleSheet))
        {
            PlotStepException.Run($"SetCurrentStyleSheet({styleSheet})", () => validator.SetCurrentStyleSheet(settings, styleSheet));
        }


        if (hideOuterFrame)
        {
            PlotStepException.Run("HiddenFrameWindow", () => TryApplyHiddenFrameWindow(validator, settings, window, job, deviceName));
        }

        PlotStepException.Run("PlotTransparency", () => settings.PlotTransparency = plotTransparency);

        PlotStepException.Run("PrintLineweights", () => settings.PrintLineweights = plotObjectLineweights);
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
            var settings = AppSettingsStore.Load();
            OutputWithJobSettings(db, job, deviceName, styleSheet, settings, plotDocument, info => RunPreview(info, documentName));
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
