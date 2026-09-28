using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ZwcadBatchPlot;

public sealed partial class SinglePlotForm : Window
{

    private sealed class MarginOption
    {
        public double Value { get; set; }
        public override string ToString() => Value > 0
            ? $"+ {Value:0.#} mm"
            : $"- {Math.Abs(Value):0.#} mm";
    }

    // _paperCombo/_outputPath/_areaLabel/_scaleLabel/_styleCombo/_styleSettingsButton/

    private readonly IReadOnlyList<PaperDetection> _candidates;

    public PaperDetection SelectedPaper
    {
        get
        {
            if (_paperCombo.SelectedIndex >= 0 && _paperCombo.SelectedIndex < _candidates.Count)
                return _candidates[_paperCombo.SelectedIndex];
            return _candidates[0];
        }
    }

    public string OutputPath => _outputPath.Text;
    public string SelectedStyle => _styleCombo.SelectedItem?.ToString() ?? "";
    public bool LeavePaperMargin => _leaveMargin.IsChecked == true;
    public double PaperMarginMm => ReadMarginValue(_marginInput);


    public bool IsPreview { get; private set; }

    public SinglePlotForm(
        string sourceFile,
        double width,
        double height,
        IReadOnlyList<PaperDetection> candidates,
        IReadOnlyList<string> styles,
        string selectedStyle)
    {
        _candidates = candidates.Count > 0
            ? candidates
            : throw new ArgumentException("Необходим хотя бы один вариант бумаги.", nameof(candidates));

        InitializeComponent();

        InitializeComponents(sourceFile, width, height, styles, selectedStyle);
    }

    private void InitializeComponents(
        string sourceFile,
        double width,
        double height,
        IReadOnlyList<string> styles,
        string selectedStyle)
    {

        _areaLabel.Text = $"Область печати: {width:0.##} × {height:0.##} (единицы чертежа)";


        var firstPaper = _candidates[0];
        _scaleLabel.Text = $"Расчётный масштаб: {firstPaper.ScaleText}";


        foreach (var candidate in _candidates)
        {
            _paperCombo.Items.Add(
                $"{candidate.PaperName}    {candidate.ScaleText}    {candidate.PaperWidthMm:0.##} × {candidate.PaperHeightMm:0.##} mm");
        }
        _paperCombo.SelectedIndex = 0;
        _paperCombo.SelectionChanged += (_, _) =>
        {
            var paper = SelectedPaper;
            _scaleLabel.Text = $"Расчётный масштаб: {paper.ScaleText}";
        };


        _outputPath.Text = BuildDefaultPath(sourceFile);


        foreach (var style in styles)
        {
            _styleCombo.Items.Add(style);
        }
        PlotStyleManager.RestoreSavedStyle(_styleCombo, selectedStyle);
        _styleSettingsButton.IsEnabled = _styleCombo.SelectedIndex >= 0;
        _styleCombo.SelectionChanged += (_, _) =>
            _styleSettingsButton.IsEnabled = _styleCombo.SelectedIndex >= 0;


        InitMarginCombo(_marginInput, 72, 1.0);
        _marginInput.IsEnabled = false;
        _leaveMargin.Checked += (_, _) => _marginInput.IsEnabled = _leaveMargin.IsChecked == true;
        _leaveMargin.Unchecked += (_, _) => _marginInput.IsEnabled = _leaveMargin.IsChecked == true;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "pdf",
            Filter = "Файлы PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileName(_outputPath.Text),
            InitialDirectory = Path.GetDirectoryName(_outputPath.Text),
            OverwritePrompt = true,
            Title = "Выберите место сохранения PDF"
        };
        if (dialog.ShowDialog(this) == true)
        {
            _outputPath.Text = dialog.FileName;
        }
    }

    private void StyleSettingsButton_Click(object sender, RoutedEventArgs e)
        => PlotStyleManager.EditSelectedStyle(this, _styleCombo.SelectedItem?.ToString());

    private void LeaveMargin_CheckChanged(object sender, RoutedEventArgs e)
        => _marginInput.IsEnabled = _leaveMargin.IsChecked == true;

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        IsPreview = true;
        DialogResult = true;
        Close();
    }

    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        IsPreview = false;
        DialogResult = true;
        Close();
    }


    private static void InitMarginCombo(ComboBox combo, int width, double savedValue)
    {
        combo.Width = width;
        combo.Items.Clear();
        for (var n = 1; n <= 10; n++)
        {
            combo.Items.Add(new MarginOption { Value = n });
            combo.Items.Add(new MarginOption { Value = -n });
        }

        var bestIdx = 0;
        var bestDiff = double.MaxValue;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is MarginOption opt)
            {
                var diff = Math.Abs(opt.Value - savedValue);
                if (diff < bestDiff) { bestDiff = diff; bestIdx = i; }
            }
        }
        combo.SelectedIndex = bestIdx;
    }


    private static double ReadMarginValue(ComboBox combo)
        => combo.SelectedItem is MarginOption opt ? opt.Value : 1.0;

    private static string BuildDefaultPath(string sourceFile)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFile);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Drawing";

        var directory = Path.GetDirectoryName(sourceFile);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return Path.Combine(directory, baseName + ".pdf");
    }
}
