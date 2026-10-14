using System.Reflection;
using System.Resources;
using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #3: after NETLOAD in nanoCAD 24, ZBP_SHOW_PANEL failed with
/// "В компоненте zwcadbatchplot.batchplotform отсутствует ресурс, соответствующий uri
/// "/ncadbatchplot;v1.0.0;component/common/views/batchplotform.xaml"" and showed the NOT_IMPLEMENTED dialogs again,
/// because resolving the pack URI loaded a second copy of NcadBatchPlot.dll.
/// </summary>
public class NetloadXamlResourceTests
{
    private const string BatchPlotFormXaml = "common/views/batchplotform.xaml";

    [Fact]
    public void BatchPlotFormPackUriResolvesToTheNetloadedAssembly()
    {
        var uri = NanoCadNetloadEmulation.FindComponentUri(TestPaths.PluginPath, BatchPlotFormXaml);
        var (name, version) = ParsePackUri(uri);

        using var nanoCad = new NanoCadNetloadEmulation(TestPaths.PluginPath);
        var plugin = nanoCad.Netload();

        var resolved = NanoCadNetloadEmulation.WpfGetLoadedAssembly(name, version);

        Assert.True(ReferenceEquals(plugin, resolved),
            $"WPF resolved '{uri}' to another copy of the plugin ({resolved.Location}); "
            + "Application.LoadComponent throws 'The component does not have a resource identified by the URI'.");
        Assert.Single(nanoCad.PluginCopies);
    }

    [Fact]
    public void PackUriVersionMatchesTheAssemblyVersionExactly()
    {
        var uri = NanoCadNetloadEmulation.FindComponentUri(TestPaths.PluginPath, BatchPlotFormXaml);
        var (_, version) = ParsePackUri(uri);
        var assemblyVersion = AssemblyName.GetAssemblyName(TestPaths.PluginPath).Version;

        // WPF compares with Version.Equals: "1.0.0" (Revision = -1) is not equal to 1.0.0.0.
        Assert.Equal(assemblyVersion, new Version(version));
    }

    [Fact]
    public void PluginContainsBatchPlotFormBaml()
    {
        var plugin = Assembly.LoadFile(TestPaths.PluginPath);
        using var stream = plugin.GetManifestResourceStream(plugin.GetName().Name + ".g.resources");
        Assert.NotNull(stream);

        using var reader = new ResourceReader(stream!);
        var names = reader.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToList();
        Assert.Contains("common/views/batchplotform.baml", names);
    }

    /// <summary>"/NcadBatchPlot;V1.0.0.0;component/..." → ("NcadBatchPlot", "1.0.0.0").</summary>
    private static (string Name, string Version) ParsePackUri(string uri)
    {
        var parts = uri.TrimStart('/').Split(';');
        var version = parts.Skip(1).FirstOrDefault(p => p.Length > 1 && (p[0] == 'v' || p[0] == 'V') && char.IsDigit(p[1]));
        Assert.NotNull(version);
        return (parts[0], version![1..]);
    }
}
