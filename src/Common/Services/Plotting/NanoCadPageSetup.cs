using System;

namespace ZwcadBatchPlot;

/// <summary>
/// nanoCAD (issue #9): page setup values that are written to the layout through nanoCAD's COM API
/// (ActiveLayout.SetCustomScale, PaperUnits). The rules are the same as in PlotterService.ConfigurePlotScale for AutoCAD,
/// but without PlotSettings, so they are unit-tested without the host.
/// </summary>
public static class NanoCadPageSetup
{
    /// <summary>AcPlotPaperUnits.acInches.</summary>
    public const int PaperUnitsInches = 0;

    /// <summary>AcPlotPaperUnits.acMillimeters.</summary>
    public const int PaperUnitsMillimeters = 1;

    /// <summary>AcPlotPaperUnits.acPixels.</summary>
    public const int PaperUnitsPixels = 2;

    /// <summary>AcPlotType.acWindow.</summary>
    public const int PlotTypeWindow = 4;

    /// <summary>AcPlotScale.acScaleToFit.</summary>
    public const int StandardScaleToFit = 0;

    /// <summary>AcPreviewMode.acFullPreview.</summary>
    public const int FullPreview = 1;

    /// <summary>
    /// Millimetres of paper per drawing unit, or null for "scale to fit".
    /// <paramref name="sheetWidthMm"/>/<paramref name="sheetHeightMm"/> — the sheet format of the job (used by positive margins:
    /// the paper is enlarged and the drawing keeps the scale of the original sheet);
    /// <paramref name="mediaWidthMm"/>/<paramref name="mediaHeightMm"/> — the paper the plotter prints on.
    /// </summary>
    public static double? ResolveMillimetersPerDrawingUnit(
        bool leaveMargin,
        bool exactScale,
        double marginMm,
        double sheetWidthMm,
        double sheetHeightMm,
        double mediaWidthMm,
        double mediaHeightMm,
        double windowWidth,
        double windowHeight)
    {
        windowWidth = Math.Abs(windowWidth);
        windowHeight = Math.Abs(windowHeight);
        var windowLong = Math.Max(windowWidth, windowHeight);
        var windowShort = Math.Min(windowWidth, windowHeight);

        if (!leaveMargin)
        {
            if (!exactScale)
            {
                return null;
            }

            var paperLong = Math.Max(mediaWidthMm, mediaHeightMm);
            var paperShort = Math.Min(mediaWidthMm, mediaHeightMm);
            if (paperShort <= 0d || windowShort <= 0d)
                throw new InvalidOperationException("Размер пользовательской бумаги или окна печати недопустим; невозможно рассчитать точный масштаб.");

            return Math.Min(paperLong / windowLong, paperShort / windowShort);
        }

        if (windowWidth <= 0d || windowHeight <= 0d)
            throw new InvalidOperationException("Размер окна печати недопустим; невозможно рассчитать масштаб.");

        if (marginMm > 0d)
        {
            var originalShortMm = Math.Min(sheetWidthMm, sheetHeightMm);
            if (originalShortMm <= 0d)
                throw new InvalidOperationException("Размер бумаги или окна печати недопустим; невозможно рассчитать поля с увеличением бумаги.");

            return originalShortMm / windowShort;
        }

        var margin = Math.Abs(marginMm) > 0d ? Math.Abs(marginMm) : 1d;
        var usableShortSide = Math.Min(mediaWidthMm, mediaHeightMm) - margin * 2d;
        if (usableShortSide <= 0d)
            throw new InvalidOperationException("Поля слишком велики и не оставляют полезной площади бумаги. Уменьшите размер полей.");

        return usableShortSide / windowShort;
    }

    /// <summary>Millimetres per drawing unit converted to the paper units of the layout (SetCustomScale numerator).</summary>
    public static double ToPaperUnits(double millimetersPerDrawingUnit, int paperUnits, double dpi)
    {
        return paperUnits switch
        {
            PaperUnitsInches => millimetersPerDrawingUnit / 25.4d,
            PaperUnitsPixels => millimetersPerDrawingUnit * (dpi > 0d ? dpi : 100d) / 25.4d,
            _ => millimetersPerDrawingUnit
        };
    }
}
