using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;
#elif AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>



///




///



///

/// </summary>
public static class RectangleFrameScanner
{

    private const string TemporaryOverlayLayer = "ZBP_TEMP_SEQUENCE_OVERLAY";



    private static readonly Dictionary<ObjectId, bool> LayerScannableCache = new();



    private static readonly Dictionary<ObjectId, List<LocalRectangle>> BlockDefinitionCache = new();


    public sealed class Result
    {
        public PlotJob Job { get; set; } = new();
        public IReadOnlyList<PaperDetection> PaperOptions { get; set; } = new PaperDetection[0];


        public double[]? CornerPoints { get; set; }
    }


    public sealed class ScanProfile
    {
        public long TotalMs { get; set; }
        public long CollectSpacesMs { get; set; }
        public long CollectEntitiesMs { get; set; }
        public long FourLineMatchMs { get; set; }
        public long FilterPackageMs { get; set; }
        public long PaperMatchMs { get; set; }
        public long DedupMs { get; set; }
        public long EmptyFilterMs { get; set; }
        public long EmptyBoxCollectMs { get; set; }
        public long AttributeFillMs { get; set; }
        public int LayoutCount { get; set; }
        public int TopLevelEntityVisits { get; set; }
        public int LineSegmentCount { get; set; }
        public int ClosedPolylineRectCount { get; set; }
        public int FourLineRectCount { get; set; }
        public int AfterPaperMatchCount { get; set; }
        public int AfterDedupCount { get; set; }
        public int AfterEmptyFilterCount { get; set; }
        public int ResultCount { get; set; }
        public bool RecognizeFourLines { get; set; }

        public string FormatReport()
        {
            return string.Join(Environment.NewLine, new[]
            {
                $"TotalMs={TotalMs}",
                $"CollectSpacesMs={CollectSpacesMs} (entities={CollectEntitiesMs}, fourLine={FourLineMatchMs})",
                $"FilterPackageMs={FilterPackageMs} (paper={PaperMatchMs}, dedup={DedupMs}, empty={EmptyFilterMs}[boxCollect={EmptyBoxCollectMs}], attrs={AttributeFillMs})",
                $"Layouts={LayoutCount} FourLines={RecognizeFourLines}",
                $"TopLevelEntityVisits={TopLevelEntityVisits} LineSegments={LineSegmentCount}",
                $"Rects: closedPL={ClosedPolylineRectCount} fourLine={FourLineRectCount} afterPaper={AfterPaperMatchCount} afterDedup={AfterDedupCount} afterEmpty={AfterEmptyFilterCount} results={ResultCount}"
            });
        }
    }


    public static bool EnableProfiling { get; set; }


    public static ScanProfile? LastProfile { get; private set; }

    [ThreadStatic]
    private static ScanProfile? _activeProfile;

    [ThreadStatic]
    private static IProgress<RectangleScanProgress>? _activeProgress;

    [ThreadStatic]
    private static CancellationToken _activeCancel;

    private static void ReportScan(string detail, int current = 0, int total = 0, string? title = null)
    {
        _activeCancel.ThrowIfCancellationRequested();
        _activeProgress?.Report(new RectangleScanProgress
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Распознавание рамок…" : title!,
            Detail = detail,
            Current = current,
            Total = total
        });
    }

    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════

    /// <summary>

    /// </summary>


    public static List<Result> ScanWindow(
        Document document,
        Extents3d scanWindow,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null)
    {
        return ScanWindow(
            document,
            new CadSelectionWindow
            {
                Bounds = LocalRectangle.FromPoints(
                    scanWindow.MinPoint.X,
                    scanWindow.MinPoint.Y,
                    scanWindow.MaxPoint.X,
                    scanWindow.MaxPoint.Y),
                UcsToWorld = Matrix3d.Identity,
                WorldToUcs = Matrix3d.Identity
            },
            paperMatchToleranceMm,
            recognizeFourLineRectangles);
    }

    /// <summary>

    /// </summary>
    public static List<Result> ScanWindow(
        Document document,
        CadSelectionWindow scanWindow,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null,
        IProgress<RectangleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var previousProgress = _activeProgress;
        var previousCancel = _activeCancel;
        _activeProgress = progress;
        _activeCancel = cancellationToken;
        try
        {
            LayerScannableCache.Clear();
            BlockDefinitionCache.Clear();
            var storedSettings = AppSettingsStore.Load();
            var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
            var recognizeFourLines =
                recognizeFourLineRectangles ?? storedSettings.RecognizeFourLineRectangleFrames;
            var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename)
                ? document.Name
                : document.Database.Filename;

            ReportScan("Чтение текущего пространства…");
            using var tr = document.Database.TransactionManager.StartTransaction();
            BlockTableRecord owner;
            Layout layout;
            if (document.Database.TileMode)
            {
                var blockTable = (BlockTable)tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                owner = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                if (!owner.IsLayout || owner.LayoutId.IsNull)
                {
                    return new List<Result>();
                }

                layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            }
            else
            {
                var currentLayoutName = LayoutManager.Current.CurrentLayout;
                var layouts = (DBDictionary)tr.GetObject(document.Database.LayoutDictionaryId, OpenMode.ForRead);
                if (!layouts.Contains(currentLayoutName))
                {
                    return new List<Result>();
                }

                layout = (Layout)tr.GetObject(layouts.GetAt(currentLayoutName), OpenMode.ForRead);
                owner = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                if (!owner.IsLayout || owner.LayoutId.IsNull)
                {
                    return new List<Result>();
                }
            }

            var rectangles = CollectRectanglesFromSpace(tr, owner, recognizeFourLines, layout.LayoutName);
            var ownerId = owner.ObjectId;
            var layoutName = layout.LayoutName;
            var layoutTabOrder = layout.TabOrder;
            var isPaperSpace = !layout.ModelType;
            tr.Commit();

            return FilterAndPackageRectangles(
                document.Database,
                rectangles,
                scanWindow,
                ownerId,
                sourceFile,
                layoutName,
                isPaperSpace,
                layoutTabOrder,
                effectivePaperToleranceMm,
                storedSettings.LongPaperSnapToleranceMm,
                storedSettings.CustomScales);
        }
        finally
        {
            _activeProgress = previousProgress;
            _activeCancel = previousCancel;
        }
    }

    /// <summary>

    ///


    /// </summary>


    public static List<Result> ScanScope(
        Document document,
        TitleBlockScanScope scope,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null,
        IProgress<RectangleScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ISet<string>? allowedLayoutNames = null)
    {
        var previousProgress = _activeProgress;
        var previousCancel = _activeCancel;
        _activeProgress = progress;
        _activeCancel = cancellationToken;

        var profile = EnableProfiling ? new ScanProfile() : null;
        _activeProfile = profile;
        var totalSw = profile != null ? Stopwatch.StartNew() : null;

        try
        {
            LayerScannableCache.Clear();
            BlockDefinitionCache.Clear();
            var storedSettings = AppSettingsStore.Load();
            var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
            var recognizeFourLines =
                recognizeFourLineRectangles ?? storedSettings.RecognizeFourLineRectangleFrames;
            if (profile != null)
            {
                profile.RecognizeFourLines = recognizeFourLines;
            }

            var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename)
                ? document.Name
                : document.Database.Filename;
            var currentSpaceName = GetCurrentSpaceName(document.Database);

            ReportScan("Чтение списка листов…");


            var spaceData = new List<(List<LocalRectangle> Rectangles, ObjectId OwnerId, string LayoutName, bool IsPaperSpace, int TabOrder)>();
            var collectSpacesSw = profile != null ? Stopwatch.StartNew() : null;
            using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                var pendingLayouts = new List<(BlockTableRecord Owner, Layout Layout)>();
                foreach (ObjectId recordId in blockTable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                    if (!owner.IsLayout || owner.LayoutId.IsNull)
                    {
                        continue;
                    }

                    var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
                    if (!ShouldScanLayout(layout, scope, currentSpaceName, allowedLayoutNames))
                    {
                        continue;
                    }

                    pendingLayouts.Add((owner, layout));
                }

                for (var layoutIndex = 0; layoutIndex < pendingLayouts.Count; layoutIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (owner, layout) = pendingLayouts[layoutIndex];
                    if (profile != null)
                    {
                        profile.LayoutCount++;
                    }

                    ReportScan(
                        $"Поиск прямоугольников ({layout.LayoutName})…",
                        layoutIndex + 1,
                        pendingLayouts.Count);
                    var rectangles = CollectRectanglesFromSpace(
                        tr,
                        owner,
                        recognizeFourLines,
                        layout.LayoutName);
                    spaceData.Add((rectangles, owner.ObjectId, layout.LayoutName, !layout.ModelType, layout.TabOrder));
                }

                tr.Commit();
            }

            if (collectSpacesSw != null && profile != null)
            {
                collectSpacesSw.Stop();
                profile.CollectSpacesMs = collectSpacesSw.ElapsedMilliseconds;
            }


            spaceData.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));


            var filterSw = profile != null ? Stopwatch.StartNew() : null;
            var allResults = new List<Result>();
            for (var i = 0; i < spaceData.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (rectangles, ownerId, layoutName, isPaperSpace, tabOrder) = spaceData[i];
                ReportScan(
                    $"Проверка бумаги и пустых рамок ({layoutName})…",
                    i + 1,
                    spaceData.Count);
                var results = FilterAndPackageRectangles(
                    document.Database,
                    rectangles,
                    isPaperSpace
                        ? null
                        : CadCoordinateSystem.CreateModelContext(document.Editor, true),
                    ownerId,
                    sourceFile,
                    layoutName,
                    isPaperSpace,
                    tabOrder,
                    effectivePaperToleranceMm,
                    storedSettings.LongPaperSnapToleranceMm,
                    storedSettings.CustomScales);
                allResults.AddRange(results);
            }

            if (filterSw != null && profile != null)
            {
                filterSw.Stop();
                profile.FilterPackageMs = filterSw.ElapsedMilliseconds;
                profile.ResultCount = allResults.Count;
            }

            if (totalSw != null && profile != null)
            {
                totalSw.Stop();
                profile.TotalMs = totalSw.ElapsedMilliseconds;
                LastProfile = profile;
            }

            ReportScan($"Распознавание завершено; рамок: {allResults.Count}", allResults.Count, Math.Max(1, allResults.Count));
            return allResults;
        }
        finally
        {
            _activeProfile = null;
            _activeProgress = previousProgress;
            _activeCancel = previousCancel;
        }
    }

    /// <summary>


    /// </summary>
    public static List<Result> ScanSelection(
        Document document,
        IEnumerable<ObjectId>? selectedIds,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null,
        IProgress<RectangleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var previousProgress = _activeProgress;
        var previousCancel = _activeCancel;
        _activeProgress = progress;
        _activeCancel = cancellationToken;
        try
        {
            if (selectedIds == null)
            {
                return new List<Result>();
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
                return new List<Result>();
            }

            LayerScannableCache.Clear();
            BlockDefinitionCache.Clear();
            var storedSettings = AppSettingsStore.Load();
            var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
            var recognizeFourLines =
                recognizeFourLineRectangles ?? storedSettings.RecognizeFourLineRectangleFrames;
            var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename)
                ? document.Name
                : document.Database.Filename;

            ReportScan("Чтение выбранных объектов…");

            var spaceData = new List<(List<LocalRectangle> Rectangles, ObjectId OwnerId, string LayoutName, bool IsPaperSpace, int TabOrder)>();
            using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                var groups = new Dictionary<ObjectId, List<ObjectId>>();
                var owners = new Dictionary<ObjectId, (BlockTableRecord Owner, Layout Layout)>();
                foreach (var id in idList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Entity? entity;
                    try
                    {
                        entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch
                    {
                        continue;
                    }

                    if (entity == null)
                    {
                        continue;
                    }

                    var ownerId = entity.OwnerId;
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
                        groups[ownerId] = new List<ObjectId>();
                    }

                    groups[ownerId].Add(id);
                }

                foreach (var pair in groups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (owner, layout) = owners[pair.Key];
                    ReportScan($"Поиск прямоугольников ({layout.LayoutName})…");
                    var rectangles = CollectRectanglesFromEntities(
                        tr,
                        pair.Value,
                        recognizeFourLines,
                        layout.LayoutName);
                    spaceData.Add((rectangles, owner.ObjectId, layout.LayoutName, !layout.ModelType, layout.TabOrder));
                }

                tr.Commit();
            }

            spaceData.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));

            var allResults = new List<Result>();
            for (var i = 0; i < spaceData.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (rectangles, ownerId, layoutName, isPaperSpace, tabOrder) = spaceData[i];
                ReportScan(
                    $"Проверка бумаги и пустых рамок ({layoutName})…",
                    i + 1,
                    spaceData.Count);
                var results = FilterAndPackageRectangles(
                    document.Database,
                    rectangles,
                    isPaperSpace
                        ? null
                        : CadCoordinateSystem.CreateModelContext(document.Editor, true),
                    ownerId,
                    sourceFile,
                    layoutName,
                    isPaperSpace,
                    tabOrder,
                    effectivePaperToleranceMm,
                    storedSettings.LongPaperSnapToleranceMm,
                    storedSettings.CustomScales);
                allResults.AddRange(results);
            }

            ReportScan($"Распознавание завершено; рамок: {allResults.Count}", allResults.Count, Math.Max(1, allResults.Count));
            return allResults;
        }
        finally
        {
            _activeProgress = previousProgress;
            _activeCancel = previousCancel;
        }
    }

    /// <summary>


    /// </summary>
    public static List<Result> ScanDatabase(
        Database db,
        string sourceFile,
        TitleBlockScanScope scope,
        ISet<string>? allowedLayoutNames = null,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null,
        IProgress<RectangleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var previousProgress = _activeProgress;
        var previousCancel = _activeCancel;
        _activeProgress = progress;
        _activeCancel = cancellationToken;

        var profile = EnableProfiling ? new ScanProfile() : null;
        _activeProfile = profile;
        var totalSw = profile != null ? Stopwatch.StartNew() : null;

        try
        {
            LayerScannableCache.Clear();
            BlockDefinitionCache.Clear();
            var storedSettings = AppSettingsStore.Load();
            var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
            var recognizeFourLines =
                recognizeFourLineRectangles ?? storedSettings.RecognizeFourLineRectangleFrames;
            if (profile != null)
            {
                profile.RecognizeFourLines = recognizeFourLines;
            }

            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                sourceFile = db.Filename ?? "";
            }

            ReportScan("Чтение списка листов…");

            var spaceData = new List<(List<LocalRectangle> Rectangles, ObjectId OwnerId, string LayoutName, bool IsPaperSpace, int TabOrder)>();
            var collectSpacesSw = profile != null ? Stopwatch.StartNew() : null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var pendingLayouts = new List<(BlockTableRecord Owner, Layout Layout)>();
                foreach (ObjectId recordId in blockTable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                    if (!owner.IsLayout || owner.LayoutId.IsNull)
                    {
                        continue;
                    }

                    var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);

                    var effectiveScope = scope == TitleBlockScanScope.CurrentSpace
                        ? TitleBlockScanScope.AllSpaces
                        : scope;
                    if (!ShouldScanLayout(layout, effectiveScope, currentSpaceName: null, allowedLayoutNames))
                    {
                        continue;
                    }

                    pendingLayouts.Add((owner, layout));
                }

                for (var layoutIndex = 0; layoutIndex < pendingLayouts.Count; layoutIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (owner, layout) = pendingLayouts[layoutIndex];
                    if (profile != null)
                    {
                        profile.LayoutCount++;
                    }

                    ReportScan(
                        $"Поиск прямоугольников ({layout.LayoutName})…",
                        layoutIndex + 1,
                        pendingLayouts.Count);
                    var rectangles = CollectRectanglesFromSpace(
                        tr,
                        owner,
                        recognizeFourLines,
                        layout.LayoutName);
                    spaceData.Add((rectangles, owner.ObjectId, layout.LayoutName, !layout.ModelType, layout.TabOrder));
                }

                tr.Commit();
            }

            if (collectSpacesSw != null && profile != null)
            {
                collectSpacesSw.Stop();
                profile.CollectSpacesMs = collectSpacesSw.ElapsedMilliseconds;
            }

            spaceData.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));

            var filterSw = profile != null ? Stopwatch.StartNew() : null;
            var allResults = new List<Result>();
            for (var i = 0; i < spaceData.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (rectangles, ownerId, layoutName, isPaperSpace, tabOrder) = spaceData[i];
                ReportScan(
                    $"Проверка бумаги и пустых рамок ({layoutName})…",
                    i + 1,
                    spaceData.Count);

                var results = FilterAndPackageRectangles(
                    db,
                    rectangles,
                    coordinateContext: null,
                    ownerId,
                    sourceFile,
                    layoutName,
                    isPaperSpace,
                    tabOrder,
                    effectivePaperToleranceMm,
                    storedSettings.LongPaperSnapToleranceMm,
                    storedSettings.CustomScales);
                foreach (var result in results)
                {
                    result.Job.IsDcsWindow = false;
                }

                allResults.AddRange(results);
            }

            if (filterSw != null && profile != null)
            {
                filterSw.Stop();
                profile.FilterPackageMs = filterSw.ElapsedMilliseconds;
                profile.ResultCount = allResults.Count;
            }

            if (totalSw != null && profile != null)
            {
                totalSw.Stop();
                profile.TotalMs = totalSw.ElapsedMilliseconds;
                LastProfile = profile;
            }

            ReportScan($"Распознавание завершено; рамок: {allResults.Count}", allResults.Count, Math.Max(1, allResults.Count));
            return allResults;
        }
        finally
        {
            _activeProfile = null;
            _activeProgress = previousProgress;
            _activeCancel = previousCancel;
        }
    }

    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════

    /// <summary>



    /// </summary>
    private static List<LocalRectangle> CollectRectanglesFromSpace(
        Transaction tr,
        BlockTableRecord owner,
        bool recognizeFourLineRectangles,
        string layoutName = "")
    {
        var profile = _activeProfile;
        var candidateIds = new List<ObjectId>();
        var topLevelVisits = 0;
        const int progressEvery = 2500;
        foreach (ObjectId id in owner)
        {
            if ((topLevelVisits % progressEvery) == 0)
            {
                _activeCancel.ThrowIfCancellationRequested();
                if (topLevelVisits == 0)
                {
                    ReportScan(
                        string.IsNullOrEmpty(layoutName)
                            ? "Фильтрация объектов-кандидатов…"
                            : $"Фильтрация объектов-кандидатов ({layoutName})…");
                }
                else
                {
                    ReportScan(
                        string.IsNullOrEmpty(layoutName)
                            ? $"Фильтрация объектов-кандидатов… обработано: {topLevelVisits:N0}"
                            : $"Фильтрация объектов-кандидатов ({layoutName})… обработано: {topLevelVisits:N0}",
                        topLevelVisits,
                        0);
                }
            }

            topLevelVisits++;
            DBObject? obj;
            try
            {
                obj = tr.GetObject(id, OpenMode.ForRead, false);
            }
            catch
            {
                continue;
            }

            if (!ScanCandidateFilter.IsRectangleFrameCandidate(obj, recognizeFourLineRectangles))
            {
                continue;
            }

            candidateIds.Add(id);
        }

        if (profile != null)
        {
            profile.TopLevelEntityVisits += topLevelVisits;
        }

        return CollectRectanglesFromEntities(tr, candidateIds, recognizeFourLineRectangles, layoutName);
    }

    /// <summary>

    /// </summary>
    private static List<LocalRectangle> CollectRectanglesFromEntities(
        Transaction tr,
        IEnumerable<ObjectId> entityIds,
        bool recognizeFourLineRectangles,
        string layoutName = "")
    {
        var profile = _activeProfile;
        var rectangles = new List<LocalRectangle>();
        var segments = recognizeFourLineRectangles ? new List<FourLineRectangleFinder.Segment>() : null;
        var entitySw = profile != null ? Stopwatch.StartNew() : null;
        var topLevelVisits = 0;
        foreach (var id in entityIds)
        {
            Entity? entity;
            try
            {
                if (id.IsNull)
                {
                    continue;
                }

                entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            }
            catch
            {
                continue;
            }

            if (entity == null
                || !ScanCandidateFilter.IsRectangleFrameCandidate(entity, recognizeFourLineRectangles))
            {
                continue;
            }

            topLevelVisits++;
            CollectEntityRectangles(
                tr,
                entity,
                Matrix3d.Identity,
                rectangles,
                segments,
                new HashSet<ObjectId>(),
                0,
                recognizeFourLineRectangles);
        }

        if (entitySw != null && profile != null)
        {
            entitySw.Stop();
            profile.CollectEntitiesMs += entitySw.ElapsedMilliseconds;
            profile.TopLevelEntityVisits += topLevelVisits;
            profile.ClosedPolylineRectCount += rectangles.Count;
            if (segments != null)
            {
                profile.LineSegmentCount += segments.Count;
            }
        }

        if (segments != null && segments.Count >= 4)
        {
            _activeCancel.ThrowIfCancellationRequested();
            ReportScan(
                string.IsNullOrEmpty(layoutName)
                    ? $"Сборка рамок из четырёх линий (отрезков: {segments.Count:N0})…"
                    : $"Сборка рамок из четырёх линий ({layoutName}; отрезков: {segments.Count:N0})…",
                0,
                0);
            var fourSw = profile != null ? Stopwatch.StartNew() : null;
            var before = rectangles.Count;
            rectangles.AddRange(FourLineRectangleFinder.Find(segments, _activeCancel, (msg, cur, tot) => ReportScan(msg, cur, tot)));
            if (fourSw != null && profile != null)
            {
                fourSw.Stop();
                profile.FourLineMatchMs += fourSw.ElapsedMilliseconds;
                profile.FourLineRectCount += rectangles.Count - before;
            }
        }

        return rectangles;
    }

    /// <summary>

    ///

    /// </summary>
    private static List<Result> FilterAndPackageRectangles(
        Database database,
        List<LocalRectangle> rectangles,
        CadSelectionWindow? coordinateContext,
        ObjectId ownerId,
        string sourceFile,
        string layoutName,
        bool isPaperSpace,
        int layoutTabOrder,
        double paperMatchToleranceMm,
        double longPaperSnapToleranceMm,
        IReadOnlyList<double>? customScales = null)
    {

        List<LocalRectangle> inWindow;
        if (coordinateContext != null && coordinateContext.Bounds.HasArea())
        {
            inWindow = rectangles
                .Where(rectangle => coordinateContext.IntersectsWorldPoints(GetWorldPoints(rectangle)))
                .ToList();
        }
        else
        {
            inWindow = rectangles.ToList();
        }


        var profile = _activeProfile;
        var paperSw = profile != null ? Stopwatch.StartNew() : null;
        var stem = Path.GetFileNameWithoutExtension(sourceFile);
        var paperMatched = new List<LocalRectangle>();
        var paperOptionsByRect = new Dictionary<LocalRectangle, IReadOnlyList<PaperDetection>>();
        var coordinateBoundsByRect = new Dictionary<LocalRectangle, LocalRectangle>();
        var paperTotal = inWindow.Count;
        for (var paperIndex = 0; paperIndex < inWindow.Count; paperIndex++)
        {
            if ((paperIndex % 50) == 0)
            {
                _activeCancel.ThrowIfCancellationRequested();
                ReportScan(
                    $"Сопоставление формата бумаги… {paperIndex:N0}/{paperTotal:N0}",
                    paperIndex,
                    Math.Max(1, paperTotal));
            }

            var rectangle = inWindow[paperIndex];
            var coordinateBounds = !isPaperSpace
                                   && coordinateContext != null
                                   && !coordinateContext.IsWorldCoordinateSystem
                ? coordinateContext.TransformWorldPointsToBounds(GetWorldPoints(rectangle))
                : null;
            var width = coordinateBounds != null
                ? coordinateBounds.MaxX - coordinateBounds.MinX
                : rectangle.ActualWidth > 0 ? rectangle.ActualWidth : rectangle.MaxX - rectangle.MinX;
            var height = coordinateBounds != null
                ? coordinateBounds.MaxY - coordinateBounds.MinY
                : rectangle.ActualHeight > 0 ? rectangle.ActualHeight : rectangle.MaxY - rectangle.MinY;

            var detectionOptions = PaperSizeDetector.CreateRectangleBatchOptions(paperMatchToleranceMm, isPaperSpace, longPaperSnapToleranceMm, customScales);
            var options = PaperSizeDetector.DetectRectangleBatchCandidates(width, height, detectionOptions);
            if (options.Count == 0)
            {
                continue;
            }

            paperMatched.Add(rectangle);
            paperOptionsByRect[rectangle] = options;
            if (coordinateBounds != null)
            {
                coordinateBoundsByRect[rectangle] = coordinateBounds;
            }
        }

        if (paperSw != null && profile != null)
        {
            paperSw.Stop();
            profile.PaperMatchMs += paperSw.ElapsedMilliseconds;
            profile.AfterPaperMatchCount += paperMatched.Count;
        }


        var dedupSw = profile != null ? Stopwatch.StartNew() : null;
        var unique = FilterRectangles(paperMatched);
        if (dedupSw != null && profile != null)
        {
            dedupSw.Stop();
            profile.DedupMs += dedupSw.ElapsedMilliseconds;
            profile.AfterDedupCount += unique.Count;
        }


        ReportScan("Проверка пустых рамок…");
        var emptySw = profile != null ? Stopwatch.StartNew() : null;
        var withContent = FilterEmptyRectangles(database, ownerId, unique);
        if (emptySw != null && profile != null)
        {
            emptySw.Stop();
            profile.EmptyFilterMs += emptySw.ElapsedMilliseconds;
            profile.AfterEmptyFilterCount += withContent.Count;
        }


        var results = new List<Result>();
        var packedRectangles = new List<LocalRectangle>(withContent.Count);
        foreach (var rectangle in withContent)
        {
            var options = paperOptionsByRect[rectangle];
            var paper = options.First();
            coordinateBoundsByRect.TryGetValue(rectangle, out var coordinateBounds);
            var width = coordinateBounds != null
                ? coordinateBounds.MaxX - coordinateBounds.MinX
                : rectangle.ActualWidth > 0 ? rectangle.ActualWidth : rectangle.MaxX - rectangle.MinX;
            var height = coordinateBounds != null
                ? coordinateBounds.MaxY - coordinateBounds.MinY
                : rectangle.ActualHeight > 0 ? rectangle.ActualHeight : rectangle.MaxY - rectangle.MinY;
            var index = results.Count;
            var result = new Result
            {
                CornerPoints = rectangle.CornerPoints,
                PaperOptions = options,
                Job = new PlotJob
                {
                    FrameSource = FrameSourceKind.Rectangle,
                    IsManualWindow = true,
                    SourceFile = sourceFile,
                    SpaceName = layoutName,
                    IsPaperSpace = isPaperSpace,
                    LayoutTabOrder = layoutTabOrder,
                    DrawingNumber = (index + 1).ToString("D2"),
                    Title = stem,
                    PaperName = paper.PaperName,
                    ScaleText = paper.ScaleText,
                    SizeText = $"{width:0.##} x {height:0.##}",
                    PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                    DetectionNote = "Универсальная пакетная печать",
                    PaperWidthMm = paper.PaperWidthMm,
                    PaperHeightMm = paper.PaperHeightMm,
                    DetectedRequiresCustomPaperRegistration = paper.RequiresCustomPaper,
                    RequiresCustomPaperRegistration = paper.RequiresCustomPaper,
                    MinX = rectangle.MinX,
                    MinY = rectangle.MinY,
                    MaxX = rectangle.MaxX,
                    MaxY = rectangle.MaxY,

                    CornerPoints = rectangle.CornerPoints == null
                        ? null
                        : (double[])rectangle.CornerPoints.Clone()
                }
            };
            if (!isPaperSpace && coordinateContext != null && coordinateBounds != null)
            {
                coordinateContext.ApplyToJob(result.Job, coordinateBounds);
            }

            results.Add(result);
            packedRectangles.Add(rectangle);
        }


        ReportScan("Распознавание атрибутов номера и наименования листа…");
        var attrSw = _activeProfile != null ? Stopwatch.StartNew() : null;
        FillAttributeIdentities(database, ownerId, results, packedRectangles);
        if (attrSw != null && _activeProfile != null)
        {
            attrSw.Stop();
            _activeProfile.AttributeFillMs += attrSw.ElapsedMilliseconds;
        }

        return results;
    }


    private const string DrawingNumberAttributeTag = "\u56FE\u53F7";


    private const string TitleAttributeTag = "\u56FE\u540D";

    /// <summary>


    /// </summary>
    private static void FillAttributeIdentities(
        Database database,
        ObjectId ownerId,
        IReadOnlyList<Result> results,
        IReadOnlyList<LocalRectangle> rectangles)
    {
        if (results.Count == 0 || results.Count != rectangles.Count)
        {
            return;
        }

        var numberCandidates = new List<(string Text, double X, double Y)>();
        var titleCandidates = new List<(string Text, double X, double Y)>();
        var settings = AppSettingsStore.Load();
        var fallbackProfile = FrameTextRegionService.FindProfile(settings, FrameSourceKind.Rectangle, "");
        CadTextExtractor.OwnerTextCache? ownerTextCache = null;
        using (var tr = database.TransactionManager.StartTransaction())
        {
            var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);
            if (fallbackProfile != null)
            {
                try
                {
                    ownerTextCache = CadTextExtractor.BuildOwnerTextCache(tr, owner);
                }
                catch
                {
                    ownerTextCache = null;
                }
            }

            foreach (ObjectId id in owner)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef)
                {
                    continue;
                }

                if (!IsEntityVisible(blockRef)
                    || string.Equals(blockRef.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase)
                    || !IsEntityLayerScannable(tr, blockRef)
                    || blockRef.AttributeCollection == null
                    || blockRef.AttributeCollection.Count == 0)
                {
                    continue;
                }

                foreach (ObjectId attributeId in blockRef.AttributeCollection)
                {
                    if (tr.GetObject(attributeId, OpenMode.ForRead, false) is not AttributeReference attribute
                        || !IsEntityVisible(attribute))
                    {
                        continue;
                    }

                    var tag = (attribute.Tag ?? "").Trim();
                    var text = GetAttributeDisplayText(attribute).Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    var point = attribute.Position;
                    if (string.Equals(tag, DrawingNumberAttributeTag, StringComparison.Ordinal))
                    {
                        numberCandidates.Add((text, point.X, point.Y));
                    }
                    else if (string.Equals(tag, TitleAttributeTag, StringComparison.Ordinal))
                    {
                        titleCandidates.Add((text, point.X, point.Y));
                    }
                }
            }

            tr.Commit();
        }

        if (numberCandidates.Count == 0 && titleCandidates.Count == 0 && fallbackProfile == null)
        {
            return;
        }

        using (var tr = database.TransactionManager.StartTransaction())
        {
            var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);
            if (fallbackProfile != null && ownerTextCache == null)
            {
                try { ownerTextCache = CadTextExtractor.BuildOwnerTextCache(tr, owner); }
                catch { ownerTextCache = null; }
            }

            for (var i = 0; i < results.Count; i++)
            {
                var rectangle = rectangles[i];
                var job = results[i].Job;
                var numberResolved = false;
                var titleResolved = false;

                if (TryPickClosestAttribute(numberCandidates, rectangle, out var drawingNumber))
                {
                    job.CadDrawingNumber = drawingNumber;
                    job.DrawingNumber = drawingNumber;
                    numberResolved = true;
                }

                if (TryPickClosestAttribute(titleCandidates, rectangle, out var title))
                {
                    job.CadTitle = title;
                    job.Title = title;
                    titleResolved = true;
                }

                if (fallbackProfile != null && (!numberResolved || !titleResolved))
                {
                    try
                    {
                        if (FrameTextRegionService.TryRead(
                                tr,
                                owner,
                                rectangle,
                                job.PaperWidthMm,
                                job.PaperHeightMm,
                                fallbackProfile,
                                ownerTextCache,
                                out var fallbackNumber,
                                out var fallbackTitle))
                        {
                            if (!numberResolved && !string.IsNullOrWhiteSpace(fallbackNumber))
                            {
                                job.CadDrawingNumber = fallbackNumber;
                                job.DrawingNumber = fallbackNumber;
                                numberResolved = true;
                            }

                            if (!titleResolved && !string.IsNullOrWhiteSpace(fallbackTitle))
                            {
                                job.CadTitle = fallbackTitle;
                                job.Title = fallbackTitle;
                                titleResolved = true;
                            }

                            if (numberResolved || titleResolved)
                                job.DetectionNote += "; text-region fallback";
                        }
                    }
                    catch
                    {
                        // Резервные области не должны ломать распознавание самой рамки.
                    }
                }
            }

            tr.Commit();
        }
    }

    /// <summary>

    /// </summary>
    private static bool TryPickClosestAttribute(
        IReadOnlyList<(string Text, double X, double Y)> candidates,
        LocalRectangle rectangle,
        out string text)
    {
        text = "";
        var bestDistance = double.MaxValue;
        string? best = null;
        var cornerX = rectangle.MaxX;
        var cornerY = rectangle.MinY;
        foreach (var candidate in candidates)
        {
            if (!IsPointInsideRectangle(rectangle, candidate.X, candidate.Y))
            {
                continue;
            }

            var dx = candidate.X - cornerX;
            var dy = candidate.Y - cornerY;
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = candidate.Text;
        }

        if (best == null)
        {
            return false;
        }

        text = best;
        return true;
    }

    /// <summary>

    /// </summary>
    private static bool IsPointInsideRectangle(LocalRectangle rectangle, double x, double y)
    {
        if (rectangle.CornerPoints is { Length: >= 8 } points)
        {
            var polygon = new[]
            {
                new Point3d(points[0], points[1], 0),
                new Point3d(points[2], points[3], 0),
                new Point3d(points[4], points[5], 0),
                new Point3d(points[6], points[7], 0)
            };
            return IsPointInsidePolygon(new Point3d(x, y, 0), polygon);
        }

        return rectangle.Contains(x, y);
    }


    private static bool IsPointInsidePolygon(Point3d point, Point3d[] polygon)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Length - 1;
             current < polygon.Length;
             previous = current++)
        {
            var start = polygon[previous];
            var end = polygon[current];
            var onSegment =
                Math.Abs((end.Y - start.Y) * (point.X - start.X) - (end.X - start.X) * (point.Y - start.Y)) <= 1e-9
                && point.X >= Math.Min(start.X, end.X) - 1e-9
                && point.X <= Math.Max(start.X, end.X) + 1e-9
                && point.Y >= Math.Min(start.Y, end.Y) - 1e-9
                && point.Y <= Math.Max(start.Y, end.Y) + 1e-9;
            if (onSegment)
            {
                return true;
            }

            var crossesScanLine = (start.Y > point.Y) != (end.Y > point.Y);
            if (crossesScanLine
                && point.X < (end.X - start.X) * (point.Y - start.Y) / (end.Y - start.Y + 1e-30) + start.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }


    private static string GetAttributeDisplayText(AttributeReference attribute)
    {
        return attribute.TextString ?? "";
    }

    private static Point3d[] GetWorldPoints(LocalRectangle rectangle)
    {
        if (rectangle.CornerPoints is { Length: >= 8 } points)
        {
            return new[]
            {
                new Point3d(points[0], points[1], 0),
                new Point3d(points[2], points[3], 0),
                new Point3d(points[4], points[5], 0),
                new Point3d(points[6], points[7], 0)
            };
        }

        return CadSelectionWindow.GetCorners(rectangle);
    }

    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════

    private static bool ShouldScanLayout(
        Layout layout,
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
                return IsCurrentSpace(layout, currentSpaceName);
            case TitleBlockScanScope.AllSpaces:
                return true;
            default:
                return false;
        }
    }

    private static bool IsCurrentSpace(Layout layout, string? currentSpaceName)
    {
        if (string.IsNullOrWhiteSpace(currentSpaceName))
        {
            return false;
        }

        return string.Equals(layout.LayoutName, currentSpaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCurrentSpaceName(Database db)
    {
        try
        {
            return LayoutManager.Current.CurrentLayout;
        }
        catch
        {
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var csId = db.CurrentSpaceId;
                if (!csId.IsNull && tr.GetObject(csId, OpenMode.ForRead) is BlockTableRecord btr)
                {
                    var layout = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);
                    return layout.LayoutName;
                }
            }
            catch
            {

            }

            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════

    /// <summary>

    ///





    ///







    /// </summary>








    private static void CollectEntityRectangles(
        Transaction tr,
        Entity entity,
        Matrix3d transform,
        ICollection<LocalRectangle> rectangles,
        ICollection<FourLineRectangleFinder.Segment>? segments,
        ISet<ObjectId> visitedDefinitions,
        int depth,
        bool recognizeFourLineRectangles)
    {

        if (string.Equals(entity.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }





        if (!IsEntityVisible(entity) || !IsFrameBoundaryLayerScannable(tr, entity))
        {
            return;
        }


        if (segments != null
            && depth <= 3
            && entity is Line line)
        {
            FourLineRectangleFinder.TryAddLine(line, transform, segments);
        }


        if (segments != null
            && depth <= 3
            && entity is Polyline plSegment
            && FourLineRectangleFinder.TryGetStraightOpenPolylineSegment(plSegment, transform, out var polylineSegment))
        {
            segments.Add(polylineSegment);
        }


        if (segments != null
            && depth <= 3
            && entity is Polyline2d pl2dSegment
            && FourLineRectangleFinder.TryGetStraightOpenPolyline2dSegment(
                tr,
                pl2dSegment,
                transform,
                out var legacyPolylineSegment))
        {
            segments.Add(legacyPolylineSegment);
        }


        if (entity is Polyline polyline


            && IsFrameBoundaryLayerScannable(tr, entity)
            && RectangleGeometry.TryGetRectangle(
                polyline,
                transform,
                requireClosed: true,
                out var rectangle))
        {

            rectangles.Add(rectangle);
        }




        if (entity is Polyline2d polyline2d
            && IsFrameBoundaryLayerScannable(tr, entity)
            && RectangleGeometry.TryGetRectangleFrom2d(
                tr,
                polyline2d,
                transform,
                requireClosed: false,
                out var rectangle2d))
        {
            rectangles.Add(rectangle2d);
        }



        if (entity is Polyline3d polyline3d
            && IsFrameBoundaryLayerScannable(tr, entity)
            && RectangleGeometry.TryGetRectangleFrom3d(
                tr,
                polyline3d,
                transform,
                requireClosed: false,
                out var rectangle3d))
        {
            rectangles.Add(rectangle3d);
        }


        if (entity is not BlockReference blockReference || depth >= 12)
        {
            return;
        }


        if (IsBlockClipped(tr, blockReference))
        {
            return;
        }

        var definitionId = blockReference.BlockTableRecord;
        var instanceXform = blockReference.BlockTransform * transform;


        if (BlockDefinitionCache.TryGetValue(definitionId, out var cachedRects))
        {
            if (cachedRects.Count > 0)
            {
                rectangles.Add(RectangleGeometry.TransformRectangle(cachedRects[0], instanceXform));
            }
            return;
        }


        if (!visitedDefinitions.Add(definitionId))
        {
            return;
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);





            var localRects = new List<LocalRectangle>();
            var blockSegments = recognizeFourLineRectangles && depth < 3
                ? new List<FourLineRectangleFinder.Segment>()
                : null;
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity nested)
                {
                    continue;
                }



                if (!IsEntityLayerVisibleForScanning(tr, nested))
                {
                    continue;
                }

                if (!IsEntityVisible(nested))
                {
                    continue;
                }


                CollectEntityRectangles(
                    tr,
                    nested,
                    Matrix3d.Identity,
                    localRects,
                    blockSegments,
                    visitedDefinitions,
                    depth + 1,
                    recognizeFourLineRectangles);
            }

            if (blockSegments != null && blockSegments.Count >= 4)
            {
                var segRects = FourLineRectangleFinder.Find(blockSegments, _activeCancel, (msg, cur, tot) => ReportScan(msg, cur, tot));
                localRects.AddRange(segRects);
            }


            List<LocalRectangle> cacheEntry;
            if (localRects.Count > 0)
            {
                var largest = localRects.OrderByDescending(Area).First();
                cacheEntry = new List<LocalRectangle> { largest };
                rectangles.Add(RectangleGeometry.TransformRectangle(largest, instanceXform));
            }
            else
            {
                cacheEntry = new List<LocalRectangle>();
            }

            BlockDefinitionCache[definitionId] = cacheEntry;
        }
        catch
        {

        }
        finally
        {

            visitedDefinitions.Remove(definitionId);
        }
    }

    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════


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

    /// <summary>



    /// </summary>
    private static bool IsEntityLayerScannable(Transaction tr, Entity entity)
    {
        try
        {
            if (entity.LayerId.IsNull)
            {
                return false;
            }

            if (LayerScannableCache.TryGetValue(entity.LayerId, out var cached))
            {
                return cached;
            }

            if (tr.GetObject(entity.LayerId, OpenMode.ForRead, false) is not LayerTableRecord layer)
            {
                LayerScannableCache[entity.LayerId] = false;
                return false;
            }

            var result = !layer.IsOff
                && !layer.IsFrozen
                && layer.IsPlottable;
            LayerScannableCache[entity.LayerId] = result;
            return result;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>


    /// </summary>
    private static bool IsFrameBoundaryLayerScannable(Transaction tr, Entity entity)
    {
        return IsEntityLayerVisibleForScanning(tr, entity);
    }

    /// <summary>



    /// </summary>
    private static bool IsEntityLayerVisibleForScanning(Transaction tr, Entity entity)
    {
        try
        {
            if (entity.LayerId.IsNull
                || tr.GetObject(entity.LayerId, OpenMode.ForRead, false) is not LayerTableRecord layer)
            {
                return false;
            }

            return !layer.IsOff && !layer.IsFrozen;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>



    /// </summary>
    private static bool IsBlockClipped(Transaction tr, BlockReference blockRef)
    {
        try
        {
            if (blockRef.ExtensionDictionary == ObjectId.Null)
            {
                return false;
            }

            var extDict = (DBDictionary)tr.GetObject(blockRef.ExtensionDictionary, OpenMode.ForRead);
            return extDict.Contains("ACAD_FILTER");
        }
        catch
        {
            return false;
        }
    }


    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════

    /// <summary>

    ///




    /// </summary>
    private static List<LocalRectangle> FilterRectangles(IEnumerable<LocalRectangle> source)
    {
        var unique = new List<LocalRectangle>();


        foreach (var rectangle in source.OrderByDescending(Area))
        {
            var tolerance = Math.Max(rectangle.MaxX - rectangle.MinX, rectangle.MaxY - rectangle.MinY) * 0.002;

            if (unique.Any(existing =>
                    SameBounds(existing, rectangle, tolerance)
                    || HasDuplicateOverlap(existing, rectangle)))
            {
                continue;
            }

            unique.Add(rectangle);
        }



        return unique
            .Where(candidate => !unique.Any(container =>
                !ReferenceEquals(container, candidate)
                && Area(container) > Area(candidate)
                && Contains(container, candidate)))
            .ToList();
    }


    private static bool SameBounds(LocalRectangle a, LocalRectangle b, double tolerance)
    {
        return Math.Abs(a.MinX - b.MinX) <= tolerance
            && Math.Abs(a.MinY - b.MinY) <= tolerance
            && Math.Abs(a.MaxX - b.MaxX) <= tolerance
            && Math.Abs(a.MaxY - b.MaxY) <= tolerance;
    }

    /// <summary>

    ///




    /// </summary>
    private static bool HasDuplicateOverlap(LocalRectangle a, LocalRectangle b)
    {
        var overlapWidth = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        var overlapHeight = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        var overlapArea = overlapWidth * overlapHeight;
        if (overlapArea <= 0)
        {
            return false;
        }

        var areaA = Area(a);
        var areaB = Area(b);
        if (areaA <= 0 || areaB <= 0)
        {
            return false;
        }


        var smallerCoverage = overlapArea / Math.Min(areaA, areaB);

        var largerCoverage = overlapArea / Math.Max(areaA, areaB);

        var widthSimilarity = Math.Min(a.MaxX - a.MinX, b.MaxX - b.MinX)
            / Math.Max(a.MaxX - a.MinX, b.MaxX - b.MinX);
        var heightSimilarity = Math.Min(a.MaxY - a.MinY, b.MaxY - b.MinY)
            / Math.Max(a.MaxY - a.MinY, b.MaxY - b.MinY);

        return smallerCoverage >= 0.90
            && largerCoverage >= 0.82
            && widthSimilarity >= 0.90
            && heightSimilarity >= 0.90;
    }

    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════


    private static bool Contains(LocalRectangle outer, LocalRectangle inner)
    {
        var tolerance = Math.Max(outer.MaxX - outer.MinX, outer.MaxY - outer.MinY) * 0.003;
        return inner.MinX >= outer.MinX - tolerance
            && inner.MinY >= outer.MinY - tolerance
            && inner.MaxX <= outer.MaxX + tolerance
            && inner.MaxY <= outer.MaxY + tolerance;
    }


    private static double Area(LocalRectangle rectangle)
    {
        return Math.Max(0, rectangle.MaxX - rectangle.MinX)
            * Math.Max(0, rectangle.MaxY - rectangle.MinY);
    }


    private static bool Intersects(LocalRectangle rectangle, LocalRectangle window)
    {
        return rectangle.MaxX >= window.MinX
            && rectangle.MinX <= window.MaxX
            && rectangle.MaxY >= window.MinY
            && rectangle.MinY <= window.MaxY;
    }

    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════

    /// <summary>

    ///


    /// </summary>




	    /// <summary>



	    /// </summary>
	    private static List<LocalRectangle> FilterEmptyRectangles(
	        Database database, ObjectId ownerId, List<LocalRectangle> candidates)
	    {
	        using var tr = database.TransactionManager.StartTransaction();
	        var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);


	        var entityBoxes = new List<LocalRectangle>();
	        var boxSw = _activeProfile != null ? Stopwatch.StartNew() : null;
	        CollectEntityBoxesForContentCheck(tr, owner, Matrix3d.Identity, entityBoxes,
	            new HashSet<ObjectId>(), 0);
	        if (boxSw != null && _activeProfile != null)
	        {
	            boxSw.Stop();
	            _activeProfile.EmptyBoxCollectMs += boxSw.ElapsedMilliseconds;
	        }


	        var result = new List<LocalRectangle>();
	        foreach (var rect in candidates)
	        {
	            if (AnyIntersects(entityBoxes, rect))
	            {
	                result.Add(rect);
	            }
	        }

	        return result;
	    }

	    /// <summary>



	    /// </summary>
	    private static void CollectEntityBoxesForContentCheck(
	        Transaction tr,
	        BlockTableRecord owner,
	        Matrix3d transform,
	        List<LocalRectangle> boxes,
	        HashSet<ObjectId> visitedDefinitions,
	        int depth)
	    {
	        foreach (ObjectId id in owner)
	        {
	            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
	            {
	                continue;
	            }


	            if (string.Equals(entity.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase))
	            {
	                continue;
	            }


	            if (!IsEntityLayerScannable(tr, entity))
	            {
	                continue;
	            }


	            if (!IsEntityVisible(entity))
	            {
	                continue;
	            }


	            try
	            {
	                var ext = entity.GeometricExtents;
	                var extMin = ext.MinPoint.TransformBy(transform);
	                var extMax = ext.MaxPoint.TransformBy(transform);
	                boxes.Add(LocalRectangle.FromPoints(
	                    Math.Min(extMin.X, extMax.X), Math.Min(extMin.Y, extMax.Y),
	                    Math.Max(extMin.X, extMax.X), Math.Max(extMin.Y, extMax.Y)));
	            }
	            catch
	            {

	            }


	            if (entity is not BlockReference blockRef || depth >= 5)
	            {
	                continue;
	            }

	            if (IsBlockClipped(tr, blockRef))
	            {
	                continue;
	            }

	            var definitionId = blockRef.BlockTableRecord;
	            if (!visitedDefinitions.Add(definitionId))
	            {
	                continue;
	            }

	            try
	            {
	                var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
	                var nestedTransform = blockRef.BlockTransform * transform;
	                CollectEntityBoxesForContentCheck(tr, definition, nestedTransform, boxes,
	                    visitedDefinitions, depth + 1);
	            }
	            catch
	            {

	            }

	            visitedDefinitions.Remove(definitionId);
	        }
	    }

	    /// <summary>


	    /// </summary>
	    private static bool AnyIntersects(List<LocalRectangle> boxes, LocalRectangle target)
	    {
	        foreach (var box in boxes)
	        {
	            if (Intersects(box, target))
	            {
	                return true;
	            }
	        }

	        return false;
	    }
	}
