using System;
using System.Windows.Threading;

namespace ZwcadBatchPlot;

/// <summary>
/// Одна печать по кнопке «Начать печать» (issue #9): начинает новый журнал %TEMP%\TeplovPrint-print.log
/// и, пока печать идёт, перехватывает исключения, дошедшие до диспетчера WPF. Иначе исключение из обработчика,
/// вызванного во время печати (окно хода работы прокачивает сообщения), закрывает CAD без сообщения об ошибке.
/// </summary>
internal sealed class PrintSessionScope : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    private PrintSessionScope(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _dispatcher.UnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>Итог печати для последней строки журнала.</summary>
    public string Result { get; set; } = "печать завершена";

    /// <summary>Исключение, перехваченное у диспетчера WPF во время печати, если было.</summary>
    public Exception? DispatcherError { get; private set; }

    public static PrintSessionScope Begin(Dispatcher dispatcher, string title)
    {
        PrintSessionLog.Start(title);
        return new PrintSessionScope(dispatcher);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dispatcher.UnhandledException -= OnDispatcherUnhandledException;
        PrintSessionLog.Stop(Result);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DispatcherError ??= e.Exception;
        PrintSessionLog.Error("Исключение в обработчике WPF во время печати (перехвачено, CAD не закрывается)", e.Exception);
        e.Handled = true;
    }
}
