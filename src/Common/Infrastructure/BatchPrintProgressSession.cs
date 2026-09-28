using System;
using System.Windows;
using System.Windows.Threading;

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
internal sealed class BatchPrintProgressSession : IDisposable
{
    private readonly BatchPrintProgressWindow _window;
    private bool _disposed;

    private BatchPrintProgressSession(BatchPrintProgressWindow window)
    {
        _window = window;
    }


    public static BatchPrintProgressSession Start(Window owner, int total, Action onCancel)
    {
        var window = new BatchPrintProgressWindow(total, onCancel)
        {
            Owner = owner
        };

        if (owner.IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.Show();
        window.Activate();
        Pump(window);
        return new BatchPrintProgressSession(window);
    }


    public void Report(int completed, int total, string detail)
    {
        if (_disposed)
        {
            return;
        }

        _window.Report(completed, total, detail);
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.Activate();
        Pump(_window);
    }


    public void SetPhase(string title, string detail)
    {
        if (_disposed)
        {
            return;
        }

        _window.SetPhase(title, detail);
        _window.Activate();
        Pump(_window);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.ForceClose();
    }

    private static void Pump(DispatcherObject source)
    {
        source.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
    }
}