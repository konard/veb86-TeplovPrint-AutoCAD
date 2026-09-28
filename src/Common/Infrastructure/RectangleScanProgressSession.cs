using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
internal sealed class RectangleScanProgressSession : IDisposable
{
    private readonly BatchPrintProgressWindow _window;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private RectangleScanProgressSession(BatchPrintProgressWindow window)
    {
        _window = window;
    }

    public CancellationToken Token => _cts.Token;

    public IProgress<RectangleScanProgress> Progress { get; private set; } = null!;


    public static RectangleScanProgressSession Start(Window owner)
    {
        var window = new BatchPrintProgressWindow(1, () => { }, scanMode: true)
        {
            Owner = owner
        };
        if (owner.IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        var session = new RectangleScanProgressSession(window);

        window.ReplaceCancelHandler(session.RequestCancel);
        session.Progress = new SynchronousProgress(session.OnProgress);
        window.Show();
        window.Activate();
        window.ReportScan(0, 0, "Подготовка сканирования…");
        Pump(window);
        return session;
    }

    private void OnProgress(RectangleScanProgress value)
    {
        if (_disposed)
        {
            return;
        }

        _window.ReportScan(value.Current, value.Total, value.Detail, value.Title);
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        Pump(_window);
    }

    private void RequestCancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.ForceClose();
        _cts.Dispose();
    }

    private static void Pump(DispatcherObject source)
        => source.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));


    private sealed class SynchronousProgress : IProgress<RectangleScanProgress>
    {
        private readonly Action<RectangleScanProgress> _handler;

        public SynchronousProgress(Action<RectangleScanProgress> handler)
            => _handler = handler;

        public void Report(RectangleScanProgress value) => _handler(value);
    }
}
