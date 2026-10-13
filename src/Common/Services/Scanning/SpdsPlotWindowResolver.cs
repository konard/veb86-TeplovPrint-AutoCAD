using System;
#if NANOCAD
using Teigha.DatabaseServices;
using Teigha.Geometry;
#elif AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

public static class SpdsPlotWindowResolver
{
    public static SpdsPlotWindow Resolve(SpdsFormatInfo info, Extents3d extents)
    {
        var fallback = SpdsPlotWindow.FromExtents(extents, "Extents fallback");
        if (!info.HasOriginX || !info.HasOriginY || info.PaperWidthMm <= 0 || info.PaperHeightMm <= 0)
            return fallback;

        var scale = info.Scale > 0 ? info.Scale : info.DimScale;
        if (scale <= 0)
            return fallback.WithNote("Extents fallback: SPDS Scale/DimScale is unavailable");

        var width = info.PaperWidthMm * scale;
        var height = info.PaperHeightMm * scale;
        if (width <= 1e-6 || height <= 1e-6)
            return fallback;

        var candidates = new[]
        {
            Create(info.OriginX, info.OriginY, info.OriginX + width, info.OriginY + height, "Origin: lower-left"),
            Create(info.OriginX - width, info.OriginY, info.OriginX, info.OriginY + height, "Origin: lower-right"),
            Create(info.OriginX, info.OriginY - height, info.OriginX + width, info.OriginY, "Origin: upper-left"),
            Create(info.OriginX - width, info.OriginY - height, info.OriginX, info.OriginY, "Origin: upper-right")
        };

        SpdsPlotWindow? best = null;
        var bestError = double.MaxValue;
        foreach (var candidate in candidates)
        {
            var error = BoundsError(candidate, extents);
            if (error < bestError)
            {
                best = candidate;
                bestError = error;
            }
        }

        var actualWidth = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
        var actualHeight = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
        var tolerance = Math.Max(5d, Math.Max(width, height) * 0.02d);
        var dimensionsMatch = Math.Abs(actualWidth - width) <= tolerance && Math.Abs(actualHeight - height) <= tolerance;
        if (best != null && dimensionsMatch && bestError <= tolerance * 4d)
            return best;

        return fallback.WithNote(
            $"Extents fallback: Origin window {width:0.###} x {height:0.###}, extents {actualWidth:0.###} x {actualHeight:0.###}, delta {bestError:0.###}");
    }

    private static SpdsPlotWindow Create(double x1, double y1, double x2, double y2, string note)
        => new SpdsPlotWindow(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2), note);

    private static double BoundsError(SpdsPlotWindow candidate, Extents3d extents)
        => Math.Abs(candidate.MinX - extents.MinPoint.X)
           + Math.Abs(candidate.MinY - extents.MinPoint.Y)
           + Math.Abs(candidate.MaxX - extents.MaxPoint.X)
           + Math.Abs(candidate.MaxY - extents.MaxPoint.Y);
}

public sealed class SpdsPlotWindow
{
    public SpdsPlotWindow(double minX, double minY, double maxX, double maxY, string note)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
        Note = note;
    }

    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public string Note { get; }

    public double[] CornerPoints => new[] { MinX, MinY, MaxX, MinY, MaxX, MaxY, MinX, MaxY };

    public SpdsPlotWindow WithNote(string note) => new SpdsPlotWindow(MinX, MinY, MaxX, MaxY, note);

    public static SpdsPlotWindow FromExtents(Extents3d extents, string note)
        => new SpdsPlotWindow(extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.X, extents.MaxPoint.Y, note);
}
