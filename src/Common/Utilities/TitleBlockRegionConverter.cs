using System;

namespace ZwcadBatchPlot;

/// <summary>



/// </summary>
public static class TitleBlockRegionConverter
{
    /// <summary>


    /// </summary>
    public static LocalRectangle FromStoredRelative(
        LocalRectangle storedRegion,
        LocalRectangle referenceFrame,
        string? coordinateMode)
    {
        if (!storedRegion.HasArea())
        {
            return new LocalRectangle();
        }

        if (string.Equals(coordinateMode, "Frame", StringComparison.OrdinalIgnoreCase))
        {
            return FromFrameRelative(storedRegion, referenceFrame);
        }

        if (string.Equals(
                coordinateMode,
                TitleBlockDefinition.DynamicRightBottomCoordinateMode,
                StringComparison.OrdinalIgnoreCase))
        {
            return FromFrameRightBottomRelative(storedRegion, referenceFrame);
        }


        return Clone(storedRegion);
    }


    public static LocalRectangle FromFrameRelative(LocalRectangle relativeRegion, LocalRectangle referenceFrame)
    {
        return LocalRectangle.FromPoints(
            relativeRegion.MinX + referenceFrame.MinX,
            relativeRegion.MinY + referenceFrame.MinY,
            relativeRegion.MaxX + referenceFrame.MinX,
            relativeRegion.MaxY + referenceFrame.MinY);
    }

    /// <summary>

    /// </summary>
    public static LocalRectangle FromFrameRightBottomRelative(LocalRectangle relativeRegion, LocalRectangle referenceFrame)
    {
        return LocalRectangle.FromPoints(
            relativeRegion.MinX + referenceFrame.MaxX,
            relativeRegion.MinY + referenceFrame.MinY,
            relativeRegion.MaxX + referenceFrame.MaxX,
            relativeRegion.MaxY + referenceFrame.MinY);
    }

    public static LocalRectangle ToFrameRelative(LocalRectangle region, LocalRectangle referenceFrame)
    {
        return LocalRectangle.FromPoints(
            region.MinX - referenceFrame.MinX,
            region.MinY - referenceFrame.MinY,
            region.MaxX - referenceFrame.MinX,
            region.MaxY - referenceFrame.MinY);
    }

    public static LocalRectangle ToFrameRightBottomRelative(LocalRectangle region, LocalRectangle referenceFrame)
    {
        return LocalRectangle.FromPoints(
            region.MinX - referenceFrame.MaxX,
            region.MinY - referenceFrame.MinY,
            region.MaxX - referenceFrame.MaxX,
            region.MaxY - referenceFrame.MinY);
    }

    private static LocalRectangle Clone(LocalRectangle region)
    {
        return LocalRectangle.FromPoints(region.MinX, region.MinY, region.MaxX, region.MaxY);
    }
}
