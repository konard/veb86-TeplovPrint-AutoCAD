using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Сканер форматов СПДС GraphiCS (CSoft). Намеренно принимает только
/// интеллектуальные объекты с DXF-именем SPDSFORMAT / RXClass mcsDbObjectFormat.
/// Геометрическое распознавание произвольных рамок и библиотека рамок здесь не используются.
/// </summary>
public static class SpdsFormatScanner
{
    public const string SpdsDxfName = "SPDSFORMAT";
    public const string SpdsRxClassName = "mcsDbObjectFormat";

    public static List<PlotJob> ScanModelSpace(Document doc, double? paperMatchToleranceMm = null)
    {
        if (doc == null) throw new ArgumentNullException(nameof(doc));
        var sourceName = string.IsNullOrWhiteSpace(doc.Database.Filename)
            ? doc.Name
            : doc.Database.Filename;

        return ScanModelSpace(doc.Database, sourceName, paperMatchToleranceMm);
    }

    public static List<PlotJob> ScanModelSpace(Database db, string sourceName, double? paperMatchToleranceMm = null)
        => Scan(db, sourceName, TitleBlockScanScope.ModelSpace, null, paperMatchToleranceMm, null, null);

    public static List<PlotJob> Scan(
        Database db,
        string sourceName,
        TitleBlockScanScope scope,
        IEnumerable<ObjectId>? selectedIds = null,
        double? paperMatchToleranceMm = null,
        ISet<string>? allowedLayoutNames = null,
        ICollection<string>? warnings = null)
    {
        if (db == null) throw new ArgumentNullException(nameof(db));

        var settings = AppSettingsStore.Load();
        var tolerance = paperMatchToleranceMm ?? settings.PaperMatchToleranceMm;
        var jobs = new List<PlotJob>();

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var matchIndex = 0;
        var ownerTextCaches = new Dictionary<ObjectId, CadTextExtractor.OwnerTextCache>();
        var ids = selectedIds?.ToList() ?? EnumerateIds(db, tr, blockTable, scope, allowedLayoutNames).ToList();
        foreach (var id in ids)
        {
            Entity? entity;
            SpdsFormatInfo? info = null;
            try
            {
                entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            }
            catch
            {
                continue;
            }

            if (entity == null)
                continue;
            if (!IsSpdsFormat(entity))
            {
                if (IsPotentialSpdsProxy(entity))
                    warnings?.Add($"DWG={sourceName}; Handle={SafeHandle(entity)}; SPDS Object Enabler is unavailable or the SPDSFORMAT object is a proxy");
                continue;
            }

            try
            {
            info = SpdsFormatPropertyReader.TryRead(entity);
            Extents3d extents;
            try
            {
                extents = entity.GeometricExtents;
            }
            catch
            {
                warnings?.Add($"DWG={sourceName}; Handle={info.Handle}; Format={info.DisplayFormat}; GeometricExtents unavailable");
                continue;
            }

            var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
            var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            if (width <= 1e-6 || height <= 1e-6)
                continue;

            var owner = (BlockTableRecord)tr.GetObject(entity.OwnerId, OpenMode.ForRead);
            if (!owner.IsLayout)
                continue;
            var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            if (allowedLayoutNames != null && !allowedLayoutNames.Contains(layout.LayoutName))
                continue;

            var options = PaperSizeDetector.CreateTitleBlockBatchOptions(
                tolerance,
                isPaperSpace: !layout.ModelType,
                settings.LongPaperSnapToleranceMm,
                settings.CustomScales);
            var paper = PaperSizeDetector.Detect(width, height, options);
            if (SpdsPaperResolver.TryResolve(info))
            {
                paper.PaperWidthMm = info.PaperWidthMm;
                paper.PaperHeightMm = info.PaperHeightMm;
                paper.PaperName = info.DisplayFormat;
                var effectiveScale = info.Scale > 0 ? info.Scale : info.DimScale;
                paper.ScaleText = effectiveScale > 0 ? PaperSizeDetector.ToScaleText(effectiveScale) : paper.ScaleText;
                paper.RequiresCustomPaper = info.RequiresCustomPaper;
            }

            var window = SpdsPlotWindowResolver.Resolve(info, extents);
            if (!string.IsNullOrWhiteSpace(info.ReadError))
                warnings?.Add($"DWG={sourceName}; Handle={info.Handle}; Format={info.DisplayFormat}; {info.ReadError}");
            if (window.Note.StartsWith("Extents fallback", StringComparison.OrdinalIgnoreCase))
                warnings?.Add($"DWG={sourceName}; Handle={info.Handle}; Format={info.DisplayFormat}; {window.Note}");

            var fallbackNumber = "";
            var fallbackTitle = "";
            var fallbackUsed = false;
            if (string.IsNullOrWhiteSpace(info.Sheet) || string.IsNullOrWhiteSpace(info.DrawingName))
            {
                var profile = FrameTextRegionService.FindProfile(settings, FrameSourceKind.Spds, SpdsDxfName);
                if (profile != null)
                {
                    try
                    {
                        if (!ownerTextCaches.TryGetValue(owner.ObjectId, out var ownerTextCache))
                        {
                            ownerTextCache = CadTextExtractor.BuildOwnerTextCache(tr, owner);
                            ownerTextCaches[owner.ObjectId] = ownerTextCache;
                        }

                        var frameBounds = LocalRectangle.FromPoints(window.MinX, window.MinY, window.MaxX, window.MaxY);
                        if (FrameTextRegionService.TryRead(
                                tr,
                                owner,
                                frameBounds,
                                paper.PaperWidthMm,
                                paper.PaperHeightMm,
                                profile,
                                ownerTextCache,
                                out fallbackNumber,
                                out fallbackTitle))
                        {
                            fallbackUsed = (!string.IsNullOrWhiteSpace(fallbackNumber) && string.IsNullOrWhiteSpace(info.Sheet))
                                || (!string.IsNullOrWhiteSpace(fallbackTitle) && string.IsNullOrWhiteSpace(info.DrawingName));
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings?.Add($"DWG={sourceName}; Handle={info.Handle}; резервные текстовые области: {ex.Message}");
                    }
                }
            }

            var drawingNumber = FirstNonEmpty(info.Sheet, fallbackNumber, info.Designation, entity.Handle.ToString());
            var title = FirstNonEmpty(info.DrawingName, fallbackTitle, info.DisplayFormat, "СПДС формат");

            jobs.Add(new PlotJob
            {
                SourceFile = sourceName,
                SpaceName = layout.LayoutName,
                IsPaperSpace = !layout.ModelType,
                LayoutTabOrder = layout.TabOrder,
                BlockName = SpdsDxfName,
                BlockHandle = entity.Handle.ToString(),
                FrameSource = FrameSourceKind.Spds,
                PreferSpatialSort = string.IsNullOrWhiteSpace(info.Sheet),
                MatchIndex = matchIndex++,
                DrawingNumber = drawingNumber,
                CadDrawingNumber = drawingNumber,
                Title = title,
                CadTitle = title,
                Phase = info.Stage,
                CadPhase = info.Stage,
                Info1 = info.Designation,
                CadInfo1 = info.Designation,
                Info2 = info.SheetCount,
                CadInfo2 = info.SheetCount,
                PaperName = string.IsNullOrWhiteSpace(info.DisplayFormat) ? paper.PaperName : info.DisplayFormat,
                ScaleText = paper.ScaleText,
                SizeText = $"{width:0.##} x {height:0.##}",
                PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                DetectionNote = $"SPDSFORMAT; properties={info.ReadMethod}; window={window.Note}; {(fallbackUsed ? "text-region fallback; " : "")}{paper.Note}",
                PaperWidthMm = paper.PaperWidthMm,
                PaperHeightMm = paper.PaperHeightMm,
                DetectedRequiresCustomPaperRegistration = paper.RequiresCustomPaper,
                RequiresCustomPaperRegistration = paper.RequiresCustomPaper,
                RequireExactPaperSize = true,
                UseExactWindowScale = true,
                MinX = window.MinX,
                MinY = window.MinY,
                MaxX = window.MaxX,
                MaxY = window.MaxY,
                CornerPoints = window.CornerPoints
            });
            }
            catch (Exception ex)
            {
                warnings?.Add($"DWG={sourceName}; Handle={info?.Handle ?? SafeHandle(entity)}; Format={info?.DisplayFormat ?? ""}; SPDSFORMAT scan error: {ex.Message}");
            }
        }

        return jobs;
    }

    private static IEnumerable<ObjectId> EnumerateIds(
        Database db,
        Transaction tr,
        BlockTable blockTable,
        TitleBlockScanScope scope,
        ISet<string>? allowedLayoutNames)
    {
        foreach (ObjectId recordId in blockTable)
        {
            var record = tr.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord;
            if (record == null || !record.IsLayout)
                continue;
            var layout = tr.GetObject(record.LayoutId, OpenMode.ForRead, false) as Layout;
            if (layout == null || !IncludeLayout(db, record, layout, scope))
                continue;
            if (allowedLayoutNames != null && !allowedLayoutNames.Contains(layout.LayoutName))
                continue;
            foreach (ObjectId id in record)
                yield return id;
        }
    }

    private static bool IncludeLayout(Database db, BlockTableRecord record, Layout layout, TitleBlockScanScope scope)
    {
        switch (scope)
        {
            case TitleBlockScanScope.ModelSpace: return layout.ModelType;
            case TitleBlockScanScope.PaperLayouts: return !layout.ModelType;
            case TitleBlockScanScope.CurrentSpace: return record.ObjectId == db.CurrentSpaceId;
            default: return true;
        }
    }

    private static string SafeHandle(Entity entity)
    {
        try { return entity.Handle.ToString(); }
        catch { return "?"; }
    }

    public static bool IsSpdsFormat(Entity entity)
    {
        try
        {
            var rx = entity.GetRXClass();
            return string.Equals(rx.DxfName, SpdsDxfName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(rx.Name, SpdsRxClassName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPotentialSpdsProxy(Entity entity)
    {
        try
        {
            var type = entity.GetType();
            if (type.Name.IndexOf("Proxy", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            foreach (var propertyName in new[] { "OriginalClassName", "ApplicationDescription", "OriginalDxfName" })
            {
                var text = type.GetProperty(propertyName)?.GetValue(entity, null)?.ToString() ?? "";
                if (text.IndexOf("SPDS", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf(SpdsRxClassName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}

/// <summary>
/// Данные одного объекта SPDSFORMAT. Reader не является обязательным для печати:
/// при отсутствии публичных CSoft-свойств сканер использует геометрию SPDSFORMAT.
/// </summary>
public sealed class SpdsFormatInfo
{
    public ObjectId ObjectId { get; set; }
    public string Handle { get; set; } = "";
    public string Format { get; set; } = "";
    public int Multiplicity { get; set; } = 1;
    public string Orientation { get; set; } = "";
    public double Scale { get; set; }
    public double DimScale { get; set; }
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public bool HasOriginX { get; set; }
    public bool HasOriginY { get; set; }
    public string Sheet { get; set; } = "";
    public string SheetCount { get; set; } = "";
    public string Designation { get; set; } = "";
    public string DrawingName { get; set; } = "";
    public string Stage { get; set; } = "";
    public bool PaperBound { get; set; }
    public string StdVersion { get; set; } = "";
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }
    public bool RequiresCustomPaper { get; set; }
    public string ReadMethod { get; set; } = "fallback";
    public string ReadError { get; set; } = "";

    public bool HasPublishedProperties
        => !string.IsNullOrWhiteSpace(Format)
           || !string.IsNullOrWhiteSpace(Sheet)
           || !string.IsNullOrWhiteSpace(Designation)
           || HasOriginX
           || HasOriginY;

    public string DisplayFormat
        => string.IsNullOrWhiteSpace(Format)
            ? ""
            : Multiplicity > 1 ? $"{Format}x{Multiplicity}" : Format;
}
