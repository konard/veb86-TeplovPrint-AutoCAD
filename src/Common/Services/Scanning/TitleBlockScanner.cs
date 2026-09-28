using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

public static class TitleBlockScanner
{
    public static List<PlotJob> Scan(Document doc, TitleBlockLibrary library)
    {
        var sourceName = string.IsNullOrWhiteSpace(doc.Database.Filename)
            ? doc.Name
            : doc.Database.Filename;
        return Scan(
            doc.Database,
            library,
            sourceName,
            null,
            TitleBlockScanScope.AllSpaces,
            GetCurrentSpaceName(doc.Database),
            null,
            CadCoordinateSystem.CreateModelContext(doc.Editor, doc.Database.TileMode));
    }

    /// <summary>

    /// </summary>
    public static List<PlotJob> Scan(
        Document doc,
        TitleBlockLibrary library,
        TitleBlockScanScope scope,
        double? paperMatchToleranceMm = null,
        ISet<string>? allowedLayoutNames = null)
    {
        var currentSpaceName = GetCurrentSpaceName(doc.Database);
        var candidateIds = ScanCandidateFilter.CollectInLayouts(
            doc.Database,
            (layout, owner) => ShouldScanLayout(layout, owner, scope, currentSpaceName, allowedLayoutNames),
            ScanCandidateFilter.IsTitleBlockCandidate);
        return Scan(doc, library, candidateIds, paperMatchToleranceMm);
    }

    public static List<PlotJob> Scan(Document doc, TitleBlockLibrary library, Extents3d? scanWindow, double? paperMatchToleranceMm = null)
    {
        var sourceName = string.IsNullOrWhiteSpace(doc.Database.Filename)
            ? doc.Name
            : doc.Database.Filename;
        return Scan(
            doc.Database,
            library,
            sourceName,
            scanWindow,
            TitleBlockScanScope.CurrentSpace,
            GetCurrentSpaceName(doc.Database),
            paperMatchToleranceMm,
            CadCoordinateSystem.CreateModelContext(doc.Editor, doc.Database.TileMode));
    }

    /// <summary>


    /// </summary>
    public static List<PlotJob> Scan(
        Document doc,
        TitleBlockLibrary library,
        CadSelectionWindow scanWindow,
        double? paperMatchToleranceMm = null)
    {
        var sourceName = string.IsNullOrWhiteSpace(doc.Database.Filename)
            ? doc.Name
            : doc.Database.Filename;
        return Scan(
                doc.Database,
                library,
                sourceName,
                null,
                TitleBlockScanScope.CurrentSpace,
                GetCurrentSpaceName(doc.Database),
                paperMatchToleranceMm,
                scanWindow)
            .Where(job => scanWindow.IntersectsWorldPoints(CadSelectionWindow.GetJobWorldCorners(job)))
            .ToList();
    }

    /// <summary>


    /// </summary>
    public static List<PlotJob> Scan(
        Document doc,
        TitleBlockLibrary library,
        IEnumerable<ObjectId>? selectedIds,
        double? paperMatchToleranceMm = null)
    {
        var sourceName = string.IsNullOrWhiteSpace(doc.Database.Filename)
            ? doc.Name
            : doc.Database.Filename;
        return ScanSelected(
            doc.Database,
            library,
            sourceName,
            selectedIds,
            paperMatchToleranceMm,
            CadCoordinateSystem.CreateModelContext(doc.Editor, doc.Database.TileMode));
    }

    public static List<PlotJob> Scan(Database db, TitleBlockLibrary library, string sourceName)
    {
        return Scan(db, library, sourceName, null);
    }

    public static List<PlotJob> Scan(Database db, TitleBlockLibrary library, string sourceName, Extents3d? scanWindow)
    {
        return Scan(db, library, sourceName, scanWindow, TitleBlockScanScope.AllSpaces, null);
    }

    public static List<PlotJob> Scan(
        Database db,
        TitleBlockLibrary library,
        string sourceName,
        Extents3d? scanWindow,
        TitleBlockScanScope scope,
        string? currentSpaceName = null,
        double? paperMatchToleranceMm = null,
        CadSelectionWindow? modelCoordinateContext = null,
        ISet<string>? allowedLayoutNames = null)
    {
        var storedSettings = AppSettingsStore.Load();
        var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = db.Filename;
        }

        TitleBlockScanCaches.Begin();
        try
        {
            return ScanCore(
                db,
                library,
                sourceName,
                scanWindow,
                scope,
                currentSpaceName,
                effectivePaperToleranceMm,
                modelCoordinateContext,
                allowedLayoutNames);
        }
        finally
        {
            TitleBlockScanCaches.End();
        }
    }

    private static List<PlotJob> ScanCore(
        Database db,
        TitleBlockLibrary library,
        string sourceName,
        Extents3d? scanWindow,
        TitleBlockScanScope scope,
        string? currentSpaceName,
        double effectivePaperToleranceMm,
        CadSelectionWindow? modelCoordinateContext,
        ISet<string>? allowedLayoutNames)
    {

        if (scanWindow == null)
        {
            var candidateIds = ScanCandidateFilter.CollectInLayouts(
                db,
                (layout, owner) => ShouldScanLayout(layout, owner, scope, currentSpaceName, allowedLayoutNames),
                ScanCandidateFilter.IsTitleBlockCandidate);
            var storedForSelected = AppSettingsStore.Load();
            return ScanSelectedCore(
                db,
                library,
                sourceName,
                candidateIds,
                effectivePaperToleranceMm,
                modelCoordinateContext,
                storedForSelected);
        }

        var jobs = new List<PlotJob>();
        var warnings = new List<string>();
        var storedSettings = AppSettingsStore.Load();

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        var libraryIndex = new TitleBlockLibraryIndex(library);

        var matchIndex = 0;
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout)
            {
                continue;
            }

            var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            var spaceName = layout.LayoutName;
            if (!ShouldScanLayout(layout, owner, scope, currentSpaceName, allowedLayoutNames))
            {
                continue;
            }

            CadTextExtractor.OwnerTextCache? ownerTextCache = null;
            try
            {
                ownerTextCache = CadTextExtractor.BuildOwnerTextCache(tr, owner, libraryIndex.NameParts);
            }
            catch (Exception ex)
            {
                warnings.Add($"Пространство={spaceName}: не удалось создать кэш текста; рамки будут обработаны по одной: {ex.Message}");
            }

            foreach (ObjectId id in owner)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead, false);
                if (!ScanCandidateFilter.IsTitleBlockCandidate(obj))
                {
                    continue;
                }

                var blockRef = (BlockReference)obj;

                var job = TryCreateJobFromBlockReference(
                    tr,
                    blockRef,
                    layout,
                    owner,
                    libraryIndex,
                    ownerTextCache,
                    scanWindow,
                    modelCoordinateContext,
                    effectivePaperToleranceMm,
                    storedSettings,
                    sourceName,
                    matchIndex,
                    warnings);
                if (job != null)
                {
                    jobs.Add(job);
                    matchIndex++;
                }
            }
        }

        tr.Commit();
        LogScanWarnings(sourceName, warnings);
        return DeduplicateOverlappingJobs(jobs)
            .OrderBy(x => x.DrawingNumber, NaturalStringComparer.Instance)
            .ThenBy(x => Path.GetFileName(x.SourceFile), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>

    /// </summary>
    private static PlotJob? TryCreateJobFromBlockReference(
        Transaction tr,
        BlockReference blockRef,
        Layout layout,
        BlockTableRecord owner,
        TitleBlockLibraryIndex libraryIndex,
        CadTextExtractor.OwnerTextCache? ownerTextCache,
        Extents3d? scanWindow,
        CadSelectionWindow? modelCoordinateContext,
        double effectivePaperToleranceMm,
        AppSettings storedSettings,
        string sourceName,
        int matchIndex,
        List<string> warnings)
    {
        var spaceName = layout.LayoutName;
        string blockName;
        try
        {
            blockName = CadTextExtractor.GetBlockName(blockRef, tr);
        }
        catch (Exception ex)
        {
            warnings.Add($"Пространство={spaceName}; дескриптор={blockRef.Handle}: не удалось прочитать имя блока: {ex.Message}");
            return null;
        }



        if (!libraryIndex.IsPotentialTitleBlock(blockName))
        {
            return null;
        }

        string identityName;
        try
        {
            identityName = CadTextExtractor.GetLibraryIdentityName(blockRef, tr);
        }
        catch (Exception ex)
        {
            warnings.Add($"Пространство={spaceName}; дескриптор={blockRef.Handle}: не удалось прочитать идентификатор блока: {ex.Message}");
            return null;
        }

        var definition = libraryIndex.TryGet(identityName);


        Matrix3d effectiveBlockTransform = blockRef.BlockTransform;
        string effectiveBlockName = identityName;
        ObjectId frameDefinitionId = blockRef.BlockTableRecord;

        Matrix3d nestedToOuter = Matrix3d.Identity;
        bool isNestedMatch = false;
        if (definition == null && libraryIndex.ShouldAttemptNestedMatch(blockName))
        {
            definition = ResolveNestedLibraryMatch(
                tr,
                blockRef,
                blockName,
                libraryIndex,
                out var nestedTransform,
                out var nestedDefinitionId);
            if (definition != null)
            {
                effectiveBlockTransform = nestedTransform * blockRef.BlockTransform;
                effectiveBlockName = definition.BlockName;
                nestedToOuter = nestedTransform;
                frameDefinitionId = nestedDefinitionId;
                isNestedMatch = true;
            }
        }

        if (definition == null && !string.Equals(identityName, blockName, StringComparison.OrdinalIgnoreCase))
        {
            definition = libraryIndex.TryGet(blockName);
            if (definition != null)
            {
                effectiveBlockName = definition.BlockName;
            }
        }
        else if (definition != null)
        {
            effectiveBlockName = definition.BlockName;
        }

        if (definition == null)
        {
            return null;
        }

        Extents3d extents;
        LocalRectangle titleRegion;
        LocalRectangle numberRegion;
        LocalRectangle dateRegion = new();
        LocalRectangle revisionRegion = new();
        LocalRectangle phaseRegion = new();
        LocalRectangle info1Region = new();
        LocalRectangle info2Region = new();
        RegionCoordinateMode coordinateMode;
        LocalRectangle referenceFrame;
        try
        {
            coordinateMode = GetCoordinateMode(definition);
            referenceFrame = ResolveReferenceFrame(
                tr,
                definition,
                blockRef,
                frameDefinitionId,
                effectiveBlockTransform,
                coordinateMode);
            extents = ResolveWorldExtents(definition, blockRef, effectiveBlockTransform, coordinateMode, referenceFrame);
            titleRegion = ResolveLocalRegion(definition.TitleRegion, effectiveBlockTransform, coordinateMode, referenceFrame);
            numberRegion = ResolveLocalRegion(definition.DrawingNumberRegion, effectiveBlockTransform, coordinateMode, referenceFrame);
            dateRegion = definition.DateRegion.HasArea()
                ? ResolveLocalRegion(definition.DateRegion, effectiveBlockTransform, coordinateMode, referenceFrame)
                : new LocalRectangle();
            revisionRegion = definition.RevisionRegion.HasArea()
                ? ResolveLocalRegion(definition.RevisionRegion, effectiveBlockTransform, coordinateMode, referenceFrame)
                : new LocalRectangle();
            phaseRegion = definition.PhaseRegion.HasArea()
                ? ResolveLocalRegion(definition.PhaseRegion, effectiveBlockTransform, coordinateMode, referenceFrame)
                : new LocalRectangle();
            info1Region = definition.Info1Region.HasArea()
                ? ResolveLocalRegion(definition.Info1Region, effectiveBlockTransform, coordinateMode, referenceFrame)
                : new LocalRectangle();
            info2Region = definition.Info2Region.HasArea()
                ? ResolveLocalRegion(definition.Info2Region, effectiveBlockTransform, coordinateMode, referenceFrame)
                : new LocalRectangle();



            if (isNestedMatch)
            {
                titleRegion = TransformLocalRegion(titleRegion, nestedToOuter);
                numberRegion = TransformLocalRegion(numberRegion, nestedToOuter);
                if (dateRegion.HasArea())
                    dateRegion = TransformLocalRegion(dateRegion, nestedToOuter);
                if (revisionRegion.HasArea())
                    revisionRegion = TransformLocalRegion(revisionRegion, nestedToOuter);
                if (phaseRegion.HasArea())
                    phaseRegion = TransformLocalRegion(phaseRegion, nestedToOuter);
                if (info1Region.HasArea())
                    info1Region = TransformLocalRegion(info1Region, nestedToOuter);
                if (info2Region.HasArea())
                    info2Region = TransformLocalRegion(info2Region, nestedToOuter);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Пространство={spaceName}; блок={blockName}; дескриптор={blockRef.Handle}: ошибка координат: {ex.Message}");
            return null;
        }

        if (scanWindow.HasValue && !Intersects(extents, scanWindow.Value))
        {
            return null;
        }






        var wcsCorners = ComputeWcsCorners(coordinateMode, referenceFrame, effectiveBlockTransform);
        var coordinateBounds = layout.ModelType && modelCoordinateContext != null
            ? modelCoordinateContext.TransformWorldPointsToBounds(wcsCorners)
            : null;
        var width = coordinateBounds != null && !modelCoordinateContext!.IsWorldCoordinateSystem
            ? coordinateBounds.MaxX - coordinateBounds.MinX
            : CornerDistance(wcsCorners, 0, 1);
        var height = coordinateBounds != null && !modelCoordinateContext!.IsWorldCoordinateSystem
            ? coordinateBounds.MaxY - coordinateBounds.MinY
            : CornerDistance(wcsCorners, 1, 2);

        var detectionOptions = PaperSizeDetector.CreateTitleBlockBatchOptions(effectivePaperToleranceMm, !layout.ModelType, storedSettings.LongPaperSnapToleranceMm, storedSettings.CustomScales);
        if (IsGenericDynamicPaperName(definition.PaperName))
        {


            detectionOptions.PreferredPaperBaseName = GetGenericDynamicPaperBaseName(definition.PaperName);
            var recordedScale = InferRecordedPaperScale(definition);
            if (recordedScale > 0)
            {
                detectionOptions.PreferredScaleValue = recordedScale;
            }
        }



        if (!IsGenericDynamicPaperName(definition.PaperName)
            && definition.PaperWidthMm > 0
            && definition.PaperHeightMm > 0)
        {
            detectionOptions.PreferredPaperWidthMm = definition.PaperWidthMm;
            detectionOptions.PreferredPaperHeightMm = definition.PaperHeightMm;
        }


        var hasArbitraryScalePaper = PaperSizeDetector.TryDetectTitleBlockAtArbitraryScale(
            width,
            height,
            definition.PaperName,
            definition.PaperWidthMm,
            definition.PaperHeightMm,
            effectivePaperToleranceMm,
            storedSettings.LongPaperSnapToleranceMm,
            out var arbitraryScalePaper);
        var detectedPaper = hasArbitraryScalePaper
            ? arbitraryScalePaper
            : PaperSizeDetector.Detect(width, height, detectionOptions);
        var paper = ApplyFixedPaper(definition, detectedPaper, width, height);
        string title;
        string number;
        string date = "";
        string revision = "";
        string phase = "";
        string info1 = "";
        string info2 = "";
        try
        {
            var blockTextCache = CadTextExtractor.BuildBlockReferenceTextCache(tr, blockRef);
            title = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, titleRegion, ownerTextCache, blockTextCache);
            number = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, numberRegion, ownerTextCache, blockTextCache);
            if (dateRegion.HasArea())
                date = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, dateRegion, ownerTextCache, blockTextCache);
            if (revisionRegion.HasArea())
                revision = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, revisionRegion, ownerTextCache, blockTextCache);
            if (phaseRegion.HasArea())
                phase = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, phaseRegion, ownerTextCache, blockTextCache);
            if (info1Region.HasArea())
                info1 = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, info1Region, ownerTextCache, blockTextCache);
            if (info2Region.HasArea())
                info2 = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, info2Region, ownerTextCache, blockTextCache);
        }
        catch (Exception ex)
        {
            warnings.Add($"Пространство={spaceName}; блок={blockName}; дескриптор={blockRef.Handle}: не удалось извлечь текст: {ex.Message}");
            title = "";
            number = "";
        }


        if (isNestedMatch)
        {
            if (dateRegion.HasArea() && string.IsNullOrWhiteSpace(date))
                warnings.Add($"Пространство={spaceName}; вложенный блок={effectiveBlockName}: область даты задана, но текст не найден");
            if (revisionRegion.HasArea() && string.IsNullOrWhiteSpace(revision))
                warnings.Add($"Пространство={spaceName}; вложенный блок={effectiveBlockName}: область редакции задана, но текст не найден");
            if (phaseRegion.HasArea() && string.IsNullOrWhiteSpace(phase))
                warnings.Add($"Пространство={spaceName}; вложенный блок={effectiveBlockName}: область стадии задана, но текст не найден");
            if (info1Region.HasArea() && string.IsNullOrWhiteSpace(info1))
                warnings.Add($"Пространство={spaceName}; вложенный блок={effectiveBlockName}: область «Информация 1» задана, но текст не найден");
            if (info2Region.HasArea() && string.IsNullOrWhiteSpace(info2))
                warnings.Add($"Пространство={spaceName}; вложенный блок={effectiveBlockName}: область «Информация 2» задана, но текст не найден");
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(number))
        {
            var fallbackProfile = FrameTextRegionService.FindProfile(
                storedSettings,
                FrameSourceKind.Library,
                effectiveBlockName);
            if (fallbackProfile != null)
            {
                try
                {
                    ownerTextCache ??= CadTextExtractor.BuildOwnerTextCache(tr, owner);
                    var frameWorld = LocalRectangle.FromPoints(
                        Enumerable.Range(0, 4).Min(index => wcsCorners[index * 2]),
                        Enumerable.Range(0, 4).Min(index => wcsCorners[index * 2 + 1]),
                        Enumerable.Range(0, 4).Max(index => wcsCorners[index * 2]),
                        Enumerable.Range(0, 4).Max(index => wcsCorners[index * 2 + 1]));

                    if (FrameTextRegionService.TryRead(
                            tr,
                            owner,
                            frameWorld,
                            paper.PaperWidthMm,
                            paper.PaperHeightMm,
                            fallbackProfile,
                            ownerTextCache,
                            out var fallbackNumber,
                            out var fallbackTitle))
                    {
                        if (string.IsNullOrWhiteSpace(number) && !string.IsNullOrWhiteSpace(fallbackNumber))
                            number = fallbackNumber;
                        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(fallbackTitle))
                            title = fallbackTitle;
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Пространство={spaceName}; блок={blockName}; дескриптор={blockRef.Handle}: резервные текстовые области: {ex.Message}");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Наименование не распознано";
        }

        if (string.IsNullOrWhiteSpace(number))
        {
            number = "Номер не распознан";
        }

        var boundaryNote = definition.HasPrintRegion ? "Область печати: задана в библиотеке рамок" : "Область печати: границы блока";
        if (coordinateMode == RegionCoordinateMode.World)
        {
            boundaryNote += "; координаты библиотеки: координаты чертежа";
        }
        else
        {
            boundaryNote += "; координаты библиотеки: локальные координаты блока";
        }


        var job = new PlotJob
        {
            SourceFile = sourceName,
            SpaceName = spaceName,
            IsPaperSpace = !layout.ModelType,
            LayoutTabOrder = layout.TabOrder,
            BlockName = effectiveBlockName,
            BlockHandle = blockRef.Handle.ToString(),
            MatchIndex = matchIndex,
            DrawingNumber = number,
            Title = title,
            Date = date,
            Revision = revision,
            Phase = phase,
            Info1 = info1,
            Info2 = info2,
            CadDrawingNumber = number,
            CadTitle = title,
            CadDate = date,
            CadRevision = revision,
            CadPhase = phase,
            CadInfo1 = info1,
            CadInfo2 = info2,
            PaperName = paper.PaperName,
            ScaleText = paper.ScaleText,
            SizeText = $"{Math.Abs(width):0.##} x {Math.Abs(height):0.##}",
            PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
            DetectionNote = $"{boundaryNote}; {paper.Note}",
            PaperWidthMm = paper.PaperWidthMm,
            PaperHeightMm = paper.PaperHeightMm,
            DetectedRequiresCustomPaperRegistration = paper.RequiresCustomPaper,
            RequiresCustomPaperRegistration = paper.RequiresCustomPaper,
            MinX = extents.MinPoint.X,
            MinY = extents.MinPoint.Y,
            MaxX = extents.MaxPoint.X,
            MaxY = extents.MaxPoint.Y,
            CornerPoints = wcsCorners
        };
        if (layout.ModelType && modelCoordinateContext != null && coordinateBounds != null)
        {
            modelCoordinateContext.ApplyToJob(job, coordinateBounds);
        }


        return job;
    }

    /// <summary>

    /// </summary>
    private static List<PlotJob> ScanSelected(
        Database db,
        TitleBlockLibrary library,
        string sourceName,
        IEnumerable<ObjectId>? selectedIds,
        double? paperMatchToleranceMm,
        CadSelectionWindow? modelCoordinateContext)
    {
        if (selectedIds == null)
        {
            return new List<PlotJob>();
        }

        var idList = new List<ObjectId>();
        foreach (var id in selectedIds)
        {
            if (!id.IsNull)
            {
                idList.Add(id);
            }
        }

        if (idList.Count == 0)
        {
            return new List<PlotJob>();
        }

        var storedSettings = AppSettingsStore.Load();
        var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = db.Filename;
        }

        TitleBlockScanCaches.Begin();
        try
        {
            return ScanSelectedCore(
                db,
                library,
                sourceName,
                idList,
                effectivePaperToleranceMm,
                modelCoordinateContext,
                storedSettings);
        }
        finally
        {
            TitleBlockScanCaches.End();
        }
    }

    private static List<PlotJob> ScanSelectedCore(
        Database db,
        TitleBlockLibrary library,
        string sourceName,
        IReadOnlyList<ObjectId> selectedIds,
        double effectivePaperToleranceMm,
        CadSelectionWindow? modelCoordinateContext,
        AppSettings storedSettings)
    {
        var jobs = new List<PlotJob>();
        var warnings = new List<string>();
        var libraryIndex = new TitleBlockLibraryIndex(library);
        var groups = new Dictionary<ObjectId, List<BlockReference>>();
        var owners = new Dictionary<ObjectId, (BlockTableRecord Owner, Layout Layout)>();

        using var tr = db.TransactionManager.StartTransaction();
        foreach (var id in selectedIds)
        {
            var blockRef = TryOpenBlockReference(tr, id);
            if (blockRef == null)
            {
                continue;
            }

            var ownerId = blockRef.OwnerId;
            if (ownerId.IsNull)
            {
                continue;
            }

            if (!owners.TryGetValue(ownerId, out var ownerInfo))
            {
                BlockTableRecord? owner;
                Layout? layout;
                try
                {
                    owner = tr.GetObject(ownerId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (owner == null || !owner.IsLayout || owner.LayoutId.IsNull)
                    {
                        continue;
                    }

                    layout = tr.GetObject(owner.LayoutId, OpenMode.ForRead, false) as Layout;
                    if (layout == null)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                ownerInfo = (owner, layout);
                owners[ownerId] = ownerInfo;
                groups[ownerId] = new List<BlockReference>();
            }

            groups[ownerId].Add(blockRef);
        }

        var matchIndex = 0;
        foreach (var pair in groups)
        {
            var (owner, layout) = owners[pair.Key];
            var spaceName = layout.LayoutName;
            CadTextExtractor.OwnerTextCache? ownerTextCache = null;
            try
            {
                ownerTextCache = CadTextExtractor.BuildOwnerTextCache(tr, owner, libraryIndex.NameParts);
            }
            catch (Exception ex)
            {
                warnings.Add($"Пространство={spaceName}: не удалось создать кэш текста; рамки будут обработаны по одной: {ex.Message}");
            }

            foreach (var blockRef in pair.Value)
            {
                var job = TryCreateJobFromBlockReference(
                    tr,
                    blockRef,
                    layout,
                    owner,
                    libraryIndex,
                    ownerTextCache,
                    scanWindow: null,
                    modelCoordinateContext,
                    effectivePaperToleranceMm,
                    storedSettings,
                    sourceName,
                    matchIndex,
                    warnings);
                if (job != null)
                {
                    jobs.Add(job);
                    matchIndex++;
                }
            }
        }

        tr.Commit();
        LogScanWarnings(sourceName, warnings);
        return DeduplicateOverlappingJobs(jobs)
            .OrderBy(x => x.DrawingNumber, NaturalStringComparer.Instance)
            .ThenBy(x => Path.GetFileName(x.SourceFile), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>

    /// </summary>
    private static BlockReference? TryOpenBlockReference(Transaction tr, ObjectId id)
    {
        try
        {
            if (id.IsNull)
            {
                return null;
            }

            return tr.GetObject(id, OpenMode.ForRead, false) as BlockReference;
        }
        catch
        {
            return null;
        }
    }

    private static List<PlotJob> DeduplicateOverlappingJobs(List<PlotJob> jobs)
    {
        var result = new List<PlotJob>();
        foreach (var job in jobs.OrderByDescending(ScoreJob))
        {
            var duplicateIndex = result.FindIndex(existing => IsSameScanSpace(existing, job) && GetOverlapRatio(existing, job) >= 0.9);
            if (duplicateIndex < 0)
            {
                result.Add(job);
                continue;
            }

            if (ScoreJob(job) > ScoreJob(result[duplicateIndex]))
            {
                result[duplicateIndex] = job;
            }
        }

        return result;
    }

    private static bool IsSameScanSpace(PlotJob a, PlotJob b)
    {
        return string.Equals(a.SourceFile, b.SourceFile, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.SpaceName, b.SpaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldScanLayout(
        Layout layout,
        BlockTableRecord owner,
        TitleBlockScanScope scope,
        string? currentSpaceName,
        ISet<string>? allowedLayoutNames = null)
    {
        if (allowedLayoutNames != null && allowedLayoutNames.Count > 0)
        {
            var name = layout.LayoutName ?? "";
            if (!allowedLayoutNames.Contains(name))
            {
                return false;
            }
        }

        switch (scope)
        {
            case TitleBlockScanScope.PaperLayouts:
                return !layout.ModelType;
            case TitleBlockScanScope.ModelSpace:
                return layout.ModelType;
            case TitleBlockScanScope.CurrentSpace:
                return IsCurrentSpace(layout, owner, currentSpaceName);
            case TitleBlockScanScope.AllSpaces:
                return true;
            default:
                return false;
        }
    }

    private static bool IsCurrentSpace(Layout layout, BlockTableRecord owner, string? currentSpaceName)
    {
        if (string.IsNullOrWhiteSpace(currentSpaceName))
        {
            return true;
        }

        return string.Equals(layout.LayoutName, currentSpaceName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(owner.Name, currentSpaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCurrentSpaceName(Database db)
    {
        try
        {
            var layoutName = LayoutManager.Current.CurrentLayout;
            if (!string.IsNullOrWhiteSpace(layoutName))
            {
                return layoutName;
            }
        }
        catch
        {
        }

        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var current = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            if (current.IsLayout)
            {
                var layout = (Layout)tr.GetObject(current.LayoutId, OpenMode.ForRead);
                tr.Commit();
                return layout.LayoutName;
            }

            tr.Commit();
        }
        catch
        {
        }

        return null;
    }

    private static int ScoreJob(PlotJob job)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(job.Title) && job.Title.IndexOf("не распознано", StringComparison.OrdinalIgnoreCase) < 0)
        {
            score += 10;
        }

        if (!string.IsNullOrWhiteSpace(job.DrawingNumber) && job.DrawingNumber.IndexOf("не распознан", StringComparison.OrdinalIgnoreCase) < 0)
        {
            score += 10;
        }

        if (Regex.IsMatch(job.DrawingNumber ?? "", @"[A-Za-z].*\d"))
        {
            score += 10;
        }

        var combined = (job.Title ?? "") + " " + (job.DrawingNumber ?? "");
        if (Regex.IsMatch(combined, "\u5BA1\\s*\u5B9A|\u5BA1\\s*\u6838|\u6821\\s*\u5BF9|\u8BBE\\s*\u8BA1|\u9879\u76EE\u8D1F\u8D23\u4EBA|\u4E13\u4E1A\u8D1F\u8D23\u4EBA"))
        {
            score -= 20;
        }

        return score;
    }

    private static double GetOverlapRatio(PlotJob a, PlotJob b)
    {
        var overlapWidth = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        var overlapHeight = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        var overlapArea = overlapWidth * overlapHeight;
        if (overlapArea <= 0)
        {
            return 0;
        }

        var smallerArea = Math.Min(JobArea(a), JobArea(b));
        return smallerArea <= 0 ? 0 : overlapArea / smallerArea;
    }

    private static double JobArea(PlotJob job)
    {
        return Math.Max(0, job.MaxX - job.MinX) * Math.Max(0, job.MaxY - job.MinY);
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

    private static Extents3d ResolveWorldExtents(TitleBlockDefinition definition, BlockReference blockRef, Matrix3d effectiveBlockTransform, RegionCoordinateMode mode, LocalRectangle referenceFrame)
    {
        if (mode == RegionCoordinateMode.World && definition.HasPrintRegion)
        {
            return ToExtents(definition.PrintRegion);
        }

        if (mode == RegionCoordinateMode.Frame || mode == RegionCoordinateMode.FrameRightBottomDynamic)
        {
            return TransformRegion(referenceFrame, effectiveBlockTransform);
        }

        return definition.HasPrintRegion
            ? TransformRegion(definition.PrintRegion, effectiveBlockTransform)
            : blockRef.GeometricExtents;
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
        ObjectId frameDefinitionId,
        Matrix3d effectiveBlockTransform,
        RegionCoordinateMode mode)
    {
        var hasSavedFrame = HasArea(definition.PrintRegion);
        if (mode == RegionCoordinateMode.FrameRightBottomDynamic)
        {


            if (BlockFrameGeometry.TryGetFrame(
                    tr,
                    frameDefinitionId,
                    out var liveFrame,
                    out _))
            {
                return liveFrame;
            }

            if (hasSavedFrame)
            {
                return definition.PrintRegion;
            }
        }

        LocalRectangle blockFrame;
        try
        {


            blockFrame = TransformExtents(blockRef.GeometricExtents, effectiveBlockTransform.Inverse());
        }
        catch
        {
            // AutoCAD can throw eInvalidExtents for valid block references whose
            // graphics extents have not been generated. A manually selected print
            // boundary is already stored in block-local coordinates and is the
            // authoritative frame in this case.
            if (hasSavedFrame)
            {
                return definition.PrintRegion;
            }

            throw;
        }

        if (hasSavedFrame)
        {
            return HasMeaningfulOverlap(definition.PrintRegion, blockFrame)
                ? definition.PrintRegion
                : blockFrame;
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

    private static bool Intersects(Extents3d a, Extents3d b)
    {
        return a.MinPoint.X <= b.MaxPoint.X
            && a.MaxPoint.X >= b.MinPoint.X
            && a.MinPoint.Y <= b.MaxPoint.Y
            && a.MaxPoint.Y >= b.MinPoint.Y;
    }

    private static Extents3d ToExtents(LocalRectangle region)
    {
        return new Extents3d(new Point3d(region.MinX, region.MinY, 0), new Point3d(region.MaxX, region.MaxY, 0));
    }

    private static Extents3d TransformRegion(LocalRectangle region, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(region.MinX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MinX, region.MaxY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MaxY, 0).TransformBy(transform)
        };

        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        return new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
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

    private static LocalRectangle TransformLocalRegion(LocalRectangle region, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(region.MinX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MinX, region.MaxY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MaxY, 0).TransformBy(transform)
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

    private static PaperDetection ApplyFixedPaper(TitleBlockDefinition definition, PaperDetection detected, double frameWidth, double frameHeight)
    {
        if (definition.PaperWidthMm <= 0 || definition.PaperHeightMm <= 0)
        {
            return detected;
        }

        var name = string.IsNullOrWhiteSpace(definition.PaperName)
            ? detected.PaperName
            : definition.PaperName;



        if (IsGenericDynamicPaperName(name))
        {
            return detected;
        }



        if (!IsCustomPaperName(name) && ShouldPreferDetectedLongPaper(name, definition, detected))
        {
            return new PaperDetection
            {
                PaperName = detected.PaperName,
                ScaleText = detected.ScaleText,
                ScaleValue = detected.ScaleValue,
                IsLong = detected.IsLong,
                PaperWidthMm = detected.PaperWidthMm,
                PaperHeightMm = detected.PaperHeightMm,
                RequiresCustomPaper = detected.RequiresCustomPaper,
                Note = $"Границы рамки распознаны как удлинённый формат; выбран автоматически определённый формат. Бумага {name} из библиотеки используется только по умолчанию при добавлении рамки. {detected.Note}"
            };
        }



        if (IsCustomPaperName(name))
        {
            var frameScale = InferFrameScale(frameWidth, frameHeight, definition.PaperWidthMm, definition.PaperHeightMm);
            if (frameScale > 0)
            {
                return new PaperDetection
                {
                    PaperName = name,
                    ScaleText = PaperSizeDetector.ToScaleText(frameScale),
                    ScaleValue = frameScale,
                    IsLong = false,
                    PaperWidthMm = definition.PaperWidthMm,
                    PaperHeightMm = definition.PaperHeightMm,
                    RequiresCustomPaper = true,
                    Note = $"Пользовательская бумага из библиотеки рамок: {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} мм; масштаб по текущим границам рамки: {PaperSizeDetector.ToScaleText(frameScale)}"
                };
            }
        }

        return new PaperDetection
        {
            PaperName = name,
            ScaleText = detected.ScaleText,
            ScaleValue = detected.ScaleValue,
            IsLong = IsLongPaperName(name),
            PaperWidthMm = definition.PaperWidthMm,
            PaperHeightMm = definition.PaperHeightMm,
            Note = $"Фиксированная бумага из библиотеки рамок: {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} мм; масштаб по границам рамки: {detected.ScaleText}"
        };
    }

    private static bool IsCustomPaperName(string paperName)
    {
        return string.Equals(paperName, PaperSizeDetector.CustomPaperName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>


    /// </summary>
    private static double InferFrameScale(double frameWidth, double frameHeight, double paperWidthMm, double paperHeightMm)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || paperWidthMm <= 0 || paperHeightMm <= 0)
        {
            return 0;
        }

        var scale = Math.Min(Math.Abs(frameWidth), Math.Abs(frameHeight))
                    / Math.Min(Math.Abs(paperWidthMm), Math.Abs(paperHeightMm));
        return double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 ? 0 : scale;
    }

    private static bool ShouldPreferDetectedLongPaper(string libraryPaperName, TitleBlockDefinition definition, PaperDetection detected)
    {
        if (!detected.IsLong || detected.PaperWidthMm <= 0 || detected.PaperHeightMm <= 0)
        {
            return false;
        }


        if (detected.RequiresCustomPaper)
        {
            return true;
        }

        if (!IsLongPaperName(libraryPaperName))
        {
            return true;
        }

        var directError = Math.Max(
            Math.Abs(definition.PaperWidthMm - detected.PaperWidthMm),
            Math.Abs(definition.PaperHeightMm - detected.PaperHeightMm));
        var rotatedError = Math.Max(
            Math.Abs(definition.PaperWidthMm - detected.PaperHeightMm),
            Math.Abs(definition.PaperHeightMm - detected.PaperWidthMm));
        return Math.Min(directError, rotatedError) > 10d;
    }

    private static bool IsLongPaperName(string paperName)
    {
        return paperName.IndexOf('+') > 0;
    }

    private static bool IsGenericDynamicPaperName(string paperName)
    {
        return !string.IsNullOrWhiteSpace(paperName)
               && paperName.EndsWith("+", StringComparison.Ordinal);
    }

    private static string GetGenericDynamicPaperBaseName(string paperName)
    {
        return IsGenericDynamicPaperName(paperName)
            ? paperName.Substring(0, paperName.Length - 1)
            : "";
    }

    /// <summary>


    /// </summary>
    private static double InferRecordedPaperScale(TitleBlockDefinition definition)
    {
        if (!definition.PrintRegion.HasArea()
            || definition.PaperWidthMm <= 0
            || definition.PaperHeightMm <= 0)
        {
            return 0;
        }

        var frameWidth = Math.Abs(definition.PrintRegion.MaxX - definition.PrintRegion.MinX);
        var frameHeight = Math.Abs(definition.PrintRegion.MaxY - definition.PrintRegion.MinY);
        var scale = Math.Min(frameWidth, frameHeight)
                    / Math.Min(Math.Abs(definition.PaperWidthMm), Math.Abs(definition.PaperHeightMm));
        return double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 ? 0 : scale;
    }

    private enum RegionCoordinateMode
    {
        Local,
        World,
        Frame,
        FrameRightBottomDynamic
    }

    private static void LogScanWarnings(string sourceName, IReadOnlyCollection<string> warnings)
    {
        if (warnings.Count == 0 || !BatchPlotLogger.IsEnabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
            var path = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "scan_debug.log");
            var lines = new List<string>
            {
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [SCAN_WARN] файл={sourceName} пропущено объектов={warnings.Count}"
            };
            lines.AddRange(warnings.Select(x => "  " + x));
            File.AppendAllLines(path, lines);
        }
        catch
        {
        }
    }

    /// <summary>


    /// </summary>
    private static IEnumerable<string> ExpandLibraryNameParts(string? blockName)
    {
        var fullName = blockName ?? "";
        if (string.IsNullOrWhiteSpace(fullName))
        {
            yield break;
        }

        yield return fullName;
        foreach (var part in fullName.Split('+'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    /// <summary>

    /// </summary>
    private sealed class TitleBlockLibraryIndex
    {
        private readonly Dictionary<string, TitleBlockDefinition> _byName;

        private readonly HashSet<string> _blockNameFilter;
        private readonly HashSet<string> _compositeOuterNames;

        public TitleBlockLibraryIndex(TitleBlockLibrary library)
        {
            _byName = new Dictionary<string, TitleBlockDefinition>(StringComparer.OrdinalIgnoreCase);
            _blockNameFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _compositeOuterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var block in library.Blocks)
            {
                if (string.IsNullOrWhiteSpace(block.BlockName))
                {
                    continue;
                }

                _byName[block.BlockName] = block;
                _blockNameFilter.Add(block.BlockName);

                var plusIndex = block.BlockName.IndexOf('+');
                if (plusIndex > 0)
                {
                    var outerName = block.BlockName.Substring(0, plusIndex).Trim();
                    if (outerName.Length > 0)
                    {
                        _compositeOuterNames.Add(outerName);
                        _blockNameFilter.Add(outerName);
                    }
                }
            }
        }

        public HashSet<string> NameParts => _blockNameFilter;

        /// <summary>



        /// </summary>
        public bool IsPotentialTitleBlock(string blockName)
        {
            if (string.IsNullOrWhiteSpace(blockName))
            {
                return false;
            }

            return _blockNameFilter.Contains(blockName);
        }


        public bool ShouldAttemptNestedMatch(string blockName)
        {
            return !string.IsNullOrWhiteSpace(blockName) && _compositeOuterNames.Contains(blockName);
        }

        public TitleBlockDefinition? TryGet(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return _byName.TryGetValue(name, out var definition) ? definition : null;
        }
    }

    /// <summary>

    /// </summary>
    private static TitleBlockDefinition? ResolveNestedLibraryMatch(
        Transaction tr,
        BlockReference outerRef,
        string outerBlockName,
        TitleBlockLibraryIndex libraryIndex,
        out Matrix3d nestedTransform,
        out ObjectId matchedDefinitionId)
    {
        nestedTransform = Matrix3d.Identity;
        matchedDefinitionId = ObjectId.Null;

        var definitionId = outerRef.BlockTableRecord;
        if (definitionId.IsNull)
        {
            return null;
        }

        var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        return ResolveNestedLibraryMatchRecursive(
            tr,
            definition,
            Matrix3d.Identity,
            outerBlockName,
            libraryIndex,
            out nestedTransform,
            out matchedDefinitionId,
            new HashSet<ObjectId>(),
            0);
    }

    private static TitleBlockDefinition? ResolveNestedLibraryMatchRecursive(
        Transaction tr,
        BlockTableRecord definition,
        Matrix3d accumulatedTransform,
        string outerBlockName,
        TitleBlockLibraryIndex libraryIndex,
        out Matrix3d nestedTransform,
        out ObjectId matchedDefinitionId,
        ISet<ObjectId> visited,
        int depth)
    {
        nestedTransform = Matrix3d.Identity;
        matchedDefinitionId = ObjectId.Null;
        if (depth > 6 || !visited.Add(definition.ObjectId))
        {
            return null;
        }

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

            string nestedName;
            try
            {
                nestedName = CadTextExtractor.GetBlockName(nested, tr);
            }
            catch
            {
                continue;
            }



            var match = depth == 0 && !string.IsNullOrWhiteSpace(outerBlockName)
                ? libraryIndex.TryGet(outerBlockName + "+" + nestedName)
                : null;
            match ??= libraryIndex.TryGet(nestedName);
            if (match != null)
            {
                nestedTransform = nested.BlockTransform * accumulatedTransform;
                matchedDefinitionId = nested.BlockTableRecord;
                return match;
            }


            try
            {
                var nestedDef = (BlockTableRecord)tr.GetObject(nested.BlockTableRecord, OpenMode.ForRead);
                var innerTransform = nested.BlockTransform * accumulatedTransform;
                var deeper = ResolveNestedLibraryMatchRecursive(
                    tr,
                    nestedDef,
                    innerTransform,
                    outerBlockName,
                    libraryIndex,
                    out var deeperTransform,
                    out var deeperDefinitionId,
                    visited,
                    depth + 1);
                if (deeper != null)
                {
                    nestedTransform = deeperTransform;
                    matchedDefinitionId = deeperDefinitionId;
                    return deeper;
                }
            }
            catch
            {
            }
        }

        visited.Remove(definition.ObjectId);
        return null;
    }

    /// <summary>



    /// </summary>

    private static double CornerDistance(double[] corners, int fromIndex, int toIndex)
    {
        var dx = corners[toIndex * 2] - corners[fromIndex * 2];
        var dy = corners[toIndex * 2 + 1] - corners[fromIndex * 2 + 1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double[] ComputeWcsCorners(
        RegionCoordinateMode mode, LocalRectangle frame, Matrix3d blockTransform)
    {

        var xform = mode == RegionCoordinateMode.World ? Matrix3d.Identity : blockTransform;

        var c = new[]
        {
            new Point3d(frame.MinX, frame.MinY, 0).TransformBy(xform),
            new Point3d(frame.MaxX, frame.MinY, 0).TransformBy(xform),
            new Point3d(frame.MaxX, frame.MaxY, 0).TransformBy(xform),
            new Point3d(frame.MinX, frame.MaxY, 0).TransformBy(xform)
        };
        return new[] { c[0].X, c[0].Y, c[1].X, c[1].Y, c[2].X, c[2].Y, c[3].X, c[3].Y };
    }

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
