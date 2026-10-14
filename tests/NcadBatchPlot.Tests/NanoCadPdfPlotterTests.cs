using System.Reflection;
using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #5: in nanoCAD 24 "Начать печать" failed with
/// "Ошибка печати: Конфигурация плоттера LA_pdf неполна: Не удалось прочитать путь драйвера из штатного плоттера
/// текущего CAD; замена конфигурации LA остановлена".
/// Before every print the plugin rebuilt LA_pdf.pc3 from its AutoCAD template and copied the HDI driver path from
/// AutoCAD's stock "DWG To PDF.pc3" (AcadPlotterInstaller.TryWriteLaPia2PairFromTemplate). nanoCAD has no such
/// plotter and no Autodesk HDI drivers: it plots PDF through its own internal PDF plotter.
/// </summary>
public class NanoCadPdfPlotterTests
{
    private const string Installer = "ZwcadBatchPlot.AcadPlotterInstaller";
    private const string SelectorType = "ZwcadBatchPlot.PlotDeviceSelector";

    [Theory]
    [InlineData(Installer + "::TryWriteLaPia2PairFromTemplate")]
    [InlineData(Installer + "::TryInstallForcedPia2PdfPlotter")]
    public void NanoCadBuildNeverRebuildsLaPlotterFromAutoCadDriver(string autoCadOnlyMethod)
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);
        Assert.True(graph.Contains(autoCadOnlyMethod), autoCadOnlyMethod + " was not found in the plugin.");

        var chains = graph.FindTransitiveCallers(autoCadOnlyMethod)
            .Where(caller => !caller.StartsWith(Installer + "::", StringComparison.Ordinal))
            .OrderBy(caller => caller, StringComparer.Ordinal)
            .Select(caller => string.Join(" -> ", graph.FindPath(caller, autoCadOnlyMethod)!))
            .ToList();

        Assert.True(chains.Count == 0,
            "NcadBatchPlot.dll still rebuilds LA_*.pc3 from AutoCAD's 'DWG To PDF.pc3' driver, which nanoCAD does not have:"
            + Environment.NewLine + string.Join(Environment.NewLine, chains));
    }

    [Theory]
    // nanoCAD help: "Internal PDF Plotter"; the Russian UI translates "internal" as "встроенный".
    [InlineData("Internal PDF Plotter", "LA_pdf.pc3", "Microsoft Print to PDF", "Internal PDF Plotter")]
    [InlineData("Встроенный PDF плоттер", "LA_pdf.pc3", "Нет", "Встроенный PDF плоттер")]
    [InlineData("nanoCAD PDF", "Microsoft Print to PDF", "Adobe PDF", "nanoCAD PDF")]
    [InlineData("DWG To PDF.pc3", "Microsoft Print to PDF", "", "DWG To PDF.pc3")]
    [InlineData("Microsoft Print to PDF", "Internal PDF Plotter", "LA_pdf.pc3", "Internal PDF Plotter")]
    public void PdfDeviceSelectionPrefersTheNanoCadInternalPlotter(string first, string second, string third, string expected)
    {
        Assert.Equal(expected, SelectPdfDevice(new[] { first, second, third }, null));
    }

    [Fact]
    public void BundledAutoCadTemplatesAreNeverSelectedOnNanoCad()
    {
        // LA_pdf.pc3 points to pdfplot16.hdi of AutoCAD: listing it in nanoCAD does not make it printable.
        Assert.Null(SelectPdfDevice(new[] { "LA_pdf.pc3", "LA_png.pc3", "LA_dwf.pc3" }, null));
        Assert.Null(SelectDwfDevice(new[] { "LA_dwf.pc3", "LA_pdf.pc3" }));
    }

    [Fact]
    public void SystemPdfPrintersAreNotUsedForFileOutput()
    {
        // Windows printers ignore PlotToFile name and either ask for a file name or write a printer spool file.
        Assert.Null(SelectPdfDevice(new[] { "Microsoft Print to PDF", "Adobe PDF", "PDF24", "Fax" }, null));
    }

    [Fact]
    public void ExplicitPdfDeviceOverrideWinsWhenItIsInstalled()
    {
        var devices = new[] { "Internal PDF Plotter", "My PDF.pc3" };
        Assert.Equal("My PDF.pc3", SelectPdfDevice(devices, "my pdf.pc3"));
        Assert.Equal("Internal PDF Plotter", SelectPdfDevice(devices, "Missing.pc3"));
    }

    [Theory]
    [InlineData("Internal DWF Plotter", "LA_dwf.pc3", "Internal DWFx Plotter", "Internal DWF Plotter")]
    [InlineData("DWF6 ePlot.pc3", "Internal DWFx Plotter", "LA_dwf.pc3", "DWF6 ePlot.pc3")]
    public void DwfDeviceSelectionSkipsBundledTemplateAndDwfx(string first, string second, string third, string expected)
    {
        Assert.Equal(expected, SelectDwfDevice(new[] { first, second, third }));
    }

    private static string? SelectPdfDevice(IEnumerable<string> devices, string? preferred)
        => (string?)Selector().GetMethod("SelectPdfDevice", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object?[] { devices, preferred });

    private static string? SelectDwfDevice(IEnumerable<string> devices)
        => (string?)Selector().GetMethod("SelectDwfDevice", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object?[] { devices });

    /// <summary>PlotDeviceSelector has no host dependencies, so it runs without nanoCAD.</summary>
    private static Type Selector()
    {
        var plugin = Assembly.LoadFile(TestPaths.PluginPath);
        return plugin.GetType(SelectorType)
               ?? throw new InvalidOperationException(SelectorType + " was not found in " + TestPaths.PluginPath);
    }
}
