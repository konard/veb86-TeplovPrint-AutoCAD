using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #9: in nanoCAD 24 "Начать печать" showed the progress window and nanoCAD closed without any message.
/// 1.0.3 (issue #7) applied the job settings to the database-resident layout (Layout.UpgradeOpen + native
/// PlotSettingsValidator calls on it, Layout.CopyFrom to restore it) and then "plotted" through the managed
/// PlotEngine. Native failures there are not catchable in .NET 6 (access violation), and the managed nanoCAD 24
/// PlotEngine does nothing anyway: every Begin*/End* method of HostMgd.PlottingServices.PlotEngine is empty,
/// so even without the crash no file could be produced. nanoCAD prints through its COM API (Document.Plot.PlotToFile).
/// </summary>
public class NanoCadPrintRouteTests
{
    private const string PlotterService = "ZwcadBatchPlot.PlotterService";
    private const string ComPlot = "ZwcadBatchPlot.NanoCadComPlot";
    private const string PlotFactory = "HostMgd.PlottingServices.PlotFactory";

    [Theory]
    [InlineData("BeginPlot")]
    [InlineData("BeginDocument")]
    [InlineData("BeginPage")]
    [InlineData("BeginGenerateGraphics")]
    [InlineData("EndGenerateGraphics")]
    [InlineData("EndPage")]
    [InlineData("EndDocument")]
    public void NanoCadManagedPlotEngineDoesNotPlot(string method)
    {
        // Evidence for the route below: the real hostmgd.dll of nanoCAD 24 has no plotting code in PlotEngine.
        var body = HostMethodBody("HostMgd.PlottingServices.PlotEngine", method);

        // "ret", or "ldarg.0; ldarg.1; stfld m_plotProgress; ret" for BeginPlot: no call into native code at all.
        Assert.DoesNotContain(body, b => b == 0x28 || b == 0x6F || b == 0x29);
    }

    [Theory]
    [InlineData("PlotDatabase", PlotFactory + "::CreatePublishEngine")]
    [InlineData("PreviewDatabase", PlotFactory + "::CreatePreviewEngine")]
    public void NanoCadNeverOutputsThroughTheEmptyManagedPlotEngine(string entry, string engineFactory)
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);

        var path = graph.FindPath($"{PlotterService}::{entry}", engineFactory);
        Assert.True(path == null, "nanoCAD plots through the empty managed PlotEngine: " + Describe(path));
    }

    [Theory]
    [InlineData("PlotDatabase", ComPlot + "::PlotToFile")]
    [InlineData("PreviewDatabase", ComPlot + "::DisplayPlotPreview")]
    public void NanoCadOutputsThroughItsComPlotObject(string entry, string comOutput)
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);

        Assert.True(graph.FindPath($"{PlotterService}::{entry}", comOutput) != null,
            $"{entry} does not reach {comOutput}: nanoCAD has no other working print route.");
    }

    [Theory]
    [InlineData("PlotDatabase", "Teigha.DatabaseServices.DBObject::UpgradeOpen")]
    [InlineData("PlotDatabase", "Teigha.DatabaseServices.PlotSettingsValidator::SetPlotWindowArea")]
    [InlineData("PlotDatabase", "Teigha.DatabaseServices.PlotSettingsValidator::SetPlotType")]
    [InlineData("PreviewDatabase", "Teigha.DatabaseServices.DBObject::UpgradeOpen")]
    [InlineData("PreviewDatabase", "Teigha.DatabaseServices.PlotSettingsValidator::SetPlotWindowArea")]
    public void NanoCadNeverRewritesTheLayoutThroughNativePlotSettings(string entry, string crashingCall)
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);

        var path = graph.FindPath($"{PlotterService}::{entry}", crashingCall);
        Assert.True(path == null, "nanoCAD still writes the page setup through the native plot settings API: " + Describe(path));
    }

    [Fact]
    public void PlottedFileWithAnAppendedExtensionIsMovedToTheRequestedPath()
    {
        var directory = Directory.CreateTempSubdirectory("teplov-ncad-output-").FullName;
        try
        {
            var requested = Path.Combine(directory, "01_План.pdf");
            File.WriteAllText(requested + ".pdf", "%PDF-1.4");

            Assert.True(ResolvePlottedFile(requested));
            Assert.True(File.Exists(requested));
            Assert.False(File.Exists(requested + ".pdf"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PlottedFileWithoutExtensionIsMovedToTheRequestedPath()
    {
        var directory = Directory.CreateTempSubdirectory("teplov-ncad-output-").FullName;
        try
        {
            var requested = Path.Combine(directory, "02_Разрез.pdf");
            File.WriteAllText(Path.Combine(directory, "02_Разрез"), "%PDF-1.4");

            Assert.True(ResolvePlottedFile(requested));
            Assert.True(File.Exists(requested));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MissingPlottedFileIsReported()
    {
        var directory = Directory.CreateTempSubdirectory("teplov-ncad-output-").FullName;
        try
        {
            Assert.False(ResolvePlottedFile(Path.Combine(directory, "03.pdf")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    // Scale to fit: no custom scale.
    [InlineData(false, false, 0d, 297d, 210d, 594d, 420d, null)]
    // Exact window scale: paper / window, 0.5 mm per drawing unit.
    [InlineData(false, true, 0d, 297d, 210d, 594d, 420d, 0.5d)]
    // Leave 10 mm margins on A4 (210 short side): (210 - 20) / 420.
    [InlineData(true, false, -10d, 297d, 210d, 594d, 420d, 190d / 420d)]
    // Positive margin enlarges the paper: original short side / window short side.
    [InlineData(true, false, 10d, 297d, 210d, 594d, 420d, 0.5d)]
    public void ComPageSetupScaleMatchesTheAutoCadRules(
        bool leaveMargin, bool exactScale, double marginMm, double paperW, double paperH, double windowW, double windowH, double? expected)
    {
        var resolve = PluginType("ZwcadBatchPlot.NanoCadPageSetup")
            .GetMethod("ResolveMillimetersPerDrawingUnit", BindingFlags.Public | BindingFlags.Static)!;

        var scale = (double?)resolve.Invoke(null, new object[]
        {
            leaveMargin, exactScale, marginMm, paperW, paperH, paperW, paperH, windowW, windowH
        });

        if (expected == null)
        {
            Assert.Null(scale);
        }
        else
        {
            Assert.NotNull(scale);
            Assert.Equal(expected.Value, scale!.Value, 6);
        }
    }

    private static bool ResolvePlottedFile(string requested)
        => (bool)PluginType("ZwcadBatchPlot.NanoCadPlotOutput")
            .GetMethod("TryMoveToRequestedPath", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { requested })!;

    private static string Describe(IReadOnlyList<string>? path) => path == null ? "" : string.Join(" -> ", path);

    /// <summary>These helpers have no host dependencies, so they run without nanoCAD.</summary>
    internal static Type PluginType(string name)
    {
        var plugin = Assembly.LoadFile(TestPaths.PluginPath);
        return plugin.GetType(name, throwOnError: false)
               ?? throw new InvalidOperationException(name + " was not found in " + TestPaths.PluginPath);
    }

    private static byte[] HostMethodBody(string typeName, string methodName)
    {
        using var stream = File.OpenRead(Path.Combine(TestPaths.NanoCadApiDir, "hostmgd.dll"));
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            if (md.GetString(type.Namespace) + "." + md.GetString(type.Name) != typeName)
            {
                continue;
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if (md.GetString(method.Name) == methodName && method.RelativeVirtualAddress != 0)
                {
                    return pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? Array.Empty<byte>();
                }
            }
        }

        throw new InvalidOperationException($"{typeName}::{methodName} was not found in hostmgd.dll");
    }
}
