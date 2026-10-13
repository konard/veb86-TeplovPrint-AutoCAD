using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using HostMgd.EditorInput;
using Teigha.Geometry;
using CadApp = HostMgd.ApplicationServices.Application;
#elif AUTOCAD
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
    internal static bool AddTitleBlockFromLibrary() => AddTitleBlockCore();

    private static bool AddTitleBlockCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            AddBlockLog("No active document.");
            return false;
        }

        var editor = doc.Editor;


        if (!editor.CurrentUserCoordinateSystem.IsEqualTo(Matrix3d.Identity))
        {
            MessageBox.Show("Перед добавлением рамки переключите ПСК на мировую систему координат (МСК).\nВведите в командной строке ПСК и нажмите Enter.",
                "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        AddBlockLog("Add title block command started.");

        try
        {
            var blockOptions = new PromptEntityOptions("\nВыберите блок рамки для добавления в библиотеку: ");
            blockOptions.SetRejectMessage("\nВыберите обычное вхождение блока.");
            blockOptions.AddAllowedClass(typeof(BlockReference), exactMatch: false);

            blockOptions.AllowObjectOnLockedLayer = true;
            var blockResult = editor.GetEntity(blockOptions);
            AddBlockLog("Block prompt status: " + blockResult.Status);
            if (blockResult.Status != PromptStatus.OK)
            {
                return false;
            }

            string blockName;
            Matrix3d blockTransform;
            Matrix3d inverse;
            ObjectId frameDefinitionId;
            bool isPaperSpace;
            bool isStretchableBlock;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockRef = (BlockReference)tr.GetObject(blockResult.ObjectId, OpenMode.ForRead);
                var outerName = CadTextExtractor.GetBlockName(blockRef, tr);
                blockName = CadTextExtractor.GetLibraryIdentityName(blockRef, tr);
                blockTransform = blockRef.BlockTransform;
                frameDefinitionId = blockRef.BlockTableRecord;


                var hasLookupStretch = HasLookupStretchProperty(blockRef);
                isStretchableBlock = HasStretchDistanceProperty(blockRef) || hasLookupStretch;
                var hasVisibilityIdentity = !string.Equals(blockName, outerName, StringComparison.OrdinalIgnoreCase);
                var hasPaperVisibilityStates = HasVisibilityStateProperty(blockRef);


                var hasNestedVisibilityStates = HasHiddenNestedBlockReference(tr, blockRef);



                var owner = (BlockTableRecord)tr.GetObject(blockRef.OwnerId, OpenMode.ForRead);
                isPaperSpace = owner.IsLayout
                    && !owner.LayoutId.IsNull
                    && !((Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead)).ModelType;





                if (TryGetVisibleNestedBlock(
                        tr,
                        blockRef,
                        out var innerName,
                        out var innerTransform,
                        out var innerDefinitionId,
                        out var isInnerStretchable))
                {
                    isStretchableBlock |= isInnerStretchable;
                    if (hasVisibilityIdentity)
                    {
                        AddBlockLog($"Visibility identity detected: outer={outerName}, stored={blockName}");
                    }
                    else if ((hasNestedVisibilityStates || !hasLookupStretch) && hasPaperVisibilityStates)
                    {
                        blockName = outerName + "+" + innerName;
                        blockTransform = innerTransform * blockRef.BlockTransform;
                        frameDefinitionId = innerDefinitionId;
                        AddBlockLog($"Legacy nested identity detected: outer={outerName}, inner={innerName}, stored={blockName}");
                    }
                }
                else if (hasVisibilityIdentity)
                {
                    AddBlockLog($"Visibility identity detected: outer={outerName}, stored={blockName}");
                }
                else if (isStretchableBlock)
                {

                    AddBlockLog($"Stretch-only block detected: block={blockName}, frameDefinition=outer evaluated definition");
                }

                tr.Commit();
            }

            AddBlockLog("Selected block: " + blockName);



            var existingLibrary = TitleBlockLibraryStore.Load();
            if (existingLibrary.Blocks.Any(x =>
                    string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase)))
            {
                var overwrite = MessageBox.Show(
                    $"В библиотеке уже есть рамка с именем {blockName}.\nЗаписать её заново?\nПри выборе «Да» прежние настройки будут заменены.",
                    "Пакетная печать",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (overwrite != MessageBoxResult.Yes)
                {
                    AddBlockLog("Duplicate block name; user chose not to overwrite.");
                    editor.WriteMessage($"\nДобавление отменено; рамка {blockName} в библиотеке не изменена.");
                    return false;
                }
            }

            inverse = blockTransform.Inverse();



            Extents3d printExtents;
            LocalRectangle referenceFrame;
            if (BlockFrameGeometry.TryGetFrame(
                    doc.Database,
                    frameDefinitionId,
                    out referenceFrame,
                    out var frameSource))
            {
                printExtents = TransformRegion(referenceFrame, blockTransform);
                AddBlockLog(frameSource switch
                {
                    BlockFrameSource.ClosedRectangle =>
                        "Outer frame detected from the largest closed rectangle inside block.",
                    BlockFrameSource.FourLineRectangle =>
                        "Outer frame detected from four-line rectangle assembly inside block.",
                    _ =>
                        "No closed/four-line rectangle found; outer frame detected from visible line geometry extents."
                });
            }
            else if (TryGetBlockExtents(doc.Database, blockResult.ObjectId, out var blockExtents))
            {
                printExtents = blockExtents;
                referenceFrame = TransformExtents(blockExtents, inverse);
            }
            else
            {
                AddBlockLog("Block geometric extents are invalid; requiring an explicit print boundary.");
                MessageBox.Show(
                    "AutoCAD не удалось получить допустимые границы блока рамки. Укажите область печати вручную.",
                    "Пакетная печать",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                if (!TryGetRegion(
                        editor,
                        "\nУкажите первый угол внешней границы печати: ",
                        "\nУкажите противоположный угол внешней границы печати: ",
                        inverse,
                        out var manualFrame))
                {
                    AddBlockLog("Required print boundary selection cancelled.");
                    return false;
                }

                referenceFrame = manualFrame;
                printExtents = TransformRegion(manualFrame, blockTransform);
            }


            using var markers = new TransientFrameMarkers(editor);



            markers.SetBox("Внешняя рамка", printExtents.MinPoint, printExtents.MaxPoint, null);
            editor.WriteMessage("\nВнешняя рамка распознана автоматически и временно показана красным. В открывшемся окне укажите области наименования, номера листа и других полей.");



            var placedFrame = RectangleGeometry.TransformRectangle(referenceFrame, blockTransform);
            var detectedWidth = placedFrame.ActualWidth > 0
                ? placedFrame.ActualWidth
                : printExtents.MaxPoint.X - printExtents.MinPoint.X;
            var detectedHeight = placedFrame.ActualHeight > 0
                ? placedFrame.ActualHeight
                : printExtents.MaxPoint.Y - printExtents.MinPoint.Y;
            var settings = AppSettingsStore.Load();
            var paperDetectionOptions = PaperSizeDetector.CreateRectangleBatchOptions(
                settings.PaperMatchToleranceMm,
                isPaperSpace,
                settings.LongPaperSnapToleranceMm,
                settings.CustomScales);

            paperDetectionOptions.IncludeGenericDynamicTitleBlockPaper = isStretchableBlock;

            var paperOptions = ArbitraryPaperPicker.DetectCandidatesOrPrompt(
                detectedWidth,
                detectedHeight,
                paperDetectionOptions);
            var detected = paperOptions[0];

            AddBlockLog($"Detected {paperOptions.Count} paper option(s); preferred: {detected.PaperName}, {detected.PaperWidthMm:0.##} x {detected.PaperHeightMm:0.##}, {detected.ScaleText}");



            LocalRectangle titleRegion;
            LocalRectangle numberRegion;
            LocalRectangle dateRegion;
            LocalRectangle revisionRegion;
            LocalRectangle phaseRegion;
            LocalRectangle info1Region;
            LocalRectangle info2Region;
            string paperName;
            double paperWidthMm;
            double paperHeightMm;
            using (var fieldDialog = new FieldBoxSelectDialog(
                       editor,
                       inverse,
                       blockTransform,
                       markers,
                       referenceFrame,
                       paperOptions,
                       paperDetectionOptions))
            {
                if (ShowModelessDialogAndWait(fieldDialog) != System.Windows.Forms.DialogResult.OK)
                {
                    AddBlockLog("Field selection cancelled.");
                    return false;
                }

                titleRegion = fieldDialog.TitleRegion;
                numberRegion = fieldDialog.DrawingNumberRegion;
                dateRegion = fieldDialog.DateRegion;
                revisionRegion = fieldDialog.RevisionRegion;
                phaseRegion = fieldDialog.PhaseRegion;
                info1Region = fieldDialog.Info1Region;
                info2Region = fieldDialog.Info2Region;


                referenceFrame = fieldDialog.ReferenceFrame;
                printExtents = TransformRegion(referenceFrame, blockTransform);

                paperName = fieldDialog.PaperName;
                paperWidthMm = fieldDialog.PaperWidthMm;
                paperHeightMm = fieldDialog.PaperHeightMm;
            }

            var usesVariableLengthTemplate = isStretchableBlock;

            AddBlockLog($"Title region: ({titleRegion.MinX:0.###},{titleRegion.MinY:0.###})-({titleRegion.MaxX:0.###},{titleRegion.MaxY:0.###})");
            AddBlockLog($"Number region: ({numberRegion.MinX:0.###},{numberRegion.MinY:0.###})-({numberRegion.MaxX:0.###},{numberRegion.MaxY:0.###})");
            if (dateRegion.HasArea()) AddBlockLog($"Date region: ({dateRegion.MinX:0.###},{dateRegion.MinY:0.###})-({dateRegion.MaxX:0.###},{dateRegion.MaxY:0.###})");
            if (revisionRegion.HasArea()) AddBlockLog($"Revision region: ({revisionRegion.MinX:0.###},{revisionRegion.MinY:0.###})-({revisionRegion.MaxX:0.###},{revisionRegion.MaxY:0.###})");
            if (phaseRegion.HasArea()) AddBlockLog($"Phase region: ({phaseRegion.MinX:0.###},{phaseRegion.MinY:0.###})-({phaseRegion.MaxX:0.###},{phaseRegion.MaxY:0.###})");
            if (info1Region.HasArea()) AddBlockLog($"Info1 region: ({info1Region.MinX:0.###},{info1Region.MinY:0.###})-({info1Region.MaxX:0.###},{info1Region.MaxY:0.###})");
            if (info2Region.HasArea()) AddBlockLog($"Info2 region: ({info2Region.MinX:0.###},{info2Region.MinY:0.###})-({info2Region.MaxX:0.###},{info2Region.MaxY:0.###})");


            const bool hasPrintRegion = true;

            var now = DateTime.Now;
            var definition = new TitleBlockDefinition
            {
                BlockName = blockName,
                HasPrintRegion = hasPrintRegion,
                CoordinateMode = usesVariableLengthTemplate
                    ? TitleBlockDefinition.DynamicRightBottomCoordinateMode
                    : "Frame",
                PrintRegion = referenceFrame,
                PaperName = paperName,
                PaperWidthMm = paperWidthMm,
                PaperHeightMm = paperHeightMm,
                TitleRegion = usesVariableLengthTemplate ? ToFrameRightBottomRelative(titleRegion, referenceFrame) : ToFrameRelative(titleRegion, referenceFrame),
                DrawingNumberRegion = usesVariableLengthTemplate ? ToFrameRightBottomRelative(numberRegion, referenceFrame) : ToFrameRelative(numberRegion, referenceFrame),
                DateRegion = dateRegion.HasArea() ? (usesVariableLengthTemplate ? ToFrameRightBottomRelative(dateRegion, referenceFrame) : ToFrameRelative(dateRegion, referenceFrame)) : new LocalRectangle(),
                RevisionRegion = revisionRegion.HasArea() ? (usesVariableLengthTemplate ? ToFrameRightBottomRelative(revisionRegion, referenceFrame) : ToFrameRelative(revisionRegion, referenceFrame)) : new LocalRectangle(),
                PhaseRegion = phaseRegion.HasArea() ? (usesVariableLengthTemplate ? ToFrameRightBottomRelative(phaseRegion, referenceFrame) : ToFrameRelative(phaseRegion, referenceFrame)) : new LocalRectangle(),

                Info1Region = info1Region.HasArea() ? (usesVariableLengthTemplate ? ToFrameRightBottomRelative(info1Region, referenceFrame) : ToFrameRelative(info1Region, referenceFrame)) : new LocalRectangle(),
                Info2Region = info2Region.HasArea() ? (usesVariableLengthTemplate ? ToFrameRightBottomRelative(info2Region, referenceFrame) : ToFrameRelative(info2Region, referenceFrame)) : new LocalRectangle(),
                CreatedAt = now,
                UpdatedAt = now
            };

            var inserted = TitleBlockLibraryStore.Upsert(definition);
            var saved = TitleBlockLibraryStore.Load();
            var savedDefinition = saved.Blocks.FirstOrDefault(x =>
                string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));

            AddBlockLog($"Saved. inserted={inserted}, libraryCount={saved.Blocks.Count}, verifyFound={savedDefinition != null}, path={TitleBlockLibraryStore.DefaultPath}");
            if (savedDefinition == null)
            {
                throw new InvalidOperationException("Не удалось проверить библиотеку после сохранения. Проверьте права доступа к файлу настроек.");
            }

            editor.WriteMessage(inserted
                ? $"\nДобавлена рамка: {blockName}"
                : $"\nОбновлена рамка: {blockName}");
            editor.WriteMessage(usesVariableLengthTemplate
                ? $"\nРастягиваемый базовый формат: {definition.PaperName} (фактическая бумага определяется при сканировании блока)"
                : $"\nФиксированная бумага: {definition.PaperName} {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} мм");
            editor.WriteMessage(hasPrintRegion
                ? "\nОбласть печати рамки сохранена."
                : "\nОбласть печати не сохранена; будут использоваться границы блока.");
            editor.WriteMessage($"\nБиблиотека рамок: {TitleBlockLibraryStore.DefaultPath}");

            var savedPaperText = usesVariableLengthTemplate
                ? $"{definition.PaperName} (фактическая бумага определяется при сканировании)"
                : $"{definition.PaperName} {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} mm";
            MessageBox.Show(
                $"Рамка сохранена: {blockName}\nБумага: {savedPaperText}",
                "Пакетная печать",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return true;
        }
        catch (System.Exception ex)
        {
            AddBlockLog("Failed: " + ex);
            editor.WriteMessage("\nНе удалось добавить рамку: " + ex.Message);
            MessageBox.Show("Не удалось добавить рамку: " + ex.Message, "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>


    /// </summary>
    private static bool TryGetVisibleNestedBlock(
        Transaction tr,
        BlockReference blockRef,
        out string innerBlockName,
        out Matrix3d innerTransform,
        out ObjectId innerDefinitionId,
        out bool isInnerStretchable)
    {
        innerBlockName = "";
        innerTransform = Matrix3d.Identity;
        innerDefinitionId = ObjectId.Null;
        isInnerStretchable = false;


        if (!blockRef.IsDynamicBlock)
        {
            return false;
        }

        var definitionId = blockRef.BlockTableRecord;
        if (definitionId.IsNull)
        {
            return false;
        }

        var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        var nestedBlocks = new List<(string Name, Matrix3d Transform, ObjectId DefinitionId, bool IsStretchable, double FrameArea)>();

        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference nested)
            {
                continue;
            }


            if (!IsEntityVisible(nested))
            {
                continue;
            }

            var nestedName = CadTextExtractor.GetBlockName(nested, tr);
            if (!string.IsNullOrWhiteSpace(nestedName))
            {
                var frameArea = 0d;
                if (BlockFrameGeometry.TryGetFrame(
                        tr,
                        nested.BlockTableRecord,
                        out var nestedFrame,
                        out _))
                {
                    frameArea = RectangleGeometry.GetActualArea(
                        RectangleGeometry.TransformRectangle(nestedFrame, nested.BlockTransform));
                }

                nestedBlocks.Add((
                    nestedName,
                    nested.BlockTransform,
                    nested.BlockTableRecord,
                    HasStretchDistanceProperty(nested) || HasLookupStretchProperty(nested),
                    frameArea));
            }
        }

        if (nestedBlocks.Count == 0)
        {
            return false;
        }



        var selected = nestedBlocks
            .OrderByDescending(block => block.FrameArea)
            .ThenByDescending(block => Math.Abs(block.Transform[0, 0] * block.Transform[1, 1]))
            .First();

        innerBlockName = selected.Name;
        innerTransform = selected.Transform;
        innerDefinitionId = selected.DefinitionId;
        isInnerStretchable = selected.IsStretchable;
        return true;
    }

    /// <summary>



    /// </summary>
    private static bool HasHiddenNestedBlockReference(Transaction tr, BlockReference blockRef)
    {
        try
        {
            if (!blockRef.IsDynamicBlock || blockRef.BlockTableRecord.IsNull)
            {
                return false;
            }

            var definition = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is BlockReference nested
                    && !IsEntityVisible(nested))
                {
                    return true;
                }
            }
        }
        catch
        {

        }

        return false;
    }

    /// <summary>


    /// </summary>
    private static bool HasStretchDistanceProperty(BlockReference blockRef)
    {
        try
        {
            if (!blockRef.IsDynamicBlock)
            {
                return false;
            }

            foreach (DynamicBlockReferenceProperty property in blockRef.DynamicBlockReferencePropertyCollection)
            {
                if (property.ReadOnly)
                {
                    continue;
                }

                if (property.UnitsType == DynamicBlockReferencePropertyUnitsType.Distance)
                {
                    return true;
                }
            }
        }
        catch
        {

        }

        return false;
    }

    /// <summary>





    /// </summary>
    private static bool HasLookupStretchProperty(BlockReference blockRef)
    {
        try
        {
            if (!blockRef.IsDynamicBlock)
            {
                return false;
            }

            foreach (DynamicBlockReferenceProperty property in blockRef.DynamicBlockReferencePropertyCollection)
            {
                if (property.ReadOnly)
                {
                    continue;
                }

                if (property.UnitsType != DynamicBlockReferencePropertyUnitsType.NoUnits
                    || property.Value is not string)
                {
                    continue;
                }

                if (CadTextExtractor.IsVisibilityPropertyName(property.PropertyName))
                {
                    continue;
                }

                var allowedValues = property.GetAllowedValues();
                if (allowedValues != null && allowedValues.Length > 1)
                {
                    return true;
                }
            }
        }
        catch
        {

        }

        return false;
    }

    /// <summary>


    /// </summary>
    private static bool HasVisibilityStateProperty(BlockReference blockRef)
    {
        try
        {
            if (!blockRef.IsDynamicBlock)
            {
                return false;
            }

            foreach (DynamicBlockReferenceProperty property in blockRef.DynamicBlockReferencePropertyCollection)
            {
                if (property.ReadOnly)
                {
                    continue;
                }

                var allowedValues = property.GetAllowedValues();
                if (allowedValues == null || allowedValues.Length <= 1)
                {
                    continue;
                }

                var explicitlyNamedVisibility = CadTextExtractor.IsVisibilityPropertyName(property.PropertyName);
                var stringStateSelector =
                    property.UnitsType == DynamicBlockReferencePropertyUnitsType.NoUnits
                    && property.Value is string;
                if (explicitlyNamedVisibility || stringStateSelector)
                {
                    return true;
                }
            }
        }
        catch
        {

        }

        return false;
    }

    /// <summary>


    /// </summary>
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
