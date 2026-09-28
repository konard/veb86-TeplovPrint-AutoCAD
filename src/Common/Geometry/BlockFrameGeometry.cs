using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

internal enum BlockFrameSource
{
    None,
    ClosedRectangle,

    FourLineRectangle,
    LineExtents
}

/// <summary>


/// </summary>
internal static class BlockFrameGeometry
{
    /// <summary>


    /// </summary>
    private const int FourLineSegmentMaxDepth = 3;

    internal static bool TryGetFrame(
        Database database,
        ObjectId rootDefinitionId,
        out LocalRectangle frame,
        out BlockFrameSource source)
    {
        using var tr = database.TransactionManager.StartTransaction();
        var result = TryGetFrame(tr, rootDefinitionId, out frame, out source);
        tr.Commit();
        return result;
    }

    internal static bool TryGetFrame(
        Transaction tr,
        ObjectId rootDefinitionId,
        out LocalRectangle frame,
        out BlockFrameSource source)
    {
        frame = new LocalRectangle();
        source = BlockFrameSource.None;
        if (rootDefinitionId.IsNull)
        {
            return false;
        }

        if (TitleBlockScanCaches.Active
            && TitleBlockScanCaches.Frames.TryGetValue(rootDefinitionId, out var cached))
        {
            frame = cached.Frame;
            source = cached.Source;
            return cached.Ok;
        }

        var closedRectangles = new List<LocalRectangle>();
        var segments = new List<FourLineRectangleFinder.Segment>();
        var lineExtents = new List<Extents3d>();
        Collect(
            tr,
            rootDefinitionId,
            Matrix3d.Identity,
            closedRectangles,
            segments,
            lineExtents,
            new HashSet<ObjectId>(),
            depth: 0);

        var fourLineRectangles = segments.Count >= 4
            ? FourLineRectangleFinder.Find(segments, CancellationToken.None)
            : new List<LocalRectangle>();

        var candidates = new List<(LocalRectangle Rect, BlockFrameSource Source)>(
            closedRectangles.Count + fourLineRectangles.Count);
        foreach (var rect in closedRectangles)
        {
            candidates.Add((rect, BlockFrameSource.ClosedRectangle));
        }
        foreach (var rect in fourLineRectangles)
        {
            candidates.Add((rect, BlockFrameSource.FourLineRectangle));
        }

        bool ok;
        if (candidates.Count > 0)
        {
            var best = candidates.OrderByDescending(item => RectangleGeometry.GetActualArea(item.Rect)).First();
            frame = best.Rect;
            source = best.Source;
            ok = frame.HasArea();
        }
        else if (lineExtents.Count == 0)
        {
            ok = false;
        }
        else
        {
            var merged = lineExtents[0];
            for (var index = 1; index < lineExtents.Count; index++)
            {
                merged.AddExtents(lineExtents[index]);
            }

            if (!HasValidExtents(merged))
            {
                ok = false;
            }
            else
            {
                frame = CreateRectangleFromExtents(merged);
                source = BlockFrameSource.LineExtents;
                ok = true;
            }
        }

        if (TitleBlockScanCaches.Active)
        {
            TitleBlockScanCaches.Frames[rootDefinitionId] = (ok, frame, source);
        }

        return ok;
    }

    private static void Collect(
        Transaction tr,
        ObjectId definitionId,
        Matrix3d definitionToRoot,
        ICollection<LocalRectangle> closedRectangles,
        ICollection<FourLineRectangleFinder.Segment> segments,
        ICollection<Extents3d> lineExtents,
        ISet<ObjectId> visitedDefinitions,
        int depth)
    {
        if (depth > 12 || definitionId.IsNull || !visitedDefinitions.Add(definitionId))
        {
            return;
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
            foreach (ObjectId entityId in definition)
            {
                if (tr.GetObject(entityId, OpenMode.ForRead, false) is not Entity entity
                    || !IsEntityVisible(entity))
                {
                    continue;
                }

                try
                {
                    var localRectangle = new LocalRectangle();
                    var isClosedRectangle = entity switch
                    {
                        Polyline polyline => RectangleGeometry.TryGetRectangle(
                            polyline, Matrix3d.Identity, requireClosed: true, out localRectangle),
                        Polyline2d polyline2d => RectangleGeometry.TryGetRectangleFrom2d(
                            tr, polyline2d, Matrix3d.Identity, requireClosed: true, out localRectangle),
                        Polyline3d polyline3d => RectangleGeometry.TryGetRectangleFrom3d(
                            tr, polyline3d, Matrix3d.Identity, requireClosed: true, out localRectangle),
                        _ => false
                    };

                    if (isClosedRectangle)
                    {
                        closedRectangles.Add(
                            RectangleGeometry.TransformRectangle(localRectangle, definitionToRoot));
                    }


                    if (depth <= FourLineSegmentMaxDepth)
                    {
                        if (entity is Line line)
                        {
                            FourLineRectangleFinder.TryAddLine(line, definitionToRoot, segments);
                        }
                        else if (entity is Polyline openPl
                            && FourLineRectangleFinder.TryGetStraightOpenPolylineSegment(
                                openPl, definitionToRoot, out var plSeg))
                        {
                            segments.Add(plSeg);
                        }
                        else if (entity is Polyline2d openPl2d
                            && FourLineRectangleFinder.TryGetStraightOpenPolyline2dSegment(
                                tr, openPl2d, definitionToRoot, out var pl2dSeg))
                        {
                            segments.Add(pl2dSeg);
                        }
                    }

                    if (entity is Line or Polyline or Polyline2d or Polyline3d)
                    {
                        var transformedExtents = TransformExtents(entity.GeometricExtents, definitionToRoot);
                        if (HasValidExtents(transformedExtents))
                        {
                            lineExtents.Add(transformedExtents);
                        }
                    }
                }
                catch
                {

                }

                if (entity is not BlockReference nested || depth >= 12)
                {
                    continue;
                }

                try
                {
                    Collect(
                        tr,
                        nested.BlockTableRecord,
                        nested.BlockTransform * definitionToRoot,
                        closedRectangles,
                        segments,
                        lineExtents,
                        visitedDefinitions,
                        depth + 1);
                }
                catch
                {

                }
            }
        }
        finally
        {
            visitedDefinitions.Remove(definitionId);
        }
    }

    private static LocalRectangle CreateRectangleFromExtents(Extents3d extents)
    {
        var rectangle = LocalRectangle.FromPoints(
            extents.MinPoint.X,
            extents.MinPoint.Y,
            extents.MaxPoint.X,
            extents.MaxPoint.Y);
        var width = rectangle.MaxX - rectangle.MinX;
        var height = rectangle.MaxY - rectangle.MinY;
        rectangle.ActualWidth = Math.Max(width, height);
        rectangle.ActualHeight = Math.Min(width, height);
        rectangle.CornerPoints = new[]
        {
            rectangle.MinX, rectangle.MinY,
            rectangle.MaxX, rectangle.MinY,
            rectangle.MaxX, rectangle.MaxY,
            rectangle.MinX, rectangle.MaxY
        };
        return rectangle;
    }

    private static Extents3d TransformExtents(Extents3d extents, Matrix3d transform)
    {
        var min = extents.MinPoint;
        var max = extents.MaxPoint;
        var points = new[]
        {
            new Point3d(min.X, min.Y, min.Z),
            new Point3d(min.X, min.Y, max.Z),
            new Point3d(min.X, max.Y, min.Z),
            new Point3d(min.X, max.Y, max.Z),
            new Point3d(max.X, min.Y, min.Z),
            new Point3d(max.X, min.Y, max.Z),
            new Point3d(max.X, max.Y, min.Z),
            new Point3d(max.X, max.Y, max.Z)
        };
        for (var index = 0; index < points.Length; index++)
        {
            points[index] = points[index].TransformBy(transform);
        }

        var result = new Extents3d(points[0], points[0]);
        for (var index = 1; index < points.Length; index++)
        {
            result.AddPoint(points[index]);
        }
        return result;
    }

    private static bool HasValidExtents(Extents3d extents)
    {
        return IsFinite(extents.MinPoint.X)
            && IsFinite(extents.MinPoint.Y)
            && IsFinite(extents.MaxPoint.X)
            && IsFinite(extents.MaxPoint.Y)
            && extents.MaxPoint.X > extents.MinPoint.X
            && extents.MaxPoint.Y > extents.MinPoint.Y;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsEntityVisible(Entity entity)
    {
        try
        {
            return entity.Visible;
        }
        catch
        {
            return true;
        }
    }
}
