using System;

namespace ZwcadBatchPlot;

public static class SpdsPaperResolver
{
    public static bool TryResolve(SpdsFormatInfo info)
    {
        if (info == null) throw new ArgumentNullException(nameof(info));
        if (!TryParseFormat(info.Format, out var baseFormat, out var formatMultiplicity)
            || !TryGetBaseSize(baseFormat, out var shortSide, out var longSide))
            return false;

        var multiplicity = info.Multiplicity > 1 ? info.Multiplicity : formatMultiplicity;
        info.Format = baseFormat;
        info.Multiplicity = Math.Max(1, multiplicity);
        var extendedSide = shortSide * info.Multiplicity;
        var horizontal = IsHorizontal(info.Orientation);
        info.PaperWidthMm = horizontal ? Math.Max(longSide, extendedSide) : Math.Min(longSide, extendedSide);
        info.PaperHeightMm = horizontal ? Math.Min(longSide, extendedSide) : Math.Max(longSide, extendedSide);
        info.RequiresCustomPaper = info.Multiplicity > 1;
        return true;
    }

    public static bool TryGetBaseSize(string format, out double shortSide, out double longSide)
    {
        shortSide = 0;
        longSide = 0;
        switch ((format ?? "").Trim().ToUpperInvariant())
        {
            case "A4": shortSide = 210; longSide = 297; return true;
            case "A3": shortSide = 297; longSide = 420; return true;
            case "A2": shortSide = 420; longSide = 594; return true;
            case "A1": shortSide = 594; longSide = 841; return true;
            case "A0": shortSide = 841; longSide = 1189; return true;
            default: return false;
        }
    }

    private static bool TryParseFormat(string value, out string baseFormat, out int multiplicity)
    {
        baseFormat = "";
        multiplicity = 1;
        var normalized = (value ?? "").Trim().ToUpperInvariant()
            .Replace('Х', 'X')
            .Replace('×', 'X')
            .Replace(" ", "");
        if (normalized.Length < 2)
            return false;

        var separator = normalized.IndexOf('X');
        baseFormat = separator < 0 ? normalized : normalized.Substring(0, separator);
        if (separator >= 0
            && int.TryParse(normalized.Substring(separator + 1), out var parsed)
            && parsed > 1)
        {
            multiplicity = parsed;
        }

        return true;
    }

    private static bool IsHorizontal(string orientation)
        => orientation.IndexOf("гориз", StringComparison.OrdinalIgnoreCase) >= 0
           || orientation.IndexOf("horiz", StringComparison.OrdinalIgnoreCase) >= 0
           || orientation.IndexOf("land", StringComparison.OrdinalIgnoreCase) >= 0;
}
