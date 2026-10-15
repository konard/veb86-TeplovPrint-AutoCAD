using System;
using System.IO;

namespace ZwcadBatchPlot;

/// <summary>
/// nanoCAD (issue #9): Plot.PlotToFile of the COM API may add the extension of the plotter driver to the file name
/// ("01_План.pdf.pdf") or drop it ("01_План"). The plotted file is moved to the path the job asked for.
/// No host dependencies, so it is unit-tested without nanoCAD.
/// </summary>
public static class NanoCadPlotOutput
{
    private static readonly string[] DriverExtensions = { ".pdf", ".png", ".jpg", ".jpeg", ".dwf", ".plt" };

    /// <summary>
    /// True if the plotted file exists at <paramref name="requested"/> or was found next to it under a name the driver
    /// produced and moved there.
    /// </summary>
    public static bool TryMoveToRequestedPath(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return false;
        }

        if (File.Exists(requested))
        {
            return true;
        }

        foreach (var candidate in Candidates(requested))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                File.Move(candidate, requested);
                PrintSessionLog.Info($"Файл печати перемещён: {candidate} -> {requested}");
                return true;
            }
            catch (Exception ex)
            {
                PrintSessionLog.Error($"Файл печати не перемещён: {candidate} -> {requested}", ex);
            }
        }

        return false;
    }

    private static string[] Candidates(string requested)
    {
        var withoutExtension = Path.Combine(
            Path.GetDirectoryName(requested) ?? "",
            Path.GetFileNameWithoutExtension(requested));
        var candidates = new string[DriverExtensions.Length * 2 + 1];
        candidates[0] = withoutExtension;
        for (var i = 0; i < DriverExtensions.Length; i++)
        {
            candidates[1 + i] = requested + DriverExtensions[i];
            candidates[1 + DriverExtensions.Length + i] = withoutExtension + DriverExtensions[i];
        }

        return candidates;
    }
}
