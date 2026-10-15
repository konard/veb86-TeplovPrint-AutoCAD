using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #9: "Лог в папку temp не записывается ... Сделай так, что бы лог делался всегда при "начать печати".
/// Каждая новая печать заменяла старый лог". The print log was kept in memory, saved to %APPDATA% only at the end
/// of a run and only with the "generate print log" option, so a crash of nanoCAD lost it entirely.
/// </summary>
[Collection(PrintSessionLogCollection.Name)]
public class PrintSessionLogTests : IDisposable
{
    private const string LogType = "ZwcadBatchPlot.PrintSessionLog";
    private readonly string _directory = Directory.CreateTempSubdirectory("teplov-print-log-").FullName;

    public void Dispose()
    {
        Invoke("Stop", "test finished");
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void LogIsWrittenToTheTempFolder()
    {
        var path = (string)Log().GetProperty("DefaultPath", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(Path.GetFullPath(path)));
        Assert.EndsWith(".log", path);
    }

    [Fact]
    public void EveryLineIsOnDiskBeforeTheNextStep()
    {
        var path = Start("Начать печать");

        Invoke("Write", "STEP", "SetWindowToPlot");

        // nanoCAD may die on the next native call: the step has to be readable while the log is still open.
        Assert.Contains("SetWindowToPlot", ReadShared(path));
    }

    [Fact]
    public void NewPrintReplacesThePreviousLog()
    {
        var path = Start("first print");
        Invoke("Write", "INFO", "line of the first print");

        Start("second print");

        var text = ReadShared(path);
        Assert.DoesNotContain("line of the first print", text);
        Assert.Contains("second print", text);
    }

    [Fact]
    public void HeaderDescribesTheEnvironment()
    {
        var text = ReadShared(Start("Начать печать"));

        Assert.Contains(RuntimeInformation.FrameworkDescription, text);
        Assert.Contains(Environment.OSVersion.VersionString, text);
        Assert.Contains("NcadBatchPlot", text);
    }

    [Fact]
    public void ErrorsAreLoggedWithTypeHResultAndInnerExceptions()
    {
        var path = Start("Начать печать");
        var error = new InvalidOperationException("outer", new SEHException("External component has thrown an exception."));

        Invoke("Error", "PlotToFile", error);

        var text = ReadShared(path);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("SEHException", text);
        Assert.Contains("0x80004005", text);
    }

    [Fact]
    public void LabelledPlotStepsAreLoggedBeforeTheyRun()
    {
        var path = Start("Начать печать");
        string? logAtStepTime = null;

        NanoCadPrintRouteTests.PluginType("ZwcadBatchPlot.PlotStepException")
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { "SetCanonicalMediaName(ISO_A3)", new Action(() => logAtStepTime = ReadShared(path)) });

        Assert.Contains("SetCanonicalMediaName(ISO_A3)", logAtStepTime);
    }

    [Fact]
    public void TraceMessagesGoToThePrintLogWhilePrinting()
    {
        var path = Start("Начать печать");

        NanoCadPrintRouteTests.PluginType("ZwcadBatchPlot.TraceLog")
            .GetMethod("Write", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { "ValidatePlotInfo: skipped" });

        Assert.Contains("ValidatePlotInfo: skipped", ReadShared(path));
    }

    [Theory]
    [InlineData("ZwcadBatchPlot.BatchPlotForm::PrintOrStop")]
    [InlineData("ZwcadBatchPlot.RectangleBatchPlotForm::PrintOrStop")]
    public void EveryPrintStartStartsTheLog(string printButton)
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);

        Assert.True(graph.FindPath(printButton, LogType + "::Start") != null,
            printButton + " does not start the %TEMP% print log.");
    }

    private string Start(string title)
        => (string)Log().GetMethod("StartAt", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { Path.Combine(_directory, "print.log"), title })!;

    private static object? Invoke(string method, params object[] args)
        => Log().GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(null, args);

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Type Log() => NanoCadPrintRouteTests.PluginType(LogType);
}

/// <summary>The print log is process-wide: its tests must not run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class PrintSessionLogCollection
{
    public const string Name = "PrintSessionLog";
}
