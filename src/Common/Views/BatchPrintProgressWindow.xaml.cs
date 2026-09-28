using System;
using System.ComponentModel;
using System.Windows;

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
public sealed partial class BatchPrintProgressWindow : Window
{
    private Action _onCancel;
    private bool _forceClose;
    private bool _cancelRequested;
    private readonly bool _scanMode;

    public BatchPrintProgressWindow(int total, Action onCancel, bool scanMode = false)
    {
        InitializeComponent();
        _onCancel = onCancel ?? throw new ArgumentNullException(nameof(onCancel));
        _scanMode = scanMode;
        if (_scanMode)
        {
            Title = "Распознавание рамок";
            _titleText.Text = "Распознавание рамок…";
            _stopButton.Content = "Отмена";
            _progressBar.IsIndeterminate = true;
            _countText.Text = "";
            _detailText.Text = "Подготовка сканирования…";
        }
        else
        {
            _progressBar.Maximum = Math.Max(1, total);
            _progressBar.Value = 0;
            _countText.Text = $"0 / {total}";
        }

        Closing += OnClosing;
    }


    internal void ReplaceCancelHandler(Action onCancel)
        => _onCancel = onCancel ?? throw new ArgumentNullException(nameof(onCancel));


    public void Report(int completed, int total, string detail)
    {
        if (total <= 0)
        {
            total = 1;
        }

        _progressBar.IsIndeterminate = false;
        _progressBar.Maximum = total;
        _progressBar.Value = Math.Max(0, Math.Min(completed, total));
        _countText.Text = $"{Math.Max(0, completed)} / {total}";
        _titleText.Text = completed >= total ? "Завершение…" : "Пакетная печать…";
        _detailText.Text = string.IsNullOrWhiteSpace(detail) ? "Подготовка…" : detail;
    }


    public void ReportScan(int current, int total, string detail, string? title = null)
    {
        _titleText.Text = string.IsNullOrWhiteSpace(title) ? "Распознавание рамок…" : title;
        _detailText.Text = string.IsNullOrWhiteSpace(detail) ? "Подготовка сканирования…" : detail;
        if (total <= 0)
        {
            _progressBar.IsIndeterminate = true;
            _countText.Text = current > 0 ? current.ToString("N0") : "";
            return;
        }

        _progressBar.IsIndeterminate = false;
        _progressBar.Maximum = Math.Max(1, total);
        _progressBar.Value = Math.Max(0, Math.Min(current, total));
        _countText.Text = $"{current} / {total}";
    }


    public void SetPhase(string title, string detail)
    {
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = _progressBar.Maximum;
        _titleText.Text = title;
        _detailText.Text = detail;
        _stopButton.IsEnabled = false;
    }


    public void ForceClose()
    {
        _forceClose = true;
        try
        {
            Close();
        }
        catch
        {

        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        RequestCancel();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose)
        {
            return;
        }


        e.Cancel = true;
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (_cancelRequested)
        {
            return;
        }

        _cancelRequested = true;
        _stopButton.IsEnabled = false;
        _stopButton.Content = _scanMode ? "Отмена…" : "Остановка…";
        _titleText.Text = _scanMode ? "Отмена…" : "Остановка…";
        try
        {
            _onCancel();
        }
        catch
        {

        }
    }
}
