using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #11.
/// nanoCAD 24: every sheet showed the modal "COM / NOT_IMPLEMENTED / CPlotObj::put_QuietErrorMode" dialog
/// (the sheet was printed after "skip once"): nanoCAD's COM Plot object does not implement the QuietErrorMode setter.
/// nanoCAD 25: every sheet failed with "AcadDocument.ActiveLayout: COMException 0x8002801D: Библиотека не зарегистрирована
/// (TYPE_E_LIBNOTREGISTERED)": nanoCAD's COM objects could not find their registered type library, so neither the
/// dynamic binder (IDispatch.GetTypeInfo) nor IDispatch.GetIDsOfNames worked.
/// </summary>
public class NanoCadComTypeLibraryTests
{
    private const string TypeLibraryType = "ZwcadBatchPlot.NanoCadComTypeLibrary";
    private const string LibraryInfoType = "ZwcadBatchPlot.NanoCadTypeLibraryInfo";
    private const int TypeLibNotRegistered = unchecked((int)0x8002801D);

    [Fact]
    public void PluginNeverTouchesPlotQuietErrorMode()
    {
        // With dynamic the member name is only an ldstr operand of the call site binder.
        Assert.DoesNotContain(PluginUserStrings(), s => s.Contains("QuietErrorMode", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryComRouteGoesThroughTheTypeLibraryRepair()
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);

        // ActiveLayout is the first IDispatch call of both the plot and the preview route.
        Assert.NotNull(graph.FindPath("ZwcadBatchPlot.NanoCadComPlot::CaptureLayout", TypeLibraryType + "::Run"));
        Assert.NotNull(graph.FindPath("ZwcadBatchPlot.NanoCadComPlot::ApplyPageSetup", TypeLibraryType + "::Run"));
        Assert.NotNull(graph.FindPath(TypeLibraryType + "::Run", TypeLibraryType + "::TryRegisterOnce"));
    }

    [Theory]
    [InlineData("Библиотека не зарегистрирована. (0x8002801D (TYPE_E_LIBNOTREGISTERED))")]
    // The first sheet in the nanoCAD 25 log: a stale IErrorInfo text with the same HRESULT.
    [InlineData("Для документа XML должен существовать документ более высокого уровня.")]
    public void TypeLibraryErrorIsRecognisedByItsHResult(string message)
    {
        var step = StepFailure("AcadDocument.ActiveLayout", new COMException(message, TypeLibNotRegistered));

        Assert.True(IsNotRegistered(step));
        Assert.True(IsNotRegistered(new TargetInvocationException(new COMException(message, TypeLibNotRegistered))));
    }

    [Fact]
    public void OtherComErrorsAreNotTypeLibraryErrors()
    {
        Assert.False(IsNotRegistered(StepFailure("AcadDocument.ActiveLayout", new COMException("E_FAIL", unchecked((int)0x80004005)))));
        Assert.False(IsNotRegistered(new InvalidOperationException()));
    }

    [Fact]
    public void StepIsRepeatedOnceAfterTheTypeLibraryIsRegistered()
    {
        var calls = 0;
        var registrations = 0;

        RunStep("AcadDocument.ActiveLayout", () =>
        {
            if (++calls == 1)
            {
                throw new COMException("Библиотека не зарегистрирована.", TypeLibNotRegistered);
            }
        }, () => ++registrations > 0);

        Assert.Equal(2, calls);
        Assert.Equal(1, registrations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnregisteredTypeLibraryErrorTellsHowToRepairNanoCad(bool registrationSucceeds)
    {
        var calls = 0;
        var cause = new COMException("Библиотека не зарегистрирована.", TypeLibNotRegistered);

        var failure = Assert.Throws<TargetInvocationException>(() => RunStep(
            "AcadDocument.ActiveLayout",
            () =>
            {
                calls++;
                throw cause;
            },
            () => registrationSucceeds)).InnerException!;

        Assert.Equal(registrationSucceeds ? 2 : 1, calls);
        Assert.Contains("TYPE_E_LIBNOTREGISTERED", failure.Message);
        Assert.Contains("AcadDocument.ActiveLayout", failure.Message);
        Assert.Contains("Восстановить", failure.Message);
        Assert.Same(cause, failure.InnerException!.InnerException);
    }

    [Fact]
    public void OtherFailuresAreNeitherRepairedNorRepeated()
    {
        var calls = 0;
        var registrations = 0;

        var failure = Assert.Throws<TargetInvocationException>(() => RunStep(
            "AcadDocument.ActiveLayout",
            () =>
            {
                calls++;
                throw new COMException("E_FAIL", unchecked((int)0x80004005));
            },
            () => ++registrations > 0)).InnerException!;

        Assert.Equal("ZwcadBatchPlot.PlotStepException", failure.GetType().FullName);
        Assert.Equal(1, calls);
        Assert.Equal(0, registrations);
    }

    [Fact]
    public void OnlyUnregisteredLibrariesDefiningThePlotMembersAreRegistered()
    {
        var oda = Guid.NewGuid();
        var other = Guid.NewGuid();
        var registered = Guid.NewGuid();
        var libraries = new[]
        {
            Library(@"C:\nanoCAD 25\OdaX_25.dll", oda, isRegistered: false, definesPlotMembers: true),
            // The same library seen twice (a .tlb next to the module): registered once.
            Library(@"C:\nanoCAD 25\OdaX_25.tlb", oda, isRegistered: false, definesPlotMembers: true),
            Library(@"C:\nanoCAD 25\Other.dll", other, isRegistered: false, definesPlotMembers: false),
            Library(@"C:\nanoCAD 25\Registered.dll", registered, isRegistered: true, definesPlotMembers: true),
        };

        var select = PluginType(TypeLibraryType).GetMethod("SelectForRegistration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var typed = Array.CreateInstance(PluginType(LibraryInfoType), libraries.Length);
        Array.Copy(libraries, typed, libraries.Length);
        var selected = ((System.Collections.IEnumerable)select.Invoke(null, new object[] { typed })!).Cast<object>().ToList();

        var path = Assert.Single(selected).GetType().GetProperty("Path")!;
        Assert.Equal(@"C:\nanoCAD 25\OdaX_25.dll", path.GetValue(selected[0]));
    }

    // Checks the oleaut32 P/Invokes on a real Windows (CI runs on windows-latest): OLE Automation's own library is always registered.
    [Fact]
    public void DescribesARegisteredWindowsTypeLibrary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Environment.SystemDirectory, "stdole2.tlb");
        var describe = PluginType(TypeLibraryType).GetMethod("TryDescribe", BindingFlags.NonPublic | BindingFlags.Static)!;
        var library = describe.Invoke(null, new object[] { path })!;

        Assert.Equal(new Guid("00020430-0000-0000-C000-000000000046"), Property(library, "LibId"));
        Assert.Equal(true, Property(library, "IsRegistered"));
        Assert.False(string.IsNullOrEmpty((string?)Property(library, "RegisteredPath")));
        Assert.Equal(false, Property(library, "DefinesPlotMembers"));
        Assert.Null(describe.Invoke(null, new object[] { Path.Combine(Environment.SystemDirectory, "kernel32.dll") }));
    }

    private static object? Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);

    private static object Library(string path, Guid libId, bool isRegistered, bool definesPlotMembers)
        => Activator.CreateInstance(PluginType(LibraryInfoType), path, libId, (ushort)1, (ushort)0, isRegistered, (string?)null, definesPlotMembers)!;

    private static bool IsNotRegistered(Exception error)
        => (bool)PluginType(TypeLibraryType)
            .GetMethod("IsNotRegistered", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { error })!;

    private static void RunStep(string step, Action action, Func<bool> register)
        => PluginType(TypeLibraryType)
            .GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(string), typeof(Action), typeof(Func<bool>) })!
            .Invoke(null, new object[] { step, action, register });

    private static Exception StepFailure(string step, Exception cause)
        => (Exception)Activator.CreateInstance(PluginType("ZwcadBatchPlot.PlotStepException"), step, cause)!;

    private static Type PluginType(string name) => NanoCadPrintRouteTests.PluginType(name);

    private static IEnumerable<string> PluginUserStrings()
    {
        using var stream = File.OpenRead(TestPaths.PluginPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var result = new List<string>();
        var handle = MetadataTokens.UserStringHandle(1);
        while (!handle.IsNil)
        {
            result.Add(md.GetUserString(handle));
            handle = md.GetNextHandle(handle);
        }

        Assert.Contains(result, s => s.Contains("ActiveLayout", StringComparison.Ordinal));
        return result;
    }
}
