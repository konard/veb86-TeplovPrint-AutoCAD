using System;
using System.Collections.Generic;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using CadRuntimeException = ZwSoft.ZwCAD.Runtime.Exception;
#endif

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
public sealed class TemporarySequenceOverlay : IDisposable
{
    private const string LayerName = "ZBP_TEMP_SEQUENCE_OVERLAY";
    private const string TextStyleName = "ZBP_TEMP_SEQUENCE_TEXT";
    private readonly Document _document;
    private readonly List<ObjectId> _entityIds = new();
    private readonly Dictionary<PlotJob, OverlayEntityGroup> _entityGroups = new();
    private PlotJob? _highlightedJob;
    private bool _defaultFrameThickness;

    private sealed class OverlayEntityGroup
    {
        public ObjectId FrameId { get; set; }
        public List<ObjectId> LabelIds { get; } = new();
        public double NormalFrameWidth { get; set; }
        public double HighlightFrameWidth { get; set; }
        public LineWeight FrameLineWeight { get; set; }
    }

    public TemporarySequenceOverlay(Document document)
    {
        _document = document;
    }

    public void Show(IReadOnlyList<PlotJob> jobs, int highlightIndex)
    {

        var highlightJob = highlightIndex >= 0 && highlightIndex < jobs.Count ? jobs[highlightIndex] : null;
        Show(jobs, highlightJob);
    }

    public void Show(IReadOnlyList<PlotJob> jobs, PlotJob? highlightJob = null)
    {
        Show(jobs, highlightJob, null);
    }

    public void Show(IReadOnlyList<PlotJob> jobs, PlotJob? highlightJob, Func<PlotJob, int, string>? labelProvider)
    {

        Clear(repaint: false);

        using var docLock = _document.LockDocument();
        using var tr = _document.Database.TransactionManager.StartTransaction();
        var db = _document.Database;
        var layerId = EnsureLayer(tr, db);
        var textStyleId = EnsureTextStyle(tr, db);



        var dcsToWcs = Matrix3d.Identity;

        var ucsAngle = 0d;
        try
        {
            if (_document.Database.TileMode)
            {
                var view = _document.Editor.GetCurrentView();
                dcsToWcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                dcsToWcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * dcsToWcs;
                dcsToWcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * dcsToWcs;

                var ucs = _document.Editor.CurrentUserCoordinateSystem;
                ucsAngle = Math.Atan2(ucs[1, 0], ucs[0, 0]);
            }
        }
        catch
        {
        }

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (!TryGetBounds(job, dcsToWcs, out var minX, out var minY, out var maxX, out var maxY))
            {
                continue;
            }

            var width = maxX - minX;
            var height = maxY - minY;
            var minSide = Math.Min(width, height);
            var padding = Math.Max(minSide * 0.035, 10d);
            var textHeight = GetTextHeight(width, height);

            var isHighlight = ReferenceEquals(job, highlightJob);
            var color = GetOverlayColor(isHighlight);

            var normalFrameWidth = GetFrameWidth(minSide);
            // The selected (yellow) frame must stay the same unobtrusive width as
            // the regular red frame.
            var highlightFrameWidth = normalFrameWidth;
            var frameWidth = isHighlight ? highlightFrameWidth : normalFrameWidth;
            var lineWeight = GetLineWeight(minSide);

            var ownerId = GetJobOwnerId(tr, db, job);
            if (ownerId.IsNull)
            {
                continue;
            }

            var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForWrite);


            var jobAngle = ucsAngle;
            var cx = (minX + maxX) / 2d;
            var cy = (minY + maxY) / 2d;
            if (job.UsesUserCoordinateSystem)
            {
                var localCenter = new Point3d(cx, cy, 0)
                    .TransformBy(CadSelectionWindow.GetJobUcsToWorld(job));
                cx = localCenter.X;
                cy = localCenter.Y;
                jobAngle = Math.Atan2(job.UcsXAxisY, job.UcsXAxisX);
            }


            var cosA = Math.Cos(jobAngle);
            var sinA = Math.Sin(jobAngle);
            var hw = (maxX - minX) / 2d + padding;
            var hh = (maxY - minY) / 2d + padding;

            Point2d Rot(double dx, double dy) =>
                new(cx + dx * cosA - dy * sinA, cy + dx * sinA + dy * cosA);

            var frame = new Polyline(4)
            {
                Closed = true,
                Color = color,
                LayerId = layerId,
                LineWeight = _defaultFrameThickness ? LineWeight.ByLineWeightDefault : lineWeight,
                ConstantWidth = _defaultFrameThickness ? 0d : frameWidth
            };
            frame.AddVertexAt(0, Rot(-hw, -hh), 0, 0, 0);
            frame.AddVertexAt(1, Rot(+hw, -hh), 0, 0, 0);
            frame.AddVertexAt(2, Rot(+hw, +hh), 0, 0, 0);
            frame.AddVertexAt(3, Rot(-hw, +hh), 0, 0, 0);

            var group = new OverlayEntityGroup
            {
                NormalFrameWidth = normalFrameWidth,
                HighlightFrameWidth = highlightFrameWidth,
                FrameLineWeight = lineWeight
            };
            group.FrameId = AddEntity(tr, owner, frame);

            // Толстая цветная рамка должна быть под чертежом, чтобы не перекрывать
            // линии/тексты/штриховки. Номер листа при этом остаётся сверху.
            MoveEntityToBottom(tr, owner, group.FrameId);

            var center = new Point3d(cx, cy, 0);

            var labelText = labelProvider?.Invoke(job, i) ?? (i + 1).ToString();
            AddBoldLabel(tr, owner, layerId, textStyleId, color, center, labelText, textHeight, jobAngle, group.LabelIds);
            _entityGroups[job] = group;
        }

        _highlightedJob = highlightJob;
        tr.Commit();
        UpdateScreenOnly();
    }

    public void Clear(bool repaint = true)
    {
        if (_entityIds.Count == 0 && _entityGroups.Count == 0)
        {
            _highlightedJob = null;
            if (repaint)
            {
                UpdateScreenOnly();
            }

            return;
        }


        var cleared = TryEraseOverlayEntities();
        if (cleared)
        {
            _entityIds.Clear();
            _entityGroups.Clear();
            _highlightedJob = null;
        }

        if (repaint)
        {
            UpdateScreenOnly();
        }
    }


    public void Dispose()
    {
        Clear(repaint: false);
    }

    /// <summary>


    /// </summary>
    private bool TryEraseOverlayEntities()
    {
        if (_entityIds.Count == 0 && _entityGroups.Count == 0)
        {
            return true;
        }

        try
        {
            using var docLock = _document.LockDocument();
            using var tr = _document.Database.TransactionManager.StartTransaction();
            var db = _document.Database;
            EnsureLayerUnlocked(tr, db);

            var trackedIds = _entityIds.Count;
            foreach (var id in _entityIds)
            {
                EraseEntityIfAlive(tr, id);
            }


            if (trackedIds == 0)
            {
                EraseAllEntitiesOnOverlayLayer(tr, db);
            }

            tr.Commit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureLayerUnlocked(Transaction tr, Database db)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(LayerName))
        {
            return;
        }

        var layer = (LayerTableRecord)tr.GetObject(table[LayerName], OpenMode.ForWrite);
        layer.IsOff = false;
        layer.IsFrozen = false;
        layer.IsLocked = false;
    }

    private static void EraseAllEntitiesOnOverlayLayer(Transaction tr, Database db)
    {
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId btrId in blockTable)
        {
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);

            if (!btr.IsLayout)
            {
                continue;
            }

            foreach (ObjectId entId in btr)
            {
                if (tr.GetObject(entId, OpenMode.ForRead, false) is not Entity entity
                    || entity.IsErased
                    || !string.Equals(entity.Layer, LayerName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entity.UpgradeOpen();
                entity.Erase();
            }
        }
    }

    private static void EraseEntityIfAlive(Transaction tr, ObjectId id)
    {
        if (id.IsNull || id.IsErased)
        {
            return;
        }

        if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
        {
            entity.Erase();
        }
    }

    private static ObjectId EnsureLayer(Transaction tr, Database db)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (table.Has(LayerName))
        {
            var existing = (LayerTableRecord)tr.GetObject(table[LayerName], OpenMode.ForWrite);
            existing.IsOff = false;
            existing.IsFrozen = false;
            existing.IsLocked = false;

            existing.IsPlottable = false;
            existing.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
            return existing.ObjectId;
        }

        table.UpgradeOpen();
        var record = new LayerTableRecord
        {
            Name = LayerName,
            Color = Color.FromColorIndex(ColorMethod.ByAci, 1),
            IsPlottable = false
        };
        var id = table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
        return id;
    }

    private static ObjectId EnsureTextStyle(Transaction tr, Database db)
    {
        var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (table.Has(TextStyleName))
        {
            var existing = (TextStyleTableRecord)tr.GetObject(table[TextStyleName], OpenMode.ForWrite);
            existing.FileName = "arial.ttf";
            existing.XScale = 1.0;
            existing.ObliquingAngle = 0;
            return existing.ObjectId;
        }

        table.UpgradeOpen();
        var record = new TextStyleTableRecord
        {
            Name = TextStyleName,
            FileName = "arial.ttf",
            XScale = 1.0,
            ObliquingAngle = 0
        };
        var id = table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
        return id;
    }

    private static void MoveEntityToBottom(Transaction tr, BlockTableRecord owner, ObjectId entityId)
    {
        try
        {
            if (entityId.IsNull || entityId.IsErased || owner.DrawOrderTableId.IsNull)
            {
                return;
            }

            var drawOrder = (DrawOrderTable)tr.GetObject(owner.DrawOrderTableId, OpenMode.ForWrite);
            var ids = new ObjectIdCollection { entityId };
            drawOrder.MoveToBottom(ids);
        }
        catch
        {
            // DrawOrder не критичен для работы overlay.
        }
    }

    private ObjectId AddEntity(Transaction tr, BlockTableRecord owner, Entity entity)
    {
        var id = owner.AppendEntity(entity);
        tr.AddNewlyCreatedDBObject(entity, true);
        _entityIds.Add(id);
        return id;
    }

    /// <summary>
    /// Временно переводит рамки overlay на обычную толщину AutoCAD.
    /// Номера и цвет остаются видимыми, но толстая заливка полилинии исчезает.
    /// Используется, например, во время выбора областей TEXT/MTEXT.
    /// </summary>
    public void SetDefaultFrameThickness(bool enabled)
    {
        // Не выходим досрочно даже если флаг уже имеет нужное значение:
        // предыдущая попытка могла не примениться к сущностям из-за временной
        // невозможности LockDocument, а состояние флага уже изменилось.
        _defaultFrameThickness = enabled;

        if (_entityGroups.Count == 0)
        {
            return;
        }

        try
        {
            using var docLock = _document.LockDocument();
            using var tr = _document.Database.TransactionManager.StartTransaction();

            foreach (var pair in _entityGroups)
            {
                var job = pair.Key;
                var group = pair.Value;
                if (group.FrameId.IsNull || group.FrameId.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(group.FrameId, OpenMode.ForWrite, false) is not Polyline frame
                    || frame.IsErased)
                {
                    continue;
                }

                var isHighlight = ReferenceEquals(job, _highlightedJob);
                frame.ConstantWidth = enabled
                    ? 0d
                    : (isHighlight ? group.HighlightFrameWidth : group.NormalFrameWidth);
                frame.LineWeight = enabled
                    ? LineWeight.ByLineWeightDefault
                    : group.FrameLineWeight;
            }

            tr.Commit();

            // Для ConstantWidth одного UpdateScreen иногда недостаточно:
            // AutoCAD может оставить старую толстую отрисовку до следующего REGEN.
            Regen();
        }
        catch
        {
            // Следующий вызов повторит применение даже при том же enabled.
        }
    }

    public void SetHighlight(PlotJob? highlightJob)
    {
        if (ReferenceEquals(_highlightedJob, highlightJob))
        {
            return;
        }

        try
        {
            using var docLock = _document.LockDocument();
            using var tr = _document.Database.TransactionManager.StartTransaction();


            ApplyHighlight(tr, _highlightedJob, false);
            ApplyHighlight(tr, highlightJob, true);

            tr.Commit();
            _highlightedJob = highlightJob;
            UpdateScreenOnly();
        }
        catch
        {

        }
    }

    private void ApplyHighlight(Transaction tr, PlotJob? job, bool highlight)
    {
        if (job == null || !_entityGroups.TryGetValue(job, out var group))
        {
            return;
        }

        var color = GetOverlayColor(highlight);
        if (!group.FrameId.IsNull && !group.FrameId.IsErased
            && tr.GetObject(group.FrameId, OpenMode.ForWrite, false) is Polyline frame
            && !frame.IsErased)
        {
            frame.Color = color;
            frame.ConstantWidth = _defaultFrameThickness
                ? 0d
                : (highlight ? group.HighlightFrameWidth : group.NormalFrameWidth);
            frame.LineWeight = _defaultFrameThickness
                ? LineWeight.ByLineWeightDefault
                : group.FrameLineWeight;
        }

        foreach (var id in group.LabelIds)
        {
            if (id.IsNull || id.IsErased)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity label && !label.IsErased)
            {
                label.Color = color;
            }
        }
    }

    private static ObjectId GetJobOwnerId(Transaction tr, Database db, PlotJob job)
    {
        try
        {
            if (job.IsPaperSpace && !string.IsNullOrWhiteSpace(job.SpaceName))
            {
                var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                if (layouts.Contains(job.SpaceName))
                {
                    var layout = (Layout)tr.GetObject(layouts.GetAt(job.SpaceName), OpenMode.ForRead);
                    return layout.BlockTableRecordId;
                }
            }

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return blockTable[BlockTableRecord.ModelSpace];
        }
        catch
        {
            return ObjectId.Null;
        }
    }

    private void AddBoldLabel(Transaction tr, BlockTableRecord owner, ObjectId layerId, ObjectId textStyleId, Color color, Point3d center, string text, double height, double rotation, List<ObjectId> labelIds)
    {

        var label = new DBText();
        label.SetDatabaseDefaults(_document.Database);
        label.TextString = text;
        label.Color = color;
        label.LayerId = layerId;
        label.TextStyleId = textStyleId;
        label.Height = height;
        label.Rotation = rotation;
        label.HorizontalMode = TextHorizontalMode.TextCenter;
        label.VerticalMode = TextVerticalMode.TextVerticalMid;
        label.Position = center;
        label.AlignmentPoint = center;
        var id = AddEntity(tr, owner, label);
        labelIds.Add(id);
        try
        {
            label.AdjustAlignment(_document.Database);
        }
        catch
        {
        }
    }


    private void Regen()
    {
        try
        {
            _document.Editor.UpdateScreen();
            _document.Editor.Regen();
        }
        catch (CadRuntimeException)
        {
        }
    }

    private void UpdateScreenOnly()
    {
        try
        {

            _document.Editor.UpdateScreen();
        }
        catch (CadRuntimeException)
        {
        }
    }

    private static Color GetOverlayColor(bool highlight)
    {
        return Color.FromColorIndex(ColorMethod.ByAci, highlight ? (short)2 : (short)1);
    }

    private static bool TryGetBounds(PlotJob job, Matrix3d dcsToWcs, out double minX, out double minY, out double maxX, out double maxY)
    {
        if (job.UsesUserCoordinateSystem)
        {
            minX = Math.Min(job.UcsMinX, job.UcsMaxX);
            minY = Math.Min(job.UcsMinY, job.UcsMaxY);
            maxX = Math.Max(job.UcsMinX, job.UcsMaxX);
            maxY = Math.Max(job.UcsMinY, job.UcsMaxY);
            return maxX - minX > 1e-6 && maxY - minY > 1e-6;
        }

        minX = Math.Min(job.MinX, job.MaxX);
        minY = Math.Min(job.MinY, job.MaxY);
        maxX = Math.Max(job.MinX, job.MaxX);
        maxY = Math.Max(job.MinY, job.MaxY);



        if (job.IsDcsWindow)
        {
            var halfW = (maxX - minX) / 2d;
            var halfH = (maxY - minY) / 2d;
            var dcsCenter = new Point3d((minX + maxX) / 2d, (minY + maxY) / 2d, 0);
            var wcsCenter = dcsCenter.TransformBy(dcsToWcs);
            minX = wcsCenter.X - halfW;
            minY = wcsCenter.Y - halfH;
            maxX = wcsCenter.X + halfW;
            maxY = wcsCenter.Y + halfH;
        }

        return maxX - minX > 1e-6 && maxY - minY > 1e-6;
    }

    private static double GetTextHeight(double width, double height)
    {
        var minSide = Math.Min(width, height);
        var maxSide = Math.Max(width, height);
        var heightBySmallSide = minSide * 0.55;
        var heightByLongSide = maxSide * 0.16;
        return Math.Max(Math.Min(heightBySmallSide, heightByLongSide), minSide * 0.35);
    }

    private static double GetFrameWidth(double minSide)
    {
        return Math.Min(Math.Max(minSide * 0.035, 10d), minSide * 0.08);
    }

    private static LineWeight GetLineWeight(double minSide)
    {
        if (minSide >= 1000)
        {
            return LineWeight.LineWeight200;
        }

        if (minSide >= 500)
        {
            return LineWeight.LineWeight140;
        }

        if (minSide >= 200)
        {
            return LineWeight.LineWeight100;
        }

        return LineWeight.LineWeight050;
    }
}
