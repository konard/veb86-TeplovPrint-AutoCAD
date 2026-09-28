using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
internal static class PlotTextGeometryFileUpdater
{
    public static string UpdateZwcadPc5(string original, bool convertToGeometry, out bool changed)
    {
        var expected = "truetype_as_text=" + (convertToGeometry ? "0" : "1");
        var updated = Regex.IsMatch(original, @"(?im)^truetype_as_text=\d+\s*$")
            ? Regex.Replace(original, @"(?im)^truetype_as_text=\d+\s*$", expected)
            : new Regex(@"(?im)^\[res_color_mem\]\s*$").Replace(
                original,
                "$0\r\n" + expected,
                1);
        changed = !string.Equals(original, updated, StringComparison.Ordinal);
        return updated;
    }

    public static bool TryUpdatePia3(
        string original,
        bool convertToGeometry,
        bool updateAllAsGeometry,
        out string updated,
        out bool changed)
    {
        updated = original;
        changed = false;
        if (!original.StartsWith("PIAFILEVERSION_3.0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var jsonStart = original.IndexOf('{');
        if (jsonStart < 0)
        {
            return false;
        }

        var root = JObject.Parse(original.Substring(jsonStart));
        var meta = root["data"]?["meta"] as JObject
            ?? throw new InvalidOperationException("В PIA3 отсутствует узел meta.");
        var truetypeAsText = !convertToGeometry;
        if (meta["truetype_as_text"]?.Value<bool>() != truetypeAsText)
        {
            meta["truetype_as_text"] = truetypeAsText;
            changed = true;
        }

        if (updateAllAsGeometry && root["data"]?["custom"] is JObject custom)
        {
            foreach (var property in custom.Properties())
            {
                if (property.Value is not JObject item
                    || !string.Equals(item["name"]?.Value<string>(), "All_As_Geometry", StringComparison.OrdinalIgnoreCase)
                    || item["value"]?.Value<bool>() == convertToGeometry)
                {
                    continue;
                }

                item["value"] = convertToGeometry;
                changed = true;
            }
        }

        if (changed)
        {
            updated = "PIAFILEVERSION_3.0,json\n" + root.ToString(Formatting.Indented);
        }
        return true;
    }
}
