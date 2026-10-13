using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using CadApp = HostMgd.ApplicationServices.Application;
#elif AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
internal sealed class NumberBox : TextBox
{
    public static readonly DependencyProperty MinProperty = DependencyProperty.Register(
        nameof(Min), typeof(double), typeof(NumberBox), new PropertyMetadata(0.0));
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
        nameof(Max), typeof(double), typeof(NumberBox), new PropertyMetadata(100.0));
    public static readonly DependencyProperty IncrementProperty = DependencyProperty.Register(
        nameof(Increment), typeof(double), typeof(NumberBox), new PropertyMetadata(1.0));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
        nameof(Decimals), typeof(int), typeof(NumberBox), new PropertyMetadata(0));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumberBox),
        new PropertyMetadata(0.0, (d, _) => ((NumberBox)d).OnValueChanged()));


    public event EventHandler? ValueChanged;

    private bool _syncing;

    public double Min
    {
        get => (double)GetValue(MinProperty);
        set => SetValue(MinProperty, value);
    }

    public double Max
    {
        get => (double)GetValue(MaxProperty);
        set => SetValue(MaxProperty, value);
    }

    public double Increment
    {
        get => (double)GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Clamp(value));
    }

    static NumberBox()
    {
        TextProperty.OverrideMetadata(
            typeof(NumberBox),
            new FrameworkPropertyMetadata(string.Empty, (d, _) => ((NumberBox)d).OnTextChanged()));
    }

    public NumberBox()
    {
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Loaded += (_, _) => SyncText();
    }

    private double Clamp(double value)
    {
        return Math.Max(Min, Math.Min(Max, value));
    }

    private void OnTextChanged()
    {
        if (_syncing)
        {
            return;
        }



        if (!TryParseText(out var parsed))
        {
            return;
        }

        if (parsed < Min || parsed > Max)
        {
            return;
        }

        if (Math.Abs(parsed - Value) > 1e-9)
        {
            _syncing = true;
            try
            {
                SetCurrentValue(ValueProperty, parsed);
            }
            finally
            {
                _syncing = false;
            }

            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnValueChanged()
    {

        if (!_syncing && !IsKeyboardFocusWithin)
        {
            SyncText();
        }

        if (!_syncing)
        {
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        CommitText();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }


    private void CommitText()
    {
        if (TryParseText(out var parsed))
        {
            Value = Clamp(parsed);
        }

        SyncText();
    }

    private bool TryParseText(out double parsed)
    {
        return double.TryParse(Text, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed)
            || double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private void SyncText()
    {
        _syncing = true;
        try
        {
            Text = Decimals > 0
                ? Value.ToString("F" + Decimals, CultureInfo.InvariantCulture)
                : Value.ToString("0", CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncing = false;
        }
    }
}


internal sealed class DirectoryColorItem
{
    public DirectoryColorItem(int index, Color color)
    {
        Index = index;
        Brush = new SolidColorBrush(color);
        Brush.Freeze();
    }

    public int Index { get; }

    public Brush Brush { get; }

    public string IndexText => Index switch
    {
        0 => "0 (по блоку)",
        256 => "256 (по слою)",
        _ => Index.ToString(CultureInfo.InvariantCulture)
    };
}


internal sealed class DirectoryColumnRow
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Centered { get; set; }
    public string Header { get; set; } = "";
    public string WidthText { get; set; } = "";
}


internal sealed class DirectoryPreviewColumn
{
    public DirectoryPreviewColumn(string header, double width, bool centered)
    {
        Header = header;
        Width = width;
        Centered = centered;
    }

    public string Header { get; }
    public double Width { get; }
    public bool Centered { get; }
}


internal sealed class DirectoryPreviewControl : FrameworkElement
{
    private IReadOnlyList<DirectoryPreviewColumn> _columns = Array.Empty<DirectoryPreviewColumn>();
    private double _rowHeight = 1;
    private double _textHeight = 1;
    private double _textWidthFactor = 0.7;
    private string _fontName = "Arial";

    public DirectoryPreviewControl()
    {
        ClipToBounds = true;
    }

    public void SetPreview(
        IReadOnlyList<DirectoryPreviewColumn> columns,
        double rowHeight,
        double textHeight,
        double textWidthFactor,
        string fontName)
    {
        _columns = columns.ToList();
        _rowHeight = Math.Max(1, rowHeight);
        _textHeight = Math.Max(1, textHeight);
        _textWidthFactor = Math.Max(0.1, textWidthFactor);
        _fontName = string.IsNullOrWhiteSpace(fontName) ? "Arial" : fontName;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.White, null, bounds);

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_columns.Count == 0)
        {
            var hint = new FormattedText(
                "Выберите столбцы, которые нужно включить в ведомость",
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12d,
                Brushes.DimGray,
                pixelsPerDip);
            dc.DrawText(
                hint,
                new Point((bounds.Width - hint.Width) / 2, (bounds.Height - hint.Height) / 2));
            return;
        }

        var totalWidth = _columns.Sum(x => x.Width);
        if (totalWidth <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var padding = 6;
        var availableWidth = Math.Max(1, bounds.Width - padding * 2);
        var availableHeight = Math.Max(1, bounds.Height - padding * 2);

        var scale = Math.Min(availableWidth / totalWidth, availableHeight / _rowHeight);
        var previewWidth = totalWidth * scale;
        var previewHeight = _rowHeight * scale;
        var x = (bounds.Width - previewWidth) / 2;
        var y = (bounds.Height - previewHeight) / 2;

        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(70, 70, 70)), 1);
        linePen.Freeze();
        foreach (var column in _columns)
        {
            var cellWidth = column.Width * scale;
            var cell = new Rect(x, y, cellWidth, previewHeight);
            dc.DrawRectangle(null, linePen, cell);


            var byRow = _rowHeight * 0.8;
            var byWidth = column.Width * 0.9 / Math.Max(1, column.Header.Length * _textWidthFactor);
            var fontPixels = Math.Max(1, Math.Min(_textHeight, Math.Min(byRow, byWidth))) * scale;

            var typeface = CreateTypeface(_fontName);
            var text = new FormattedText(
                column.Header,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                Math.Max(1, fontPixels),
                Brushes.Black,
                pixelsPerDip);
            text.MaxTextWidth = Math.Max(1, cell.Width - Math.Min(4, cell.Width * 0.04) * 2);
            text.Trimming = TextTrimming.CharacterEllipsis;

            var inset = Math.Min(4, cell.Width * 0.04);
            var textX = column.Centered
                ? cell.X + (cell.Width - text.Width) / 2
                : cell.X + inset;
            var textY = cell.Y + (cell.Height - text.Height) / 2;
            dc.PushClip(new System.Windows.Media.RectangleGeometry(cell));
            dc.DrawText(text, new Point(textX, textY));
            dc.Pop();

            x += cellWidth;
        }
    }

    private static Typeface CreateTypeface(string fontName)
    {
        try
        {
            return new Typeface(new FontFamily(fontName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        }
        catch
        {
            return new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        }
    }
}

public sealed partial class SettingsForm : Window
{
    private const string DefaultTextStyleDisplay = "(по умолчанию)";

    private readonly ObservableCollection<DirectoryColumnRow> _directoryRows = new();

    public string? RequestedDirectoryColumnKey { get; private set; }
    public bool RequestPickDirectoryRowHeight { get; private set; }
    public bool RequestPickDirectoryTextAppearance { get; private set; }
    public bool RequestPickScaleFromCad { get; private set; }

    /// <summary>


    /// </summary>
    public static int InitialTabIndex { get; set; }

    /// <summary>

    /// </summary>
    public int SelectedTabIndex { get; private set; }

    public SettingsForm()
    {
        InitializeComponent();

        _generatePrintLog.ToolTip =
            "Общий переключатель журнала. Разрешает запись печати, разделения DWG, предупреждений сканирования и диагностики рамок. По умолчанию выключен. Папка журнала: " + BatchPlotLogger.LogDirectory;


        _longPaperSnapTolerance.ValueChanged += (_, _) => UpdateFileNamePreview();
        _fileNameSequenceStart.ValueChanged += (_, _) => UpdateFileNamePreview();
        _fileNameSequenceDigits.ValueChanged += (_, _) => UpdateFileNamePreview();
        _directoryTextHeight.ValueChanged += (_, _) => UpdateDirectoryPreview();
        _directoryTextWidthFactor.ValueChanged += (_, _) => UpdateDirectoryPreview();
        _directoryRowHeight.ValueChanged += (_, _) => UpdateDirectoryPreview();

        ConfigureDirectoryColorIndex();
        ConfigureLongPaperNameFormat();
        LoadTextStyles();

        _directoryColumnsGrid.ItemsSource = _directoryRows;

        var hasDocument = GetActiveDocument() != null;
        _pickRowHeightButton.IsEnabled = hasDocument;
        _pickTextAppearanceButton.IsEnabled = hasDocument;
        _pickScaleButton.IsEnabled = hasDocument;

        Closing += (_, _) =>
        {
            SelectedTabIndex = _tabs.SelectedIndex;
            InitialTabIndex = SelectedTabIndex;
        };

        LoadSettings();


        if (InitialTabIndex >= 0 && InitialTabIndex < _tabs.Items.Count)
        {
            _tabs.SelectedIndex = InitialTabIndex;
        }

        UpdateFooterHint();
    }

    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFooterHint();
    }

    private void UpdateFooterHint()
    {
        if (_footerHint == null)
        {
            return;
        }

        _footerHint.Text = _tabs.SelectedIndex switch
        {
            0 => "Основные настройки применяются к печати по рамкам, прямоугольникам и одному листу.",
            1 => "Предпросмотр имени файла обновляется сразу; после сохранения правила используются при печати и разделении DWG.",
            2 => "Ведомость листов создаётся в текущем пространстве CAD; её столбцы соответствуют полям, найденным при пакетной печати.",
            3 => "После добавления рамки поддерживаются произвольные масштабы; этот список ограничивает распознавание только для печати по прямоугольникам.",
            _ => ""
        };
    }



    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ResetDefaults();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }





    private void OnPatternTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFileNamePreview();
    }

    private void OnAutoDigitsChanged(object sender, RoutedEventArgs e)
    {
        UpdateSequenceDigitsState();
        UpdateFileNamePreview();
    }

    private void OnLongPaperFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFileNamePreview();
    }

    private void ConfigureLongPaperNameFormat()
    {
        _longPaperNameFormat.Items.Clear();
        _longPaperNameFormat.Items.Add("Вариант 1 (дробь): A3+1/8, A2+3/4");
        _longPaperNameFormat.Items.Add("Вариант 2 (десятичный): A3+0,125, A2+0,75");
        _longPaperNameFormat.Items.Add("Вариант 3 (резерв)");
        _longPaperNameFormat.Items.Add("Вариант 4 (резерв)");
        _longPaperNameFormat.Items.Add("Вариант 5 (резерв)");
        _longPaperNameFormat.Items.Add("Вариант 6 (резерв)");
        _longPaperNameFormat.SelectedIndex = 0;
    }

    private void UpdateFileNamePreview()
    {
        if (string.IsNullOrWhiteSpace(_fileNamePattern.Text))
        {
        _fileNamePreview.Text = "(введите правило имени файла)";
            return;
        }

        var example = new PlotJob
        {
            DrawingNumber = "АР-003",
            Revision = "1.0",
            Title = "План расположения конструкций",
            Date = "2026-07-18",
            Info1 = "Информация 1",
            Info2 = "Информация 2",
            Phase = "Рабочая документация",
            PaperName = "A2"
        };
        var startNumber = (int)_fileNameSequenceStart.Value;
        var sequenceDigits = FileNameSanitizer.ResolveSequenceDigits(
            _autoFileNameSequenceDigits.IsChecked == true,
            (int)_fileNameSequenceDigits.Value,
            startNumber,
            1);
        _fileNamePreview.Text = FileNameSanitizer.FormatFileNamePattern(
            _fileNamePattern.Text,
            example,
            startNumber,
            sequenceDigits,
            (LongPaperNameFormat)Math.Max(0, _longPaperNameFormat.SelectedIndex),
            _longPaperSnapTolerance.Value);
        if (_autoFileNameSequenceDigits.IsChecked == true)
        {
        _fileNamePreview.Text += " (разрядность определяется общим числом листов)";
        }
    }

    private void UpdateSequenceDigitsState()
    {
        _fileNameSequenceDigits.IsEnabled = _autoFileNameSequenceDigits.IsChecked != true;
    }



    private void OnTextStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDirectoryPreview();
    }

    private void OnPickTextAppearanceClick(object sender, RoutedEventArgs e)
    {
        RequestTextAppearanceFromCad();
    }

    private void OnPickRowHeightClick(object sender, RoutedEventArgs e)
    {
        RequestRowHeightFromCad();
    }

    private void ConfigureDirectoryColorIndex()
    {
        var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
        panelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var swatchFactory = new FrameworkElementFactory(typeof(Rectangle));
        swatchFactory.SetValue(FrameworkElement.WidthProperty, 12.0);
        swatchFactory.SetValue(FrameworkElement.HeightProperty, 12.0);
        swatchFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 0, 0));
        swatchFactory.SetValue(System.Windows.Shapes.Shape.FillProperty, new Binding(nameof(DirectoryColorItem.Brush)));
        panelFactory.AppendChild(swatchFactory);

        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 0, 0, 0));
        textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        textFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(DirectoryColorItem.IndexText)));
        panelFactory.AppendChild(textFactory);

        _directoryColorIndex.ItemTemplate = new DataTemplate { VisualTree = panelFactory };

        for (var index = 0; index <= 256; index++)
        {
            _directoryColorIndex.Items.Add(new DirectoryColorItem(index, GetAciPreviewColor(index)));
        }
    }

    private static Color GetAciPreviewColor(int index)
    {
        var fixedColors = new[]
        {
            Colors.DimGray,
            Color.FromRgb(255, 0, 0),
            Color.FromRgb(255, 255, 0),
            Color.FromRgb(0, 255, 0),
            Color.FromRgb(0, 255, 255),
            Color.FromRgb(0, 0, 255),
            Color.FromRgb(255, 0, 255),
            Color.FromRgb(255, 255, 255),
            Color.FromRgb(128, 128, 128),
            Color.FromRgb(192, 192, 192)
        };
        if (index >= 0 && index < fixedColors.Length)
        {
            return fixedColors[index];
        }

        if (index >= 10 && index <= 249)
        {

            var hue = ((index - 10) / 10) * 15.0;
            var tone = (index - 10) % 10;
            var brightnessLevels = new[] { 255, 255, 165, 165, 127, 127, 76, 76, 38, 38 };
            var saturation = tone % 2 == 0 ? 1.0 : 0.5;
            return ColorFromHsv(hue, saturation, brightnessLevels[tone] / 255.0);
        }

        var grays = new[] { 51, 80, 105, 130, 190, 255 };
        if (index >= 250 && index <= 255)
        {
            var gray = grays[index - 250];
            return Color.FromRgb((byte)gray, (byte)gray, (byte)gray);
        }

        return Colors.DimGray;
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        var sector = hue / 60.0;
        var wholeSector = (int)Math.Floor(sector) % 6;
        var fraction = sector - Math.Floor(sector);
        var p = value * (1 - saturation);
        var q = value * (1 - fraction * saturation);
        var t = value * (1 - (1 - fraction) * saturation);
        var (red, green, blue) = wholeSector switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };
        return Color.FromRgb(
            (byte)Math.Round(red * 255),
            (byte)Math.Round(green * 255),
            (byte)Math.Round(blue * 255));
    }



    private void OnGridEnabledClick(object sender, RoutedEventArgs e)
    {
        UpdateDirectoryPreview();
    }

    private void OnGridCenteredClick(object sender, RoutedEventArgs e)
    {
        UpdateDirectoryPreview();
    }

    private void OnGridWidthTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDirectoryPreview();
    }

    private void OnGridPickWidthClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not DirectoryColumnRow row)
        {
            return;
        }

        var rowIndex = _directoryRows.IndexOf(row);
        RequestColumnWidthFromCad(rowIndex);
    }

    private void OnGridMoveUpClick(object sender, RoutedEventArgs e)
    {
        MoveDirectoryColumn(GetRowIndexFromSender(sender), -1);
    }

    private void OnGridMoveDownClick(object sender, RoutedEventArgs e)
    {
        MoveDirectoryColumn(GetRowIndexFromSender(sender), 1);
    }

    private int GetRowIndexFromSender(object sender)
    {
        if (((Button)sender).DataContext is not DirectoryColumnRow row)
        {
            return -1;
        }

        return _directoryRows.IndexOf(row);
    }

    private void MoveDirectoryColumn(int rowIndex, int offset)
    {
        var targetIndex = rowIndex + offset;
        if (rowIndex < 0 || targetIndex < 0 || targetIndex >= _directoryRows.Count)
        {
            return;
        }


        _directoryRows.Move(rowIndex, targetIndex);
        _directoryColumnsGrid.SelectedItem = _directoryRows[targetIndex];
        _directoryColumnsGrid.ScrollIntoView(_directoryRows[targetIndex]);
        UpdateDirectoryPreview();
    }

    private void LoadDirectoryColumns(IEnumerable<DirectoryColumnSetting> columns)
    {
        _directoryRows.Clear();
        foreach (var column in columns)
        {
            _directoryRows.Add(new DirectoryColumnRow
            {
                Key = column.Key,
                Enabled = column.Enabled,
                Centered = column.Centered,
                Header = column.Header,
                WidthText = column.Width.ToString("0.##", CultureInfo.CurrentCulture)
            });
        }

        UpdateDirectoryPreview();
    }

    private void UpdateDirectoryPreview()
    {
        var columns = new List<DirectoryPreviewColumn>();
        foreach (var row in _directoryRows)
        {
            if (!row.Enabled)
            {
                continue;
            }

            var widthText = row.WidthText ?? "";
            if (!double.TryParse(widthText, NumberStyles.Float, CultureInfo.CurrentCulture, out var width)
                && !double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out width))
            {
                continue;
            }

            if (width <= 0)
            {
                continue;
            }

            columns.Add(new DirectoryPreviewColumn(row.Header, width, row.Centered));
        }

        var selectedStyle = _directoryTextStyle.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(selectedStyle) || selectedStyle == DefaultTextStyleDisplay)
        {
            selectedStyle = "Arial";
        }

        var styleName = selectedStyle ?? "Arial";

        _directoryOrderPreview.SetPreview(
            columns,
            _directoryRowHeight.Value,
            _directoryTextHeight.Value,
            _directoryTextWidthFactor.Value,
            styleName);
    }

    private bool TryReadDirectoryColumns(out List<DirectoryColumnSetting> columns)
    {
        columns = new List<DirectoryColumnSetting>();
        foreach (var row in _directoryRows)
        {
            var key = row.Key ?? "";
            var header = row.Header?.Trim() ?? "";
            var widthText = row.WidthText?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(header))
            {
        System.Windows.MessageBox.Show("Название столбца ведомости не может быть пустым.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectAndScrollToRow(row);
                return false;
            }

            if (!double.TryParse(widthText, NumberStyles.Float, CultureInfo.CurrentCulture, out var width)
                && !double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out width))
            {
                width = 0;
            }

            if (width <= 0)
            {
        System.Windows.MessageBox.Show($"Ширина столбца ведомости «{header}» должна быть больше 0.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectAndScrollToRow(row);
                return false;
            }

            columns.Add(new DirectoryColumnSetting
            {
                Key = key,
                Header = header,
                Enabled = row.Enabled,
                Centered = row.Centered,
                Width = width
            });
        }

        if (!columns.Any(x => x.Enabled))
        {
        System.Windows.MessageBox.Show("Включите хотя бы одно поле ведомости.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void SelectAndScrollToRow(DirectoryColumnRow row)
    {
        _directoryColumnsGrid.SelectedItem = row;
        _directoryColumnsGrid.ScrollIntoView(row);
    }

    private void LoadTextStyles()
    {
        _directoryTextStyle.Items.Clear();
        _directoryTextStyle.Items.Add(DefaultTextStyleDisplay);

        var document = GetActiveDocument();
        if (document != null)
        {
            try
            {
                using var tr = document.Database.TransactionManager.StartTransaction();
                var table = (TextStyleTable)tr.GetObject(document.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var record = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!string.IsNullOrWhiteSpace(record.Name))
                    {
                        _directoryTextStyle.Items.Add(record.Name);
                    }
                }

                tr.Commit();
            }
            catch
            {
            }
        }


        if (!_directoryTextStyle.Items.Cast<object>().Any(x =>
            string.Equals(x?.ToString(), "Arial", StringComparison.OrdinalIgnoreCase)))
        {
            _directoryTextStyle.Items.Insert(1, "Arial");
        }

        if (_directoryTextStyle.Items.Count > 0)
        {
            _directoryTextStyle.SelectedIndex = 0;
        }
    }

    private void SelectTextStyle(string? name)
    {
        var target = string.IsNullOrWhiteSpace(name) ? DefaultTextStyleDisplay : name;
        for (var i = 0; i < _directoryTextStyle.Items.Count; i++)
        {
            if (string.Equals(_directoryTextStyle.Items[i]?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                _directoryTextStyle.SelectedIndex = i;
                return;
            }
        }

        if (_directoryTextStyle.Items.Count > 0)
        {
            _directoryTextStyle.SelectedIndex = 0;
        }
    }



    private void RequestColumnWidthFromCad(int rowIndex)
    {
        if (GetActiveDocument() == null)
        {
        System.Windows.MessageBox.Show("Нет доступного документа CAD.", "Настройки пакетной печати", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (rowIndex < 0 || rowIndex >= _directoryRows.Count
            || !TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        var key = _directoryRows[rowIndex].Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }


        AppSettingsStore.Save(settings);
        RequestedDirectoryColumnKey = key;
        DialogResult = true;
        Close();
    }

    private void RequestRowHeightFromCad()
    {
        if (GetActiveDocument() == null)
        {
            System.Windows.MessageBox.Show("Нет доступного документа CAD.", "Настройки пакетной печати", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadSettingsFromControls(out var settings))
        {
            return;
        }


        AppSettingsStore.Save(settings);
        RequestPickDirectoryRowHeight = true;
        DialogResult = true;
        Close();
    }

    private void RequestTextAppearanceFromCad()
    {
        if (GetActiveDocument() == null)
        {
            System.Windows.MessageBox.Show("Нет доступного документа CAD.", "Настройки пакетной печати", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadSettingsFromControls(out var settings))
        {
            return;
        }


        AppSettingsStore.Save(settings);
        RequestPickDirectoryTextAppearance = true;
        DialogResult = true;
        Close();
    }




    private sealed class ScaleListItem
    {
        public ScaleListItem(double value, bool isCustom)
        {
            Value = value;
            IsCustom = isCustom;
        }

        public double Value { get; }
        public bool IsCustom { get; }

        public override string ToString()
        {
        return PaperSizeDetector.ToScaleText(Value) + (IsCustom ? " (пользовательский)" : "");
        }
    }

    private void OnScaleListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateScaleListState();
    }

    private void OnScaleInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        AddCustomScale();
        e.Handled = true;
    }

    private void OnAddScaleClick(object sender, RoutedEventArgs e)
    {
        AddCustomScale();
    }

    private void OnPickScaleClick(object sender, RoutedEventArgs e)
    {
        RequestScaleFromCad();
    }

    private void OnRemoveScaleClick(object sender, RoutedEventArgs e)
    {
        RemoveSelectedCustomScales();
    }

    private void ReloadScaleList(AppSettings settings)
    {
        _scaleList.Items.Clear();
        foreach (var scale in PaperSizeDetector.BuiltInScales)
        {
            _scaleList.Items.Add(new ScaleListItem(scale, false));
        }

        foreach (var scale in settings.CustomScales)
        {
            _scaleList.Items.Add(new ScaleListItem(scale, true));
        }

        UpdateScaleListState();
    }


    private void UpdateScaleListState()
    {
        var items = _scaleList.Items.Cast<ScaleListItem>().ToList();
        var selected = _scaleList.SelectedItems.Cast<ScaleListItem>().ToList();
        var builtInCount = items.Count(item => !item.IsCustom);
        var customCount = items.Count - builtInCount;
        _scaleListSummary.Text = $"Встроенных: {builtInCount}; пользовательских: {customCount} (Ctrl/Shift — множественный выбор)";

        var canDelete = selected.Count > 0 && selected.All(item => item.IsCustom);
        _removeScaleButton.IsEnabled = canDelete;

        _scaleSelectionHint.Text = selected.Count switch
        {
            0 => "Выберите пользовательский масштаб для удаления; встроенные масштабы сохраняются.",
            _ when canDelete => $"Выбрано пользовательских масштабов: {selected.Count}. Их можно удалить.",
            _ => "В выбор входят встроенные масштабы, которые нельзя удалить."
        };
        _scaleSelectionHint.Foreground = selected.Count > 0 && !canDelete
            ? new SolidColorBrush(Color.FromRgb(174, 100, 25))
            : Brushes.DimGray;
    }

    private List<double> ReadCustomScalesFromList()
    {
        return _scaleList.Items
            .Cast<ScaleListItem>()
            .Where(x => x.IsCustom)
            .Select(x => x.Value)
            .ToList();
    }

    private void AddCustomScale()
    {
        if (!PaperSizeDetector.TryParseScale(_scaleInput.Text, out var scale))
        {
            System.Windows.MessageBox.Show(
                "Не удалось распознать масштаб. Введите 143 (1:143), 0,25 (4:1) или значение вида 1:143.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_scaleList.Items.Cast<ScaleListItem>().Any(x => Math.Abs(x.Value - scale) < 1e-6))
        {
            System.Windows.MessageBox.Show(
                $"Масштаб {PaperSizeDetector.ToScaleText(scale)} уже есть в списке.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }


        var insertIndex = _scaleList.Items.Count;
        for (var i = 0; i < _scaleList.Items.Count; i++)
        {
            if (_scaleList.Items[i] is ScaleListItem existing && existing.IsCustom && existing.Value > scale)
            {
                insertIndex = i;
                break;
            }
        }

        var added = new ScaleListItem(scale, true);
        _scaleList.Items.Insert(insertIndex, added);
        _scaleList.SelectedItem = added;
        UpdateScaleListState();
        _scaleInput.Clear();
        _scaleInput.Focus();
    }

    private void RemoveSelectedCustomScales()
    {
        var selected = _scaleList.SelectedItems.Cast<ScaleListItem>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (selected.Any(x => !x.IsCustom))
        {
            System.Windows.MessageBox.Show(
                "Встроенные масштабы нельзя удалить. Удаляются только пользовательские масштабы.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        foreach (var item in selected)
        {
            _scaleList.Items.Remove(item);
        }

        UpdateScaleListState();
    }

    private void RequestScaleFromCad()
    {
        if (GetActiveDocument() == null)
        {
            System.Windows.MessageBox.Show("Нет доступного документа CAD.", "Настройки пакетной печати", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadSettingsFromControls(out var settings))
        {
            return;
        }


        AppSettingsStore.Save(settings);
        RequestPickScaleFromCad = true;
        DialogResult = true;
        Close();
    }



    private void LoadSettings()
    {
        Apply(AppSettingsStore.Load());
    }

    private void Apply(AppSettings settings)
    {
        _paperTolerance.Value = settings.PaperMatchToleranceMm;
        _recognizeFourLineRectangleFrames.IsChecked = settings.RecognizeFourLineRectangleFrames;
        _hideFrameBoundaryWhenPlotting.IsChecked = settings.HideFrameBoundaryWhenPlotting;
        _plotTransparency.IsChecked = settings.PlotTransparency;
        _plotObjectLineweights.IsChecked = settings.PlotObjectLineweights;
        _addSequenceWhenPdfExists.IsChecked = settings.AddSequenceWhenPdfExists;
        _useFileNameAsPdfBookmark.IsChecked = settings.UseFileNameAsPdfBookmark;
        _useSheetTitleAsPdfBookmark.IsChecked = settings.UseSheetTitleAsPdfBookmark;
        _mergePdfByPaperSize.IsChecked = settings.MergePdfByPaperSize;
        _keepIndividualPdfsWhenMerging.IsChecked = settings.KeepIndividualPdfsWhenMerging;
        _openOutputDirectoryAfterBatchPrint.IsChecked = settings.OpenOutputDirectoryAfterBatchPrint;
        _openMergedPdfAfterMerge.IsChecked = settings.OpenMergedPdfAfterMerge;
        _generatePrintLog.IsChecked = settings.GeneratePrintLog;
        _convertTextToGeometryWhenPlotting.IsChecked = settings.ConvertTextToGeometryWhenPlotting;
        _rasterExportDpi.Value = RasterExportSettings.NormalizeDpi(settings.RasterExportDpi);
        _fileNamePattern.Text = settings.PdfFileNamePattern;
        _fileNameSequenceStart.Value = Math.Max(
            _fileNameSequenceStart.Min,
            Math.Min(_fileNameSequenceStart.Max, settings.FileNameSequenceStartNumber));
        _fileNameSequenceDigits.Value = Math.Max(
            _fileNameSequenceDigits.Min,
            Math.Min(_fileNameSequenceDigits.Max, settings.FileNameSequenceDigits));
        _autoFileNameSequenceDigits.IsChecked = settings.AutoFileNameSequenceDigits;
        UpdateSequenceDigitsState();
        UpdateFileNamePreview();
        _openExternalDwgForPlot.IsChecked = settings.OpenExternalDwgForPlot;
        _directoryColorIndex.SelectedIndex = Math.Max(0, Math.Min(256, settings.DirectoryColorIndex));
        _directoryTextHeight.Value = settings.DirectoryTextHeight;
        _directoryTextWidthFactor.Value = settings.DirectoryTextWidthFactor;
        _directoryRowHeight.Value = settings.DirectoryRowHeight;
        _directoryLayerName.Text = settings.DirectoryLayerName;
        _directoryDrawHeader.IsChecked = settings.DirectoryDrawHeader;
        _directoryDrawGridLines.IsChecked = settings.DirectoryDrawGridLines;
        SelectTextStyle(settings.DirectoryTextStyleName);
        LoadDirectoryColumns(settings.DirectoryColumns);
        _longPaperNameFormat.SelectedIndex = Math.Max(0, Math.Min(5, (int)settings.LongPaperNameFormat));
        _longPaperSnapTolerance.Value = settings.LongPaperSnapToleranceMm;
        ReloadScaleList(settings);
    }

    private void SaveSettings()
    {
        if (!TryReadSettingsFromControls(out var current))
        {
            return;
        }

        AppSettingsStore.Save(current);
        DialogResult = true;
        Close();
    }

    private bool TryReadSettingsFromControls(out AppSettings current)
    {
        current = AppSettingsStore.Load();
        if (!TryReadDirectoryColumns(out var directoryColumns))
        {
            return false;
        }

        current.PaperMatchToleranceMm = _paperTolerance.Value;
        current.RecognizeFourLineRectangleFrames = _recognizeFourLineRectangleFrames.IsChecked == true;
        current.HideFrameBoundaryWhenPlotting = _hideFrameBoundaryWhenPlotting.IsChecked == true;
        current.PlotTransparency = _plotTransparency.IsChecked == true;
        current.PlotObjectLineweights = _plotObjectLineweights.IsChecked == true;
        current.AddSequenceWhenPdfExists = _addSequenceWhenPdfExists.IsChecked == true;
        current.UseFileNameAsPdfBookmark = _useFileNameAsPdfBookmark.IsChecked == true;
        current.UseSheetTitleAsPdfBookmark = _useSheetTitleAsPdfBookmark.IsChecked == true;
        current.MergePdfByPaperSize = _mergePdfByPaperSize.IsChecked == true;
        current.KeepIndividualPdfsWhenMerging = _keepIndividualPdfsWhenMerging.IsChecked == true;
        current.OpenOutputDirectoryAfterBatchPrint = _openOutputDirectoryAfterBatchPrint.IsChecked == true;
        current.OpenMergedPdfAfterMerge = _openMergedPdfAfterMerge.IsChecked == true;
        current.GeneratePrintLog = _generatePrintLog.IsChecked == true;
        current.ConvertTextToGeometryWhenPlotting = _convertTextToGeometryWhenPlotting.IsChecked == true;
        current.RasterExportDpi = RasterExportSettings.NormalizeDpi((int)Math.Round(_rasterExportDpi.Value));
        if (string.IsNullOrWhiteSpace(_fileNamePattern.Text))
        {
            System.Windows.MessageBox.Show("Введите правило формирования имени файла.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        current.PdfFileNamePattern = _fileNamePattern.Text;
        current.FileNameSequenceStartNumber = (int)_fileNameSequenceStart.Value;
        current.FileNameSequenceDigits = (int)_fileNameSequenceDigits.Value;
        current.AutoFileNameSequenceDigits = _autoFileNameSequenceDigits.IsChecked == true;
        current.OpenExternalDwgForPlot = _openExternalDwgForPlot.IsChecked == true;
        current.DirectoryColorIndex = _directoryColorIndex.SelectedItem is DirectoryColorItem colorItem
            ? colorItem.Index
            : 7;
        current.DirectoryTextHeight = _directoryTextHeight.Value;
        current.DirectoryTextWidthFactor = _directoryTextWidthFactor.Value;
        current.DirectoryRowHeight = _directoryRowHeight.Value;
        current.DirectoryTextHeightRatio = Math.Max(0.01, Math.Min(0.9, current.DirectoryTextHeight / current.DirectoryRowHeight));
        current.DirectoryTextStyleName = _directoryTextStyle.SelectedItem?.ToString() == DefaultTextStyleDisplay
            ? ""
            : _directoryTextStyle.SelectedItem?.ToString() ?? "";
        current.DirectoryLayerName = string.IsNullOrWhiteSpace(_directoryLayerName.Text) ? "0" : _directoryLayerName.Text.Trim();
        current.DirectoryDrawHeader = _directoryDrawHeader.IsChecked == true;
        current.DirectoryDrawGridLines = _directoryDrawGridLines.IsChecked == true;
        current.DirectoryColumns = directoryColumns;
        current.LongPaperNameFormat = (LongPaperNameFormat)Math.Max(0, Math.Min(5, _longPaperNameFormat.SelectedIndex));
        current.LongPaperSnapToleranceMm = _longPaperSnapTolerance.Value;
        current.CustomScales = ReadCustomScalesFromList();
        return true;
    }

    private Document? GetActiveDocument()
    {
        try
        {

            return CadApp.DocumentManager.MdiActiveDocument;
        }
        catch
        {
            return null;
        }
    }

    private void ResetDefaults()
    {
        Apply(AppSettingsStore.Default());
    }

}
