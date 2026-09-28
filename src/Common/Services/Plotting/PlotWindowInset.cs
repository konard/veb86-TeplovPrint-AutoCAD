using System;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

/// <summary>



/// </summary>
internal static class PlotWindowInset
{

    internal const double PaperInsetMm = 1.0;


    internal const double MinimumRemainingShortSideMm = 2.0;

    /// <summary>


    /// </summary>





    internal static double ResolveMillimetersPerDrawingUnit(
        Extents2d window,
        double paperWidthMm,
        double paperHeightMm,
        PlotJob job)
    {
        return ResolveMillimetersPerDrawingUnit(
            Math.Abs(window.MaxPoint.X - window.MinPoint.X),
            Math.Abs(window.MaxPoint.Y - window.MinPoint.Y),
            paperWidthMm,
            paperHeightMm,
            job);
    }

    /// <summary>

    /// </summary>






    internal static double ResolveMillimetersPerDrawingUnit(
        double windowWidth,
        double windowHeight,
        double paperWidthMm,
        double paperHeightMm,
        PlotJob job)
    {
        if (windowWidth <= 1e-12 || windowHeight <= 1e-12)
        {
            return 0;
        }

        if (!job.LeavePaperMargin)
        {
            var paperLong = Math.Max(paperWidthMm, paperHeightMm);
            var paperShort = Math.Min(paperWidthMm, paperHeightMm);
            var windowLong = Math.Max(windowWidth, windowHeight);
            var windowShort = Math.Min(windowWidth, windowHeight);
            if (paperShort <= 1e-12 || windowShort <= 1e-12)
            {
                return 0;
            }

            return Math.Min(paperLong / windowLong, paperShort / windowShort);
        }

        if (job.PaperMarginMm > 0d)
        {
            var originalShortMm = Math.Min(job.PaperWidthMm, job.PaperHeightMm);
            var windowShortSide = Math.Min(windowWidth, windowHeight);
            if (originalShortMm <= 1e-12 || windowShortSide <= 1e-12)
            {
                return 0;
            }

            return originalShortMm / windowShortSide;
        }

        var marginMm = Math.Abs(job.PaperMarginMm) > 0d ? Math.Abs(job.PaperMarginMm) : 1d;
        var paperShortSide = Math.Min(paperWidthMm, paperHeightMm);
        var usableShortSide = paperShortSide - marginMm * 2d;
        var windowShortForMargin = Math.Min(windowWidth, windowHeight);
        if (usableShortSide <= 1e-12 || windowShortForMargin <= 1e-12)
        {
            return 0;
        }

        return usableShortSide / windowShortForMargin;
    }

    /// <summary>

    /// </summary>





    internal static bool TryInsetByPaperMillimeters(
        Extents2d window,
        double millimetersPerDrawingUnit,
        double insetPaperMm,
        out Extents2d insetWindow)
    {
        insetWindow = window;
        if (millimetersPerDrawingUnit <= 1e-12 || insetPaperMm <= 0)
        {
            return false;
        }

        var drawingInset = insetPaperMm / millimetersPerDrawingUnit;
        var minX = Math.Min(window.MinPoint.X, window.MaxPoint.X);
        var minY = Math.Min(window.MinPoint.Y, window.MaxPoint.Y);
        var maxX = Math.Max(window.MinPoint.X, window.MaxPoint.X);
        var maxY = Math.Max(window.MinPoint.Y, window.MaxPoint.Y);
        var width = maxX - minX;
        var height = maxY - minY;
        if (width <= drawingInset * 2d || height <= drawingInset * 2d)
        {
            return false;
        }

        var remainingShortMm = Math.Min(width, height) * millimetersPerDrawingUnit - insetPaperMm * 2d;
        if (remainingShortMm < MinimumRemainingShortSideMm)
        {
            return false;
        }

        insetWindow = new Extents2d(
            minX + drawingInset,
            minY + drawingInset,
            maxX - drawingInset,
            maxY - drawingInset);
        return true;
    }
}
