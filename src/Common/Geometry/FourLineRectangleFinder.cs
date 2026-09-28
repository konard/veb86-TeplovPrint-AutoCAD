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

/// <summary>



/// </summary>
internal static class FourLineRectangleFinder
{

    internal struct Segment
    {
        public Point3d Start;
        public Point3d End;
    }


    private struct SegmentEndpoint
    {
        public Point3d Point;
        public int SegmentIndex;
    }

    /// <summary>

    /// </summary>
    private sealed class EndpointQuadtree
    {
        private const int NodeCapacity = 16;
        private const int MaximumDepth = 16;

        private readonly double _minX;
        private readonly double _minY;
        private readonly double _maxX;
        private readonly double _maxY;
        private readonly int _depth;
        private readonly List<SegmentEndpoint> _items = new();
        private EndpointQuadtree[]? _children;

        public EndpointQuadtree(double minX, double minY, double maxX, double maxY, int depth = 0)
        {
            _minX = minX;
            _minY = minY;
            _maxX = maxX;
            _maxY = maxY;
            _depth = depth;
        }

        public void Insert(SegmentEndpoint item)
        {
            if (_children != null)
            {
                ChildFor(item.Point).Insert(item);
                return;
            }

            _items.Add(item);
            if (_items.Count <= NodeCapacity || _depth >= MaximumDepth)
            {
                return;
            }

            Subdivide();
            foreach (var existing in _items)
            {
                ChildFor(existing.Point).Insert(existing);
            }
            _items.Clear();
        }

        public void Query(double minX, double minY, double maxX, double maxY, ICollection<SegmentEndpoint> result)
        {
            if (maxX < _minX || minX > _maxX || maxY < _minY || minY > _maxY)
            {
                return;
            }

            if (_children != null)
            {
                foreach (var child in _children)
                {
                    child.Query(minX, minY, maxX, maxY, result);
                }
                return;
            }

            foreach (var item in _items)
            {
                if (item.Point.X >= minX && item.Point.X <= maxX
                    && item.Point.Y >= minY && item.Point.Y <= maxY)
                {
                    result.Add(item);
                }
            }
        }

        private void Subdivide()
        {
            var midX = (_minX + _maxX) / 2d;
            var midY = (_minY + _maxY) / 2d;
            _children = new[]
            {
                new EndpointQuadtree(_minX, _minY, midX, midY, _depth + 1),
                new EndpointQuadtree(midX, _minY, _maxX, midY, _depth + 1),
                new EndpointQuadtree(_minX, midY, midX, _maxY, _depth + 1),
                new EndpointQuadtree(midX, midY, _maxX, _maxY, _depth + 1)
            };
        }

        private EndpointQuadtree ChildFor(Point3d point)
        {
            var midX = (_minX + _maxX) / 2d;
            var midY = (_minY + _maxY) / 2d;
            var index = (point.X >= midX ? 1 : 0) + (point.Y >= midY ? 2 : 0);
            return _children![index];
        }
    }

    /// <summary>

    /// </summary>
    private static double FourLineEndpointTolerance(double segmentLength)
    {
        return Math.Max(0.01, segmentLength * 0.001);
    }

    /// <summary>

    /// </summary>



    internal static List<LocalRectangle> Find(
        IReadOnlyList<Segment> segments,
        CancellationToken cancel = default,
        Action<string, int, int>? reportProgress = null)
    {
        var rectangles = new List<LocalRectangle>();
        if (segments.Count < 4)
        {
            return rectangles;
        }

        var lengths = new double[segments.Count];
        var maxEndpointTolerance = 0.01;
        for (var i = 0; i < segments.Count; i++)
        {
            lengths[i] = segments[i].Start.DistanceTo(segments[i].End);
            maxEndpointTolerance = Math.Max(maxEndpointTolerance, FourLineEndpointTolerance(lengths[i]));
        }


        const int maximumConnectionsAtCorner = 8;

        var minX = segments.Min(segment => Math.Min(segment.Start.X, segment.End.X));
        var minY = segments.Min(segment => Math.Min(segment.Start.Y, segment.End.Y));
        var maxX = segments.Max(segment => Math.Max(segment.Start.X, segment.End.X));
        var maxY = segments.Max(segment => Math.Max(segment.Start.Y, segment.End.Y));
        var padding = Math.Max(maxEndpointTolerance, Math.Max(maxX - minX, maxY - minY) * 1e-12);
        var endpointTree = new EndpointQuadtree(
            minX - padding,
            minY - padding,
            maxX + padding,
            maxY + padding);
        for (var i = 0; i < segments.Count; i++)
        {
            endpointTree.Insert(new SegmentEndpoint { Point = segments[i].Start, SegmentIndex = i });
            endpointTree.Insert(new SegmentEndpoint { Point = segments[i].End, SegmentIndex = i });
        }

        bool IsNear(Point3d a, Point3d b, double tol)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz <= tol * tol;
        }

        List<int> FindConnected(ISet<int> excludedIndices, Point3d point, double tolerance)
        {
            var candidates = new List<(int Index, double Distance)>();
            var seen = new HashSet<int>();
            var endpoints = new List<SegmentEndpoint>();
            endpointTree.Query(
                point.X - tolerance,
                point.Y - tolerance,
                point.X + tolerance,
                point.Y + tolerance,
                endpoints);
            foreach (var endpoint in endpoints)
            {
                var segmentIndex = endpoint.SegmentIndex;
                if (excludedIndices.Contains(segmentIndex) || !seen.Add(segmentIndex))
                {
                    continue;
                }

                var segment = segments[segmentIndex];
                var distance = Math.Min(segment.Start.DistanceTo(point), segment.End.DistanceTo(point));
                if (distance <= tolerance)
                {
                    candidates.Add((segmentIndex, distance));
                }
            }

            candidates.Sort((left, right) => left.Distance.CompareTo(right.Distance));
            var take = Math.Min(maximumConnectionsAtCorner, candidates.Count);
            var result = new List<int>(take);
            for (var index = 0; index < take; index++)
            {
                result.Add(candidates[index].Index);
            }

            return result;
        }

        Point3d OtherEnd(int segIndex, Point3d point, double tolerance)
        {
            var s = segments[segIndex];
            if (IsNear(s.Start, point, tolerance))
            {
                return s.End;
            }

            if (IsNear(s.End, point, tolerance))
            {
                return s.Start;
            }

            return new Point3d(double.MaxValue, double.MaxValue, 0);
        }

        var foundKeys = new HashSet<string>();

        void ExploreCycle(int i1, Point3d pointA, Point3d pointB)
        {
            var toleranceAtB = FourLineEndpointTolerance(lengths[i1]);
            var connectedAtB = FindConnected(new HashSet<int> { i1 }, pointB, toleranceAtB);

            foreach (var i2 in connectedAtB)
            {
                var joinTolerance12 = Math.Max(toleranceAtB, FourLineEndpointTolerance(lengths[i2]));
                var pointC = OtherEnd(i2, pointB, joinTolerance12);
                var toleranceAtC = FourLineEndpointTolerance(lengths[i2]);
                var usedAfterSecond = new HashSet<int> { i1, i2 };
                var connectedAtC = FindConnected(usedAfterSecond, pointC, toleranceAtC);

                foreach (var i3 in connectedAtC)
                {
                    var joinTolerance23 = Math.Max(toleranceAtC, FourLineEndpointTolerance(lengths[i3]));
                    var pointD = OtherEnd(i3, pointC, joinTolerance23);
                    var toleranceAtD = FourLineEndpointTolerance(lengths[i3]);
                    var usedAfterThird = new HashSet<int> { i1, i2, i3 };
                    var connectedAtD = FindConnected(usedAfterThird, pointD, toleranceAtD);

                    foreach (var i4 in connectedAtD)
                    {
                        var joinTolerance34 = Math.Max(toleranceAtD, FourLineEndpointTolerance(lengths[i4]));
                        var backToA = OtherEnd(i4, pointD, joinTolerance34);
                        var closeTolerance = Math.Max(
                            FourLineEndpointTolerance(lengths[i4]),
                            FourLineEndpointTolerance(lengths[i1]));
                        if (!IsNear(backToA, pointA, closeTolerance))
                        {
                            continue;
                        }

                        if (!TryIntersectLines(segments[i4], segments[i1], out var cornerA)
                            || !TryIntersectLines(segments[i1], segments[i2], out var cornerB)
                            || !TryIntersectLines(segments[i2], segments[i3], out var cornerC)
                            || !TryIntersectLines(segments[i3], segments[i4], out var cornerD))
                        {
                            continue;
                        }

                        var corners = new[] { cornerA, cornerB, cornerC, cornerD };
                        if (!TryBuildRectangleFromCorners(corners, out var rectangle))
                        {
                            continue;
                        }

                        var ids = new[] { i1, i2, i3, i4 };
                        Array.Sort(ids);
                        var key = string.Join(",", ids);
                        if (!foundKeys.Add(key))
                        {
                            continue;
                        }

                        rectangles.Add(rectangle);
                    }
                }
            }
        }

        for (var i1 = 0; i1 < segments.Count; i1++)
        {
            if ((i1 % 2000) == 0)
            {
                cancel.ThrowIfCancellationRequested();
                reportProgress?.Invoke(
                    $"Сборка рамок из четырёх линий… {i1:N0}/{segments.Count:N0}",
                    i1,
                    segments.Count);
            }

            var first = segments[i1];
            ExploreCycle(i1, first.Start, first.End);
            ExploreCycle(i1, first.End, first.Start);
        }

        return rectangles;
    }


    private static bool TryIntersectLines(Segment a, Segment b, out Point3d intersection)
    {
        intersection = new Point3d();
        var dx1 = a.End.X - a.Start.X;
        var dy1 = a.End.Y - a.Start.Y;
        var dx2 = b.End.X - b.Start.X;
        var dy2 = b.End.Y - b.Start.Y;
        var denom = dx1 * dy2 - dy1 * dx2;
        if (Math.Abs(denom) < 1e-12)
        {
            return false;
        }

        var t = ((b.Start.X - a.Start.X) * dy2 - (b.Start.Y - a.Start.Y) * dx2) / denom;
        intersection = new Point3d(a.Start.X + t * dx1, a.Start.Y + t * dy1, 0);
        return true;
    }

    /// <summary>


    /// </summary>
    private static bool TryBuildRectangleFromCorners(Point3d[] corners, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();

        var minX = corners.Min(p => p.X);
        var minY = corners.Min(p => p.Y);
        var maxX = corners.Max(p => p.X);
        var maxY = corners.Max(p => p.Y);
        var boxWidth = maxX - minX;
        var boxHeight = maxY - minY;
        if (boxWidth <= 1e-6 || boxHeight <= 1e-6)
        {
            return false;
        }

        var tolerance = Math.Max(boxWidth, boxHeight) * 0.001;
        if (corners.Max(point => point.Z) - corners.Min(point => point.Z) > tolerance)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            for (var j = i + 1; j < 4; j++)
            {
                if (SamePoint(corners[i], corners[j], tolerance))
                {
                    return false;
                }
            }
        }

        var edgeX = new double[4];
        var edgeY = new double[4];
        var edgeLength = new double[4];
        for (var index = 0; index < 4; index++)
        {
            var next = (index + 1) % 4;
            edgeX[index] = corners[next].X - corners[index].X;
            edgeY[index] = corners[next].Y - corners[index].Y;
            edgeLength[index] = Math.Sqrt(
                edgeX[index] * edgeX[index] + edgeY[index] * edgeY[index]);
            if (edgeLength[index] <= tolerance)
            {
                return false;
            }
        }

        const double directionTolerance = 0.001;
        for (var index = 0; index < 4; index++)
        {
            var next = (index + 1) % 4;
            var normalizedDot = Math.Abs(
                edgeX[index] * edgeX[next] + edgeY[index] * edgeY[next])
                / (edgeLength[index] * edgeLength[next]);
            if (normalizedDot > directionTolerance)
            {
                return false;
            }
        }

        var parallel02 = Math.Abs(edgeX[0] * edgeY[2] - edgeY[0] * edgeX[2])
                         / (edgeLength[0] * edgeLength[2]);
        var parallel13 = Math.Abs(edgeX[1] * edgeY[3] - edgeY[1] * edgeX[3])
                         / (edgeLength[1] * edgeLength[3]);
        if (parallel02 > directionTolerance
            || parallel13 > directionTolerance
            || Math.Abs(edgeLength[0] - edgeLength[2]) > Math.Max(tolerance, edgeLength[0] * 0.001)
            || Math.Abs(edgeLength[1] - edgeLength[3]) > Math.Max(tolerance, edgeLength[1] * 0.001))
        {
            return false;
        }

        var d02 = corners[0].DistanceTo(corners[2]);
        var d13 = corners[1].DistanceTo(corners[3]);
        if (Math.Abs(d02 - d13) > tolerance)
        {
            return false;
        }

        var mid02 = new Point3d(
            (corners[0].X + corners[2].X) / 2d,
            (corners[0].Y + corners[2].Y) / 2d, 0);
        var mid13 = new Point3d(
            (corners[1].X + corners[3].X) / 2d,
            (corners[1].Y + corners[3].Y) / 2d, 0);
        if (mid02.DistanceTo(mid13) > tolerance)
        {
            return false;
        }

        var side01 = corners[0].DistanceTo(corners[1]);
        var side12 = corners[1].DistanceTo(corners[2]);
        var actualWidth = Math.Max(side01, side12);
        var actualHeight = Math.Min(side01, side12);

        rectangle = LocalRectangle.FromPoints(minX, minY, maxX, maxY);
        rectangle.ActualWidth = actualWidth;
        rectangle.ActualHeight = actualHeight;
        rectangle.CornerPoints = new[]
        {
            corners[0].X, corners[0].Y,
            corners[1].X, corners[1].Y,
            corners[2].X, corners[2].Y,
            corners[3].X, corners[3].Y
        };
        return true;
    }

    private static bool SamePoint(Point3d a, Point3d b, double tolerance)
    {
        return Math.Abs(a.X - b.X) <= tolerance
            && Math.Abs(a.Y - b.Y) <= tolerance;
    }

    /// <summary>


    /// </summary>
    internal static bool TryGetStraightOpenPolylineSegment(
        Polyline polyline,
        Matrix3d transform,
        out Segment segment)
    {
        segment = new Segment();
        if (polyline.Closed || polyline.NumberOfVertices < 2)
        {
            return false;
        }

        var points = new List<Point3d>(polyline.NumberOfVertices);
        for (var index = 0; index < polyline.NumberOfVertices; index++)
        {
            if (index < polyline.NumberOfVertices - 1
                && Math.Abs(polyline.GetBulgeAt(index)) > 1e-9)
            {
                return false;
            }
            points.Add(polyline.GetPoint3dAt(index).TransformBy(transform));
        }

        return TryBuildStraightSegment(points, out segment);
    }


    internal static bool TryGetStraightOpenPolyline2dSegment(
        Transaction tr,
        Polyline2d polyline,
        Matrix3d transform,
        out Segment segment)
    {
        segment = new Segment();
        if (polyline.Closed)
        {
            return false;
        }

        var vertices = new List<Vertex2d>();
        foreach (ObjectId vertexId in polyline)
        {
            if (tr.GetObject(vertexId, OpenMode.ForRead, false) is Vertex2d vertex)
            {
                vertices.Add(vertex);
            }
        }
        if (vertices.Count < 2)
        {
            return false;
        }

        var points = new List<Point3d>(vertices.Count);
        for (var index = 0; index < vertices.Count; index++)
        {
            if (index < vertices.Count - 1 && Math.Abs(vertices[index].Bulge) > 1e-9)
            {
                return false;
            }
            points.Add(vertices[index].Position.TransformBy(transform));
        }

        return TryBuildStraightSegment(points, out segment);
    }

    internal static bool TryBuildStraightSegment(IReadOnlyList<Point3d> points, out Segment segment)
    {
        segment = new Segment();
        var start = points[0];
        var end = points[points.Count - 1];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 1e-12)
        {
            return false;
        }

        var length = Math.Sqrt(lengthSquared);
        var distanceTolerance = Math.Max(1e-6, length * 1e-8);
        var parameterTolerance = distanceTolerance / length;
        var previousParameter = -parameterTolerance;
        foreach (var point in points)
        {
            var cross = (point.X - start.X) * dy - (point.Y - start.Y) * dx;
            if (Math.Abs(cross) / length > distanceTolerance)
            {
                return false;
            }

            var parameter = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
            if (parameter < -parameterTolerance
                || parameter > 1d + parameterTolerance
                || parameter + parameterTolerance < previousParameter)
            {
                return false;
            }
            previousParameter = parameter;
        }

        segment = new Segment { Start = start, End = end };
        return true;
    }


    internal static bool TryAddLine(Line line, Matrix3d transform, ICollection<Segment> segments)
    {
        var segment = new Segment
        {
            Start = line.StartPoint.TransformBy(transform),
            End = line.EndPoint.TransformBy(transform)
        };
        if (segment.Start.DistanceTo(segment.End) <= 1e-6)
        {
            return false;
        }

        segments.Add(segment);
        return true;
    }
}
