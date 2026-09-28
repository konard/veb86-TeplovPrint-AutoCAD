using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
public sealed partial class DrawingNumberReorderControl : UserControl
{
    private readonly int _jobCount;

    public string Prefix => PrefixBox.Text.Trim();
    public string Suffix => SuffixBox.Text.Trim();
    public int StartNumber => int.TryParse(StartNumberBox.Text, out var v) ? Math.Max(1, Math.Min(9999, v)) : 1;

    public int Digits => DigitsCombo?.SelectedIndex > 0 ? DigitsCombo.SelectedIndex : 0;

    public bool HorizontalFirst => LeftToRightRadio.IsChecked == true;


    public event Action? PreviewRequested;


    public event Action? OkRequested;


    public event Action? CancelRequested;

    public DrawingNumberReorderControl(int jobCount, string detectedPrefix = "", bool horizontalFirst = false)
    {
        InitializeComponent();
        _jobCount = jobCount;
        PrefixBox.Text = detectedPrefix;
        if (horizontalFirst)
            LeftToRightRadio.IsChecked = true;
        else
            TopToBottomRadio.IsChecked = true;

        DigitsCombo.Items.Add("Автоматически (по количеству листов)");
        for (var d = 1; d <= 6; d++)
        {
            DigitsCombo.Items.Add(d + " разр.");
        }
        DigitsCombo.SelectedIndex = 0;
        UpdatePreview();
    }

    private void UpdatePreview()
    {

        if (PrefixBox == null || SuffixBox == null || StartNumberBox == null || PreviewLabel == null || DigitsCombo == null)
            return;

        var prefix = Prefix;
        var suffix = Suffix;
        var start = StartNumber;

        StartNumberBox.Text = start.ToString(CultureInfo.InvariantCulture);
        var maxNumber = _jobCount + start - 1;
        var digits = Digits > 0
            ? Digits
            : Math.Max(2, maxNumber.ToString().Length);
        var samples = Math.Min(5, _jobCount);
        var items = new string[samples];
        for (var i = 0; i < samples; i++)
        {
            items[i] = prefix + (start + i).ToString($"D{digits}") + suffix;
        }

        var tail = _jobCount > samples ? " ..." : "";
        PreviewLabel.Text = $"Пример: {string.Join(", ", items)}{tail}  (листов: {_jobCount})";
    }

    private void OnTextChanged(object sender, RoutedEventArgs e) => UpdatePreview();
    private void OnDigitsChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void OnStartNumberPreviewTextInput(object sender, TextCompositionEventArgs e)
    {

        e.Handled = e.Text.Any(c => !char.IsDigit(c));
    }

    private void OnStartNumberUp(object sender, RoutedEventArgs e)
    {
        var v = StartNumber;
        if (v < 9999) { StartNumberBox.Text = (v + 1).ToString(CultureInfo.InvariantCulture); }
    }

    private void OnStartNumberDown(object sender, RoutedEventArgs e)
    {
        var v = StartNumber;
        if (v > 1) { StartNumberBox.Text = (v - 1).ToString(CultureInfo.InvariantCulture); }
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => OkRequested?.Invoke();

    private void OnPreviewClick(object sender, RoutedEventArgs e) => PreviewRequested?.Invoke();

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
