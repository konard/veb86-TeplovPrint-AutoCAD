using System;
using System.Collections.Generic;
using System.IO;

namespace ZwcadBatchPlot;


public sealed class FrameTextRegionProfile
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Область наименования в мм бумаги относительно правого нижнего угла рамки:
    /// X обычно отрицательный (влево), Y положительный (вверх).
    /// </summary>
    public LocalRectangle TitleRegionMm { get; set; } = new();

    /// <summary>
    /// Область номера листа в мм бумаги относительно правого нижнего угла рамки.
    /// </summary>
    public LocalRectangle DrawingNumberRegionMm { get; set; } = new();
}

public sealed class TitleBlockDefinition
{

    public const string DynamicRightBottomCoordinateMode = "FrameRightBottomDynamic";

    public string BlockName { get; set; } = "";
    public bool HasPrintRegion { get; set; }
    public string CoordinateMode { get; set; } = "Local";
    public LocalRectangle PrintRegion { get; set; } = new();
    public string PaperName { get; set; } = "";
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }
    public LocalRectangle TitleRegion { get; set; } = new();
    public LocalRectangle DrawingNumberRegion { get; set; } = new();
    public LocalRectangle DateRegion { get; set; } = new();
    public LocalRectangle RevisionRegion { get; set; } = new();
    public LocalRectangle PhaseRegion { get; set; } = new();
    public LocalRectangle Info1Region { get; set; } = new();
    public LocalRectangle Info2Region { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class TitleBlockLibrary
{
    public int Version { get; set; } = 2;
    public List<TitleBlockDefinition> Blocks { get; set; } = new();
}

public sealed class LocalRectangle
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }


    public double ActualWidth { get; set; }

    public double ActualHeight { get; set; }

    public double[]? CornerPoints { get; set; }

    public static LocalRectangle FromPoints(double x1, double y1, double x2, double y2)
    {
        return new LocalRectangle
        {
            MinX = Math.Min(x1, x2),
            MinY = Math.Min(y1, y2),
            MaxX = Math.Max(x1, x2),
            MaxY = Math.Max(y1, y2)
        };
    }

    public bool Contains(double x, double y, double tolerance = 1e-6)
    {
        return x >= MinX - tolerance
            && x <= MaxX + tolerance
            && y >= MinY - tolerance
            && y <= MaxY + tolerance;
    }


    public bool HasArea(double tolerance = 1e-6)
    {
        return Math.Abs(MaxX - MinX) > tolerance
            && Math.Abs(MaxY - MinY) > tolerance;
    }
}

public sealed class PlotJob
{
    public bool Selected { get; set; } = true;
    public bool IsManualWindow { get; set; }
    public string SourceFile { get; set; } = "";
    public string OutputFileName => Path.GetFileName(OutputPath);
    /// <summary>


    /// </summary>
    public string DisplayOutputFileName { get; set; } = "";
    public long SortPriority { get; set; }
    public string SpaceName { get; set; } = "";
    public bool IsPaperSpace { get; set; }
    /// <summary>


    /// </summary>
    public int LayoutTabOrder { get; set; } = int.MaxValue;
    public string BlockName { get; set; } = "";
    public string BlockHandle { get; set; } = "";
    public FrameSourceKind FrameSource { get; set; }
    public bool PreferSpatialSort { get; set; }
    public int MatchIndex { get; set; }
    public string DrawingNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string CadDrawingNumber { get; set; } = "";
    public string CadTitle { get; set; } = "";
    public string Date { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Info1 { get; set; } = "";
    public string Info2 { get; set; } = "";
    public string CadDate { get; set; } = "";
    public string CadRevision { get; set; } = "";
    public string CadPhase { get; set; } = "";
    public string CadInfo1 { get; set; } = "";
    public string CadInfo2 { get; set; } = "";
    public string PaperName { get; set; } = "";
    public string ScaleText { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string PaperSizeText { get; set; } = "";
    public string DetectionNote { get; set; } = "";
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    /// <summary>


    /// </summary>
    public bool UsesUserCoordinateSystem { get; set; }
    public double UcsMinX { get; set; }
    public double UcsMinY { get; set; }
    public double UcsMaxX { get; set; }
    public double UcsMaxY { get; set; }
    public double UcsOriginX { get; set; }
    public double UcsOriginY { get; set; }
    public double UcsOriginZ { get; set; }
    public double UcsXAxisX { get; set; } = 1d;
    public double UcsXAxisY { get; set; }
    public double UcsXAxisZ { get; set; }
    public double UcsYAxisX { get; set; }
    public double UcsYAxisY { get; set; } = 1d;
    public double UcsYAxisZ { get; set; }
    public string OutputPath { get; set; } = "";

    public bool LeavePaperMargin { get; set; }
    /// <summary>


    /// </summary>
    public double PaperMarginMm { get; set; } = 1d;

    public double EffectivePaperWidthMm { get; set; }

    public double EffectivePaperHeightMm { get; set; }


    public bool IsDcsWindow { get; set; }

    public bool RequireExactPaperSize { get; set; }

    public bool UseExactWindowScale { get; set; }

    public bool CustomPaperWasAdded { get; set; }
    /// <summary>


    /// </summary>
    public bool DetectedRequiresCustomPaperRegistration { get; set; }

    public bool RequiresCustomPaperRegistration { get; set; }

    public string StyleSheet { get; set; } = "";

    public double[]? CornerPoints { get; set; }
}

public enum TitleBlockScanScope
{
    AllSpaces,
    PaperLayouts,
    CurrentSpace,
    ModelSpace
}

public enum FrameSourceKind
{
    Library,
    Spds,
    Rectangle,
    Manual
}

public enum FrameSourceMode
{
    Auto,
    Library,
    Spds,
    Rectangles,
    Manual
}

public sealed class PaperDetection
{
    public string PaperName { get; set; } = "Неизвестно";
    public string ScaleText { get; set; } = "Неизвестно";
    public double ScaleValue { get; set; }
    public bool IsLong { get; set; }
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }
    public string Note { get; set; } = "";

    public bool RequiresCustomPaper { get; set; }
}
