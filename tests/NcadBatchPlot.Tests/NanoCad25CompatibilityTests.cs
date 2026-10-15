using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #11: the same NcadBatchPlot.dll (built against the nanoCAD 24 API) is loaded by nanoCAD 25.
/// Both hosts run .NET 6 (nanoCAD 25.0.7046 log: ".NET 6.0.36"), so a separate build is only needed
/// if nanoCAD 25 dropped or changed a member the plugin calls, or stubbed it out ("Mgd NOT_IMPLEMENTED").
/// </summary>
public class NanoCad25CompatibilityTests
{
    [Fact]
    public void EveryNanoCadMemberThePluginCallsExistsInNanoCad25()
    {
        var defined = NanoCadApiStubs.FindDefinedMembers(TestPaths.NanoCad25ApiDir);
        var used = NanoCadApiStubs.FindHostApiReferences(TestPaths.PluginPath);

        Assert.NotEmpty(used);
        var missing = used.Where(m => !defined.Contains(m)).OrderBy(m => m, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "NcadBatchPlot.dll calls members that nanoCAD 25 does not have (MissingMethodException there):"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void PluginDoesNotCallMembersNotImplementedInNanoCad25()
    {
        var stubs = NanoCadApiStubs.FindStubs(TestPaths.NanoCad25ApiDir);
        var used = NanoCadApiStubs.FindHostApiReferences(TestPaths.PluginPath);

        Assert.NotEmpty(stubs);
        var notImplemented = used.Where(stubs.Contains).OrderBy(m => m, StringComparer.Ordinal).ToList();
        Assert.True(notImplemented.Count == 0,
            "NcadBatchPlot.dll calls nanoCAD 25 members that only show the 'Mgd NOT_IMPLEMENTED' dialog:"
            + Environment.NewLine + string.Join(Environment.NewLine, notImplemented));
    }
}
