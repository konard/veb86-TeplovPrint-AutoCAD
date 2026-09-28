using System;
using System.Threading;

namespace ZwcadBatchPlot;


internal static class BatchPlotHostProgress
{
    private static int _suppressEngineDialog;

    /// <summary>Признак того, что ход пакетной печати уже отображается окном плагина.</summary>
    internal static bool SuppressEnginePlotDialog => Volatile.Read(ref _suppressEngineDialog) > 0;

    /// <summary>Входит в режим собственного окна прогресса; допускает вложенные вызовы.</summary>
    internal static void Begin() => Interlocked.Increment(ref _suppressEngineDialog);

    /// <summary>Выходит из режима собственного окна прогресса.</summary>
    internal static void End() => Interlocked.Decrement(ref _suppressEngineDialog);
}
