using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif



namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    
    private static void ConfigurePlotScale(
        PlotSettingsValidator validator,
        PlotSettings settings,
        Extents2d window,
        PlotJob job,
        string deviceName,
        bool hideOuterFrame = false)
    {
        if (!job.LeavePaperMargin)
        {
            if (job.UseExactWindowScale || hideOuterFrame)
            {
                SetExactWindowScale(validator, settings, window, deviceName);
                return;
            }

            validator.SetUseStandardScale(settings, true);
            validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
            return;
        }

        var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
        var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
        if (windowWidth <= 0d || windowHeight <= 0d)
            throw new InvalidOperationException("Размер окна печати недопустим; невозможно рассчитать масштаб.");

        if (job.PaperMarginMm > 0d)
        {



            var originalShortMm = Math.Min(job.PaperWidthMm, job.PaperHeightMm);
            var windowShortSide = Math.Min(windowWidth, windowHeight);
            if (originalShortMm <= 0d || windowShortSide <= 0d)
                throw new InvalidOperationException("Размер бумаги или окна печати недопустим; невозможно рассчитать поля с увеличением бумаги.");
            var scale = originalShortMm / windowShortSide;
            validator.SetUseStandardScale(settings, false);
            validator.SetCustomPrintScale(
                settings, new CustomScale(ToPlotPaperUnitScale(settings, scale, deviceName), 1d));
            return;
        }


        var marginMm = Math.Abs(job.PaperMarginMm) > 0d ? Math.Abs(job.PaperMarginMm) : 1d;
        var paperSize = GetPlotPaperSizeMm(settings, deviceName);
        var paperShortSide = Math.Min(paperSize.X, paperSize.Y);
        var windowShort = Math.Min(windowWidth, windowHeight);
        var usableShortSide = paperShortSide - marginMm * 2d;

        if (usableShortSide <= 0d)
            throw new InvalidOperationException("Поля слишком велики и не оставляют полезной площади бумаги. Уменьшите размер полей.");


        var scaleReduced = usableShortSide / windowShort;
        validator.SetUseStandardScale(settings, false);
        validator.SetCustomPrintScale(
            settings, new CustomScale(ToPlotPaperUnitScale(settings, scaleReduced, deviceName), 1d));
    }

    
    private static void SetExactWindowScale(
        PlotSettingsValidator validator,
        PlotSettings settings,
        Extents2d window,
        string deviceName)
    {
        var paper = GetPlotPaperSizeMm(settings, deviceName);
        var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
        var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
        var paperLong = Math.Max(paper.X, paper.Y);
        var paperShort = Math.Min(paper.X, paper.Y);
        var windowLong = Math.Max(windowWidth, windowHeight);
        var windowShort = Math.Min(windowWidth, windowHeight);
        if (paperShort <= 0d || windowShort <= 0d)
            throw new InvalidOperationException("Размер пользовательской бумаги или окна печати недопустим; невозможно рассчитать точный масштаб.");

        var scale = Math.Min(paperLong / windowLong, paperShort / windowShort);
        validator.SetUseStandardScale(settings, false);
        validator.SetCustomPrintScale(
            settings, new CustomScale(ToPlotPaperUnitScale(settings, scale, deviceName), 1d));
    }

    
    private static void TryApplyHiddenFrameWindow(
        PlotSettingsValidator validator,
        PlotSettings settings,
        Extents2d originalWindow,
        PlotJob job,
        string deviceName)
    {
        var paper = GetPlotPaperSizeMm(settings, deviceName);
        var millimetersPerDrawingUnit = PlotWindowInset.ResolveMillimetersPerDrawingUnit(
            originalWindow,
            paper.X,
            paper.Y,
            job);
        if (!PlotWindowInset.TryInsetByPaperMillimeters(
                originalWindow,
                millimetersPerDrawingUnit,
                PlotWindowInset.PaperInsetMm,
                out var insetWindow))
        {
            return;
        }

        validator.SetPlotWindowArea(settings, insetWindow);
    }

    
    private static double ToPlotPaperUnitScale(
        PlotSettings settings,
        double millimetersPerDrawingUnit,
        string deviceName)
    {
        if (settings.PlotPaperUnits == PlotPaperUnit.Inches)
        {
            return millimetersPerDrawingUnit / 25.4d;
        }

        if (settings.PlotPaperUnits == PlotPaperUnit.Pixels)
        {
            var dpi = AcadPlotterInstaller.GetRasterDpi(deviceName);
            var dpiValue = Math.Max(dpi.X, dpi.Y);
            if (dpiValue <= 0d)
            {
                dpiValue = 100d;
            }

            return millimetersPerDrawingUnit * dpiValue / 25.4d;
        }

        return millimetersPerDrawingUnit;
    }

    
    private static void ResetAndCenterPlot(PlotSettingsValidator validator, PlotSettings settings)
    {

        validator.SetPlotCentered(settings, false);
        validator.SetPlotOrigin(settings, Point2d.Origin);
        validator.SetPlotCentered(settings, true);
    }
}
