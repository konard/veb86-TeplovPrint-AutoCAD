using System.Reflection;
using System.Resources;
using System.Text;
using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #13: the main window of nanoCAD has a "Принтер" list next to CTB, and PDF (also PNG and JPG, which are made
/// from the PDF) is printed with the printer chosen there. Before, nanoCAD always printed with the automatically
/// chosen internal PDF plotter, and another printer could only be set with the TEPLOV_PRINT_PDF_DEVICE variable.
/// </summary>
public class NanoCadPrinterSelectionTests
{
    private const string SelectorType = "ZwcadBatchPlot.PlotDeviceSelector";
    private const string Installer = "ZwcadBatchPlot.AcadPlotterInstaller";
    private const string Form = "ZwcadBatchPlot.BatchPlotForm";

    private static readonly string[] NanoCadDevices =
    {
        "Нет", "Internal PDF Plotter", "Internal DWF Plotter", "Microsoft Print to PDF", "My PDF.pc3", "LA_pdf.pc3", "HP LaserJet"
    };

    [Theory]
    [InlineData("Microsoft Print to PDF", "Microsoft Print to PDF")]
    [InlineData("my pdf.pc3", "My PDF.pc3")]
    [InlineData("  HP LaserJet  ", "HP LaserJet")]
    public void ChosenPrinterIsUsedForPdf(string chosen, string expected)
    {
        Assert.Equal(expected, ResolvePdfDevice(NanoCadDevices, chosen, null));
    }

    [Fact]
    public void ChosenPrinterWinsOverTheEnvironmentOverride()
    {
        Assert.Equal("HP LaserJet", ResolvePdfDevice(NanoCadDevices, "HP LaserJet", "My PDF.pc3"));
    }

    [Theory]
    // Not chosen yet, or the chosen printer was removed: TEPLOV_PRINT_PDF_DEVICE, then the internal PDF plotter.
    [InlineData(null, null, "Internal PDF Plotter")]
    [InlineData("", "My PDF.pc3", "My PDF.pc3")]
    [InlineData("Removed printer", null, "Internal PDF Plotter")]
    [InlineData("Removed printer", "My PDF.pc3", "My PDF.pc3")]
    // "None" and the AutoCAD templates cannot print in nanoCAD even if they are saved as the choice.
    [InlineData("Нет", null, "Internal PDF Plotter")]
    [InlineData("LA_pdf.pc3", null, "Internal PDF Plotter")]
    public void WithoutAnInstalledChoiceThePdfDeviceIsChosenAutomatically(string? chosen, string? environment, string expected)
    {
        Assert.Equal(expected, ResolvePdfDevice(NanoCadDevices, chosen, environment));
    }

    [Fact]
    public void PrinterListHasEveryNanoCadDeviceExceptNoneAndAutoCadTemplates()
    {
        var choices = ListPrinterChoices(new[]
        {
            "None", "Нет", "", "  ", "Internal PDF Plotter", "LA_pdf.pc3", "LA_dwf.pc3", "Microsoft Print to PDF",
            "internal pdf plotter", "HP LaserJet", "Internal DWF Plotter"
        });

        // The order of nanoCAD is kept.
        Assert.Equal(
            new[] { "Internal PDF Plotter", "Microsoft Print to PDF", "HP LaserJet", "Internal DWF Plotter" },
            choices);
    }

    [Fact]
    public void AutomaticPdfDeviceIsSelectedInThePrinterList()
    {
        var automatic = ResolvePdfDevice(NanoCadDevices, null, null);
        Assert.Contains(automatic, ListPrinterChoices(NanoCadDevices));
    }

    [Fact]
    public void NanoCadBuildOffersThePrinterChoice()
    {
        var installer = Plugin().GetType(Installer)!;
        var allows = installer.GetProperty("AllowsPdfPrinterChoice", BindingFlags.Public | BindingFlags.Static)!;
        Assert.True((bool)allows.GetValue(null)!);
    }

    [Fact]
    public void NanoCadPrintsWithThePrinterChosenInTheMainWindow()
    {
        var installer = Plugin().GetType(Installer)!;
        var resolve = installer.GetMethod("ResolvePdfPlotDevice", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal("Microsoft Print to PDF", resolve.Invoke(null, new object?[] { NanoCadDevices, "Microsoft Print to PDF" }));
        Assert.Equal("Internal PDF Plotter", resolve.Invoke(null, new object?[] { NanoCadDevices, null }));
    }

    [Theory]
    // The main window, the rectangle batch window and ZBP_SINGLE_PLOT print with the same chosen printer.
    [InlineData(Form + "::LoadPlotOptions")]
    [InlineData("ZwcadBatchPlot.RectangleBatchPlotForm::LoadPlotOptions")]
    [InlineData("ZwcadBatchPlot.BatchPlotCommands::ResolveSinglePlotOptions")]
    public void PrintWindowsResolveThePdfDeviceFromTheChosenPrinter(string entry)
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);
        Assert.True(graph.Contains(entry), entry + " was not found in the plugin.");
        Assert.True(graph.FindPath(entry, SelectorType + "::ResolvePdfDevice") != null,
            entry + " does not resolve the PDF device through PlotDeviceSelector.ResolvePdfDevice (the chosen printer).");
    }

    [Fact]
    public void MainWindowListsThePrintersAndSavesTheChoice()
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);
        Assert.NotNull(graph.FindPath(Form + "::LoadPlotOptions", SelectorType + "::ListPrinterChoices"));
        Assert.True(graph.Contains(Form + "::Printer_SelectionChanged"), "The «Принтер» list has no selection handler.");
        // Wired from the XAML (generated Connect), and the choice is kept in the settings for the next start.
        Assert.NotEmpty(graph.FindTransitiveCallers(Form + "::Printer_SelectionChanged"));
        Assert.NotNull(graph.FindPath(Form + "::Printer_SelectionChanged", "ZwcadBatchPlot.AppSettings::set_PdfPrinter"));
    }

    [Fact]
    public void MainWindowXamlHasThePrinterListNextToCtb()
    {
        var baml = Encoding.UTF8.GetString(BatchPlotFormBaml());
        var style = baml.IndexOf("_styleCombo", StringComparison.Ordinal);
        var printer = baml.IndexOf("_printerCombo", StringComparison.Ordinal);
        var button = baml.IndexOf("_printButton", StringComparison.Ordinal);

        Assert.True(style >= 0, "_styleCombo (CTB) was not found in batchplotform.baml.");
        Assert.True(printer >= 0, "_printerCombo was not found in batchplotform.baml.");
        Assert.Contains("Принтер:", baml);
        // Declared right after the CTB list, in the same grid of output settings.
        Assert.True(style < printer && printer < button, "_printerCombo is not placed next to the CTB list.");
    }

    private static byte[] BatchPlotFormBaml()
    {
        var plugin = Plugin();
        using var stream = plugin.GetManifestResourceStream(plugin.GetName().Name + ".g.resources");
        Assert.NotNull(stream);
        using var reader = new ResourceReader(stream!);
        reader.GetResourceData("common/views/batchplotform.baml", out _, out var data);
        return data;
    }

    private static string? ResolvePdfDevice(IEnumerable<string> devices, string? chosen, string? environment)
        => (string?)Selector().GetMethod("ResolvePdfDevice", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object?[] { devices, chosen, environment });

    private static List<string> ListPrinterChoices(IEnumerable<string> devices)
        => (List<string>)Selector().GetMethod("ListPrinterChoices", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object?[] { devices })!;

    private static Assembly Plugin() => Assembly.LoadFile(TestPaths.PluginPath);

    /// <summary>PlotDeviceSelector has no host dependencies, so it runs without nanoCAD.</summary>
    private static Type Selector()
        => Plugin().GetType(SelectorType)
           ?? throw new InvalidOperationException(SelectorType + " was not found in " + TestPaths.PluginPath);
}
