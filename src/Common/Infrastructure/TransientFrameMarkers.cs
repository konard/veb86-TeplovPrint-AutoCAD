using System;
using System.Collections.Generic;
#if AUTOCAD
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
#else
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.GraphicsInterface;

using Polyline = ZwSoft.ZwCAD.DatabaseServices.Polyline;
#endif

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
public sealed class TransientFrameMarkers : IDisposable
{


    private const int FirstSubSystemId = 128;

    private static readonly Color MarkerColor = Color.FromColorIndex(ColorMethod.ByAci, 1);

    private readonly Editor _editor;
    private readonly Dictionary<string, int> _subSystemIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entity>> _markers = new(StringComparer.Ordinal);
    private int _nextSubSystemId = FirstSubSystemId;

    public TransientFrameMarkers(Editor editor)
    {
        _editor = editor;
    }

    /// <summary>


    /// </summary>

    public void SetBox(string key, Point3d corner1, Point3d corner2, string? label, bool refresh = true)
    {
        Remove(key, refresh: false);

        var entities = BuildBoxEntities(corner1, corner2, label);
        var subSystemId = GetSubSystemId(key);
        var transientManager = TransientManager.CurrentTransientManager;
        foreach (var entity in entities)
        {


            transientManager.AddTransient(entity, TransientDrawingMode.DirectTopmost, subSystemId, new IntegerCollection());
        }

        _markers[key] = entities;
        if (refresh)
        {
            RefreshDisplay();
        }
    }

    /// <summary>

    /// </summary>

    public void Remove(string key, bool refresh = true)
    {
        if (!_markers.TryGetValue(key, out var entities))
        {
            return;
        }

        TransientManager.CurrentTransientManager.EraseTransients(
            TransientDrawingMode.DirectTopmost, _subSystemIds[key], new IntegerCollection());
        foreach (var entity in entities)
        {
            entity.Dispose();
        }

        _markers.Remove(key);
        if (refresh)
        {
            RefreshDisplay();
        }
    }

    /// <summary>


    /// </summary>
    public void RefreshDisplay(bool regenerate = false)
    {
        try
        {
            _editor.UpdateScreen();
            if (regenerate)
            {
                _editor.Regen();
            }
        }
        catch
        {

        }
    }

    /// <summary>

    /// </summary>
    public void Clear()
    {
        foreach (var key in new List<string>(_markers.Keys))
        {
            Remove(key, refresh: false);
        }

        RefreshDisplay();
    }

    public void Dispose() => Clear();

    private int GetSubSystemId(string key)
    {
        if (!_subSystemIds.TryGetValue(key, out var id))
        {
            id = _nextSubSystemId++;
            _subSystemIds[key] = id;
        }

        return id;
    }

    private static List<Entity> BuildBoxEntities(Point3d corner1, Point3d corner2, string? label)
    {
        var minX = Math.Min(corner1.X, corner2.X);
        var minY = Math.Min(corner1.Y, corner2.Y);
        var maxX = Math.Max(corner1.X, corner2.X);
        var maxY = Math.Max(corner1.Y, corner2.Y);
        var z = (corner1.Z + corner2.Z) / 2;

        var rectangle = new Polyline();
        rectangle.AddVertexAt(0, new Point2d(minX, minY), 0, 0, 0);
        rectangle.AddVertexAt(1, new Point2d(maxX, minY), 0, 0, 0);
        rectangle.AddVertexAt(2, new Point2d(maxX, maxY), 0, 0, 0);
        rectangle.AddVertexAt(3, new Point2d(minX, maxY), 0, 0, 0);
        rectangle.Closed = true;
        rectangle.Color = MarkerColor;

        var diagonal1 = new Line(new Point3d(minX, minY, z), new Point3d(maxX, maxY, z)) { Color = MarkerColor };
        var diagonal2 = new Line(new Point3d(minX, maxY, z), new Point3d(maxX, minY, z)) { Color = MarkerColor };

        var entities = new List<Entity> { rectangle, diagonal1, diagonal2 };

        if (!string.IsNullOrWhiteSpace(label))
        {
            var width = maxX - minX;
            var height = maxY - minY;

            var textHeight = Math.Min(Math.Min(height * 0.35, width / Math.Max(label!.Length, 1)), 120d);
            if (textHeight > 1e-6)
            {
                var center = new Point3d((minX + maxX) / 2, (minY + maxY) / 2, z);
                var text = new DBText
                {
                    TextString = label,
                    Height = textHeight,
                    HorizontalMode = TextHorizontalMode.TextCenter,
                    VerticalMode = TextVerticalMode.TextVerticalMid,
                    Color = MarkerColor
                };


                text.AlignmentPoint = center;
                text.Position = center;
                entities.Add(text);
            }
        }

        return entities;
    }
}
