using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;


public enum LongPaperNameFormat
{

    Fraction = 0,

    Decimal = 1,
    Reserved2 = 2,
    Reserved3 = 3,
    Reserved4 = 4,
    Reserved5 = 5,
}


public enum TitleBlockSortMode
{

    DrawingNumber = 0,

    Spatial = 1
}

public sealed class DirectoryColumnSetting
{
    public string Key { get; set; } = "";
    public string Header { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Centered { get; set; } = true;
    public double Width { get; set; }

    public DirectoryColumnSetting Clone()
    {
        return new DirectoryColumnSetting
        {
            Key = Key,
            Header = Header,
            Enabled = Enabled,
            Centered = Centered,
            Width = Width
        };
    }
}

public sealed class AppSettings
{
    public string LastPlotDevice { get; set; } = "";

    /// <summary>
    /// nanoCAD (issue #13): the printer chosen in the "Принтер" list of the main window for PDF, PNG and JPG output;
    /// empty — chosen automatically (the internal PDF plotter of nanoCAD).
    /// </summary>
    public string PdfPrinter { get; set; } = "";
    public string LastStyleSheet { get; set; } = "";
    public double PaperMatchToleranceMm { get; set; } = 1.0;

    public Dictionary<string, string> CommandAliases { get; set; } = new();
    /// <summary>


    /// </summary>
    public bool RecognizeFourLineRectangleFrames { get; set; } = true;
    /// <summary>


    /// </summary>
    public bool HideFrameBoundaryWhenPlotting { get; set; } = false;
    public bool AddSequenceWhenPdfExists { get; set; } = false;

    public bool MergePdf { get; set; }

    public bool UseFileNameAsPdfBookmark { get; set; }

    public bool UseSheetTitleAsPdfBookmark { get; set; }

    public bool MergePdfByPaperSize { get; set; }

    public bool OpenOutputDirectoryAfterBatchPrint { get; set; } = true;

    public bool OpenMergedPdfAfterMerge { get; set; } = true;

    public bool KeepIndividualPdfsWhenMerging { get; set; }

    /// <summary>

    /// </summary>
    public bool GeneratePrintLog { get; set; }
    /// <summary>

    /// </summary>
    public bool ConvertTextToGeometryWhenPlotting { get; set; }
    /// <summary>

    /// </summary>
    public bool PlotTransparency { get; set; } = true;
    /// <summary>

    /// </summary>
    public bool PlotObjectLineweights { get; set; }
    /// <summary>


    /// </summary>
    public int RasterExportDpi { get; set; } = RasterExportSettings.DefaultDpi;
    /// <summary>

    /// </summary>
    public int RasterExportJpegQuality { get; set; } = RasterExportSettings.DefaultJpegQuality;
    public bool AddFileNameSequence { get; set; }
    public bool LeavePaperMargin { get; set; }
    public double PaperMarginMm { get; set; } = 1;

    public TitleBlockSortMode TitleBlockBatchSortMode { get; set; } = TitleBlockSortMode.DrawingNumber;
    public FrameSourceMode BatchFrameSourceMode { get; set; } = FrameSourceMode.Library;
    public int BatchFrameSourceModeVersion { get; set; } = 2;

    public bool SortOrderHorizontalFirst { get; set; }

    public LongPaperNameFormat LongPaperNameFormat { get; set; } = LongPaperNameFormat.Fraction;
    /// <summary>


    /// </summary>
    public double LongPaperSnapToleranceMm { get; set; } = 3.0;
    public string PdfFileNameSeparator { get; set; } = "_";
    public List<string> PdfFileNameFields { get; set; } = new() { "DrawingNumber", "Title" };
    /// <summary>


    /// </summary>
    public string PdfFileNamePattern { get; set; } = "";

    public int FileNameSequenceDigits { get; set; } = 2;

    public bool AutoFileNameSequenceDigits { get; set; }

    public int FileNameSequenceStartNumber { get; set; } = 1;
    public bool OpenExternalDwgForPlot { get; set; } = true;
    public double DirectoryIndexWidth { get; set; } = 900;
    public double DirectoryNumberWidth { get; set; } = 3200;
    public double DirectoryTitleWidth { get; set; } = 5200;
    public double DirectoryPaperWidth { get; set; } = 1200;
    public double DirectoryRemarkWidth { get; set; } = 1400;
    public double DirectoryRowHeight { get; set; } = 650;
    public double DirectoryTextHeightRatio { get; set; } = 0.42;
    public string DirectoryTextStyleName { get; set; } = "Arial";
    public int DirectoryColorIndex { get; set; } = 7;
    public int DirectorySettingsVersion { get; set; }
    public double DirectoryTextHeight { get; set; } = 450;
    public double DirectoryTextWidthFactor { get; set; } = 1.0;
    public string DirectoryLayerName { get; set; } = "0";
    public bool DirectoryDrawHeader { get; set; } = true;
    public bool DirectoryDrawGridLines { get; set; } = true;
    public List<DirectoryColumnSetting> DirectoryColumns { get; set; } = new();
    /// <summary>


    /// </summary>
    public List<double> CustomScales { get; set; } = new();
    public List<FrameTextRegionProfile> FrameTextRegionProfiles { get; set; } = new();
}

public static class AppSettingsStore
{

    private static readonly HashSet<string> AllowedFileNameSeparators = new(StringComparer.Ordinal)
    {
        "_", " ", "+", "-", ".", "~", "=", ""
    };

    public static string Path =>
        System.IO.Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                return LoadFrom(Path);
            }

            var backupPath = Path + ".bak";
            return File.Exists(backupPath) ? LoadFrom(backupPath) : Normalize(new AppSettings());
        }
        catch
        {
            var backupPath = Path + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    return LoadFrom(backupPath);
                }
                catch
                {
                }
            }

            return Normalize(new AppSettings());
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
        var json = JsonConvert.SerializeObject(Normalize(settings), Formatting.Indented);
        WriteAtomically(Path, json);
    }

    public static AppSettings Default()
    {
        return Normalize(new AppSettings());
    }

    private static AppSettings Normalize(AppSettings settings)
    {

        if (!Enum.IsDefined(typeof(FrameSourceMode), settings.BatchFrameSourceMode))
        {
            settings.BatchFrameSourceMode = FrameSourceMode.Library;
        }

        // В старых настройках режимом по умолчанию была только библиотека рамок.
        // Один раз переводим такие профили в общий режим, чтобы обычная кнопка
        // «Сканировать чертёж» находила также рамки CSoft СПДС.
        if (settings.BatchFrameSourceModeVersion < 1)
        {
            if (settings.BatchFrameSourceMode == FrameSourceMode.Library)
            {
                settings.BatchFrameSourceMode = FrameSourceMode.Auto;
            }

            settings.BatchFrameSourceModeVersion = 1;
        }

        // Общий режим сканирования заменён отдельными источниками форматов.
        if (settings.BatchFrameSourceMode == FrameSourceMode.Auto)
        {
            settings.BatchFrameSourceMode = FrameSourceMode.Library;
        }

        if (settings.BatchFrameSourceModeVersion < 2)
        {
            settings.BatchFrameSourceModeVersion = 2;
        }


        if (!Enum.IsDefined(typeof(TitleBlockSortMode), settings.TitleBlockBatchSortMode))
        {
            settings.TitleBlockBatchSortMode = TitleBlockSortMode.DrawingNumber;
        }

        if (settings.PaperMatchToleranceMm <= 0)
        {
            settings.PaperMatchToleranceMm = 1.0;
        }

        if (settings.LongPaperSnapToleranceMm <= 0)
        {
            settings.LongPaperSnapToleranceMm = 3.0;
        }


        settings.CommandAliases = CommandAliasManager.NormalizeAliases(settings.CommandAliases);

        settings.FrameTextRegionProfiles ??= new List<FrameTextRegionProfile>();
        settings.FrameTextRegionProfiles = settings.FrameTextRegionProfiles
            .Where(profile => profile != null && !string.IsNullOrWhiteSpace(profile.Key))
            .GroupBy(profile => profile.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var profile = group.Last();
                profile.Key = profile.Key.Trim();
                profile.TitleRegionMm ??= new LocalRectangle();
                profile.DrawingNumberRegionMm ??= new LocalRectangle();
                return profile;
            })
            .ToList();

        if (settings.DirectoryIndexWidth <= 0)
        {
            settings.DirectoryIndexWidth = 900;
        }

        if (settings.DirectoryNumberWidth <= 0)
        {
            settings.DirectoryNumberWidth = 3200;
        }

        if (settings.DirectoryTitleWidth <= 0)
        {
            settings.DirectoryTitleWidth = 5200;
        }

        if (settings.DirectoryPaperWidth <= 0)
        {
            settings.DirectoryPaperWidth = 1200;
        }

        if (settings.DirectoryRemarkWidth <= 0)
        {
            settings.DirectoryRemarkWidth = 1400;
        }

        if (settings.DirectoryRowHeight <= 0)
        {
            settings.DirectoryRowHeight = 650;
        }

        if (settings.DirectoryTextHeightRatio <= 0 || settings.DirectoryTextHeightRatio > 0.9)
        {
            settings.DirectoryTextHeightRatio = 0.42;
        }

        settings.PdfPrinter = settings.PdfPrinter?.Trim() ?? "";
        settings.DirectoryTextStyleName ??= "";
        var legacyDirectoryFont = string.Equals(settings.DirectoryTextStyleName.Trim(), "\u5B8B\u4F53", StringComparison.Ordinal)
                                  || string.Equals(settings.DirectoryTextStyleName.Trim(), "SimSun", StringComparison.OrdinalIgnoreCase);
        if (legacyDirectoryFont)
        {
            settings.DirectoryTextStyleName = "Arial";
            if (Math.Abs(settings.DirectoryTextWidthFactor - 0.7d) < 1e-6)
            {
                settings.DirectoryTextWidthFactor = 1.0d;
            }
        }
        if (settings.DirectoryColorIndex < 0 || settings.DirectoryColorIndex > 256)
        {
            settings.DirectoryColorIndex = 7;
        }


        if (settings.DirectorySettingsVersion < 2)
        {
            if (settings.DirectoryTextHeight <= 0 || Math.Abs(settings.DirectoryTextHeight - 300) < 1e-6)
            {
                settings.DirectoryTextHeight = 450;
            }
            settings.DirectorySettingsVersion = 2;
        }
        else if (settings.DirectoryTextHeight <= 0)
        {
            settings.DirectoryTextHeight = Math.Max(1, settings.DirectoryRowHeight * settings.DirectoryTextHeightRatio);
        }

        if (settings.DirectoryTextWidthFactor <= 0 || settings.DirectoryTextWidthFactor > 10)
        {
            settings.DirectoryTextWidthFactor = 1.0;
        }

        settings.DirectoryLayerName = string.IsNullOrWhiteSpace(settings.DirectoryLayerName)
            ? "0"
            : settings.DirectoryLayerName.Trim();

        settings.DirectoryColumns = NormalizeDirectoryColumns(settings);
        if (settings.DirectorySettingsVersion < 3)
        {
            ApplyDirectoryVersion3Defaults(settings);
            settings.DirectorySettingsVersion = 3;
        }
        SyncLegacyDirectoryWidths(settings);
        if (settings.PaperMarginMm == 0)
        {
            settings.PaperMarginMm = 1;
        }

        settings.PdfFileNameSeparator = NormalizeFileNameSeparator(settings.PdfFileNameSeparator);
        if (settings.PdfFileNameFields == null || settings.PdfFileNameFields.Count == 0)
        {
            settings.PdfFileNameFields = new List<string> { "DrawingNumber", "Title" };
        }
        else
        {

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new List<string>();
            foreach (var item in settings.PdfFileNameFields)
            {
                if (seen.Add(item))
                    deduped.Add(item);
            }
            settings.PdfFileNameFields = deduped;
        }


        if (!settings.PdfFileNameFields.Any(f => string.Equals(f, "DrawingNumber", StringComparison.OrdinalIgnoreCase)))
        {
            settings.PdfFileNameFields.Add("DrawingNumber");
        }



        if (string.IsNullOrWhiteSpace(settings.PdfFileNamePattern))
        {
            settings.PdfFileNamePattern = BuildLegacyFileNamePattern(
                settings.PdfFileNameFields,
                settings.PdfFileNameSeparator);
        }

        if (settings.FileNameSequenceDigits < 0 || settings.FileNameSequenceDigits > 10)
        {
            settings.FileNameSequenceDigits = 2;
        }
        if (settings.FileNameSequenceStartNumber < 0)
        {
            settings.FileNameSequenceStartNumber = 1;
        }

        settings.CustomScales = NormalizeCustomScales(settings.CustomScales);
        settings.RasterExportDpi = RasterExportSettings.NormalizeDpi(settings.RasterExportDpi);
        settings.RasterExportJpegQuality = RasterExportSettings.NormalizeJpegQuality(settings.RasterExportJpegQuality);

        return settings;
    }

    /// <summary>

    /// </summary>
    private static List<double> NormalizeCustomScales(List<double>? scales)
    {
        var normalized = new List<double>();
        if (scales != null)
        {
            foreach (var scale in scales)
            {
                if (scale <= 0
                    || normalized.Any(x => Math.Abs(x - scale) < 1e-6)
                    || PaperSizeDetector.BuiltInScales.Any(x => Math.Abs(x - scale) < 1e-6))
                {
                    continue;
                }

                normalized.Add(scale);
            }
        }

        normalized.Sort();
        return normalized;
    }

    private static List<DirectoryColumnSetting> NormalizeDirectoryColumns(AppSettings settings)
    {

        var defaults = CreateDefaultDirectoryColumns(settings);
        if (settings.DirectoryColumns == null || settings.DirectoryColumns.Count == 0)
        {
            return defaults;
        }

        var defaultByKey = defaults.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var normalized = new List<DirectoryColumnSetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in settings.DirectoryColumns)
        {
            if (column == null || !defaultByKey.TryGetValue(column.Key ?? "", out var fallback) || !seen.Add(fallback.Key))
            {
                continue;
            }

            normalized.Add(new DirectoryColumnSetting
            {
                Key = fallback.Key,
                Header = NormalizeDirectoryHeader(fallback.Key, column.Header, fallback.Header),
                Enabled = column.Enabled,
                Centered = column.Centered,
                Width = column.Width > 0 ? column.Width : fallback.Width
            });
        }


        foreach (var fallback in defaults.Where(x => !seen.Contains(x.Key)))
        {
            var added = fallback.Clone();
            added.Enabled = false;
            normalized.Add(added);
        }

        return normalized;
    }

    private static string NormalizeDirectoryHeader(string key, string? header, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(header) ? fallback : header!.Trim();
        var legacyChineseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sequence"] = "\u5E8F\u53F7",
            ["DrawingNumber"] = "\u56FE\u53F7",
            ["Title"] = "\u56FE\u540D",
            ["PaperName"] = "\u56FE\u5E45",
            ["Revision"] = "\u7248\u6B21",
            ["Date"] = "\u65E5\u671F",
            ["Phase"] = "\u8BBE\u8BA1\u9636\u6BB5",
            ["Info1"] = "\u4FE1\u606F1",
            ["Info2"] = "\u4FE1\u606F2"
        };

        return legacyChineseHeaders.TryGetValue(key, out var oldHeader)
               && string.Equals(value, oldHeader, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static List<DirectoryColumnSetting> CreateDefaultDirectoryColumns(AppSettings settings)
    {
        return new List<DirectoryColumnSetting>
        {
            new() { Key = "Sequence", Header = "№", Enabled = true, Centered = true, Width = settings.DirectoryIndexWidth },
            new() { Key = "DrawingNumber", Header = "Номер листа", Enabled = true, Centered = true, Width = settings.DirectoryNumberWidth },
            new() { Key = "Title", Header = "Наименование", Enabled = true, Centered = true, Width = settings.DirectoryTitleWidth },
            new() { Key = "PaperName", Header = "Формат", Enabled = true, Centered = true, Width = settings.DirectoryPaperWidth },
            new() { Key = "Revision", Header = "Редакция", Enabled = false, Centered = true, Width = 2800 },
            new() { Key = "Date", Header = "Дата", Enabled = false, Centered = true, Width = 2000 },
            new() { Key = "Phase", Header = "Стадия", Enabled = false, Centered = true, Width = 2400 },
            new() { Key = "Info1", Header = "Информация 1", Enabled = false, Centered = true, Width = 2000 },
            new() { Key = "Info2", Header = "Информация 2", Enabled = false, Centered = true, Width = 2000 }
        };
    }

    private static void ApplyDirectoryVersion3Defaults(AppSettings settings)
    {

        var order = new[] { "Sequence", "DrawingNumber", "Title", "PaperName", "Revision", "Date", "Phase", "Info1", "Info2" };
        var enabled = new HashSet<string>(
            new[] { "Sequence", "DrawingNumber", "Title", "PaperName" },
            StringComparer.OrdinalIgnoreCase);
        var byKey = settings.DirectoryColumns.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        settings.DirectoryColumns = order
            .Where(byKey.ContainsKey)
            .Select(key =>
            {
                var column = byKey[key];
                column.Enabled = enabled.Contains(key);
                column.Centered = true;
                return column;
            })
            .ToList();
        settings.DirectoryDrawHeader = true;
        settings.DirectoryDrawGridLines = true;
        settings.DirectoryLayerName = "0";
        settings.DirectoryTextStyleName = "Arial";
    }

    private static void SyncLegacyDirectoryWidths(AppSettings settings)
    {

        settings.DirectoryIndexWidth = FindDirectoryWidth(settings, "Sequence", settings.DirectoryIndexWidth);
        settings.DirectoryNumberWidth = FindDirectoryWidth(settings, "DrawingNumber", settings.DirectoryNumberWidth);
        settings.DirectoryTitleWidth = FindDirectoryWidth(settings, "Title", settings.DirectoryTitleWidth);
        settings.DirectoryPaperWidth = FindDirectoryWidth(settings, "PaperName", settings.DirectoryPaperWidth);
        settings.DirectoryRemarkWidth = Math.Max(1, settings.DirectoryRemarkWidth);
    }

    private static double FindDirectoryWidth(AppSettings settings, string key, double fallback)
    {
        return settings.DirectoryColumns.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))?.Width
            ?? Math.Max(1, fallback);
    }

    public static string NormalizeFileNameSeparator(string? separator)
    {
        return AllowedFileNameSeparators.Contains(separator ?? "_") ? separator ?? "_" : "_";
    }

    private static string BuildLegacyFileNamePattern(IEnumerable<string> fieldKeys, string separator)
    {
        var tokens = new List<string>();
        foreach (var key in fieldKeys)
        {
            var token = key switch
            {
                "DrawingNumber" => "A",
                "Revision" => "B",
                "Title" => "C",
                "Date" => "D",
                "Info1" => "E",
                "Info2" => "F",
                "Phase" => "G",
                "PaperName" => "T",
                "Sequence" => "N",
                _ => ""
            };
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }

        return tokens.Count > 0 ? string.Join(separator, tokens) : "A_C";
    }

    private static void WriteAtomically(string path, string contents)
    {
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";
        File.WriteAllText(tempPath, contents);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, backupPath, true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static AppSettings LoadFrom(string path)
    {
        var json = File.ReadAllText(path);
        return Normalize(JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings());
    }
}
