using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
public static class OutputPaperNameResolver
{
    private static readonly IReadOnlyDictionary<string, double> StandardLongSides =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = 1189d,
            ["A1"] = 841d,
            ["A2"] = 594d,
            ["A3"] = 420d,
            ["A4"] = 297d
        };

    public static string Resolve(PlotJob job, double longSideToleranceMm)
    {
        if (job == null)
        {
            return "";
        }

        return Resolve(
            job.PaperName,
            job.PaperWidthMm,
            job.PaperHeightMm,
            longSideToleranceMm);
    }

    /// <summary>


    /// </summary>
    public static string Resolve(
        string? actualPaperName,
        double paperWidthMm,
        double paperHeightMm,
        double longSideToleranceMm)
    {
        var originalName = actualPaperName?.Trim() ?? "";
        var plusIndex = originalName.IndexOf('+');
        if (plusIndex <= 0)
        {

            return originalName;
        }

        var baseName = originalName.Substring(0, plusIndex).Trim();
        if (!StandardLongSides.TryGetValue(baseName, out var standardLongSide))
        {
            return originalName;
        }

        var measuredLongSide = Math.Max(Math.Abs(paperWidthMm), Math.Abs(paperHeightMm));
        if (measuredLongSide <= standardLongSide || longSideToleranceMm < 0)
        {
            return originalName;
        }


        var totalUnits8 = Math.Max(
            9,
            (int)Math.Round(
                measuredLongSide / standardLongSide * 8d,
                MidpointRounding.AwayFromZero));
        var snappedLongSide = standardLongSide * totalUnits8 / 8d;
        var longSideErrorMm = Math.Abs(measuredLongSide - snappedLongSide);
        if (longSideErrorMm > longSideToleranceMm)
        {
            return originalName;
        }

        var extension = (totalUnits8 - 8) / 8d;
        return baseName + "+" + extension.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
