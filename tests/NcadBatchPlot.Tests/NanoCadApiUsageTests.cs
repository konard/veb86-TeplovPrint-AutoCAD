using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #3: NETLOAD and ZBP_SHOW_PANEL showed "Mgd / NOT_IMPLEMENTED / RefreshList | get_MenuBar | get_MenuGroups"
/// dialogs because the plugin called members that nanoCAD 24 only stubs out.
/// </summary>
public class NanoCadApiUsageTests
{
    [Fact]
    public void StubScannerFindsMembersReportedInIssue()
    {
        var stubs = NanoCadApiStubs.FindStubs(TestPaths.NanoCadApiDir);

        Assert.Contains("HostMgd.ApplicationServices.Application::get_MenuBar()", stubs);
        Assert.Contains("HostMgd.ApplicationServices.Application::get_MenuGroups()", stubs);
        Assert.Contains("HostMgd.PlottingServices.PlotConfigManager::RefreshList(HostMgd.PlottingServices.RefreshCode)", stubs);
    }

    [Fact]
    public void PluginDoesNotCallNotImplementedNanoCadMembers()
    {
        var stubs = NanoCadApiStubs.FindStubs(TestPaths.NanoCadApiDir);
        var used = NanoCadApiStubs.FindHostApiReferences(TestPaths.PluginPath);

        Assert.NotEmpty(used);
        var notImplemented = used.Where(stubs.Contains).OrderBy(m => m, StringComparer.Ordinal).ToList();
        Assert.True(notImplemented.Count == 0,
            "NcadBatchPlot.dll calls nanoCAD 24 members that only show the 'Mgd NOT_IMPLEMENTED' dialog:"
            + Environment.NewLine + string.Join(Environment.NewLine, notImplemented));
    }
}
