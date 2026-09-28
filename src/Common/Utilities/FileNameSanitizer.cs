using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ZwcadBatchPlot;

public static class FileNameSanitizer
{
    private const int DefaultMaxFileNameLength = 120;
    private const int LegacyMaxPathLength = 240;
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string Clean(string value)
    {
        var cleaned = new string((value ?? "").Select(ch => InvalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Без имени" : cleaned;
    }

    public static string MakeUnique(string directory, string fileNameWithoutExtension)
    {
        return MakeUnique(directory, fileNameWithoutExtension, null);
    }

    public static string MakeUnique(string directory, string fileNameWithoutExtension, ISet<string>? reservedPaths)
    {
        return MakeUnique(directory, fileNameWithoutExtension, reservedPaths, true);
    }

    public static string MakeUnique(
        string directory,
        string fileNameWithoutExtension,
        ISet<string>? reservedPaths,
        bool avoidExistingFile,
        string extension = ".pdf",
        bool createDirectory = true)
    {
        if (createDirectory)
        {
            Directory.CreateDirectory(directory);
        }
        var clean = TrimFileNameForPath(Clean(fileNameWithoutExtension), directory, extension);
        var path = Path.Combine(directory, clean + extension);
        var index = 1;
        while ((avoidExistingFile && File.Exists(path)) || reservedPaths?.Contains(path) == true)
        {
            var suffix = "_" + index;
            var maxNameLength = GetMaxFileNameLength(directory, extension);
            var uniqueName = TrimToLength(clean, Math.Max(1, maxNameLength - suffix.Length)) + suffix;
            path = Path.Combine(directory, uniqueName + extension);
            index++;
        }

        reservedPaths?.Add(path);
        return path;
    }

    private static string TrimFileNameForPath(string value, string directory, string extension = ".pdf")
    {
        return TrimToLength(value, GetMaxFileNameLength(directory, extension));
    }

    private static int GetMaxFileNameLength(string directory, string extension = ".pdf")
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return DefaultMaxFileNameLength;
        }

        return Math.Min(
            DefaultMaxFileNameLength,
            Math.Max(1, LegacyMaxPathLength - Path.GetFullPath(directory).Length - extension.Length - 1));
    }

    /// <summary>




    /// </summary>
    public static string NormalizeLongPaperFraction(string paperName, LongPaperNameFormat format = LongPaperNameFormat.Fraction)
    {
        if (string.IsNullOrEmpty(paperName)) return paperName ?? "";


        var result = LongPaperFractionPattern.Replace(paperName, match =>
        {
            var numerator = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var denominator = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (denominator == 0) return match.Value;
            if (format == LongPaperNameFormat.Decimal)
            {
                var ext = numerator / (double)denominator;
                return "+" + ext.ToString("0.###", CultureInfo.InvariantCulture);
            }

            return "+" + match.Groups[1].Value + "∕" + match.Groups[2].Value;
        });



        if (format == LongPaperNameFormat.Fraction)
        {
            result = LongPaperDecimalExtPattern.Replace(result, match =>
            {
                var dec = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (dec <= 0d) return match.Value;
                var units8 = (int)Math.Round(dec * 8, MidpointRounding.AwayFromZero);

                if (Math.Abs(dec * 8 - units8) > 0.001) return match.Value;
                var gcd = Gcd(units8, 8);
                return "+" + (units8 / gcd) + "∕" + (8 / gcd);
            });
        }

        return result;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { var r = a % b; a = b; b = r; }
        return a;
    }

    private static readonly Regex LongPaperFractionPattern =
        new Regex(@"\+(\d+)/(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);


    private static readonly Regex LongPaperDecimalExtPattern =
        new Regex(@"\+(\d+\.\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>



    /// </summary>
    public static string FormatFileNamePattern(
        string? pattern,
        PlotJob job,
        int? sequenceNumber = null,
        int sequenceDigits = 0,
        LongPaperNameFormat longPaperNameFormat = LongPaperNameFormat.Fraction,
        double longPaperSnapToleranceMm = 3d)
    {
        var result = new StringBuilder();
        var value = pattern ?? "";
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\\' && index + 1 < value.Length)
            {
                result.Append(value[++index]);
                continue;
            }

            var replacement = character switch
            {
                'A' => job.DrawingNumber,
                'B' => job.Revision,
                'C' => job.Title,
                'D' => job.Date,
                'E' => job.Info1,
                'F' => job.Info2,
                'G' => job.Phase,
                'T' => NormalizeLongPaperFraction(
                    OutputPaperNameResolver.Resolve(job, longPaperSnapToleranceMm),
                    longPaperNameFormat),
                'N' => sequenceNumber.HasValue ? FormatSequenceNumber(sequenceNumber.Value, sequenceDigits) : "",
                _ => null
            };
            if (replacement == null)
            {
                result.Append(character);
            }
            else if (!string.IsNullOrWhiteSpace(replacement))
            {
                result.Append(replacement.Trim());
            }
        }

        var formatted = result.ToString();
        if (string.IsNullOrWhiteSpace(formatted))
        {
            formatted = string.IsNullOrWhiteSpace(job.DrawingNumber) ? "Без имени" : job.DrawingNumber;
        }

        return Clean(formatted);
    }

    public static int ResolveSequenceDigits(
        bool autoDigits,
        int configuredDigits,
        int startNumber,
        int totalCount)
    {
        if (!autoDigits)
        {
            return Math.Max(0, Math.Min(10, configuredDigits));
        }

        var lastNumber = (long)Math.Max(0, startNumber) + Math.Max(0, totalCount - 1);
        return Math.Max(1, Math.Min(10, lastNumber.ToString(CultureInfo.InvariantCulture).Length));
    }

    private static string FormatSequenceNumber(int sequenceNumber, int sequenceDigits)
    {
        var digits = Math.Max(0, Math.Min(10, sequenceDigits));
        return digits == 0
            ? sequenceNumber.ToString(CultureInfo.InvariantCulture)
            : sequenceNumber.ToString($"D{digits}", CultureInfo.InvariantCulture);
    }

    private static string TrimToLength(string value, int maxLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Без имени" : value.Trim();
        return value.Length <= maxLength ? value : value.Substring(0, maxLength).Trim();
    }

    /// <summary>



    /// </summary>
    public static List<string> GetFileNameParts(
        PlotJob job,
        List<string> fieldKeys,
        int sequenceNumber = 0,
        int sequenceDigits = 2,
        LongPaperNameFormat longPaperNameFormat = LongPaperNameFormat.Fraction,
        double longPaperSnapToleranceMm = 3d)
    {
        var parts = new List<string>();
        foreach (var key in fieldKeys)
        {
            var value = key switch
            {
                "DrawingNumber" => job.DrawingNumber,
                "Title" => job.Title,
                "Date" => job.Date,
                "Revision" => job.Revision,
                "Phase" => job.Phase,
                "Info1" => job.Info1,
                "Info2" => job.Info2,
                "PaperName" => NormalizeLongPaperFraction(
                    OutputPaperNameResolver.Resolve(job, longPaperSnapToleranceMm),
                    longPaperNameFormat),
                "Sequence" => sequenceNumber > 0 ? sequenceNumber.ToString($"D{Math.Max(1, Math.Min(10, sequenceDigits))}") : "",
                _ => ""
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value.Trim());
            }
        }

        return parts;
    }
}
