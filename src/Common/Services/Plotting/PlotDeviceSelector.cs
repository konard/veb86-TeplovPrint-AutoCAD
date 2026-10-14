using System;
using System.Collections.Generic;
using System.Linq;

namespace ZwcadBatchPlot;

/// <summary>
/// Picks a file output device from PlotSettingsValidator.GetPlotDeviceList() in a CAD that cannot use
/// the bundled LA_*.pc3 (nanoCAD, issue #5). Has no CAD dependencies so it is unit-tested without the host.
/// </summary>
public static class PlotDeviceSelector
{
    /// <summary>nanoCAD help calls it "Internal PDF Plotter"; the Russian UI says "встроенный"/"внутренний".</summary>
    private static readonly string[] InternalPlotterMarkers = { "internal", "встроен", "внутрен" };

    /// <summary>
    /// Windows printers ignore the PlotToFile name: they either ask for a file name or write a printer spool file.
    /// </summary>
    private static readonly string[] SystemPdfPrinterMarkers =
    {
        "Microsoft Print to PDF", "Adobe PDF", "PDF24", "PDFCreator", "Foxit", "CutePDF", "Bullzip",
        "doPDF", "novaPDF", "Nitro", "PDF-XChange", "PrimoPDF", "Universal Document Converter"
    };

    /// <summary>
    /// PDF device for file output, or null. An installed <paramref name="preferred"/> device wins;
    /// then the CAD internal plotter, devices named after nanoCAD, other PC3/PC5 configurations, other devices.
    /// </summary>
    public static string? SelectPdfDevice(IEnumerable<string> devices, string? preferred)
    {
        var list = Normalize(devices);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = list.FirstOrDefault(device => string.Equals(device, preferred!.Trim(), StringComparison.OrdinalIgnoreCase)
                                                      && !IsBundledTemplate(device));
            if (match != null)
            {
                return match;
            }
        }

        return Select(list, "PDF", excludedMarker: null);
    }

    /// <summary>DWF (not DWFx) device for file output, or null.</summary>
    public static string? SelectDwfDevice(IEnumerable<string> devices)
    {
        return Select(Normalize(devices), "DWF", excludedMarker: "DWFx");
    }

    /// <summary>LA_pdf.pc3, LA_dwf.pc3, ...: AutoCAD templates of this plugin bound to Autodesk HDI drivers.</summary>
    public static bool IsBundledTemplate(string device)
    {
        var name = device.Trim();
        return name.StartsWith("LA_", StringComparison.OrdinalIgnoreCase)
               && (name.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".pc5", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> Normalize(IEnumerable<string> devices)
    {
        return (devices ?? Array.Empty<string>())
            .Where(device => !string.IsNullOrWhiteSpace(device))
            .Select(device => device.Trim())
            .ToList();
    }

    private static string? Select(IReadOnlyList<string> devices, string kind, string? excludedMarker)
    {
        // OrderBy is stable: equally ranked devices keep the CAD order.
        return devices
            .Where(device => Contains(device, kind)
                             && (excludedMarker == null || !Contains(device, excludedMarker))
                             && !IsBundledTemplate(device)
                             && !SystemPdfPrinterMarkers.Any(marker => Contains(device, marker)))
            .OrderBy(Rank)
            .FirstOrDefault();
    }

    private static int Rank(string device)
    {
        if (InternalPlotterMarkers.Any(marker => Contains(device, marker)))
        {
            return 0;
        }

        if (Contains(device, "nanoCAD"))
        {
            return 1;
        }

        return device.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase)
               || device.EndsWith(".pc5", StringComparison.OrdinalIgnoreCase)
            ? 2
            : 3;
    }

    private static bool Contains(string value, string marker)
    {
        return value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
