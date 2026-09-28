using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed partial class BatchPlotCommands
{
    /// <summary>


    /// </summary>
    internal static bool EditTitleBlockFromLibrary(string blockName)
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            MessageBox.Show("Нет доступного чертежа CAD для редактирования.", "Библиотека рамок", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var editor = doc.Editor;
        if (!editor.CurrentUserCoordinateSystem.IsEqualTo(Matrix3d.Identity))
        {
            MessageBox.Show(
                "Перед редактированием рамки переключите ПСК на мировую систему координат (МСК).\nВведите в командной строке ПСК и нажмите Enter.",
                "Библиотека рамок",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        try
        {
            var library = TitleBlockLibraryStore.Load();
            var existing = library.Blocks.FirstOrDefault(x =>
                string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                MessageBox.Show("Запись не найдена в библиотеке рамок. Перечитайте библиотеку и повторите попытку.", "Библиотека рамок", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!TryFindEditableReference(doc.Database, existing.BlockName, out var match))
            {
                MessageBox.Show(
                    $"В текущем чертеже не найдена рамка {existing.BlockName}.\nОткройте содержащий её DWG или переключите динамический блок в нужное состояние видимости.",
                    "Библиотека рамок",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }


            if (!string.Equals(LayoutManager.Current.CurrentLayout, match.LayoutName, StringComparison.OrdinalIgnoreCase))
            {
                LayoutManager.Current.CurrentLayout = match.LayoutName;
                System.Windows.Application.Current?.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
            }


            CadWindowFocus.ActivateCadWindow();

            if (!editor.CurrentUserCoordinateSystem.IsEqualTo(Matrix3d.Identity))
            {
                MessageBox.Show(
                    $"Рамка находится в пространстве «{match.LayoutName}», где сейчас используется не МСК.\nПереключитесь на МСК и снова нажмите «Изменить».",
                    "Библиотека рамок",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            var mode = GetEditCoordinateMode(existing);
            var inverse = match.BlockTransform.Inverse();
            var referenceFrame = ResolveEditReferenceFrame(doc.Database, existing, match, mode, inverse);
            var worldFrame = TransformRegion(referenceFrame, match.BlockTransform);
            CenterEditorOnWorldExtents(editor, worldFrame);

            var initialState = new FieldBoxSelectInitialState
            {
                TitleRegion = ResolveEditFieldRegion(existing.TitleRegion, mode, referenceFrame, inverse),
                DrawingNumberRegion = ResolveEditFieldRegion(existing.DrawingNumberRegion, mode, referenceFrame, inverse),
                DateRegion = ResolveEditFieldRegion(existing.DateRegion, mode, referenceFrame, inverse),
                RevisionRegion = ResolveEditFieldRegion(existing.RevisionRegion, mode, referenceFrame, inverse),
                PhaseRegion = ResolveEditFieldRegion(existing.PhaseRegion, mode, referenceFrame, inverse),
                Info1Region = ResolveEditFieldRegion(existing.Info1Region, mode, referenceFrame, inverse),
                Info2Region = ResolveEditFieldRegion(existing.Info2Region, mode, referenceFrame, inverse),
                PaperName = existing.PaperName,
                PaperWidthMm = existing.PaperWidthMm,
                PaperHeightMm = existing.PaperHeightMm
            };

            var placedFrame = RectangleGeometry.TransformRectangle(referenceFrame, match.BlockTransform);
            var detectedWidth = placedFrame.ActualWidth > 0
                ? placedFrame.ActualWidth
                : worldFrame.MaxPoint.X - worldFrame.MinPoint.X;
            var detectedHeight = placedFrame.ActualHeight > 0
                ? placedFrame.ActualHeight
                : worldFrame.MaxPoint.Y - worldFrame.MinPoint.Y;
            var settings = AppSettingsStore.Load();
            var paperDetectionOptions = PaperSizeDetector.CreateRectangleBatchOptions(
                settings.PaperMatchToleranceMm,
                match.IsPaperSpace,
                settings.LongPaperSnapToleranceMm,
                settings.CustomScales);
            paperDetectionOptions.IncludeGenericDynamicTitleBlockPaper =
                mode == EditCoordinateMode.FrameRightBottomDynamic;
            if (mode == EditCoordinateMode.FrameRightBottomDynamic)
            {
                paperDetectionOptions.PreferredPaperBaseName = GetGenericDynamicPaperBaseName(existing.PaperName);
            }

            if (mode != EditCoordinateMode.FrameRightBottomDynamic)
            {
                paperDetectionOptions.PreferredPaperWidthMm = existing.PaperWidthMm;
                paperDetectionOptions.PreferredPaperHeightMm = existing.PaperHeightMm;
            }

            var paperOptions = ArbitraryPaperPicker.DetectCandidatesOrPrompt(
                    detectedWidth,
                    detectedHeight,
                    paperDetectionOptions)
                .ToList();
            EnsureConfiguredPaperOption(paperOptions, existing, detectedWidth, detectedHeight);

            using var markers = new TransientFrameMarkers(editor);
            using var dialog = new FieldBoxSelectDialog(
                editor,
                inverse,
                match.BlockTransform,
                markers,
                referenceFrame,
                paperOptions,
                paperDetectionOptions,
                initialState);

            editor.WriteMessage($"\nРамка {existing.BlockName} найдена. Красные временные области показывают настроенные поля; нажмите «Выбрать» для изменения.");
            CadWindowFocus.ActivateCadWindow();
            if (ShowModelessDialogAndWait(dialog) != System.Windows.Forms.DialogResult.OK)
            {
                return false;
            }

            referenceFrame = dialog.ReferenceFrame;
            var usesVariableLengthTemplate = IsGenericDynamicPaperName(dialog.PaperName);
            var now = DateTime.Now;
            var updated = new TitleBlockDefinition
            {
                BlockName = existing.BlockName,
                HasPrintRegion = true,
                CoordinateMode = usesVariableLengthTemplate
                    ? TitleBlockDefinition.DynamicRightBottomCoordinateMode
                    : "Frame",
                PrintRegion = referenceFrame,
                PaperName = dialog.PaperName,
                PaperWidthMm = dialog.PaperWidthMm,
                PaperHeightMm = dialog.PaperHeightMm,
                TitleRegion = ToStoredFrameRelative(dialog.TitleRegion, referenceFrame, usesVariableLengthTemplate),
                DrawingNumberRegion = ToStoredFrameRelative(dialog.DrawingNumberRegion, referenceFrame, usesVariableLengthTemplate),
                DateRegion = ToOptionalStoredFrameRelative(dialog.DateRegion, referenceFrame, usesVariableLengthTemplate),
                RevisionRegion = ToOptionalStoredFrameRelative(dialog.RevisionRegion, referenceFrame, usesVariableLengthTemplate),
                PhaseRegion = ToOptionalStoredFrameRelative(dialog.PhaseRegion, referenceFrame, usesVariableLengthTemplate),
                Info1Region = ToOptionalStoredFrameRelative(dialog.Info1Region, referenceFrame, usesVariableLengthTemplate),
                Info2Region = ToOptionalStoredFrameRelative(dialog.Info2Region, referenceFrame, usesVariableLengthTemplate),
                CreatedAt = existing.CreatedAt == default ? now : existing.CreatedAt,
                UpdatedAt = now
            };

            TitleBlockLibraryStore.Upsert(updated);
            MessageBox.Show(
                $"Рамка обновлена: {updated.BlockName}\nБумага: {updated.PaperName} {updated.PaperWidthMm:0.##} × {updated.PaperHeightMm:0.##} мм",
                "Библиотека рамок",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            AddBlockLog("Edit title block failed: " + ex);
            editor.WriteMessage("\nНе удалось изменить рамку: " + ex.Message);
            MessageBox.Show("Не удалось изменить рамку: " + ex.Message, "Библиотека рамок", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static LocalRectangle ToOptionalStoredFrameRelative(
        LocalRectangle region,
        LocalRectangle referenceFrame,
        bool useRightBottomAnchor)
    {
        return region.HasArea()
            ? ToStoredFrameRelative(region, referenceFrame, useRightBottomAnchor)
            : new LocalRectangle();
    }

    private static LocalRectangle ToStoredFrameRelative(
        LocalRectangle region,
        LocalRectangle referenceFrame,
        bool useRightBottomAnchor)
    {
        return useRightBottomAnchor
            ? ToFrameRightBottomRelative(region, referenceFrame)
            : ToFrameRelative(region, referenceFrame);
    }

    private static EditCoordinateMode GetEditCoordinateMode(TitleBlockDefinition definition)
    {
        if (string.Equals(definition.CoordinateMode, "Frame", StringComparison.OrdinalIgnoreCase))
        {
            return EditCoordinateMode.Frame;
        }

        if (string.Equals(
                definition.CoordinateMode,
                TitleBlockDefinition.DynamicRightBottomCoordinateMode,
                StringComparison.OrdinalIgnoreCase))
        {
            return EditCoordinateMode.FrameRightBottomDynamic;
        }

        return string.Equals(definition.CoordinateMode, "World", StringComparison.OrdinalIgnoreCase)
            ? EditCoordinateMode.World
            : EditCoordinateMode.Local;
    }

    private static LocalRectangle ResolveEditReferenceFrame(
        Database database,
        TitleBlockDefinition definition,
        EditableTitleBlockReference match,
        EditCoordinateMode mode,
        Matrix3d inverse)
    {
        if (mode == EditCoordinateMode.FrameRightBottomDynamic
            && BlockFrameGeometry.TryGetFrame(
                database,
                match.FrameDefinitionId,
                out var liveFrame,
                out _))
        {
            return liveFrame;
        }

        if (definition.HasPrintRegion && definition.PrintRegion.HasArea())
        {
            return mode == EditCoordinateMode.World
                ? TransformEditRegion(definition.PrintRegion, inverse)
                : definition.PrintRegion;
        }

        if (BlockFrameGeometry.TryGetFrame(
                database,
                match.FrameDefinitionId,
                out var detectedFrame,
                out _))
        {
            return detectedFrame;
        }

        if (TryGetBlockExtents(database, match.BlockReferenceId, out var blockExtents))
        {
            return TransformExtents(blockExtents, inverse);
        }

        throw new InvalidOperationException("Не удалось получить допустимую область печати рамки.");
    }

    private static LocalRectangle ResolveEditFieldRegion(
        LocalRectangle storedRegion,
        EditCoordinateMode mode,
        LocalRectangle referenceFrame,
        Matrix3d inverse)
    {
        if (!storedRegion.HasArea())
        {
            return new LocalRectangle();
        }

        if (mode == EditCoordinateMode.Frame)
        {
            return TitleBlockRegionConverter.FromFrameRelative(storedRegion, referenceFrame);
        }

        if (mode == EditCoordinateMode.FrameRightBottomDynamic)
        {
            return TitleBlockRegionConverter.FromFrameRightBottomRelative(storedRegion, referenceFrame);
        }

        return mode == EditCoordinateMode.World
            ? TransformEditRegion(storedRegion, inverse)
            : storedRegion;
    }

    private static LocalRectangle TransformEditRegion(LocalRectangle region, Matrix3d transform)
    {
        var extents = new Extents3d(
            new Point3d(region.MinX, region.MinY, 0),
            new Point3d(region.MaxX, region.MaxY, 0));
        return TransformExtents(extents, transform);
    }

    private static void EnsureConfiguredPaperOption(
        IList<PaperDetection> options,
        TitleBlockDefinition definition,
        double drawingWidth,
        double drawingHeight)
    {
        if (string.IsNullOrWhiteSpace(definition.PaperName)
            || definition.PaperWidthMm <= 0
            || definition.PaperHeightMm <= 0)
        {
            return;
        }

        var exists = options.Any(x =>
            string.Equals(x.PaperName, definition.PaperName, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(x.PaperWidthMm - definition.PaperWidthMm) <= 0.01d
            && Math.Abs(x.PaperHeightMm - definition.PaperHeightMm) <= 0.01d);
        if (exists)
        {
            return;
        }

        var scaleX = drawingWidth / definition.PaperWidthMm;
        var scaleY = drawingHeight / definition.PaperHeightMm;
        var scale = scaleX > 0 && scaleY > 0 ? (scaleX + scaleY) / 2d : Math.Max(scaleX, scaleY);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1d;
        }


        options.Insert(0, new PaperDetection
        {
            PaperName = definition.PaperName,
            PaperWidthMm = definition.PaperWidthMm,
            PaperHeightMm = definition.PaperHeightMm,
            ScaleValue = scale,
            ScaleText = FormatEditScale(scale),
            IsLong = definition.PaperName.IndexOf('+') > 0,
            RequiresCustomPaper = string.Equals(definition.PaperName, PaperSizeDetector.CustomPaperName, StringComparison.OrdinalIgnoreCase),
            Note = "Исходная настройка из библиотеки рамок"
        });
    }

    private static string FormatEditScale(double scale)
    {
        return scale >= 1d
            ? "1:" + scale.ToString("0.###", CultureInfo.InvariantCulture)
            : (1d / scale).ToString("0.###", CultureInfo.InvariantCulture) + ":1";
    }

    private static void CenterEditorOnWorldExtents(Editor editor, Extents3d worldExtents)
    {
        var dcsExtents = TransformWorldExtents(worldExtents, BuildWcsToDcsMatrix(editor));
        var width = Math.Max(dcsExtents.MaxPoint.X - dcsExtents.MinPoint.X, 1d) * 1.15d;
        var height = Math.Max(dcsExtents.MaxPoint.Y - dcsExtents.MinPoint.Y, 1d) * 1.15d;

        using var view = editor.GetCurrentView();
        var aspect = view.Height > 1e-9 && view.Width > 1e-9 ? view.Width / view.Height : 1.6d;
        if (width / height > aspect)
        {
            height = width / aspect;
        }
        else
        {
            width = height * aspect;
        }

        view.CenterPoint = new Point2d(
            (dcsExtents.MinPoint.X + dcsExtents.MaxPoint.X) / 2d,
            (dcsExtents.MinPoint.Y + dcsExtents.MaxPoint.Y) / 2d);
        view.Width = width;
        view.Height = height;
        editor.SetCurrentView(view);
        editor.Regen();
        editor.UpdateScreen();
    }

    private static bool TryFindEditableReference(
        Database database,
        string storedBlockName,
        out EditableTitleBlockReference match)
    {
        match = null!;
        using var tr = database.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
        var currentSpaceId = database.CurrentSpaceId;
        var owners = blockTable
            .Cast<ObjectId>()
            .Select(id => (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead))
            .Where(owner => owner.IsLayout)
            .OrderBy(owner => owner.ObjectId.Equals(currentSpaceId) ? 0 : 1)
            .ToList();

        foreach (var owner in owners)
        {
            var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            foreach (ObjectId id in owner)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef
                    || !IsEntityVisible(blockRef))
                {
                    continue;
                }

                var outerName = CadTextExtractor.GetBlockName(blockRef, tr);
                var identityName = CadTextExtractor.GetLibraryIdentityName(blockRef, tr);
                Matrix3d nestedTransform;
                ObjectId frameDefinitionId;
                if (string.Equals(storedBlockName, identityName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(storedBlockName, outerName, StringComparison.OrdinalIgnoreCase))
                {
                    nestedTransform = Matrix3d.Identity;
                    frameDefinitionId = blockRef.BlockTableRecord;
                }
                else if (!TryFindNestedDefinition(
                             tr,
                             blockRef.BlockTableRecord,
                             outerName,
                             storedBlockName,
                             Matrix3d.Identity,
                             new HashSet<ObjectId>(),
                             0,
                             out nestedTransform,
                             out frameDefinitionId))
                {
                    continue;
                }

                match = new EditableTitleBlockReference
                {
                    BlockReferenceId = blockRef.ObjectId,
                    BlockTransform = nestedTransform * blockRef.BlockTransform,
                    FrameDefinitionId = frameDefinitionId,
                    LayoutName = layout.LayoutName,
                    IsPaperSpace = !layout.ModelType
                };
                return true;
            }
        }

        return false;
    }

    private static bool TryFindNestedDefinition(
        Transaction tr,
        ObjectId definitionId,
        string outerName,
        string storedBlockName,
        Matrix3d accumulatedTransform,
        ISet<ObjectId> visited,
        int depth,
        out Matrix3d nestedTransform,
        out ObjectId matchedDefinitionId)
    {
        nestedTransform = Matrix3d.Identity;
        matchedDefinitionId = ObjectId.Null;
        if (depth > 6 || definitionId.IsNull || !visited.Add(definitionId))
        {
            return false;
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference nested
                    || !IsEntityVisible(nested))
                {
                    continue;
                }

                var nestedName = CadTextExtractor.GetBlockName(nested, tr);
                var compoundMatch = depth == 0
                    && string.Equals(outerName + "+" + nestedName, storedBlockName, StringComparison.OrdinalIgnoreCase);
                var legacyInnerMatch = string.Equals(nestedName, storedBlockName, StringComparison.OrdinalIgnoreCase);
                var currentTransform = nested.BlockTransform * accumulatedTransform;
                if (compoundMatch || legacyInnerMatch)
                {
                    nestedTransform = currentTransform;
                    matchedDefinitionId = nested.BlockTableRecord;
                    return true;
                }

                if (TryFindNestedDefinition(
                        tr,
                        nested.BlockTableRecord,
                        outerName,
                        storedBlockName,
                        currentTransform,
                        visited,
                        depth + 1,
                        out nestedTransform,
                        out matchedDefinitionId))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            visited.Remove(definitionId);
        }
    }

    private sealed class EditableTitleBlockReference
    {
        public ObjectId BlockReferenceId { get; set; }
        public Matrix3d BlockTransform { get; set; } = Matrix3d.Identity;
        public ObjectId FrameDefinitionId { get; set; }
        public string LayoutName { get; set; } = "";
        public bool IsPaperSpace { get; set; }
    }

    private enum EditCoordinateMode
    {
        Local,
        World,
        Frame,
        FrameRightBottomDynamic
    }
}
