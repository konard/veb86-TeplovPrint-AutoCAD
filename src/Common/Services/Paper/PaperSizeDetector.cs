using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZwcadBatchPlot;

public static class PaperSizeDetector
{
    private const double StandardPaperTolerance = 0.04d;
    private const double DefaultLongPaperShortSideTolerance = 0.04d;
    private const double LongPaperSnapTolerance = 0.08d;
    private const int LongPaperIncrementDenominator = 8;
    private const int MinimumLongPaperUnits = LongPaperIncrementDenominator + 1;

    public sealed class DetectionOptions
    {
        public double LongPaperShortSideTolerance { get; set; } = DefaultLongPaperShortSideTolerance;

        public double? LongPaperShortSideToleranceMm { get; set; }
        /// <summary>



        /// </summary>
        public double? StandardPaperToleranceMm { get; set; }

        public double? LongPaperSnapToleranceMm { get; set; }

        public bool AllowArbitraryLongSide { get; set; }

        public double? PreferredScaleValue { get; set; }

        public IReadOnlyList<double>? PreferredScaleValues { get; set; }

        public double PreferredPaperWidthMm { get; set; }

        public double PreferredPaperHeightMm { get; set; }
        /// <summary>

        /// </summary>
        public string PreferredPaperBaseName { get; set; } = "";
        /// <summary>


        /// </summary>
        public bool UseRectangleShortSideMatching { get; set; }
        /// <summary>



        /// </summary>
        public bool IncludeGenericDynamicTitleBlockPaper { get; set; }
        /// <summary>

        /// </summary>
        public IReadOnlyList<double>? ExtraScales { get; set; }
    }

    public static readonly DetectionOptions DefaultDetectionOptions = new();

    private sealed class PaperCandidate
    {
        public StandardPaper Paper { get; set; } = null!;
        public double Scale { get; set; }
        public double Score { get; set; }
        public bool IsLong { get; set; }
        public double PaperWidthMm { get; set; }
        public double PaperHeightMm { get; set; }
        public string Reason { get; set; } = "";
        public bool RequiresCustomPaper { get; set; }
    }

    private sealed class StandardPaper
    {
        public StandardPaper(string name, double shortSide, double longSide)
        {
            Name = name;
            ShortSide = shortSide;
            LongSide = longSide;
        }

        public string Name { get; }
        public double ShortSide { get; }
        public double LongSide { get; }
    }

    private static readonly StandardPaper[] Standards =
    {
        new("A0", 841, 1189),
        new("A1", 594, 841),
        new("A2", 420, 594),
        new("A3", 297, 420),
        new("A4", 210, 297)
    };

    private static readonly double[] CommonScales =
    {
        0.01d, 0.02d, 0.025d, 0.04d, 0.05d, 0.1d, 0.2d, 0.25d, 0.4d, 0.5d,
        1d, 2d, 5d, 10d, 20d, 25d, 30d, 40d, 50d, 60d, 75d, 80d, 90d, 100d,
        120d, 125d, 150d, 200d, 250d, 300d, 400d, 500d, 600d, 1000d
    };


    public static IReadOnlyList<double> BuiltInScales => CommonScales;

    /// <summary>



    /// </summary>
    public const string CustomPaperName = "Пользовательский";

    /// <summary>


    /// </summary>
    public const double AspectRatioMatchTolerance = 0.01d;

    /// <summary>


    /// </summary>
    public const double RectangleAspectRatioMatchTolerance = 0.001d;

    private static readonly int[] IntegerScales = { 1, 2, 4, 5, 8, 10, 20, 25, 50, 100, 200, 500, 1000 };

    /// <summary>



    /// </summary>
    public static int GuessScale(double drawingWidth, double drawingHeight)
    {
        var shortSide = Math.Min(drawingWidth, drawingHeight);
        if (shortSide <= 1e-6) return 0;

        var standardShorts = new[] { 210d, 297d, 420d, 594d, 841d };
        var bestScale = 0;
        var bestDistance = double.MaxValue;

        foreach (var scale in IntegerScales)
        {
            var paperShort = shortSide / scale;
            if (paperShort < 100 || paperShort > 900) continue;


            var nearest = standardShorts.OrderBy(s => Math.Abs(s - paperShort)).First();
            var distance = Math.Abs(nearest - paperShort);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestScale = scale;
            }
        }

        return bestScale;
    }

    /// <summary>


    /// </summary>
    public static (double PaperWidth, double PaperHeight, double Scale) CalculateCustomPaper(
        double drawingWidth, double drawingHeight, int scale)
    {
        var paperWidth = drawingWidth / scale;
        var paperHeight = drawingHeight / scale;
        return (paperWidth, paperHeight, scale);
    }

    /// <summary>




    /// </summary>
    public static IReadOnlyList<PaperDetection> DetectByAspectRatio(
        double width,
        double height,
        double maxAspectRatioError = AspectRatioMatchTolerance)
    {
        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var actualShort = Math.Min(actualWidth, actualHeight);
        var actualLong = Math.Max(actualWidth, actualHeight);
        if (actualShort <= 1e-9d)
        {
            return new PaperDetection[0];
        }

        var drawingRatio = actualLong / actualShort;
        var landscape = actualWidth >= actualHeight;
        var matches = new List<PaperDetection>();
        AddAspectRatioMatches(matches, actualShort, actualLong, drawingRatio, landscape, maxAspectRatioError, elongated: false);
        AddAspectRatioMatches(matches, actualShort, actualLong, drawingRatio, landscape, maxAspectRatioError, elongated: true);
        return matches;
    }

    /// <summary>


    /// </summary>
    private static void AddAspectRatioMatches(
        List<PaperDetection> matches,
        double actualShort,
        double actualLong,
        double drawingRatio,
        bool landscape,
        double maxAspectRatioError,
        bool elongated)
    {
        for (var i = Standards.Length - 1; i >= 0; i--)
        {
            var paper = Standards[i];
            var scale = actualShort / paper.ShortSide;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0d)
            {
                continue;
            }

            var restoredLongMm = actualLong / scale;
            double paperLongMm;
            double ratioError;
            if (elongated)
            {

                if (restoredLongMm <= paper.LongSide * 1.03d)
                {
                    continue;
                }

                paperLongMm = SnapLongSide(restoredLongMm, paper.LongSide);
                ratioError = Math.Abs(drawingRatio - paperLongMm / paper.ShortSide);
            }
            else
            {
                paperLongMm = paper.LongSide;
                ratioError = Math.Abs(drawingRatio - paper.LongSide / paper.ShortSide);
            }

            if (ratioError >= maxAspectRatioError)
            {
                continue;
            }

            var paperWidthMm = landscape ? paperLongMm : paper.ShortSide;
            var paperHeightMm = landscape ? paper.ShortSide : paperLongMm;
            var paperName = elongated
                ? paper.Name + "+" + LongPaperExtension(paperWidthMm, paperHeightMm, paper)
                : paper.Name;
            matches.Add(new PaperDetection
            {
                PaperName = paperName,
                ScaleValue = scale,
                ScaleText = ToScaleText(scale),
                IsLong = elongated,
                PaperWidthMm = paperWidthMm,
                PaperHeightMm = paperHeightMm,
                Note = elongated
                    ? $"{paper.Name}, удлинённый формат; ошибка пропорций {ratioError:0.####} (допуск {maxAspectRatioError}); "
                      + $"длинная сторона привязана к шагу 1/8: {paperLongMm:0.###} мм; масштаб {ToScaleText(scale)}"
                    : $"{paper.Name}, стандартный формат; ошибка пропорций {ratioError:0.####} (допуск {maxAspectRatioError}); "
                      + $"при масштабе {ToScaleText(scale)} восстановленная длинная сторона {restoredLongMm:0.###} мм"
            });
        }
    }

    /// <summary>


    /// </summary>
    public static int IndexOfPreferredAspectRatioPaper(
        double width,
        double height,
        IReadOnlyList<PaperDetection> papers)
    {
        var actualShort = Math.Min(Math.Abs(width), Math.Abs(height));
        var actualLong = Math.Max(Math.Abs(width), Math.Abs(height));
        if (papers.Count == 0 || actualShort <= 1e-9d)
        {
            return -1;
        }

        var drawingRatio = actualLong / actualShort;
        var bestIndex = 0;
        var bestError = double.MaxValue;
        var bestRoundness = double.MaxValue;
        var bestShortSide = double.MaxValue;
        for (var i = 0; i < papers.Count; i++)
        {
            var paper = papers[i];
            var paperShort = Math.Min(paper.PaperWidthMm, paper.PaperHeightMm);
            var paperLong = Math.Max(paper.PaperWidthMm, paper.PaperHeightMm);
            if (paperShort <= 1e-9d)
            {
                continue;
            }

            var error = Math.Abs(drawingRatio - paperLong / paperShort);
            var roundness = ScaleIntegerDeviation(paper.ScaleValue);
            if (error < bestError - 1e-12d
                || (Math.Abs(error - bestError) <= 1e-12d && roundness < bestRoundness - 1e-9d)
                || (Math.Abs(error - bestError) <= 1e-12d
                    && Math.Abs(roundness - bestRoundness) <= 1e-9d
                    && paperShort < bestShortSide))
            {
                bestError = error;
                bestRoundness = roundness;
                bestShortSide = paperShort;
                bestIndex = i;
            }
        }

        return bestIndex;
    }


    private static double ScaleIntegerDeviation(double scale)
    {
        var reference = scale >= 1d ? scale : 1d / Math.Max(scale, 1e-9d);
        var rounded = Math.Round(reference);
        if (rounded < 1d)
        {
            return Math.Abs(reference - 1d);
        }

        return Math.Abs(reference - rounded) / rounded;
    }

    public static PaperDetection Detect(double width, double height)
    {
        return Detect(width, height, DefaultDetectionOptions);
    }

    public static PaperDetection Detect(double width, double height, DetectionOptions options)
    {
        var candidates = GetCandidateDetails(width, height, options);
        if (candidates.Count == 0)
        {
            return FallbackDetect(Math.Abs(width), Math.Abs(height));
        }

        var detected = ToDetection(candidates[0]);
        return options.IncludeGenericDynamicTitleBlockPaper
            ? ToGenericDynamicTitleBlockPaper(detected)
            : detected;
    }

    public static IReadOnlyList<PaperDetection> DetectCandidates(double width, double height)
    {
        return DetectCandidates(width, height, DefaultDetectionOptions);
    }

    public static IReadOnlyList<PaperDetection> DetectCandidates(double width, double height, DetectionOptions options)
    {
        var details = GetCandidateDetails(width, height, options);
        if (details.Count == 0)
        {
            return new PaperDetection[0];
        }

        var scoreLimit = Math.Min(0.04d, Math.Max(0.015d, details[0].Score + 0.015d));
        var filtered = details
            .Where(candidate => candidate.Score <= scoreLimit)
            .GroupBy(
                candidate => $"{candidate.Paper.Name}|{candidate.IsLong}|{candidate.Scale:0.########}|{candidate.PaperWidthMm:0.##}|{candidate.PaperHeightMm:0.##}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(12)
            .Select(ToDetection)
            .ToList();


        var indexOf1To100 = options.PreferredScaleValues == null && !options.PreferredScaleValue.HasValue
            ? filtered.FindIndex(x => Math.Abs(x.ScaleValue - 100d) < 0.001)
            : -1;
        if (indexOf1To100 > 0)
        {
            var preferred = filtered[indexOf1To100];
            filtered.RemoveAt(indexOf1To100);
            filtered.Insert(0, preferred);
        }

        if (!options.IncludeGenericDynamicTitleBlockPaper)
        {
            return filtered;
        }


        return filtered
            .Select(ToGenericDynamicTitleBlockPaper)
            .GroupBy(
                paper => $"{paper.PaperName}|{paper.ScaleValue:0.########}|{paper.PaperWidthMm:0.##}|{paper.PaperHeightMm:0.##}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(12)
            .ToList();
    }

    /// <summary>


    /// </summary>
    public static IReadOnlyList<PaperDetection> DetectCandidatesOrFallback(
        double width,
        double height,
        DetectionOptions options)
    {
        var candidates = DetectCandidates(width, height, options);
        if (candidates.Count > 0)
        {
            return candidates;
        }



        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var scale = GuessScale(actualWidth, actualHeight);
        if (scale <= 0)
        {
            scale = 1;
        }

        return new[]
        {
            new PaperDetection
            {
                PaperName = "Пользовательский",
                PaperWidthMm = actualWidth / scale,
                PaperHeightMm = actualHeight / scale,
                ScaleValue = scale,
                ScaleText = ToScaleText(scale),
                RequiresCustomPaper = true,
                Note = $"Стандартный или модульный формат не найден; создана пользовательская бумага по фактическим размерам (расчётный масштаб {ToScaleText(scale)})"
            }
        };
    }


    public static string FormatOption(PaperDetection paper)
    {
        return $"{paper.PaperName} | {paper.PaperWidthMm:0.##}×{paper.PaperHeightMm:0.##}mm | {paper.ScaleText}";
    }

    /// <summary>



    /// </summary>
    public static DetectionOptions CreateTitleBlockBatchOptions(double paperMatchToleranceMm, bool isPaperSpace, double? longPaperSnapToleranceMm = null, IReadOnlyList<double>? customScales = null)
    {
        return new DetectionOptions
        {
            AllowArbitraryLongSide = true,
            StandardPaperToleranceMm = Math.Max(0d, paperMatchToleranceMm),
            LongPaperShortSideToleranceMm = Math.Max(0d, paperMatchToleranceMm),
            LongPaperSnapToleranceMm = longPaperSnapToleranceMm,
            PreferredScaleValue = isPaperSpace ? 1d : 100d,
            ExtraScales = customScales
        };
    }

    /// <summary>




    /// </summary>
    public static bool TryDetectTitleBlockAtArbitraryScale(
        double frameWidth,
        double frameHeight,
        string recordedPaperName,
        double recordedPaperWidthMm,
        double recordedPaperHeightMm,
        double paperMatchToleranceMm,
        double? longPaperSnapToleranceMm,
        out PaperDetection detected)
    {
        detected = new PaperDetection();
        var actualWidth = Math.Abs(frameWidth);
        var actualHeight = Math.Abs(frameHeight);
        var paperWidth = Math.Abs(recordedPaperWidthMm);
        var paperHeight = Math.Abs(recordedPaperHeightMm);
        if (actualWidth <= 1e-9d
            || actualHeight <= 1e-9d
            || paperWidth <= 1e-9d
            || paperHeight <= 1e-9d)
        {
            return false;
        }

        var actualShort = Math.Min(actualWidth, actualHeight);
        var actualLong = Math.Max(actualWidth, actualHeight);
        var recordedShort = Math.Min(paperWidth, paperHeight);
        var scale = actualShort / recordedShort;
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0d)
        {
            return false;
        }

        var name = string.IsNullOrWhiteSpace(recordedPaperName)
            ? CustomPaperName
            : recordedPaperName.Trim();
        var scaleText = ToScaleText(scale);



        if (string.Equals(name, CustomPaperName, StringComparison.OrdinalIgnoreCase))
        {
            detected = new PaperDetection
            {
                PaperName = name,
                ScaleValue = scale,
                ScaleText = scaleText,
                PaperWidthMm = recordedPaperWidthMm,
                PaperHeightMm = recordedPaperHeightMm,
                RequiresCustomPaper = true,
                Note = $"Пользовательская бумага из библиотеки рамок: {paperWidth:0.##} x {paperHeight:0.##} мм; масштаб по короткой стороне: {scaleText}"
            };
            return true;
        }

        var toleranceMm = Math.Max(0d, paperMatchToleranceMm);
        var standard = ResolveRecordedTitleBlockPaper(name, recordedShort, toleranceMm);
        if (standard == null)
        {

            detected = new PaperDetection
            {
                PaperName = name,
                ScaleValue = scale,
                ScaleText = scaleText,
                IsLong = name.IndexOf('+') > 0,
                PaperWidthMm = recordedPaperWidthMm,
                PaperHeightMm = recordedPaperHeightMm,
                RequiresCustomPaper = true,
                Note = $"Бумага из библиотеки не соответствует A0–A4; используется размер {paperWidth:0.##} x {paperHeight:0.##} мм; масштаб по короткой стороне: {scaleText}"
            };
            return true;
        }

        var restoredLongMm = actualLong / scale;
        var landscape = actualWidth >= actualHeight;
        if (restoredLongMm <= standard.LongSide + toleranceMm)
        {
            detected = new PaperDetection
            {
                PaperName = standard.Name,
                ScaleValue = scale,
                ScaleText = scaleText,
                PaperWidthMm = landscape ? standard.LongSide : standard.ShortSide,
                PaperHeightMm = landscape ? standard.ShortSide : standard.LongSide,
                Note = $"Стандартный формат {standard.Name}; по записанной короткой стороне {recordedShort:0.###} мм определён масштаб {scaleText}; восстановленная длинная сторона {restoredLongMm:0.###} мм"
            };
            return true;
        }

        var snappedLongMm = SnapLongSide(restoredLongMm, standard.LongSide);
        var snapErrorMm = Math.Abs(restoredLongMm - snappedLongMm);
        var snapToleranceMm = Math.Max(0d, longPaperSnapToleranceMm ?? toleranceMm);
        var canSnap = snapErrorMm <= snapToleranceMm;
        var outputLongMm = canSnap ? snappedLongMm : restoredLongMm;
        var candidate = new PaperCandidate
        {
            Paper = standard,
            Scale = scale,
            Score = RelativeError(restoredLongMm, outputLongMm),
            IsLong = true,
            RequiresCustomPaper = !canSnap,
            PaperWidthMm = landscape ? outputLongMm : standard.ShortSide,
            PaperHeightMm = landscape ? standard.ShortSide : outputLongMm,
            Reason = canSnap
                ? $"По короткой стороне {recordedShort:0.###} мм определён масштаб {scaleText}; длинная сторона привязана к шагу 1/8: {snappedLongMm:0.###} мм (ошибка {snapErrorMm:0.###} мм, допуск {snapToleranceMm:0.###} мм)"
                : $"По короткой стороне {recordedShort:0.###} мм определён масштаб {scaleText}; длинная сторона создана по фактическому размеру {restoredLongMm:0.######} мм"
        };
        detected = ToDetection(candidate);
        return true;
    }

    /// <summary>


    /// </summary>
    public static DetectionOptions CreateRectangleBatchOptions(double paperMatchToleranceMm, bool isPaperSpace, double? longPaperSnapToleranceMm = null, IReadOnlyList<double>? customScales = null)
    {
        var toleranceMm = Math.Max(0d, paperMatchToleranceMm);
        return new DetectionOptions
        {
            UseRectangleShortSideMatching = true,
            LongPaperShortSideToleranceMm = toleranceMm,
            LongPaperSnapToleranceMm = longPaperSnapToleranceMm,
            PreferredScaleValues = isPaperSpace
                ? new[] { 1d, 100d }
                : new[] { 100d, 1d },
            ExtraScales = customScales
        };
    }

    /// <summary>



    /// </summary>
    public static IReadOnlyList<PaperDetection> DetectRectangleBatchCandidates(
        double width,
        double height,
        DetectionOptions options)
    {
        // Универсальный сканер прямоугольных рамок обязан использовать только
        // встроенные и явно добавленные пользователем масштабы. Прежний запасной
        // поиск лишь по пропорциям вычислял произвольные значения вроде 1:5.474
        // и создавал ложные удлинённые форматы наподобие A3+97/2.
        return DetectCandidates(width, height, options);
    }

    /// <summary>

    /// </summary>
    public static IReadOnlyList<PaperDetection> DetectRectangleBatchAspectRatioCandidates(
        double width,
        double height)
    {
        return OrderAspectRatioCandidatesForRectangleBatch(
            width,
            height,
            ExpandStandardASeriesAspectCandidates(
                width,
                height,
                DetectByAspectRatio(width, height, RectangleAspectRatioMatchTolerance)));
    }

    /// <summary>
    /// Возвращает все стандартные форматы A-серии для ручного выбора.
    /// Масштаб каждого варианта рассчитывается по фактической короткой стороне
    /// выбранного формата, поэтому A1 и A3 не могут получить один и тот же масштаб.
    /// </summary>
    public static IReadOnlyList<PaperDetection> CreateStandardFormatCandidates(double width, double height)
    {
        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var actualShort = Math.Min(actualWidth, actualHeight);
        if (actualShort <= 1e-9d)
        {
            return Array.Empty<PaperDetection>();
        }

        var landscape = actualWidth >= actualHeight;
        return Standards
            .Select(paper =>
            {
                var scale = actualShort / paper.ShortSide;
                return new PaperDetection
                {
                    PaperName = paper.Name,
                    ScaleValue = scale,
                    ScaleText = ToScaleText(scale),
                    PaperWidthMm = landscape ? paper.LongSide : paper.ShortSide,
                    PaperHeightMm = landscape ? paper.ShortSide : paper.LongSide,
                    Note = $"Формат {paper.Name} выбран вручную; масштаб пересчитан по короткой стороне: {ToScaleText(scale)}"
                };
            })
            .ToList();
    }

    /// <summary>


    /// </summary>
    private static IReadOnlyList<PaperDetection> ExpandStandardASeriesAspectCandidates(
        double width,
        double height,
        IReadOnlyList<PaperDetection> aspectMatches)
    {
        if (aspectMatches.Count == 0)
        {
            return aspectMatches;
        }

        var hasStandardA = false;
        foreach (var match in aspectMatches)
        {
            if (match.IsLong)
            {
                continue;
            }

            foreach (var standard in Standards)
            {
                if (string.Equals(standard.Name, match.PaperName, StringComparison.OrdinalIgnoreCase))
                {
                    hasStandardA = true;
                    break;
                }
            }

            if (hasStandardA)
            {
                break;
            }
        }

        if (!hasStandardA)
        {
            return aspectMatches;
        }

        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var actualShort = Math.Min(actualWidth, actualHeight);
        if (actualShort <= 1e-9d)
        {
            return aspectMatches;
        }

        var landscape = actualWidth >= actualHeight;
        var byName = new Dictionary<string, PaperDetection>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in aspectMatches)
        {
            if (!byName.ContainsKey(match.PaperName))
            {
                byName[match.PaperName] = match;
            }
        }

        for (var i = Standards.Length - 1; i >= 0; i--)
        {
            var paper = Standards[i];
            if (byName.ContainsKey(paper.Name))
            {
                continue;
            }

            var scale = actualShort / paper.ShortSide;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0d)
            {
                continue;
            }

            byName[paper.Name] = new PaperDetection
            {
                PaperName = paper.Name,
                ScaleValue = scale,
                ScaleText = ToScaleText(scale),
                IsLong = false,
                PaperWidthMm = landscape ? paper.LongSide : paper.ShortSide,
                PaperHeightMm = landscape ? paper.ShortSide : paper.LongSide,
                Note = $"Стандартный формат {paper.Name} (пересчёт в той же серии); масштаб {ToScaleText(scale)}"
            };
        }

        var expanded = new List<PaperDetection>(byName.Count);
        for (var i = Standards.Length - 1; i >= 0; i--)
        {
            if (byName.TryGetValue(Standards[i].Name, out var standardMatch) && !standardMatch.IsLong)
            {
                expanded.Add(standardMatch);
            }
        }

        foreach (var match in aspectMatches)
        {
            if (!match.IsLong)
            {
                continue;
            }

            var already = false;
            foreach (var item in expanded)
            {
                if (string.Equals(item.PaperName, match.PaperName, StringComparison.OrdinalIgnoreCase))
                {
                    already = true;
                    break;
                }
            }

            if (!already)
            {
                expanded.Add(match);
            }
        }

        return expanded;
    }

    /// <summary>

    /// </summary>
    private static IReadOnlyList<PaperDetection> OrderAspectRatioCandidatesForRectangleBatch(
        double width,
        double height,
        IReadOnlyList<PaperDetection> aspectMatches)
    {
        if (aspectMatches.Count == 0)
        {
            return aspectMatches;
        }

        var preferredIndex = IndexOfPreferredAspectRatioPaper(width, height, aspectMatches);
        if (preferredIndex <= 0)
        {
            return aspectMatches.Count <= 12
                ? aspectMatches
                : aspectMatches.Take(12).ToList();
        }

        var ordered = new List<PaperDetection>(Math.Min(12, aspectMatches.Count))
        {
            aspectMatches[preferredIndex]
        };
        for (var i = 0; i < aspectMatches.Count && ordered.Count < 12; i++)
        {
            if (i != preferredIndex)
            {
                ordered.Add(aspectMatches[i]);
            }
        }

        return ordered;
    }

    private static List<PaperCandidate> GetCandidateDetails(double width, double height, DetectionOptions options)
    {
        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var candidates = new List<PaperCandidate>();
        var scales = MergeScales(options.ExtraScales);
        foreach (var paper in Standards)
        {
            foreach (var scale in scales)
            {
                AddCandidate(candidates, paper, scale, actualWidth, actualHeight, options);
            }
        }

        return candidates

            .OrderBy(x => MatchesPreferredPaper(x, options) ? 0 : 1)

            .ThenBy(x => MatchesPreferredPaperBase(x, options) ? 0 : 1)

            .ThenBy(x => GetScalePreferenceRank(x.Scale, options))
            .ThenBy(x => x.Score)
            .ThenBy(x => x.IsLong ? 0 : 1)
            .ThenByDescending(x => x.Scale)
            .ThenBy(x => Array.IndexOf(Standards, x.Paper))
            .ToList();
    }

    private static PaperDetection ToDetection(PaperCandidate best)
    {
        var paperName = best.IsLong
            ? best.Paper.Name + "+" + (best.RequiresCustomPaper
                ? ContinuousLongPaperExtension(best.PaperWidthMm, best.PaperHeightMm, best.Paper)
                : LongPaperExtension(best.PaperWidthMm, best.PaperHeightMm, best.Paper))
            : best.Paper.Name;
        var paperSizeText = $"{best.PaperWidthMm:0.##} x {best.PaperHeightMm:0.##} mm";
        return new PaperDetection
        {
            PaperName = paperName,
            ScaleValue = best.Scale,
            ScaleText = ToScaleText(best.Scale),
            IsLong = best.IsLong,
            PaperWidthMm = best.PaperWidthMm,
            PaperHeightMm = best.PaperHeightMm,
            RequiresCustomPaper = best.RequiresCustomPaper,
            Note = best.IsLong
                ? $"{best.Paper.Name}, удлинённый формат; {best.Reason}; бумага {paperSizeText}"
                : $"{best.Paper.Name}, стандартный формат; {best.Reason}; бумага {paperSizeText}"
        };
    }

    /// <summary>

    /// </summary>
    private static PaperDetection ToGenericDynamicTitleBlockPaper(PaperDetection paper)
    {
        var plusIndex = paper.PaperName.IndexOf('+');
        var baseName = plusIndex > 0 ? paper.PaperName.Substring(0, plusIndex) : paper.PaperName;
        if (!Standards.Any(standard => string.Equals(standard.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return paper;
        }

        return new PaperDetection
        {
            PaperName = baseName + "+",
            ScaleText = paper.ScaleText,
            ScaleValue = paper.ScaleValue,
            IsLong = true,
            PaperWidthMm = paper.PaperWidthMm,
            PaperHeightMm = paper.PaperHeightMm,
            RequiresCustomPaper = paper.RequiresCustomPaper,
            Note = $"Растягиваемая рамка записана как {baseName}+; фактическая бумага определяется при сканировании"
        };
    }

    public static (double Width, double Height) GetDefaultSize(string paperName, double currentWidth = 0, double currentHeight = 0)
    {
        var plusIndex = paperName.IndexOf('+');
        var baseName = plusIndex >= 0 ? paperName.Substring(0, plusIndex) : paperName;
        var standard = Standards.FirstOrDefault(x => string.Equals(x.Name, baseName, StringComparison.OrdinalIgnoreCase));
        if (standard == null)
        {
            return (Math.Max(currentWidth, 1), Math.Max(currentHeight, 1));
        }

        var landscape = currentWidth <= 0 || currentHeight <= 0 || currentWidth >= currentHeight;
        if (plusIndex < 0)
        {
            return landscape ? (standard.LongSide, standard.ShortSide) : (standard.ShortSide, standard.LongSide);
        }

        var measuredLong = Math.Max(currentWidth, currentHeight);
        var longSide = measuredLong > standard.LongSide
            ? SnapLongSide(measuredLong, standard.LongSide)
            : standard.LongSide * MinimumLongPaperUnits / (double)LongPaperIncrementDenominator;
        return landscape ? (longSide, standard.ShortSide) : (standard.ShortSide, longSide);
    }

    private static void AddCandidate(List<PaperCandidate> candidates, StandardPaper paper, double scale, double actualWidth, double actualHeight, DetectionOptions options)
    {
        var widthMm = actualWidth / scale;
        var heightMm = actualHeight / scale;

        AddOrientation(candidates, paper, scale, widthMm, heightMm, actualWidth, actualHeight, paper.LongSide, paper.ShortSide, options);
        AddOrientation(candidates, paper, scale, widthMm, heightMm, actualWidth, actualHeight, paper.ShortSide, paper.LongSide, options);
    }

    private static void AddOrientation(
        List<PaperCandidate> candidates,
        StandardPaper paper,
        double scale,
        double widthMm,
        double heightMm,
        double actualWidth,
        double actualHeight,
        double standardWidth,
        double standardHeight,
        DetectionOptions options)
    {
        if (options.UseRectangleShortSideMatching)
        {
            AddRectangleShortSideCandidate(
                candidates,
                paper,
                scale,
                widthMm,
                heightMm,
                actualWidth,
                actualHeight,
                options);
            return;
        }

        var widthError = RelativeError(widthMm, standardWidth);
        var heightError = RelativeError(heightMm, standardHeight);
        var widthErrorMm = Math.Abs(widthMm - standardWidth);
        var heightErrorMm = Math.Abs(heightMm - standardHeight);
        var matchesStandardPaper = options.StandardPaperToleranceMm.HasValue
            ? widthErrorMm <= options.StandardPaperToleranceMm.Value
              && heightErrorMm <= options.StandardPaperToleranceMm.Value
            : widthError <= StandardPaperTolerance
              && heightError <= StandardPaperTolerance;
        if (matchesStandardPaper)
        {


            var reason = options.StandardPaperToleranceMm.HasValue
                ? $"Сопоставление по стандартному масштабу: ошибка ширины {widthErrorMm:0.###} мм, высоты {heightErrorMm:0.###} мм, допуск {options.StandardPaperToleranceMm.Value:0.###} мм; размер CAD {actualWidth:0.##} x {actualHeight:0.##}"
                : $"Сопоставление по стандартному масштабу: ошибка ширины {widthError:P1}, высоты {heightError:P1}; размер CAD {actualWidth:0.##} x {actualHeight:0.##}";
            candidates.Add(new PaperCandidate
            {
                Paper = paper,
                Scale = scale,
                Score = Math.Max(widthError, heightError),
                IsLong = false,
                PaperWidthMm = standardWidth,
                PaperHeightMm = standardHeight,
                Reason = reason
            });
        }

        var expectedShort = Math.Min(standardWidth, standardHeight);
        var expectedLong = Math.Max(standardWidth, standardHeight);
        var actualShort = Math.Min(widthMm, heightMm);
        var actualLong = Math.Max(widthMm, heightMm);
        var shortError = RelativeError(actualShort, expectedShort);
        var isLong = actualLong > expectedLong * 1.03;
        if (options.AllowArbitraryLongSide)
        {
            var shortErrorMm = Math.Abs(actualShort - expectedShort);
            var shortToleranceMm = options.LongPaperShortSideToleranceMm ?? 0d;



            if (actualLong > expectedLong + shortToleranceMm && shortErrorMm <= shortToleranceMm)
            {
                var snappedLongMm = SnapLongSide(actualLong, expectedLong);
                var snapErrorMm = Math.Abs(actualLong - snappedLongMm);
                var snapToleranceMm = options.LongPaperSnapToleranceMm;
                var canSnap = snapToleranceMm.HasValue && snapErrorMm <= snapToleranceMm.Value;
                var isLandscape = widthMm >= heightMm;
                candidates.Add(new PaperCandidate
                {
                    Paper = paper,
                    Scale = scale,
                    Score = shortError,
                    IsLong = true,
                    RequiresCustomPaper = !canSnap,
                    PaperWidthMm = canSnap
                        ? (isLandscape ? snappedLongMm : expectedShort)
                        : widthMm,
                    PaperHeightMm = canSnap
                        ? (isLandscape ? expectedShort : snappedLongMm)
                        : heightMm,
                    Reason = canSnap
                        ? $"Ошибка короткой стороны относительно {expectedShort:0.##} мм: {shortErrorMm:0.###} мм (допуск {shortToleranceMm:0.###} мм); длинная сторона привязана к шагу 1/8: {snappedLongMm:0.###} мм (ошибка {snapErrorMm:0.###} мм, допуск {snapToleranceMm.GetValueOrDefault():0.###} мм)"
                        : $"Ошибка короткой стороны относительно {expectedShort:0.##} мм: {shortErrorMm:0.###} мм (допуск {shortToleranceMm:0.###} мм); длинная сторона создана по фактическому размеру {actualLong:0.######} мм"
                });
            }

            return;
        }

        if (!isLong || shortError > options.LongPaperShortSideTolerance)
        {
            return;
        }

        var snappedLong = SnapLongSide(actualLong, expectedLong);
        var longError = RelativeError(actualLong, snappedLong);
        if (longError > LongPaperSnapTolerance)
        {
            return;
        }

        var landscape = widthMm >= heightMm;
        candidates.Add(new PaperCandidate
        {
            Paper = paper,
            Scale = scale,
            Score = shortError + longError * 0.35,
            IsLong = true,
            PaperWidthMm = landscape ? snappedLong : expectedShort,
            PaperHeightMm = landscape ? expectedShort : snappedLong,
            Reason = $"Короткая сторона зафиксирована на {expectedShort:0.##} мм; длинная сторона по шагу 1/8 стандартной стороны: {snappedLong:0.##} мм; ошибки: короткая {shortError:P1}, длинная {longError:P1}; размер CAD {actualWidth:0.##} x {actualHeight:0.##}"
        });
    }

    /// <summary>


    /// </summary>
    private static void AddRectangleShortSideCandidate(
        List<PaperCandidate> candidates,
        StandardPaper paper,
        double scale,
        double widthMm,
        double heightMm,
        double actualWidth,
        double actualHeight,
        DetectionOptions options)
    {
        var actualShort = Math.Min(widthMm, heightMm);
        var actualLong = Math.Max(widthMm, heightMm);
        var toleranceMm = options.LongPaperShortSideToleranceMm ?? 0d;
        var shortErrorMm = Math.Abs(actualShort - paper.ShortSide);
        if (shortErrorMm > toleranceMm)
        {
            return;
        }

        var longErrorFromStandardMm = Math.Abs(actualLong - paper.LongSide);
        var landscape = widthMm >= heightMm;
        if (longErrorFromStandardMm <= toleranceMm)
        {
            candidates.Add(new PaperCandidate
            {
                Paper = paper,
                Scale = scale,
                Score = Math.Max(
                    RelativeError(actualShort, paper.ShortSide),
                    RelativeError(actualLong, paper.LongSide)),
                IsLong = false,
                PaperWidthMm = landscape ? paper.LongSide : paper.ShortSide,
                PaperHeightMm = landscape ? paper.ShortSide : paper.LongSide,
                Reason = $"Ошибки короткой стороны {shortErrorMm:0.###} мм и длинной стороны {longErrorFromStandardMm:0.###} мм находятся в допуске {toleranceMm:0.###} мм; размер CAD {actualWidth:0.##} x {actualHeight:0.##}"
            });
            return;
        }


        if (actualLong + toleranceMm < paper.LongSide)
        {
            return;
        }

        var snappedLong = SnapLongSide(actualLong, paper.LongSide);
        var modularErrorMm = Math.Abs(actualLong - snappedLong);

        var snapToleranceMm = options.LongPaperSnapToleranceMm ?? toleranceMm;
        if (modularErrorMm <= snapToleranceMm)
        {
            candidates.Add(new PaperCandidate
            {
                Paper = paper,
                Scale = scale,
                Score = RelativeError(actualShort, paper.ShortSide)
                    + RelativeError(actualLong, snappedLong) * 0.35d,
                IsLong = true,
                PaperWidthMm = landscape ? snappedLong : paper.ShortSide,
                PaperHeightMm = landscape ? paper.ShortSide : snappedLong,
                Reason = $"Ошибка короткой стороны {shortErrorMm:0.###} мм; длинная сторона соответствует шагу 1/8: {snappedLong:0.###} мм; модульная ошибка {modularErrorMm:0.###} мм (допуск {snapToleranceMm:0.###} мм)"
            });
            return;
        }

        candidates.Add(new PaperCandidate
        {
            Paper = paper,
            Scale = scale,
            Score = RelativeError(actualShort, paper.ShortSide),
            IsLong = true,
            RequiresCustomPaper = true,
            PaperWidthMm = widthMm,
            PaperHeightMm = heightMm,
            Reason = $"Ошибка короткой стороны {shortErrorMm:0.###} мм (допуск {toleranceMm:0.###} мм); отклонение длинной стороны от ближайшего шага 1/8: {modularErrorMm:0.###} мм; создано по фактическому размеру {actualLong:0.######} мм"
        });
    }

    private static double SnapLongSide(double measuredLong, double standardLong)
    {
        var units = Math.Max(MinimumLongPaperUnits, (int)Math.Round(measuredLong / standardLong * LongPaperIncrementDenominator, MidpointRounding.AwayFromZero));
        return standardLong * units / (double)LongPaperIncrementDenominator;
    }

    /// <summary>


    /// </summary>
    public static bool ArePhysicalSizesCompatible(
        double widthMmA,
        double heightMmA,
        double widthMmB,
        double heightMmB,
        double? toleranceMm = null)
    {
        if (widthMmA <= 0d || heightMmA <= 0d || widthMmB <= 0d || heightMmB <= 0d)
        {
            return false;
        }

        var tol = Math.Max(0.05d, toleranceMm ?? 1d);
        var direct =
            Math.Abs(widthMmA - widthMmB) <= tol
            && Math.Abs(heightMmA - heightMmB) <= tol;
        if (direct)
        {
            return true;
        }

        return Math.Abs(widthMmA - heightMmB) <= tol
            && Math.Abs(heightMmA - widthMmB) <= tol;
    }

    /// <summary>


    /// </summary>
    private static bool MatchesPreferredPaper(PaperCandidate candidate, DetectionOptions options)
    {
        if (options.PreferredPaperWidthMm <= 0d || options.PreferredPaperHeightMm <= 0d)
        {
            return false;
        }

        return ArePhysicalSizesCompatible(
            candidate.PaperWidthMm,
            candidate.PaperHeightMm,
            options.PreferredPaperWidthMm,
            options.PreferredPaperHeightMm,
            options.LongPaperShortSideToleranceMm);
    }

    private static bool MatchesPreferredPaperBase(PaperCandidate candidate, DetectionOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.PreferredPaperBaseName)
            && string.Equals(
                candidate.Paper.Name,
                options.PreferredPaperBaseName,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>


    /// </summary>
    private static StandardPaper? ResolveRecordedTitleBlockPaper(string paperName, double recordedShortMm, double toleranceMm)
    {
        var nearest = Standards
            .OrderBy(paper => Math.Abs(paper.ShortSide - recordedShortMm))
            .First();
        if (Math.Abs(nearest.ShortSide - recordedShortMm) <= Math.Max(0.05d, toleranceMm))
        {
            return nearest;
        }

        var plusIndex = paperName.IndexOf('+');
        var baseName = plusIndex > 0 ? paperName.Substring(0, plusIndex) : paperName;
        return Standards.FirstOrDefault(paper =>
            string.Equals(paper.Name, baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static int GetScalePreferenceRank(double scale, DetectionOptions options)
    {
        if (options.PreferredScaleValues != null)
        {
            for (var i = 0; i < options.PreferredScaleValues.Count; i++)
            {
                if (Math.Abs(scale - options.PreferredScaleValues[i]) < 0.001d)
                {
                    return i;
                }
            }

            return options.PreferredScaleValues.Count;
        }

        return options.PreferredScaleValue.HasValue
            && Math.Abs(scale - options.PreferredScaleValue.Value) < 0.001d
            ? 0
            : 1;
    }

    private static string LongPaperExtension(double paperWidthMm, double paperHeightMm, StandardPaper standard)
    {
        var longSide = Math.Max(paperWidthMm, paperHeightMm);
        var units = (int)Math.Round(longSide / standard.LongSide * LongPaperIncrementDenominator, MidpointRounding.AwayFromZero);
        var ext = units - LongPaperIncrementDenominator;
        return ext <= 0 ? "" : FormatLongPaperExtension(ext);
    }

    private static string ContinuousLongPaperExtension(double paperWidthMm, double paperHeightMm, StandardPaper standard)
    {
        var extension = Math.Max(paperWidthMm, paperHeightMm) / standard.LongSide - 1d;
        return Math.Max(0d, extension).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatLongPaperExtension(int extensionUnits)
    {
        if (extensionUnits <= 0)
        {
            return "";
        }


        var divisor = GreatestCommonDivisor(extensionUnits, LongPaperIncrementDenominator);
        return $"{extensionUnits / divisor}/{LongPaperIncrementDenominator / divisor}";
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return Math.Abs(left);
    }

    private static PaperDetection FallbackDetect(double actualWidth, double actualHeight)
    {
        var shortSide = Math.Min(actualWidth, actualHeight);
        var longSide = Math.Max(actualWidth, actualHeight);
        return new PaperDetection
        {
            Note = $"Формат A0–A4 не найден; фактический размер {longSide:0.##} x {shortSide:0.##}"
        };
    }

    private static double RelativeError(double value, double target)
    {
        return Math.Abs(value - target) / Math.Max(target, 1);
    }

    /// <summary>

    /// </summary>
    private static List<double> MergeScales(IReadOnlyList<double>? extraScales)
    {
        var scales = new List<double>(CommonScales);
        if (extraScales == null)
        {
            return scales;
        }

        foreach (var extra in extraScales)
        {
            if (extra > 0 && !scales.Any(x => Math.Abs(x - extra) < 1e-6))
            {
                scales.Add(extra);
            }
        }

        return scales;
    }

    /// <summary>


    /// </summary>
    public static bool TryParseScale(string? text, out double scale)
    {
        scale = 0;
        if (text == null)
        {
            return false;
        }

        var normalized = text.Trim().Replace('：', ':').Replace(" ", "");
        if (normalized.Length == 0)
        {
            return false;
        }
        var colonIndex = normalized.IndexOf(':');
        if (colonIndex >= 0)
        {
            var left = normalized.Substring(0, colonIndex);
            var right = normalized.Substring(colonIndex + 1);
            if (right.IndexOf(':') >= 0
                || !TryParseNumber(left, out var leftValue)
                || !TryParseNumber(right, out var rightValue)
                || leftValue <= 0
                || rightValue <= 0)
            {
                return false;
            }

            scale = rightValue / leftValue;
            return true;
        }

        if (!TryParseNumber(normalized, out var number) || number <= 0)
        {
            return false;
        }

        scale = number;
        return true;
    }

    private static bool TryParseNumber(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }


    public static string ToScaleText(double scale)
    {
        if (Math.Abs(scale - 1) < 0.001)
        {
            return "1:1";
        }

        if (scale > 1)
        {
            return "1:" + scale.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return (1d / scale).ToString("0.###", CultureInfo.InvariantCulture) + ":1";
    }
}
