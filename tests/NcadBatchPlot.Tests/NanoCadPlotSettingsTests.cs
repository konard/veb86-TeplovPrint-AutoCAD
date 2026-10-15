using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #7: in nanoCAD 24 every sheet failed with "AutoCAD отклонил текущие параметры печати ...
/// ISO A3_L/Degrees000: External component has thrown an exception. | ... Degrees090 | Degrees270 | Degrees180".
/// The job settings were applied by nanoCAD's native PlotSettingsValidator to a PlotSettings that is not in the
/// database (PlotInfo.OverrideSettings), the native code threw something other than OdError and the wrapper
/// reported a bare SEHException, without saying which setting was refused.
/// </summary>
public class NanoCadPlotSettingsTests
{
    private const string PlotterService = "ZwcadBatchPlot.PlotterService";
    private const string StepExceptionType = "ZwcadBatchPlot.PlotStepException";
    private const string NanoCadMessage = "External component has thrown an exception.";

    [Theory]
    [InlineData("PlotDatabase")]
    [InlineData("PreviewDatabase")]
    public void RejectedOverrideSettingsAreAppliedToTheLayoutItself(string entry)
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);

        var path = graph.FindPath($"{PlotterService}::{entry}", $"{PlotterService}::OutputWithConfiguredLayout");
        Assert.True(path != null, $"{entry} has no nanoCAD route that plots the configured layout instead of OverrideSettings.");
        Assert.NotNull(graph.FindPath($"{PlotterService}::{entry}", $"{PlotterService}::OutputWithOverrideSettings"));
    }

    [Fact]
    public void LayoutPageSetupIsRestoredAfterPlotting()
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);

        Assert.NotNull(graph.FindPath($"{PlotterService}::OutputWithConfiguredLayout", $"{PlotterService}::RestoreLayoutPlotSettings"));
    }

    [Fact]
    public void EveryPlotSettingsStepIsLabelled()
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);

        Assert.NotNull(graph.FindPath($"{PlotterService}::ConfigurePlotSettings", $"{StepExceptionType}::Run"));
        Assert.NotNull(graph.FindPath($"{PlotterService}::CreateValidatedPlotCore", $"{StepExceptionType}::Run"));
    }

    [Fact]
    public void StepErrorNamesTheRefusedSettingAndTheExceptionType()
    {
        var message = (string)StepException().GetMethod("Describe", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { "SetCurrentStyleSheet(monochrome.ctb)", new SEHException(NanoCadMessage) })!;

        Assert.Equal("SetCurrentStyleSheet(monochrome.ctb): SEHException 0x80004005: " + NanoCadMessage, message);
    }

    [Fact]
    public void RunLabelsNativeFailureAndKeepsTheCause()
    {
        var native = new SEHException(NanoCadMessage);

        var failure = RunStep("SetPlotWindowArea", () => throw native);

        Assert.Equal(StepExceptionType, failure.GetType().FullName);
        Assert.Same(native, failure.InnerException);
        Assert.StartsWith("SetPlotWindowArea: SEHException", failure.Message);
        Assert.True((bool)failure.GetType().GetProperty("IsNativeFailure")!.GetValue(failure)!);
    }

    [Fact]
    public void RunDoesNotLabelAnAlreadyLabelledFailureAgain()
    {
        var labelled = (Exception)Activator.CreateInstance(StepException(), "SetPlotWindowArea", new SEHException(NanoCadMessage))!;

        Assert.Same(labelled, RunStep("HiddenFrameWindow", () => throw labelled));
    }

    private static Exception RunStep(string step, Action action)
    {
        var run = StepException().GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        var invocation = Assert.Throws<TargetInvocationException>(() => run.Invoke(null, new object[] { step, action }));
        return invocation.InnerException!;
    }

    /// <summary>PlotStepException has no host dependencies, so it runs without nanoCAD.</summary>
    private static Type StepException()
    {
        var plugin = Assembly.LoadFile(TestPaths.PluginPath);
        return plugin.GetType(StepExceptionType)
               ?? throw new InvalidOperationException(StepExceptionType + " was not found in " + TestPaths.PluginPath);
    }
}
