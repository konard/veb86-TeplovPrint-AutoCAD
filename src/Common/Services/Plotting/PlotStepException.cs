using System;
using System.Runtime.InteropServices;

namespace ZwcadBatchPlot;

/// <summary>
/// A PlotSettingsValidator / PlotSettings call that the CAD refused, named after the configuration step.
/// nanoCAD 24 reports failures of its native plot code only as a bare SEHException
/// "External component has thrown an exception" (issue #7), so without the step name and the exception type
/// the batch report does not say which setting was refused. Has no CAD dependencies so it is unit-tested without the host.
/// </summary>
public sealed class PlotStepException : Exception
{
    public PlotStepException(string step, Exception innerException)
        : base(Describe(step, innerException), innerException)
    {
        Step = step;
    }

    /// <summary>Configuration step, e.g. "SetCurrentStyleSheet(monochrome.ctb)".</summary>
    public string Step { get; }

    /// <summary>The CAD native code threw something other than its own error type (SEHException).</summary>
    public bool IsNativeFailure => InnerException is SEHException;

    /// <summary>"Step: ExceptionType 0xHRESULT: message" — one line for the batch report and the trace log.</summary>
    public static string Describe(string step, Exception error)
    {
        if (error == null)
        {
            throw new ArgumentNullException(nameof(error));
        }

        return $"{step}: {error.GetType().Name} 0x{error.HResult:X8}: {error.Message}";
    }

    /// <summary>Runs one configuration step and wraps its failure into <see cref="PlotStepException"/>.</summary>
    public static void Run(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (!(ex is PlotStepException))
        {
            var failure = new PlotStepException(step, ex);
            TraceLog.Write("Plot step failed: " + failure.Message);
            throw failure;
        }
    }
}
