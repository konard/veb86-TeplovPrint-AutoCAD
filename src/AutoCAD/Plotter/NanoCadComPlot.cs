#if NANOCAD
using System;
using System.Collections.Generic;
using System.Globalization;
using HostMgd.ApplicationServices;

namespace ZwcadBatchPlot;

/// <summary>Page setup of one job for the nanoCAD COM layout (issue #9). Window coordinates are the DCS window of the job.</summary>
internal sealed class NanoCadComPageSetup
{
    public string LayoutName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string MediaName { get; set; } = "";
    public int PaperUnits { get; set; } = NanoCadPageSetup.PaperUnitsMillimeters;
    public double WindowMinX { get; set; }
    public double WindowMinY { get; set; }
    public double WindowMaxX { get; set; }
    public double WindowMaxY { get; set; }

    /// <summary>0..3 = AcPlotRotation ac0degrees..ac270degrees, the same values as PlotRotation.</summary>
    public int Rotation { get; set; }

    /// <summary>Paper units per drawing unit; null — scale to fit.</summary>
    public double? CustomScale { get; set; }

    public string StyleSheet { get; set; } = "";
    public bool PlotWithLineweights { get; set; }

    public override string ToString()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "лист={0}; устройство={1}; бумага={2}; единицы={3}; окно=({4:0.###},{5:0.###})-({6:0.###},{7:0.###}); поворот={8}; масштаб={9}; стиль={10}; веса линий={11}",
            LayoutName,
            DeviceName,
            MediaName,
            PaperUnits,
            WindowMinX,
            WindowMinY,
            WindowMaxX,
            WindowMaxY,
            Rotation * 90,
            CustomScale == null ? "вписать" : CustomScale.Value.ToString("0.########", CultureInfo.InvariantCulture) + ":1",
            StyleSheet,
            PlotWithLineweights);
    }
}

/// <summary>
/// nanoCAD 24 prints through its COM API (issue #9). The managed HostMgd.PlottingServices.PlotEngine of nanoCAD 24
/// is empty (every Begin*/End* method only returns), and writing the page setup to the database-resident layout
/// through the native PlotSettingsValidator closed nanoCAD without any message. The page setup is written to
/// Document.AcadDocument.ActiveLayout and the sheet is printed by Document.AcadDocument.Plot.PlotToFile, as nanoCAD's
/// own "Plot" command and AutoCAD-compatible COM scripts do. Every COM call is a labelled step of the print log:
/// if nanoCAD dies inside it, %TEMP%\TeplovPrint-print.log ends with that step.
/// </summary>
internal static class NanoCadComPlot
{
    /// <summary>The COM document (IAcadDocument-compatible) of a nanoCAD document.</summary>
    public static object GetComDocument(Document? document)
    {
        if (document == null)
        {
            throw new InvalidOperationException("nanoCAD печатает только открытые документы: документ для печати не найден.");
        }

        object? comDocument = null;
        PlotStepException.Run("Document.AcadDocument", () => comDocument = document.AcadDocument);
        return comDocument ?? throw new InvalidOperationException("nanoCAD не вернул COM-объект документа (Document.AcadDocument).");
    }

    /// <summary>Remembers the page setup of the active layout to restore it after printing.</summary>
    public static NanoCadComLayoutSnapshot CaptureLayout(object comDocument, string expectedLayoutName)
    {
        dynamic layout = ActiveLayout(comDocument);
        string activeName = "";
        PlotStepException.Run("ActiveLayout.Name", () => activeName = Convert.ToString(layout.Name, CultureInfo.InvariantCulture) ?? "");
        if (!string.IsNullOrWhiteSpace(expectedLayoutName)
            && !string.Equals(activeName, expectedLayoutName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Активный лист nanoCAD «{activeName}» не совпадает с листом задания «{expectedLayoutName}». Печать остановлена, чтобы не вывести неверную область.");
        }

        return NanoCadComLayoutSnapshot.Capture(layout);
    }

    /// <summary>Writes the page setup of the job to the active layout.</summary>
    public static void ApplyPageSetup(object comDocument, NanoCadComPageSetup setup)
    {
        PrintSessionLog.Info("Параметры листа nanoCAD (COM): " + setup);
        dynamic layout = ActiveLayout(comDocument);

        PlotStepException.Run($"ActiveLayout.ConfigName = {setup.DeviceName}", () => layout.ConfigName = setup.DeviceName);
        PlotStepException.Run("ActiveLayout.RefreshPlotDeviceInfo()", () => layout.RefreshPlotDeviceInfo());
        PlotStepException.Run($"ActiveLayout.CanonicalMediaName = {setup.MediaName}", () => layout.CanonicalMediaName = setup.MediaName);
        PlotStepException.Run($"ActiveLayout.PaperUnits = {setup.PaperUnits}", () => layout.PaperUnits = setup.PaperUnits);

        var lowerLeft = new[] { setup.WindowMinX, setup.WindowMinY };
        var upperRight = new[] { setup.WindowMaxX, setup.WindowMaxY };
        PlotStepException.Run(
            string.Format(CultureInfo.InvariantCulture, "ActiveLayout.SetWindowToPlot(({0},{1}), ({2},{3}))", lowerLeft[0], lowerLeft[1], upperRight[0], upperRight[1]),
            () => layout.SetWindowToPlot(lowerLeft, upperRight));
        PlotStepException.Run("ActiveLayout.PlotType = acWindow", () => layout.PlotType = NanoCadPageSetup.PlotTypeWindow);
        PlotStepException.Run($"ActiveLayout.PlotRotation = {setup.Rotation}", () => layout.PlotRotation = setup.Rotation);

        if (setup.CustomScale is double scale)
        {
            PlotStepException.Run("ActiveLayout.UseStandardScale = False", () => layout.UseStandardScale = false);
            PlotStepException.Run(
                string.Format(CultureInfo.InvariantCulture, "ActiveLayout.SetCustomScale({0}, 1)", scale),
                () => layout.SetCustomScale(scale, 1d));
        }
        else
        {
            PlotStepException.Run("ActiveLayout.UseStandardScale = True", () => layout.UseStandardScale = true);
            PlotStepException.Run("ActiveLayout.StandardScale = acScaleToFit", () => layout.StandardScale = NanoCadPageSetup.StandardScaleToFit);
        }

        PlotStepException.Run("ActiveLayout.CenterPlot = True", () => layout.CenterPlot = true);
        if (!string.IsNullOrWhiteSpace(setup.StyleSheet))
        {
            PlotStepException.Run($"ActiveLayout.StyleSheet = {setup.StyleSheet}", () => layout.StyleSheet = setup.StyleSheet);
        }

        PlotStepException.Run($"ActiveLayout.PlotWithLineweights = {setup.PlotWithLineweights}", () => layout.PlotWithLineweights = setup.PlotWithLineweights);
    }

    /// <summary>Prints the active layout to <paramref name="outputPath"/> with Plot.PlotToFile.</summary>
    public static void PlotToFile(object comDocument, string deviceName, string outputPath)
    {
        // Plot.QuietErrorMode is not set: nanoCAD 24 does not implement its setter and shows the modal
        // "COM / NOT_IMPLEMENTED / CPlotObj::put_QuietErrorMode" dialog on every sheet (issue #11).
        var plot = PlotObject(comDocument);

        object? result = null;
        try
        {
            // PlotToFile(PlotFile, PlotConfig): the plotter is passed again, as AutoCAD-compatible scripts do.
            PlotStepException.Run($"Plot.PlotToFile({outputPath}, {deviceName})", () => result = plot.PlotToFile(outputPath, deviceName));
        }
        catch (PlotStepException withConfig) when (!withConfig.IsNativeFailure)
        {
            PrintSessionLog.Write("WARN", "Plot.PlotToFile с файлом плоттера не выполнен, повтор без него.");
            PlotStepException.Run($"Plot.PlotToFile({outputPath})", () => result = plot.PlotToFile(outputPath));
        }

        PrintSessionLog.Info("Plot.PlotToFile вернул: " + (result == null ? "<нет значения>" : Convert.ToString(result, CultureInfo.InvariantCulture)));
        if (result is bool plotted && !plotted)
        {
            throw new InvalidOperationException(
                $"nanoCAD не выполнил печать в файл (Plot.PlotToFile вернул False): {outputPath}. Подробности в журнале печати: {PrintSessionLog.LastPath}");
        }
    }

    /// <summary>Shows nanoCAD's preview of the active layout (Plot.DisplayPlotPreview(acFullPreview)).</summary>
    public static void DisplayPlotPreview(object comDocument)
    {
        var plot = PlotObject(comDocument);
        PlotStepException.Run("Plot.DisplayPlotPreview(acFullPreview)", () => plot.DisplayPlotPreview(NanoCadPageSetup.FullPreview));
    }

    private static dynamic ActiveLayout(object comDocument)
    {
        object? layout = null;
        // The first IDispatch call of every print and preview: repairs nanoCAD's unregistered type library (issue #11).
        NanoCadComTypeLibrary.Run("AcadDocument.ActiveLayout", () => layout = ((dynamic)comDocument).ActiveLayout);
        return layout ?? throw new InvalidOperationException("nanoCAD не вернул активный лист документа (ActiveLayout).");
    }

    private static dynamic PlotObject(object comDocument)
    {
        object? plot = null;
        NanoCadComTypeLibrary.Run("AcadDocument.Plot", () => plot = ((dynamic)comDocument).Plot);
        return plot ?? throw new InvalidOperationException("nanoCAD не вернул объект печати документа (AcadDocument.Plot).");
    }

    internal static void TrySet(string step, Action action)
    {
        try
        {
            PlotStepException.Run(step, action);
        }
        catch (PlotStepException ex)
        {
            PrintSessionLog.Write("WARN", "Не выполнено (не обязательно): " + ex.Message);
        }
    }
}

/// <summary>Page setup of the active nanoCAD layout before printing; restored step by step, failures are only logged.</summary>
internal sealed class NanoCadComLayoutSnapshot
{
    // Restore order: the plotter first (it resets the media list of the layout).
    private static readonly (string Name, Func<dynamic, object?> Get, Action<dynamic, object> Set)[] Properties =
    {
        ("ConfigName", l => l.ConfigName, (l, v) => { l.ConfigName = (string)v; l.RefreshPlotDeviceInfo(); }),
        ("CanonicalMediaName", l => l.CanonicalMediaName, (l, v) => l.CanonicalMediaName = (string)v),
        ("PaperUnits", l => l.PaperUnits, (l, v) => l.PaperUnits = v),
        ("PlotRotation", l => l.PlotRotation, (l, v) => l.PlotRotation = v),
        ("PlotType", l => l.PlotType, (l, v) => l.PlotType = v),
        ("UseStandardScale", l => l.UseStandardScale, (l, v) => l.UseStandardScale = v),
        ("StandardScale", l => l.StandardScale, (l, v) => l.StandardScale = v),
        ("CenterPlot", l => l.CenterPlot, (l, v) => l.CenterPlot = v),
        ("StyleSheet", l => l.StyleSheet, (l, v) => l.StyleSheet = (string)v),
        ("PlotWithLineweights", l => l.PlotWithLineweights, (l, v) => l.PlotWithLineweights = v)
    };

    private readonly object _layout;
    private readonly List<(int Index, object Value)> _values = new();

    private NanoCadComLayoutSnapshot(object layout)
    {
        _layout = layout;
    }

    public static NanoCadComLayoutSnapshot Capture(object layout)
    {
        var snapshot = new NanoCadComLayoutSnapshot(layout);
        var text = new List<string>();
        for (var i = 0; i < Properties.Length; i++)
        {
            try
            {
                object? value = Properties[i].Get(layout);
                if (value != null)
                {
                    snapshot._values.Add((i, value));
                    text.Add(Properties[i].Name + "=" + Convert.ToString(value, CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                PrintSessionLog.Write("WARN", $"ActiveLayout.{Properties[i].Name} не прочитан: {ex.GetType().Name}: {ex.Message}");
            }
        }

        PrintSessionLog.Info("Исходные параметры листа nanoCAD: " + string.Join("; ", text));
        return snapshot;
    }

    /// <summary>Writes the remembered values back. Never throws: the sheet is already printed.</summary>
    public void Restore()
    {
        foreach (var (index, value) in _values)
        {
            var property = Properties[index];
            NanoCadComPlot.TrySet(
                $"Восстановление ActiveLayout.{property.Name} = {Convert.ToString(value, CultureInfo.InvariantCulture)}",
                () => property.Set(_layout, value));
        }
    }
}
#endif
