using System;
using System.Diagnostics;
using System.IO;

namespace ZwcadBatchPlot;

/// <summary>
/// Диагностический журнал загрузки плагина. По умолчанию выключен.
/// Включается переменной окружения TEPLOV_PRINT_TRACE=1 (до запуска CAD):
/// сообщения пишутся в System.Diagnostics.Trace и в %TEMP%\TeplovPrint-trace.log.
/// </summary>
internal static class TraceLog
{
    public static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("TEPLOV_PRINT_TRACE"), "1", StringComparison.Ordinal);

    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{typeof(TraceLog).Assembly.GetHashCode():X8}] {message}";
        try
        {
            Trace.WriteLine(line, "TeplovPrint");
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "TeplovPrint-trace.log"), line + Environment.NewLine);
        }
        catch
        {
        }
    }
}
