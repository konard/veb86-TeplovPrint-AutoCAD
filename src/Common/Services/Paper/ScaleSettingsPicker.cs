using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.EditorInput;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>




/// </summary>
public static class ScaleSettingsPicker
{

    private static readonly (string Name, double ShortSideMm)[] PaperOptions =
    {
        ("A4", 210d), ("A3", 297d), ("A2", 420d), ("A1", 594d), ("A0", 841d)
    };

    public static bool PromptScaleFromFrame(Document document, AppSettings settings, out AppSettings updated, out string message)
    {
        updated = settings;
        var editor = document.Editor;
        var first = editor.GetPoint(new PromptPointOptions("\nУкажите первый угол рамки: "));
        if (first.Status != PromptStatus.OK)
        {
            message = "Определение масштаба отменено.";
            return false;
        }

        var second = editor.GetCorner(new PromptCornerOptions("\nУкажите противоположный угол рамки: ", first.Value));
        if (second.Status != PromptStatus.OK)
        {
            message = "Определение масштаба отменено.";
            return false;
        }

        var shortSide = Math.Min(
            Math.Abs(second.Value.X - first.Value.X),
            Math.Abs(second.Value.Y - first.Value.Y));
        if (shortSide <= 1e-6)
        {
            message = "Короткая сторона выбранной области равна нулю; масштаб не добавлен.";
            return false;
        }

        var dialog = new ScalePickConfirmDialog(shortSide);
        if (CadDialog.ShowModal(dialog) != true)
        {
            message = "Определение масштаба отменено.";
            return false;
        }

        var scale = dialog.ScaleValue;
        var scaleText = PaperSizeDetector.ToScaleText(scale);
        if (PaperSizeDetector.BuiltInScales.Any(x => Math.Abs(x - scale) < 1e-6)
            || settings.CustomScales.Any(x => Math.Abs(x - scale) < 1e-6))
        {
            message = $"Масштаб {scaleText} уже есть в списке.";
            return true;
        }

        updated.CustomScales.Add(scale);
        AppSettingsStore.Save(updated);
        message = $"Добавлен масштаб {scaleText} (выбранная короткая сторона {shortSide:0.##}; расчёт по короткой стороне {dialog.PaperName} {dialog.PaperShortSideMm:0.##} мм).";
        return true;
    }

    /// <summary>

    /// </summary>
    private sealed class ScalePickConfirmDialog : Window
    {

        private const double IntegerSnapTolerance = 0.005d;

        private static readonly System.Windows.Media.FontFamily DefaultFont = new("Segoe UI");

        private readonly double _frameShortSide;
        private readonly ComboBox _paper = new();
        private readonly TextBox _scaleText = new();

        public double ScaleValue { get; private set; }
        public string PaperName { get; private set; } = "";
        public double PaperShortSideMm { get; private set; }

        public ScalePickConfirmDialog(double frameShortSide)
        {
            _frameShortSide = frameShortSide;
            Title = "Определение масштаба";
            Width = 380;
            MinWidth = 340;
            Height = 170;
            MinHeight = 150;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = DefaultFont;
            FontSize = 11;

            var root = new Grid { Margin = new Thickness(10) };
            for (var i = 0; i < 2; i++)
            {
                root.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(110) : new GridLength(1, GridUnitType.Star) });
            }
            for (var i = 0; i < 3; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = i < 2 ? new GridLength(32) : new GridLength(1, GridUnitType.Star) });
            }

            AddLabel(root, "Формат листа:", 0, 0);
            _paper.IsEditable = false;
            _paper.Width = 180;
            _paper.VerticalAlignment = VerticalAlignment.Center;
            _paper.HorizontalAlignment = HorizontalAlignment.Left;
            foreach (var (name, shortSideMm) in PaperOptions)
            {
                _paper.Items.Add($"{name} (короткая сторона {shortSideMm:0.##} мм)");
            }
            _paper.SelectedIndex = GuessPaperIndex();
            _paper.SelectionChanged += (_, _) => RecalculateScaleText();
            Grid.SetColumn(_paper, 1);
            Grid.SetRow(_paper, 0);
            root.Children.Add(_paper);

            AddLabel(root, "Масштаб:", 0, 1);
            _scaleText.Width = 180;
            _scaleText.VerticalAlignment = VerticalAlignment.Center;
            _scaleText.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(_scaleText, 1);
            Grid.SetRow(_scaleText, 1);
            root.Children.Add(_scaleText);


            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            var ok = CreateButton("ОК", 82);
            ok.Click += (_, _) => Confirm();
            var cancel = CreateButton("Отмена", 82);
            cancel.Click += (_, _) =>
            {
                DialogResult = false;
                Close();
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            Grid.SetColumnSpan(buttons, 2);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
            RecalculateScaleText();
        }

        private static void AddLabel(Grid root, string text, int column, int row)
        {
            var label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, column);
            Grid.SetRow(label, row);
            root.Children.Add(label);
        }

        private static Button CreateButton(string text, int minWidth)
        {
            return new Button
            {
                Content = text,
                MinWidth = minWidth,
                MinHeight = 22,
                Padding = new Thickness(6, 0, 6, 0)
            };
        }


        private int GuessPaperIndex()
        {
            var bestIndex = 0;
            var bestDeviation = double.MaxValue;
            for (var i = 0; i < PaperOptions.Length; i++)
            {
                var scale = _frameShortSide / PaperOptions[i].ShortSideMm;
                var reference = scale >= 1 ? scale : 1d / scale;
                var deviation = Math.Abs(reference - Math.Round(reference)) / Math.Max(1, Math.Round(reference));
                if (deviation < bestDeviation)
                {
                    bestDeviation = deviation;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private void RecalculateScaleText()
        {
            var scale = _frameShortSide / PaperOptions[_paper.SelectedIndex].ShortSideMm;
            _scaleText.Text = PaperSizeDetector.ToScaleText(SnapToInteger(scale));
        }


        private static double SnapToInteger(double scale)
        {
            var reference = scale >= 1 ? scale : 1d / scale;
            var rounded = Math.Round(reference);
            if (rounded >= 1 && Math.Abs(reference - rounded) / rounded <= IntegerSnapTolerance)
            {
                return scale >= 1 ? rounded : 1d / rounded;
            }

            return scale;
        }

        private void Confirm()
        {
            if (!PaperSizeDetector.TryParseScale(_scaleText.Text, out var scale))
            {
                MessageBox.Show(
                    "Не удалось распознать масштаб. Введите 143 (1:143), 0,25 (4:1) или значение вида 1:143.",
                    Title ?? "",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ScaleValue = scale;
            var (name, shortSideMm) = PaperOptions[_paper.SelectedIndex];
            PaperName = name;
            PaperShortSideMm = shortSideMm;
            DialogResult = true;
            Close();
        }
    }
}
