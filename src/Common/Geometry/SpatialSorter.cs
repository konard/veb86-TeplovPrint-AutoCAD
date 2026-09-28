using System;
using System.Collections.Generic;
using System.Linq;

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
public static class SpatialSorter
{
    /// <summary>


    /// </summary>
    public static List<PlotJob> SortByLayout(IReadOnlyList<PlotJob> jobs, bool horizontalFirst)
    {
        var result = new List<PlotJob>(jobs.Count);
        var layoutGroups = jobs
            .Select((job, index) => new { Job = job, Index = index })

            .GroupBy(item => item.Job.SpaceName ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Min(item => item.Job.LayoutTabOrder))

            .ThenBy(group => group.Min(item => item.Index))
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        foreach (var layoutGroup in layoutGroups)
        {
            result.AddRange(Sort(
                layoutGroup.Select(item => item.Job).ToList(),
                horizontalFirst));
        }

        return result;
    }




    public static List<PlotJob> Sort(IReadOnlyList<PlotJob> jobs, bool horizontalFirst)
    {
        if (jobs.Count <= 1)
        {
            return jobs.ToList();
        }

        var remaining = jobs
            .Select((job, index) => new SortItem(job, index))
            .ToList();
        var result = new List<PlotJob>(jobs.Count);




        while (remaining.Count > 0)
        {
            SortItem anchor;
            List<SortItem> band;
            if (horizontalFirst)
            {

                anchor = remaining
                    .OrderBy(item => MinX(item.Job))
                    .ThenByDescending(item => MaxY(item.Job))
                    .ThenBy(item => item.OriginalIndex)
                    .First();
                band = remaining
                    .Where(item => SharesBandWithAnchor(anchor.Job, item.Job, horizontalFirst: true))
                    .OrderByDescending(item => CenterY(item.Job))
                    .ThenBy(item => CenterX(item.Job))
                    .ThenBy(item => item.OriginalIndex)
                    .ToList();
            }
            else
            {

                anchor = remaining
                    .OrderByDescending(item => MaxY(item.Job))
                    .ThenBy(item => MinX(item.Job))
                    .ThenBy(item => item.OriginalIndex)
                    .First();
                band = remaining
                    .Where(item => SharesBandWithAnchor(anchor.Job, item.Job, horizontalFirst: false))
                    .OrderBy(item => CenterX(item.Job))
                    .ThenByDescending(item => CenterY(item.Job))
                    .ThenBy(item => item.OriginalIndex)
                    .ToList();
            }

            result.AddRange(band.Select(item => item.Job));
            var selectedIndices = new HashSet<int>(band.Select(item => item.OriginalIndex));
            remaining.RemoveAll(item => selectedIndices.Contains(item.OriginalIndex));
        }

        return result;
    }

    private static bool SharesBandWithAnchor(PlotJob anchor, PlotJob candidate, bool horizontalFirst)
    {
        if (ReferenceEquals(anchor, candidate))
        {
            return true;
        }

        if (horizontalFirst)
        {
            var overlap = Math.Min(MaxX(anchor), MaxX(candidate)) - Math.Max(MinX(anchor), MinX(candidate));
            var smallerWidth = Math.Min(MaxX(anchor) - MinX(anchor), MaxX(candidate) - MinX(candidate));
            return smallerWidth > 1e-6 && overlap >= smallerWidth * 0.3d;
        }

        var verticalOverlap = Math.Min(MaxY(anchor), MaxY(candidate)) - Math.Max(MinY(anchor), MinY(candidate));
        var smallerHeight = Math.Min(MaxY(anchor) - MinY(anchor), MaxY(candidate) - MinY(candidate));
        return smallerHeight > 1e-6 && verticalOverlap >= smallerHeight * 0.3d;
    }

    private sealed class SortItem
    {
        public SortItem(PlotJob job, int originalIndex)
        {
            Job = job;
            OriginalIndex = originalIndex;
        }

        public PlotJob Job { get; }
        public int OriginalIndex { get; }
    }


    private static double MinX(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMinX : job.MinX;
    private static double MinY(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMinY : job.MinY;
    private static double MaxX(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMaxX : job.MaxX;
    private static double MaxY(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMaxY : job.MaxY;
    private static double CenterX(PlotJob job) => (MinX(job) + MaxX(job)) / 2d;
    private static double CenterY(PlotJob job) => (MinY(job) + MaxY(job)) / 2d;
}
