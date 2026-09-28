using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
#if !ZWCAD
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#endif
using PiaNO;

namespace ZwcadBatchPlot;

/// <summary>





/// </summary>
public static class PmpCustomPaper
{
    private const double ExactSizeToleranceMm = 0.05d;

    public sealed class Registration
    {
        public string PaperName { get; set; } = "";
        public bool WasAdded { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
    }

    public sealed class PaperRequest
    {
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
    }

    
    public static Registration? RegisterCustomPaper(
        string pmpPath,
        double widthMm,
        double heightMm,
        bool writeIfMissing = true)
    {
        if (!File.Exists(pmpPath)) return null;

        try
        {
            var raw = File.ReadAllText(pmpPath);
            if (string.IsNullOrWhiteSpace(raw)) return null;

#if !ZWCAD
            if (raw.StartsWith("PIAFILEVERSION_3.0", StringComparison.OrdinalIgnoreCase))
                return RegisterPia3(pmpPath, raw, widthMm, heightMm, writeIfMissing);
#endif

            if (raw.StartsWith("[Meta]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[Meta]\r", StringComparison.OrdinalIgnoreCase))
                return RegisterZwcadIni(pmpPath, raw, widthMm, heightMm, writeIfMissing);

            return RegisterPia2(pmpPath, widthMm, heightMm, writeIfMissing);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>


    /// </summary>
    public static void RemoveCustomPaper(string pmpPath, string paperName)
    {
        if (!File.Exists(pmpPath) || string.IsNullOrWhiteSpace(paperName))
            return;

        try
        {
            var raw = File.ReadAllText(pmpPath);
            if (string.IsNullOrWhiteSpace(raw)) return;

#if !ZWCAD
            if (raw.StartsWith("PIAFILEVERSION_3.0", StringComparison.OrdinalIgnoreCase))
                RemovePia3(pmpPath, raw, paperName);
            else
#endif
            if (raw.StartsWith("[Meta]", StringComparison.OrdinalIgnoreCase)
                     || raw.StartsWith("[Meta]\r", StringComparison.OrdinalIgnoreCase))
                RemoveZwcadIni(pmpPath, raw, paperName);
            else
                RemovePia2(pmpPath, paperName);
        }
        catch
        {

        }
    }

#if !ZWCAD
    private static Registration? RegisterPia3(
        string pmpPath,
        string raw,
        double widthMm,
        double heightMm,
        bool writeIfMissing)
    {
        var jsonStart = raw.IndexOf('{');
        if (jsonStart < 0) return null;
        var root = JObject.Parse(raw.Substring(jsonStart));

        var udm = root["data"]?["udm"] as JObject;
        if (udm == null) return null;
        var media = udm["media"] as JObject;
        if (media == null) return null;
        var desc = media["description"] as JObject;
        var size = media["size"] as JObject;
        if (desc == null || size == null) return null;


        var wFmt = FormatMm(widthMm);
        var hFmt = FormatMm(heightMm);
        var paperName = CustomPaperName(widthMm, heightMm);
        foreach (var prop in desc.Properties())
        {
            var entry = prop.Value as JObject;
            if (entry == null || entry["caps_type"]?.Value<int>() != 2) continue;
            var urx = entry["media_bounds_urx"]?.Value<double>() ?? 0;
            var ury = entry["media_bounds_ury"]?.Value<double>() ?? 0;
            if (SameSize(urx, ury, widthMm, heightMm))
            {

                var match = size[prop.Name] as JObject;
                return new Registration
                {
                    PaperName = match?["localized_name"]?.Value<string>() ?? prop.Name,
                    WasAdded = false
                };
            }
        }

        if (!writeIfMissing)
        {
            return null;
        }


        var maxIdx = 0;
        foreach (var prop in desc.Properties())
            if (int.TryParse(prop.Name, out var idx) && idx > maxIdx)
                maxIdx = idx;
        var index = (maxIdx + 1).ToString();


        var area = widthMm * heightMm;
        var descName = MediaDescriptionName(paperName, widthMm, heightMm);
        var descEntry = new JObject
        {
            ["caps_type"] = 2,
            ["dimensional"] = true,
            ["media_bounds_urx"] = widthMm,
            ["media_bounds_ury"] = heightMm,
            ["name"] = descName,
            ["printable_area"] = area,
            ["printable_bounds_llx"] = 0.0,
            ["printable_bounds_lly"] = 0.0,
            ["printable_bounds_urx"] = widthMm,
            ["printable_bounds_ury"] = heightMm
        };
        desc[index] = descEntry;


        var sizeEntry = new JObject
        {
            ["caps_type"] = 2,
            ["landscape_mode"] = true,
            ["localized_name"] = paperName,
            ["media_description_name"] = descName,
            ["media_group"] = 15,
            ["name"] = $"UserDefinedMetric {paperName} ({wFmt} x {hFmt}mm)"
        };
        size[index] = sizeEntry;


        var newJson = root.ToString(Formatting.Indented);
        File.WriteAllText(pmpPath, "PIAFILEVERSION_3.0,json\n" + newJson);

        return new Registration { PaperName = paperName, WasAdded = true };
    }
#endif

    private static Registration? RegisterPia2(string pmpPath, double widthMm, double heightMm, bool writeIfMissing)
    {

        try
        {
            var config = new PlotterConfiguration(pmpPath);
            var desc = config["udm"]?["media"]?["description"];
            var size = config["udm"]?["media"]?["size"];
            if (desc == null || size == null) return null;

            var paperName = CustomPaperName(widthMm, heightMm);


            foreach (var child in desc)
            {
                var caps = child.GetValue("caps_type");
                if (caps != "2") continue;
                double.TryParse(child.GetValue("media_bounds_urx"), NumberStyles.Float, CultureInfo.InvariantCulture, out var urx);
                double.TryParse(child.GetValue("media_bounds_ury"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ury);
                if (SameSize(urx, ury, widthMm, heightMm))
                {

                    var match = size[child.NodeName];
                    var repaired = MovePia2StringValue(child, "name");
                    if (match != null)
                    {
                        repaired |= MovePia2StringValue(match, "name");
                        repaired |= MovePia2StringValue(match, "localized_name");
                        repaired |= MovePia2StringValue(match, "media_description_name");
                    }
                    if (repaired && writeIfMissing)
                        config.Saves(pmpPath);

                    var matchName = match == null ? "" : GetPia2StringValue(match, "localized_name");
                    return new Registration
                    {
                        PaperName = !string.IsNullOrWhiteSpace(matchName) ? matchName : child.NodeName,
                        WasAdded = false
                    };
                }
            }

            if (!writeIfMissing)
            {
                return null;
            }


            var maxIdx = 0;
            foreach (var child in desc)
                if (int.TryParse(child.NodeName, out var idx) && idx > maxIdx)
                    maxIdx = idx;
            var index = (maxIdx + 1).ToString();


            var area = widthMm * heightMm;
            var descName = MediaDescriptionName(paperName, widthMm, heightMm);
            var descEntry = desc.Add(index);
            descEntry.SetValue("caps_type", "2");
            descEntry.SetValue("dimensional", "TRUE");
            descEntry.SetValue("media_bounds_urx", FormatNumber(widthMm));
            descEntry.SetValue("media_bounds_ury", FormatNumber(heightMm));
            descEntry.SetValue("name_str", descName);
            descEntry.SetValue("printable_area", FormatNumber(area));
            descEntry.SetValue("printable_bounds_llx", "0.0");
            descEntry.SetValue("printable_bounds_lly", "0.0");
            descEntry.SetValue("printable_bounds_urx", FormatNumber(widthMm));
            descEntry.SetValue("printable_bounds_ury", FormatNumber(heightMm));


            var sizeEntry = size.Add(index);
            sizeEntry.SetValue("caps_type", "2");
            sizeEntry.SetValue("landscape_mode", "TRUE");
            sizeEntry.SetValue("localized_name_str", paperName);
            sizeEntry.SetValue("media_description_name_str", descName);
            sizeEntry.SetValue("media_group", "15");
            sizeEntry.SetValue("name_str", $"UserDefinedMetric {paperName} ({FormatMm(widthMm)} x {FormatMm(heightMm)}mm)");

            config.Saves(pmpPath);
            return new Registration { PaperName = paperName, WasAdded = true };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>

    /// [Meta] ... userdef_num=N ...
    /// [user]
    /// paper_name0=...  paper_local_name0=...  size_x0=...  size_y0=...
    /// </summary>
    private static Registration? RegisterZwcadIni(
        string pmpPath,
        string raw,
        double widthMm,
        double heightMm,
        bool writeIfMissing)
    {

        var existingLocalName = FindZwcadPaperBySize(raw, widthMm, heightMm);
        if (existingLocalName != null)
            return new Registration { PaperName = existingLocalName, WasAdded = false };

        if (!writeIfMissing)
        {
            return null;
        }


        var numMatch = Regex.Match(raw, @"userdef_num=(\d+)");
        if (!numMatch.Success) return null;
        var index = int.Parse(numMatch.Groups[1].Value);


        var localName = $"LA_Custom_{FormatPlain(widthMm)}x{FormatPlain(heightMm)}";


        while (Regex.IsMatch(raw, $@"(?m)^paper_name{index}=")
               || Regex.IsMatch(raw, $@"(?m)^paper_local_name{index}=")
               || raw.IndexOf(localName, StringComparison.OrdinalIgnoreCase) >= 0)
        {

            index++;
            localName = $"LA_Custom_{FormatPlain(widthMm)}x{FormatPlain(heightMm)}_{index}";
            if (index > 9999) return null;
        }


        var w = widthMm.ToString("0.000000", CultureInfo.InvariantCulture);
        var h = heightMm.ToString("0.000000", CultureInfo.InvariantCulture);
        var area = (widthMm * heightMm).ToString("0.000000", CultureInfo.InvariantCulture);
        var newEntry = $"\r\npaper_name{index}=UserDefinedMetric ({w} x {h} mm)\r\n" +
                       $"paper_local_name{index}={localName}\r\n" +
                       $"size_x{index}={w}\r\n" +
                       $"size_y{index}={h}\r\n" +
                       $"llx{index}=0.000000\r\n" +
                       $"lly{index}=0.000000\r\n" +
                       $"urx{index}={w}\r\n" +
                       $"ury{index}={h}\r\n" +
                       $"actual_x{index}={w}\r\n" +
                       $"actual_y{index}={h}\r\n" +
                       $"area{index}={area}\r\n" +
                       $"Unit{index}=1";


        var newRaw = Regex.Replace(raw, @"userdef_num=\d+", $"userdef_num={index + 1}");


        newRaw = newRaw.TrimEnd() + newEntry + "\r\n";

        File.WriteAllText(pmpPath, newRaw);
        return new Registration { PaperName = localName, WasAdded = true };
    }

    private static string? FindZwcadPaperBySize(string raw, double widthMm, double heightMm)
    {
        foreach (Match match in Regex.Matches(raw, @"(?m)^paper_local_name(?<index>\d+)=(?<name>[^\r\n]+)"))
        {
            var index = match.Groups["index"].Value;
            var x = Regex.Match(raw, $@"(?m)^size_x{index}=(?<value>[-+0-9.,]+)");
            var y = Regex.Match(raw, $@"(?m)^size_y{index}=(?<value>[-+0-9.,]+)");
            if (!x.Success || !y.Success
                || !double.TryParse(x.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var actualX)
                || !double.TryParse(y.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var actualY))
            {
                continue;
            }

            if (SameSize(actualX, actualY, widthMm, heightMm))
                return match.Groups["name"].Value.Trim();
        }

        return null;
    }

    
    public static IReadOnlyList<Registration>? RegisterCustomPapers(
        string pmpPath,
        IEnumerable<PaperRequest> requests)
    {
        if (!File.Exists(pmpPath)) return null;

        var normalized = new List<PaperRequest>();
        foreach (var request in requests ?? Enumerable.Empty<PaperRequest>())
        {
            if (request.WidthMm <= 0d || request.HeightMm <= 0d)
                continue;

            if (normalized.Any(existing => SameSize(
                    existing.WidthMm,
                    existing.HeightMm,
                    request.WidthMm,
                    request.HeightMm)))
            {
                continue;
            }

            normalized.Add(new PaperRequest
            {
                WidthMm = request.WidthMm,
                HeightMm = request.HeightMm
            });
        }

        if (normalized.Count == 0)
            return new List<Registration>();

        var existing = new List<Registration>();
        var missing = new List<PaperRequest>();
        foreach (var request in normalized)
        {
            var found = RegisterCustomPaper(pmpPath, request.WidthMm, request.HeightMm, writeIfMissing: false);
            if (found == null)
            {
                missing.Add(request);
                continue;
            }

            found.WidthMm = request.WidthMm;
            found.HeightMm = request.HeightMm;
            existing.Add(found);
        }

        if (missing.Count == 0)
        {
            return existing;
        }

        var directory = Path.GetDirectoryName(pmpPath) ?? "";
        var stagedPath = Path.Combine(
            directory,
            Path.GetFileName(pmpPath) + ".batch-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var originalBytes = File.ReadAllBytes(pmpPath);
            File.Copy(pmpPath, stagedPath, true);
            var registrations = new List<Registration>(existing);
            foreach (var request in missing)
            {
                var registration = RegisterCustomPaper(stagedPath, request.WidthMm, request.HeightMm);
                if (registration == null)
                    return null;

                registration.WidthMm = request.WidthMm;
                registration.HeightMm = request.HeightMm;
                registrations.Add(registration);
            }

            var stagedBytes = File.ReadAllBytes(stagedPath);
            if (!originalBytes.SequenceEqual(stagedBytes))
            {

                File.Copy(stagedPath, pmpPath, true);
            }

            return registrations;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(stagedPath)) File.Delete(stagedPath);
            }
            catch
            {

            }
        }
    }

    private static string GetPia2StringValue(PiaNode node, string key)
    {
        if (node.NodeMap.TryGetValue(key + "_str", out var quotedValue))
            return quotedValue;
        return node.NodeMap.TryGetValue(key, out var legacyValue) ? legacyValue : "";
    }

    private static bool MovePia2StringValue(PiaNode node, string key)
    {
        var quotedKey = key + "_str";
        if (node.NodeMap.ContainsKey(quotedKey))
        {

            return node.NodeMap.Remove(key);
        }

        if (!node.NodeMap.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return false;

        node.NodeMap[quotedKey] = value;
        node.NodeMap.Remove(key);
        return true;
    }

    private static bool SameSize(double leftWidth, double leftHeight, double rightWidth, double rightHeight)
    {
        var direct = Math.Max(Math.Abs(leftWidth - rightWidth), Math.Abs(leftHeight - rightHeight));
        var rotated = Math.Max(Math.Abs(leftWidth - rightHeight), Math.Abs(leftHeight - rightWidth));
        return Math.Min(direct, rotated) <= ExactSizeToleranceMm;
    }

    private static string CustomPaperName(double widthMm, double heightMm)
    {
        return $"LA_Custom_{FormatPlain(widthMm)}x{FormatPlain(heightMm)}";
    }

    private static string MediaDescriptionName(string paperName, double widthMm, double heightMm)
    {
        var area = widthMm * heightMm;
        return $"UserDefinedMetric {paperName} Landscape {FormatMm(widthMm)}W x {FormatMm(heightMm)}H - (0, 0) x ({FormatPlain(widthMm)}, {FormatPlain(heightMm)}) ={FormatPlain(area)} mm";
    }

    private static string FormatMm(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatPlain(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatNumber(double value) => value.ToString("0.0#########", CultureInfo.InvariantCulture);

#if !ZWCAD
    private static void RemovePia3(string pmpPath, string raw, string paperName)
    {
        var jsonStart = raw.IndexOf('{');
        if (jsonStart < 0) return;
        var root = JObject.Parse(raw.Substring(jsonStart));

        var size = root["data"]?["udm"]?["media"]?["size"] as JObject;
        var desc = root["data"]?["udm"]?["media"]?["description"] as JObject;
        if (size == null || desc == null) return;


        string? targetIndex = null;
        foreach (var prop in size.Properties())
        {
            var entry = prop.Value as JObject;
            if (entry?["localized_name"]?.Value<string>() == paperName)
            {
                targetIndex = prop.Name;
                break;
            }
        }
        if (targetIndex == null) return;


        desc.Remove(targetIndex);
        size.Remove(targetIndex);
        ReindexPia3Media(desc, size);

        var newJson = root.ToString(Formatting.Indented);
        File.WriteAllText(pmpPath, "PIAFILEVERSION_3.0,json\n" + newJson);
    }
#endif

    private static void RemovePia2(string pmpPath, string paperName)
    {
        try
        {
            var config = new PlotterConfiguration(pmpPath);
            var desc = config["udm"]?["media"]?["description"];
            var size = config["udm"]?["media"]?["size"];
            if (desc == null || size == null) return;


            string? targetIndex = null;
            foreach (var child in size)
            {
                if (child.GetValue("localized_name") == paperName)
                {
                    targetIndex = child.NodeName;
                    break;
                }
            }
            if (targetIndex == null) return;

            desc.Remove(targetIndex);
            size.Remove(targetIndex);
            ReindexPia2Node(desc);
            ReindexPia2Node(size);
            config.Saves(pmpPath);
        }
        catch { }
    }

    private static void RemoveZwcadIni(string pmpPath, string raw, string paperName)
    {
        var blockPattern = @"(?ms)^paper_name(?<index>\d+)=[^\r\n]*\r?\n"
            + @"paper_local_name\k<index>=(?<local>[^\r\n]*)\r?\n"
            + @"size_x\k<index>=[^\r\n]*\r?\nsize_y\k<index>=[^\r\n]*\r?\n"
            + @"llx\k<index>=[^\r\n]*\r?\nlly\k<index>=[^\r\n]*\r?\n"
            + @"urx\k<index>=[^\r\n]*\r?\nury\k<index>=[^\r\n]*\r?\n"
            + @"actual_x\k<index>=[^\r\n]*\r?\nactual_y\k<index>=[^\r\n]*\r?\n"
            + @"area\k<index>=[^\r\n]*\r?\nUnit\k<index>=1[^\r\n]*(?:\r?\n)?";
        var matches = Regex.Matches(raw, blockPattern);
        if (matches.Count == 0)
            return;

        var remaining = new List<string>();
        var removed = false;
        foreach (Match match in matches)
        {
            if (string.Equals(match.Groups["local"].Value.Trim(), paperName, StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
                continue;
            }

            var newIndex = remaining.Count;
            var block = Regex.Replace(
                match.Value,
                @"(?m)^(paper_name|paper_local_name|size_x|size_y|llx|lly|urx|ury|actual_x|actual_y|area|Unit)\d+=",
                m => m.Groups[1].Value + newIndex.ToString(CultureInfo.InvariantCulture) + "=");
            remaining.Add(block.TrimEnd('\r', '\n'));
        }

        if (!removed)
            return;

        var prefix = raw.Substring(0, matches[0].Index).TrimEnd('\r', '\n');
        var suffixStart = matches[matches.Count - 1].Index + matches[matches.Count - 1].Length;
        var suffix = raw.Substring(suffixStart).Trim('\r', '\n');
        var newRaw = Regex.Replace(prefix, @"userdef_num=\d+", $"userdef_num={remaining.Count}");
        newRaw += "\r\n" + string.Join("\r\n", remaining) + "\r\n";
        if (!string.IsNullOrWhiteSpace(suffix))
            newRaw += suffix + "\r\n";

        File.WriteAllText(pmpPath, newRaw);
    }

#if !ZWCAD
    private static void ReindexPia3Media(JObject desc, JObject size)
    {
        var keys = desc.Properties().Select(x => x.Name)
            .Union(size.Properties().Select(x => x.Name), StringComparer.OrdinalIgnoreCase)
            .OrderBy(ParseIndex)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var descValues = keys.Select(key => desc[key]?.DeepClone()).ToList();
        var sizeValues = keys.Select(key => size[key]?.DeepClone()).ToList();
        desc.RemoveAll();
        size.RemoveAll();
        for (var i = 0; i < keys.Count; i++)
        {
            if (descValues[i] != null) desc[i.ToString(CultureInfo.InvariantCulture)] = descValues[i];
            if (sizeValues[i] != null) size[i.ToString(CultureInfo.InvariantCulture)] = sizeValues[i];
        }
    }
#endif

    private static void ReindexPia2Node(PiaNode node)
    {
        var ordered = node.ChildNodes
            .OrderBy(child => ParseIndex(child.NodeName))
            .ThenBy(child => child.NodeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        node.ChildNodes.Clear();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].NodeName = i.ToString(CultureInfo.InvariantCulture);
            node.ChildNodes.Add(ordered[i]);
        }
    }

    private static int ParseIndex(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? index
            : int.MaxValue;
    }
}
