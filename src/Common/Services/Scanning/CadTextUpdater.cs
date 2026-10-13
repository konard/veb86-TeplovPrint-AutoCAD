using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#elif NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using CadApp = HostMgd.ApplicationServices.Application;
#elif ACAD_CORE
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public static class CadTextUpdater
{
    public static bool TryUpdateOpenDocument(PlotJob job, string? newTitle, string? newNumber, Document currentDocument, out string message)
    {
        message = "";
        var doc = FindTargetDocument(job, currentDocument);
        if (doc == null)
        {
            message = "Соответствующий DWG не открыт: изменена только таблица, CAD не обновлён.";
            return false;
        }

        using (doc.LockDocument())
        {
            try
            {
                return TryUpdate(doc.Database, job, newTitle, newNumber, out message);
            }
            catch (Exception ex)
            {

                message = "Не удалось записать значение обратно в CAD: " + ex.Message;
                return false;
            }
        }
    }

    /// <summary>



    /// </summary>
    public static int UpdateDrawingNumbers(IReadOnlyList<PlotJob> jobs, Document currentDocument, Action<string>? reportFailure = null)
    {
        var updated = 0;
        TitleBlockLibrary? library = null;
        foreach (var group in jobs.GroupBy(j => FindTargetDocument(j, currentDocument)))
        {
            var doc = group.Key;
            if (doc == null)
            {
                foreach (var job in group)
                {
                    reportFailure?.Invoke($"Не удалось записать номер {job.DrawingNumber} в CAD: соответствующий DWG не открыт; изменена только таблица.");
                }
                continue;
            }

            try
            {
                using (doc.LockDocument())
                {
                    library ??= TitleBlockLibraryStore.Load();
                    var db = doc.Database;
                    using var tr = db.TransactionManager.StartTransaction();
                    var layerStates = UnlockLayers(tr, db);
                    foreach (var job in group)
                    {
                        if (IsSpdsFormatJob(job))
                        {
                            try
                            {
                                if (TryUpdateSpdsInTransaction(tr, db, job, null, job.DrawingNumber, out var spdsMessage))
                                {
                                    updated++;
                                }
                                else
                                {
                                    reportFailure?.Invoke($"Не удалось записать номер {job.DrawingNumber} в СПДС: {spdsMessage}");
                                }
                            }
                            catch (Exception ex)
                            {
                                reportFailure?.Invoke($"Не удалось записать номер {job.DrawingNumber} в СПДС: {ex.Message}");
                            }

                            continue;
                        }

                        var definition = library.Blocks.FirstOrDefault(x =>
                            string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase));
                        if (definition == null)
                        {
                            reportFailure?.Invoke($"Не удалось записать номер {job.DrawingNumber} в CAD: определение блока не найдено в библиотеке рамок.");
                            continue;
                        }

                        try
                        {
                            if (TryUpdateInTransaction(tr, db, definition, job, null, job.DrawingNumber, out var message))
                            {
                                updated++;
                            }
                            else
                            {
                                reportFailure?.Invoke($"Не удалось записать номер {job.DrawingNumber} в CAD: {message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            reportFailure?.Invoke($"Не удалось записать номер {job.DrawingNumber} в CAD: {ex.Message}");
                        }
                    }


                    RestoreLayerLocks(tr, layerStates);
                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                foreach (var job in group)
                {
                    reportFailure?.Invoke($"Не удалось записать номер {job.DrawingNumber} в CAD: {ex.Message}");
                }
            }
        }

        return updated;
    }

    private static bool TryUpdate(Database db, PlotJob job, string? newTitle, string? newNumber, out string message)
    {
        message = "";

        using var tr = db.TransactionManager.StartTransaction();
        var layerStates = UnlockLayers(tr, db);
        var updated = false;
        if (IsSpdsFormatJob(job))
        {
            updated = TryUpdateSpdsInTransaction(tr, db, job, newTitle, newNumber, out message);
        }
        else
        {
            var library = TitleBlockLibraryStore.Load();
            var definition = library.Blocks.FirstOrDefault(x => string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase));
            if (definition == null)
            {
                message = "Определение блока не найдено в библиотеке рамок.";
            }
            else
            {
                updated = TryUpdateInTransaction(tr, db, definition, job, newTitle, newNumber, out message);
            }
        }

        RestoreLayerLocks(tr, layerStates);
        if (!updated)
        {
            return false;
        }

        tr.Commit();
        return true;
    }

    private static bool IsSpdsFormatJob(PlotJob job)
        => job.FrameSource == FrameSourceKind.Spds
           || string.Equals(job.BlockName, SpdsFormatScanner.SpdsDxfName, StringComparison.OrdinalIgnoreCase);

    private static bool TryUpdateSpdsInTransaction(
        Transaction tr,
        Database db,
        PlotJob job,
        string? newTitle,
        string? newNumber,
        out string message)
    {
        message = "";
        var format = FindSpdsFormat(tr, db, job);
        if (format == null)
        {
            message = "Объект SPDSFORMAT не найден.";
            return false;
        }

        var requested = 0;
        var changed = 0;
        if (newTitle != null)
        {
            requested++;
            if (TrySetSpdsProperty(format, "DrawingName1", newTitle))
            {
                changed++;
            }
        }

        if (newNumber != null)
        {
            requested++;
            if (TrySetSpdsProperty(format, "Sheet", newNumber))
            {
                changed++;
            }
        }

        if (changed != requested)
        {
            message = "Свойства «Имя листа» / «Лист» недоступны для записи через СПДС Object Enabler.";
            return false;
        }

        message = $"В СПДС обновлено свойств: {changed}.";
        return true;
    }

    private static Entity? FindSpdsFormat(Transaction tr, Database db, PlotJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.BlockHandle))
        {
            try
            {
                var handle = new Handle(Convert.ToInt64(job.BlockHandle, 16));
                var id = db.GetObjectId(false, handle, 0);
                if (id.IsValid && !id.IsNull && !id.IsErased
                    && tr.GetObject(id, OpenMode.ForWrite, false) is Entity byHandle
                    && SpdsFormatScanner.IsSpdsFormat(byHandle))
                {
                    return byHandle;
                }
            }
            catch
            {
                // Ниже остаётся поиск по пространству листа для старых заданий без handle.
            }
        }

        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var owner = FindOwnerRecord(tr, blockTable, job.SpaceName);
        if (owner == null)
        {
            return null;
        }

        return owner
            .Cast<ObjectId>()
            .Select(id => tr.GetObject(id, OpenMode.ForWrite, false) as Entity)
            .FirstOrDefault(entity => entity != null && SpdsFormatScanner.IsSpdsFormat(entity));
    }

    private static bool TrySetSpdsProperty(Entity format, string propertyName, string value)
    {
        var type = format.GetType();
        var property = type.GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property != null && property.CanWrite)
        {
            try
            {
                property.SetValue(format, value, null);
                return true;
            }
            catch
            {
                // У некоторых выпусков СПДС свойство публикуется только COM-обёрткой.
            }
        }

        try
        {
            var acadObject = type.GetProperty("AcadObject", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(format, null);
            if (acadObject == null)
            {
                return false;
            }

            acadObject.GetType().InvokeMember(
                propertyName,
                BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                null,
                acadObject,
                new object[] { value });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryUpdateInTransaction(Transaction tr, Database db, TitleBlockDefinition definition, PlotJob job, string? newTitle, string? newNumber, out string message)
    {
        message = "";
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var owner = FindOwnerRecord(tr, blockTable, job.SpaceName);
        if (owner == null)
        {
            message = "Соответствующее пространство не найдено.";
            return false;
        }

        var blockRef = FindBlockReference(tr, db, owner, job, definition);
        if (blockRef == null)
        {
            message = "Соответствующий блок рамки не найден.";
            return false;
        }

        var coordinateMode = GetCoordinateMode(definition);
        var referenceFrame = ResolveReferenceFrame(tr, definition, blockRef, coordinateMode);
        var titleRegion = ResolveLocalRegion(definition.TitleRegion, blockRef.BlockTransform, coordinateMode, referenceFrame);
        var numberRegion = ResolveLocalRegion(definition.DrawingNumberRegion, blockRef.BlockTransform, coordinateMode, referenceFrame);

        var changed = 0;
        if (newTitle != null)
        {
            changed += UpdateRegionText(tr, owner, blockRef, titleRegion, newTitle);
        }

        if (newNumber != null)
        {
            changed += UpdateRegionText(tr, owner, blockRef, numberRegion, newNumber);
        }

        if (changed == 0)
        {
            message = "Не найден редактируемый текст. Если номер или наименование являются постоянным текстом определения блока, замените их атрибутами либо отдельным текстом вне рамки.";
            return false;
        }

        message = $"В CAD обновлено текстовых значений: {changed}.";
        return true;
    }

    private static List<(ObjectId LayerId, bool WasLocked)> UnlockLayers(Transaction tr, Database db)
    {
        var states = new List<(ObjectId LayerId, bool WasLocked)>();
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId layerId in layerTable)
        {
            var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
            states.Add((layerId, layer.IsLocked));
            if (!layer.IsLocked)
            {
                continue;
            }

            layer.UpgradeOpen();
            layer.IsLocked = false;
        }

        return states;
    }

    private static void RestoreLayerLocks(Transaction tr, List<(ObjectId LayerId, bool WasLocked)> states)
    {
        foreach (var state in states)
        {
            if (!state.WasLocked || state.LayerId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(state.LayerId, OpenMode.ForWrite, false) is LayerTableRecord layer)
            {
                layer.IsLocked = true;
            }
        }
    }

    private static BlockReference? FindBlockReferenceByHandle(Transaction tr, Database db, PlotJob job)
    {
        if (string.IsNullOrWhiteSpace(job.BlockHandle))
        {
            return null;
        }

        try
        {
            var handle = new Handle(Convert.ToInt64(job.BlockHandle, 16));
            var id = db.GetObjectId(false, handle, 0);
            if (!id.IsValid || id.IsNull || id.IsErased)
            {
                return null;
            }

            return tr.GetObject(id, OpenMode.ForRead, false) is BlockReference blockRef
                && CadTextExtractor.BlockNameMatches(job.BlockName, blockRef, tr)
                ? blockRef
                : null;
        }
        catch
        {

            return null;
        }
    }

    private static BlockTableRecord? FindOwnerRecord(Transaction tr, BlockTable blockTable, string spaceName)
    {
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout)
            {
                continue;
            }

            var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            if (string.Equals(owner.Name, spaceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout.LayoutName, spaceName, StringComparison.OrdinalIgnoreCase))
            {
                return owner;
            }
        }

        return null;
    }

    private static BlockReference? FindBlockReference(Transaction tr, Database db, BlockTableRecord owner, PlotJob job, TitleBlockDefinition definition)
    {

        var byHandleId = FindBlockReferenceByHandle(tr, db, job);
        if (byHandleId != null)
        {
            return byHandleId;
        }

        var matches = owner
            .Cast<ObjectId>()
            .Select(id => tr.GetObject(id, OpenMode.ForRead, false))
            .OfType<BlockReference>()
            .Where(blockRef => CadTextExtractor.BlockNameMatches(job.BlockName, blockRef, tr))
            .ToList();

        var coordinateMode = GetCoordinateMode(definition);
        var byHandle = matches.FirstOrDefault(blockRef =>
            !string.IsNullOrWhiteSpace(job.BlockHandle)
            && string.Equals(blockRef.Handle.ToString(), job.BlockHandle, StringComparison.OrdinalIgnoreCase));
        var byOriginalText = matches.FirstOrDefault(blockRef =>
        {
            var referenceFrame = ResolveReferenceFrame(tr, definition, blockRef, coordinateMode);
            var titleRegion = ResolveLocalRegion(definition.TitleRegion, blockRef.BlockTransform, coordinateMode, referenceFrame);
            var numberRegion = ResolveLocalRegion(definition.DrawingNumberRegion, blockRef.BlockTransform, coordinateMode, referenceFrame);
            return string.Equals(CadTextExtractor.ExtractRegionText(tr, blockRef, owner, numberRegion), job.CadDrawingNumber, StringComparison.Ordinal)
                && string.Equals(CadTextExtractor.ExtractRegionText(tr, blockRef, owner, titleRegion), job.CadTitle, StringComparison.Ordinal);
        });

        return byHandle ?? byOriginalText ?? (matches.Count == 1 ? matches[0] : null);
    }

    private static int UpdateRegionText(Transaction tr, BlockTableRecord owner, BlockReference blockRef, LocalRectangle region, string value)
    {
        var changed = 0;
        var inverse = blockRef.BlockTransform.Inverse();

        foreach (ObjectId attributeId in blockRef.AttributeCollection)
        {
            if (!attributeId.IsValid || attributeId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(attributeId, OpenMode.ForWrite, false) is AttributeReference attribute)
            {
                var local = attribute.Position.TransformBy(inverse);
                if (IsInRegion(attribute, inverse, region, local))
                {
                    attribute.TextString = value;
                    changed++;
                }
            }
        }

        foreach (ObjectId id in owner)
        {
            if (id == blockRef.ObjectId)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForWrite, false) is not Entity entity)
            {
                continue;
            }

            if (entity is DBText dbText)
            {
                var local = dbText.Position.TransformBy(inverse);
                if (IsInRegion(dbText, inverse, region, local))
                {
                    dbText.TextString = value;
                    changed++;
                }
            }
            else if (entity is MText mText)
            {
                var local = mText.Location.TransformBy(inverse);
                if (IsInRegion(mText, inverse, region, local))
                {
                    mText.Contents = value;
                    changed++;
                }
            }
        }

        return changed;
    }

    private static bool IsInRegion(Entity entity, Matrix3d entityToLocal, LocalRectangle region, Point3d fallbackPoint)
    {
        if (TryGetTransformedExtents(entity, entityToLocal, out var extents))
        {
            return Intersects(region, extents);
        }

        return region.Contains(fallbackPoint.X, fallbackPoint.Y);
    }

    private static bool TryGetTransformedExtents(Entity entity, Matrix3d transform, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        try
        {
            var extents = entity.GeometricExtents;
            var points = new[]
            {
                new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
                new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform),
                new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
                new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform)
            };

            rectangle = LocalRectangle.FromPoints(
                points.Min(p => p.X),
                points.Min(p => p.Y),
                points.Max(p => p.X),
                points.Max(p => p.Y));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RegionCoordinateMode GetCoordinateMode(TitleBlockDefinition definition)
    {
        if (string.Equals(definition.CoordinateMode, "Frame", StringComparison.OrdinalIgnoreCase))
        {
            return RegionCoordinateMode.Frame;
        }

        if (string.Equals(
                definition.CoordinateMode,
                TitleBlockDefinition.DynamicRightBottomCoordinateMode,
                StringComparison.OrdinalIgnoreCase))
        {
            return RegionCoordinateMode.FrameRightBottomDynamic;
        }

        return string.Equals(definition.CoordinateMode, "World", StringComparison.OrdinalIgnoreCase)
            ? RegionCoordinateMode.World
            : RegionCoordinateMode.Local;
    }

    private static LocalRectangle ResolveLocalRegion(LocalRectangle region, Matrix3d blockTransform, RegionCoordinateMode mode, LocalRectangle referenceFrame)
    {
        if (mode == RegionCoordinateMode.Frame)
        {
            return OffsetRegion(region, referenceFrame.MinX, referenceFrame.MinY);
        }

        if (mode == RegionCoordinateMode.FrameRightBottomDynamic)
        {
            return OffsetRegion(region, referenceFrame.MaxX, referenceFrame.MinY);
        }

        if (mode == RegionCoordinateMode.Local)
        {
            return region;
        }

        var inverse = blockTransform.Inverse();
        var points = new[]
        {
            new Point3d(region.MinX, region.MinY, 0).TransformBy(inverse),
            new Point3d(region.MinX, region.MaxY, 0).TransformBy(inverse),
            new Point3d(region.MaxX, region.MinY, 0).TransformBy(inverse),
            new Point3d(region.MaxX, region.MaxY, 0).TransformBy(inverse)
        };

        return LocalRectangle.FromPoints(
            points.Min(p => p.X),
            points.Min(p => p.Y),
            points.Max(p => p.X),
            points.Max(p => p.Y));
    }

    private static LocalRectangle ResolveReferenceFrame(
        Transaction tr,
        TitleBlockDefinition definition,
        BlockReference blockRef,
        RegionCoordinateMode mode)
    {
        if (mode == RegionCoordinateMode.FrameRightBottomDynamic
            && BlockFrameGeometry.TryGetFrame(
                tr,
                blockRef.BlockTableRecord,
                out var liveFrame,
                out _))
        {
            return liveFrame;
        }

        var blockFrame = TransformExtents(blockRef.GeometricExtents, blockRef.BlockTransform.Inverse());
        if (HasArea(definition.PrintRegion))
        {
            if (HasMeaningfulOverlap(definition.PrintRegion, blockFrame))
            {
                return definition.PrintRegion;
            }

            return blockFrame;
        }

        return blockFrame;
    }

    private static bool HasMeaningfulOverlap(LocalRectangle a, LocalRectangle b)
    {
        var overlapWidth = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        var overlapHeight = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        var overlapArea = overlapWidth * overlapHeight;
        if (overlapArea <= 0)
        {
            return false;
        }

        var smallerArea = Math.Min(RectangleArea(a), RectangleArea(b));
        return smallerArea > 0 && overlapArea / smallerArea >= 0.25;
    }

    private static double RectangleArea(LocalRectangle rectangle)
    {
        return Math.Max(0, rectangle.MaxX - rectangle.MinX)
            * Math.Max(0, rectangle.MaxY - rectangle.MinY);
    }

    private static LocalRectangle TransformExtents(Extents3d extents, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform)
        };

        return LocalRectangle.FromPoints(
            points.Min(p => p.X),
            points.Min(p => p.Y),
            points.Max(p => p.X),
            points.Max(p => p.Y));
    }

    private static LocalRectangle OffsetRegion(LocalRectangle region, double offsetX, double offsetY)
    {
        return LocalRectangle.FromPoints(
            region.MinX + offsetX,
            region.MinY + offsetY,
            region.MaxX + offsetX,
            region.MaxY + offsetY);
    }

    private static bool HasArea(LocalRectangle region)
    {
        return Math.Abs(region.MaxX - region.MinX) > 1e-6
            && Math.Abs(region.MaxY - region.MinY) > 1e-6;
    }

    private static bool Intersects(LocalRectangle a, LocalRectangle b)
    {
        return a.MinX <= b.MaxX
            && a.MaxX >= b.MinX
            && a.MinY <= b.MaxY
            && a.MaxY >= b.MinY;
    }

    private static Document? FindTargetDocument(PlotJob job, Document currentDocument)
    {
        if (IsSameDocument(job.SourceFile, currentDocument))
        {
            return currentDocument;
        }

        foreach (Document doc in CadApp.DocumentManager)
        {
            if (IsSameDocument(job.SourceFile, doc))
            {
                return doc;
            }
        }

        return null;
    }

    private static bool IsSameDocument(string sourceFile, Document doc)
    {
        var docFile = doc.Database.Filename;
        if (string.IsNullOrWhiteSpace(sourceFile) || string.IsNullOrWhiteSpace(docFile))
        {
            return string.Equals(sourceFile, doc.Name, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(docFile), StringComparison.OrdinalIgnoreCase);
    }

    private enum RegionCoordinateMode
    {
        Local,
        World,
        Frame,
        FrameRightBottomDynamic
    }
}
