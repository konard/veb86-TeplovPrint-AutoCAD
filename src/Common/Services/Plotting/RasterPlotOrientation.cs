namespace ZwcadBatchPlot;

internal static class RasterPlotOrientation
{
    internal static void GetDcsOrientedPaperSize(
        PlotJob job,
        double windowWidth,
        double windowHeight,
        out double targetWidth,
        out double targetHeight)
    {
        var paperWidth = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var paperHeight = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm;
        var paperLong = System.Math.Max(paperWidth, paperHeight);
        var paperShort = System.Math.Min(paperWidth, paperHeight);



        var isLandscape = windowWidth > 1e-9 && windowHeight > 1e-9
            ? windowWidth >= windowHeight
            : paperWidth >= paperHeight;
        targetWidth = isLandscape ? paperLong : paperShort;
        targetHeight = isLandscape ? paperShort : paperLong;
    }
}
