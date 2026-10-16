using System;
using System.Threading;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using HostMgd.EditorInput;
using Teigha.Geometry;
using CadApp = HostMgd.ApplicationServices.Application;
#elif AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
public sealed partial class RectangleBatchPlotForm : Window
{
    private sealed class MarginOption
    {
        public double Value { get; set; }
        public override string ToString() => Value > 0
            ? $"+ {Value:0.#} mm"
            : $"- {Math.Abs(Value):0.#} mm";
    }

    private sealed class Row : INotifyPropertyChanged
    {
        private string _fileName = "";
        private string _paperChoice = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public PlotJob Job { get; set; } = new();
        public IReadOnlyList<PaperDetection> Options { get; set; } = new PaperDetection[0];


        public IReadOnlyList<string> PaperOptions =>
            Options.Select(PaperSizeDetector.FormatOption).ToList();

        public bool Selected
        {
            get => Job.Selected;
            set
            {
                if (Job.Selected == value) return;
                Job.Selected = value;
                OnPropertyChanged(nameof(Selected));
            }
        }

        public string FileName
        {
            get => _fileName;
            set
            {
                if (string.Equals(_fileName, value, StringComparison.Ordinal)) return;
                _fileName = value;
                OnPropertyChanged(nameof(FileName));
            }
        }


        public string DrawingNumber => Job.DrawingNumber ?? "";


        public string Title => Job.Title ?? "";

        public string PaperChoice
        {
            get => _paperChoice;
            set
            {
                if (string.Equals(_paperChoice, value, StringComparison.Ordinal)) return;
                _paperChoice = value;
                OnPropertyChanged(nameof(PaperChoice));
            }
        }

        public string Scale { get; private set; } = "";


        public string Number { get; private set; } = "";

        public void RefreshFromJob()
        {
            Scale = Job.ScaleText;
            OnPropertyChanged(nameof(Scale));
            OnPropertyChanged(nameof(DrawingNumber));
            OnPropertyChanged(nameof(Title));
        }

        public void SetNumber(int index)
        {
            var text = index.ToString();
            if (string.Equals(Number, text, StringComparison.Ordinal)) return;
            Number = text;
            OnPropertyChanged(nameof(Number));
        }

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private readonly Document _document;
    private readonly AppSettings _settings;
    private readonly TemporarySequenceOverlay _overlay;
    private int _highlightedJobIndex = -1;
    private int _overlayScheduleGeneration;
    private readonly BindingList<Row> _rows = new();
    private readonly BindingList<Row> _displayRows = new();
    private CancellationTokenSource? _printCts;
    private List<ObjectId>? _scanSelectionIds;
    private TitleBlockScanScope? _lastScanScope;
    private bool _updating;
    private bool _updatingPrintSelection;
    private bool _viewSortedByHeader;
    private bool _outputDirectoryIsCustom;
    private bool _outputDirectoryModified;
    private bool _suppressTextEvents;
    private bool _suppressComboEvents;
    private bool _suppressPaperEvents;
    private string _sortMemberPath = "";
    private List<Row>? _pendingPrintToggleRows;
    private string _dwfPlotDevice = "";
    private string _pdfPlotDevice = AcadPlotterInstaller.UsesBundledLaPlotters ? AcadPlotterInstaller.PreferredPdfPlotter : "";
    private bool _styleSelectionReady;

    private bool _hasAttributeIdentity;
    private List<(PlotJob Job, string DrawingNumber)>? _lastOverlayRebuildKey;
    private bool _overlayPainted;

    private bool _suppressSequenceOverlayFromMultiFile;

    public RectangleBatchPlotForm(Document document)
    {
        _document = document;
        _settings = AppSettingsStore.Load();
        _overlay = new TemporarySequenceOverlay(document);
        InitializeComponent();
        InitializeGrid();
        InitMarginCombo(_marginInput, _settings.PaperMarginMm);
        _marginInput.IsEnabled = _leaveMargin.IsChecked == true;
        _leaveMargin.IsChecked = _settings.LeavePaperMargin;
        _mergePdf.IsChecked = _settings.MergePdf;
        LoadPlotOptions();
        Closing += (_, _) =>
        {

            _printCts?.Cancel();
        };
    }

    private void InitializeGrid()
    {
        _grid.ItemsSource = _displayRows;
    }

    protected override void OnClosed(EventArgs e)
    {
        _overlayScheduleGeneration++;

        _overlay.Dispose();
        base.OnClosed(e);
    }


    public void Dispose() => Close();



    private void ScanCurrentDrawing_Click(object sender, RoutedEventArgs e) => ScanCurrentDrawing();

    private void ScanSelectedObjects_Click(object sender, RoutedEventArgs e) => ScanSelectedObjects();

    private void AddDwgFiles_Click(object sender, RoutedEventArgs e) => AddDwgFiles();

    private void ReloadFrames_Click(object sender, RoutedEventArgs e) => ReloadFrames();

    private void ClearRows_Click(object sender, RoutedEventArgs e) => ClearRows();

    private void ClearRows()
    {
        _suppressSequenceOverlayFromMultiFile = false;
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_rows.Count == 0 && _displayRows.Count == 0)
        {
            return;
        }

        if (MessageBox.Show("Очистить текущий список прямоугольных рамок?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        ReplaceBindingListContents(_rows, Array.Empty<Row>());
        ReplaceBindingListContents(_displayRows, Array.Empty<Row>());
        _scanSelectionIds = null;
        _lastScanScope = null;
        _hasAttributeIdentity = false;
        UpdateAttributeIdentityColumns();
        _viewSortedByHeader = false;
        _sortMemberPath = "";
        try
        {
            _overlay.Clear();
        }
        catch
        {
        }
    }



    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e) => ChooseOutputDirectory();

    private void OutputDirectory_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextEvents)
        {
            return;
        }


        _outputDirectoryModified = true;
        RefreshOutputPaths();
    }

    private void OutputDirectory_LostFocus(object sender, RoutedEventArgs e)
        => ApplyManuallyEnteredOutputDirectory();

    private void SavePathMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents)
        {
            return;
        }

        ApplySelectedSavePathMode();
    }

    private void OutputFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents)
        {
            return;
        }

        UpdateOutputFormatUi();
    }

    private void Style_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _styleSettingsButton.IsEnabled = _style.SelectedIndex >= 0 && !IsDwgOutput;
        if (_styleSelectionReady)
        {
            SaveCurrentPlotOptions();
        }
    }

    private void StyleSettings_Click(object sender, RoutedEventArgs e)
        => PlotStyleManager.EditSelectedStyle(this, _style.SelectedItem?.ToString());

    private void LeaveMargin_CheckedChanged(object sender, RoutedEventArgs e)
        => _marginInput.IsEnabled = SupportsLeaveMargin && _leaveMargin.IsChecked == true;

    private void PrintOrStop_Click(object sender, RoutedEventArgs e) => PrintOrStop();

    private void SortSettings_Click(object sender, RoutedEventArgs e) => ShowSortSettings();

    private void ScaleSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(3);

    private void GeneralSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(0);

        private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {

        e.Handled = true;
        ApplyHeaderSort(e.Column);
    }

    /// <summary>

    /// </summary>
    private bool TryHandleColumnHeaderSortClick(DependencyObject? source)
    {
        var header = FindAncestor<DataGridColumnHeader>(source);
        if (header?.Column == null)
        {
            return false;
        }

        ApplyHeaderSort(header.Column);
        return true;
    }

    private void ApplyHeaderSort(DataGridColumn column)
    {
        var columnIndex = _grid.Columns.IndexOf(column);
        if (columnIndex < 0 || columnIndex >= _grid.Columns.Count)
        {
            return;
        }

        var memberPath = column.SortMemberPath ?? "";
        if (_viewSortedByHeader && _sortMemberPath == memberPath)
        {
            _viewSortedByHeader = false;
            _sortMemberPath = "";
            RefreshDisplayRows();
            UpdateVisuals();
            return;
        }

        _viewSortedByHeader = true;
        _sortMemberPath = memberPath;
        var sorted = _rows.OrderBy(row => GetHeaderSortValue(row, memberPath), NaturalStringComparer.Instance).ToList();
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            ReplaceBindingListContents(_displayRows, sorted);
        }
        finally
        {
            _updating = wasUpdating;
        }

        UpdateDisplayIndexes();
        UpdateVisuals();
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private string GetHeaderSortValue(Row row, string memberPath)
    {
        switch (memberPath)
        {
            case nameof(Row.Selected):
                return row.Selected ? "1" : "0";
            case nameof(Row.FileName):
                return row.FileName;
            case nameof(Row.DrawingNumber):
                return row.DrawingNumber;
            case nameof(Row.Title):
                return row.Title;
            case nameof(Row.PaperChoice):
                return row.PaperChoice;
            case nameof(Row.Scale):
                return row.Scale;
        }


        return _rows.IndexOf(row).ToString("D8");
    }

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TryHandleColumnHeaderSortClick(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        var row = HitTestRow(e.OriginalSource as DependencyObject)?.Item as Row;
        if (row == null)
        {
            return;
        }

        if (e.OriginalSource is System.Windows.Controls.CheckBox
            && _grid.SelectedItems.Count > 1)
        {

            var highlightedRows = HighlightedRows();
            _pendingPrintToggleRows = highlightedRows.Contains(row) ? highlightedRows : null;
        }


        _highlightedJobIndex = _rows.IndexOf(row);
        _overlay.SetHighlight(row.Job);
    }

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = HitTestRow(e.OriginalSource as DependencyObject);
        if (container?.Item is not Row row)
        {
            return;
        }

        if (!_grid.SelectedItems.Contains(row))
        {
            if (_grid.SelectedItems.Count <= 1)
            {
                _grid.UnselectAll();
            }

            container.IsSelected = true;
        }

        if (_grid.SelectedItems.Contains(row))
        {
            _grid.CurrentItem = row;
        }
    }

    private static DataGridRow? HitTestRow(DependencyObject? source)
    {
        while (source != null && source is not DataGridRow)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return source as DataGridRow;
    }

    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {

        _changePaperItem.IsEnabled = CanBatchChangePaper(HighlightedRows());
    }

    private void PrintCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_updating || _updatingPrintSelection)
        {
            return;
        }

        if (((System.Windows.Controls.CheckBox)sender).DataContext is not Row row)
        {
            return;
        }


        ApplyPrintSelectionToHighlightedRows(row);
        RemoveUnselectedRows();
        RefreshFileNames();
        RefreshOutputPaths();
        UpdateVisuals();
    }

    private void PaperChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _updatingPrintSelection || _suppressPaperEvents)
        {
            return;
        }

        if (((ComboBox)sender).DataContext is not Row row)
        {
            return;
        }


        var value = row.PaperChoice;
        var option = row.Options.FirstOrDefault(candidate => string.Equals(PaperSizeDetector.FormatOption(candidate), value, StringComparison.Ordinal));
        if (option != null)
        {
            ApplyPaper(row.Job, option);
            row.RefreshFromJob();
            RefreshFileNames();
            RefreshOutputPaths();
            UpdateVisuals();
        }
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not Row row)
        {
            return;
        }

        if (IsDwgOutput)
        {
            MessageBox.Show("Вывод DWG выполняет разделение чертежа; предварительный просмотр печати недоступен.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }


        var device = SelectedDevice();
        if (string.IsNullOrWhiteSpace(device))
        {
            MessageBox.Show($"Не найдено устройство вывода для {SelectedOutputFormat}.{(IsDwfOutput ? "" : AcadPlotterInstaller.PdfDeviceNotFoundHint)}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }



        CadWindowFocus.HideForCadInput(this);
        try
        {
            SaveCurrentPlotOptions();
            row.Job.LeavePaperMargin = SupportsLeaveMargin && _leaveMargin.IsChecked == true;
            row.Job.PaperMarginMm = ReadMarginValue(_marginInput);

            var previewJobs = _rows
                .Where(candidate => candidate.Selected || ReferenceEquals(candidate, row))
                .Select(candidate => candidate.Job)
                .ToList();

            ApplyLeaveMarginSelection(previewJobs);
            CustomPaperBatchPreparer.Prepare(previewJobs, device);

            PendingPlotPreview.Start(new PendingPlotPreview.Request
            {
                Job = row.Job,
                DeviceName = device,
                StyleSheet = PlotStyleManager.ResolveJobStyle(row.Job, SelectedStyle()),
                Document = _document,
                OnFinally = () => CadWindowFocus.RestoreDialog(this),
                OnError = ex => MessageBox.Show(
                    "Ошибка предварительного просмотра: " + ex.Message,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error)
            }, Dispatcher);
        }
        catch (Exception ex)
        {
            PendingPlotPreview.Take();
            MessageBox.Show("Ошибка предварительного просмотра: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            CadWindowFocus.RestoreDialog(this);
        }
    }

    private void BatchChangePaper_Click(object sender, RoutedEventArgs e) => BatchChangeHighlightedPaper();

    private void MarkNotPrint_Click(object sender, RoutedEventArgs e) => MarkHighlightedNotPrint();

    private void DeleteHighlighted_Click(object sender, RoutedEventArgs e) => DeleteHighlighted();



    private void LoadRows(IReadOnlyList<RectangleFrameScanner.Result> results, bool append = false)
    {
        var rows = new List<Row>(results.Count);
        foreach (var result in results)
        {
            var option = result.PaperOptions[0];
            rows.Add(new Row
            {
                Job = result.Job,
                Options = result.PaperOptions,
                PaperChoice = PaperSizeDetector.FormatOption(option)
            });
        }

        if (append)
        {
            var existingKeys = new HashSet<string>(_rows.Select(row => PlotJobIdentityKey(row.Job)), StringComparer.OrdinalIgnoreCase);
            var merged = _rows.ToList();
            foreach (var row in rows)
            {
                if (existingKeys.Add(PlotJobIdentityKey(row.Job)))
                {
                    merged.Add(row);
                }
            }

            rows = merged;
        }

        _hasAttributeIdentity = rows.Any(row =>
            !string.IsNullOrWhiteSpace(row.Job.CadDrawingNumber)
            || !string.IsNullOrWhiteSpace(row.Job.CadTitle));
        if (_hasAttributeIdentity)
        {

            foreach (var row in rows)
            {
                row.Job.DrawingNumber = row.Job.CadDrawingNumber ?? "";
                row.Job.Title = row.Job.CadTitle ?? "";
            }
        }

        UpdateAttributeIdentityColumns();
        ReplaceBindingListContents(_rows, rows);
        _viewSortedByHeader = false;
        _sortMemberPath = "";
        SortRows();
    }

    private static string PlotJobIdentityKey(PlotJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.BlockHandle))
        {
            return $"H|{job.SourceFile}|{job.SpaceName}|{job.BlockHandle}";
        }

        return $"G|{job.SourceFile}|{job.SpaceName}|{job.MinX:0.###}|{job.MinY:0.###}|{job.MaxX:0.###}|{job.MaxY:0.###}";
    }


    private void UpdateAttributeIdentityColumns()
    {
        var visibility = _hasAttributeIdentity
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        _drawingNumberColumn.Visibility = visibility;
        _titleColumn.Visibility = visibility;
    }

    private void RefreshFileNames()
    {
        if (_hasAttributeIdentity)
        {
            RefreshAttributeIdentityFileNames();
            return;
        }

        var fallbackStem = GetDocumentFileStem();
        var digits = Math.Max(1, Math.Min(10, _settings.FileNameSequenceDigits));
        var printIndex = 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].Selected)
            {
                continue;
            }

            printIndex++;
            var stem = GetJobFileStem(_rows[i].Job, fallbackStem);
            _rows[i].Job.DrawingNumber = printIndex.ToString($"D{digits}");
            _rows[i].FileName = $"{stem}{printIndex.ToString($"D{digits}")}{SelectedOutputExtension}";
            _rows[i].RefreshFromJob();
        }

        RefreshOutputPaths();
    }

    /// <summary>


    /// </summary>
    private void RefreshAttributeIdentityFileNames()
    {
        var stem = GetDocumentFileStem();
        var legacyDigits = Math.Max(1, Math.Min(10, _settings.FileNameSequenceDigits));
        var selectedCount = _rows.Count(row => row.Selected);
        var patternDigits = FileNameSanitizer.ResolveSequenceDigits(
            _settings.AutoFileNameSequenceDigits,
            _settings.FileNameSequenceDigits,
            _settings.FileNameSequenceStartNumber,
            selectedCount);
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = _outputDirectory.Text.Trim();
        var selectedIndex = 0;
        var printIndex = 0;
        foreach (var row in _rows)
        {
            if (!row.Selected)
            {
                continue;
            }

            var sequenceNumber = _settings.FileNameSequenceStartNumber + selectedIndex;
            selectedIndex++;
            printIndex++;

            string baseName;
            if (RowHasAttributeIdentity(row))
            {
                baseName = FileNameSanitizer.FormatFileNamePattern(
                    _settings.PdfFileNamePattern,
                    row.Job,
                    sequenceNumber,
                    patternDigits,
                    _settings.LongPaperNameFormat,
                    _settings.LongPaperSnapToleranceMm);
            }
            else
            {

                var jobStem = GetJobFileStem(row.Job, stem);
                baseName = $"{jobStem}{printIndex.ToString($"D{legacyDigits}")}";
            }

            var fullPath = FileNameSanitizer.MakeUnique(
                string.IsNullOrWhiteSpace(directory) ? "." : directory,
                baseName,
                reservedPaths,
                _settings.AddSequenceWhenPdfExists,
                SelectedOutputExtension,
                createDirectory: false);
            reservedPaths.Add(fullPath);
            row.FileName = Path.GetFileName(fullPath);
            row.RefreshFromJob();
        }

        RefreshOutputPaths();
    }


    private string GetDocumentFileStem()
    {
        var stem = Path.GetFileNameWithoutExtension(_document.Database.Filename);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = Path.GetFileNameWithoutExtension(_document.Name);
        }

        return stem ?? "";
    }


    private static string GetJobFileStem(PlotJob job, string fallbackStem)
    {
        if (!string.IsNullOrWhiteSpace(job.SourceFile))
        {
            var stem = Path.GetFileNameWithoutExtension(job.SourceFile);
            if (!string.IsNullOrWhiteSpace(stem))
            {
                return stem;
            }
        }

        return fallbackStem ?? "";
    }


    private static bool RowHasAttributeIdentity(Row row)
        => !string.IsNullOrWhiteSpace(row.Job.CadDrawingNumber)
           || !string.IsNullOrWhiteSpace(row.Job.CadTitle);

    private void ReloadFrames()
    {
        _suppressSequenceOverlayFromMultiFile = false;
        try
        {
            List<RectangleFrameScanner.Result> results;
            if (_lastScanScope.HasValue)
            {
                results = ScanScopeWithProgress(_lastScanScope.Value);
            }
            else if (_scanSelectionIds != null)
            {
                results = ScanSelectionWithProgress(_scanSelectionIds);
            }
            else
            {
                return;
            }

            if (results.Count == 0)
            {

                LoadRows(results);
                MessageBox.Show("После повторного сканирования прямоугольные рамки не найдены.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TransformResultsToDcs(results);
            LoadRows(results);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("Распознавание отменено.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось повторно распознать рамки: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private TitleBlockScanScope? PromptScanScope() => BatchPlotCommands.PromptScanScope(this);

    /// <summary>

    /// </summary>
    private void ShowScanFailure(string action, Exception ex)
    {
        var detail = ex.ToString();
        try
        {
            _document.Editor.WriteMessage("\n" + action + "\n" + detail + "\n");
        }
        catch
        {
        }

        var logPath = "";
        try
        {
            Directory.CreateDirectory(BatchPlotLogger.LogDirectory);
            logPath = Path.Combine(
                BatchPlotLogger.LogDirectory,
                "ScanError_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log");
            File.WriteAllText(logPath, action + Environment.NewLine + detail, Encoding.UTF8);
        }
        catch
        {
            logPath = "";
        }

        try
        {
            System.Windows.Clipboard.SetText(detail);
        }
        catch
        {
        }

        var body = action + "\n\n" + detail;
        body += string.IsNullOrWhiteSpace(logPath)
            ? "\n\nВыполнена попытка скопировать полный текст в буфер обмена."
            : "\n\nПолный текст скопирован в буфер обмена и записан в журнал:\n" + logPath;

        const int maxChars = 6000;
        if (body.Length > maxChars)
        {
            body = body.Substring(0, maxChars) + "\n…(текст сокращён; полная версия находится в буфере обмена или журнале)";
        }

        MessageBox.Show(body, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void AddDwgFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Файлы DWG (*.dwg)|*.dwg",
            Multiselect = true,
            Title = "Выберите DWG для пакетной печати"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var files = dialog.FileNames
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            return;
        }

        var catalogErrors = new List<string>();
        var spaces = DwgSpaceCatalog.ListSpaces(files, catalogErrors);
        if (catalogErrors.Count > 0)
        {

        }

        if (spaces.Count == 0)
        {
            MessageBox.Show(
                catalogErrors.Count > 0
                    ? "Не удалось получить модель или листы для сканирования.\n" + string.Join("\n", catalogErrors)
                    : "В выбранных DWG нет доступной модели или листов.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var defaultStyle = SelectedStyle();
        var picker = new ScanSpacePickerDialog(
            spaces,
            defaultStyle,
            PlotStyleManager.GetAvailableCtbStyles());
        if (CadDialog.ShowModal(picker, this) != true)
        {
            return;
        }

        var selected = picker.SelectedSpaces;
        if (selected.Count == 0)
        {
            return;
        }


        ReplaceBindingListContents(_rows, Array.Empty<Row>());
        ReplaceBindingListContents(_displayRows, Array.Empty<Row>());
        ClearSequenceOverlay();
        _lastScanScope = null;
        _scanSelectionIds = null;
        _hasAttributeIdentity = false;
        UpdateAttributeIdentityColumns();

        var allResults = new List<RectangleFrameScanner.Result>();
        var errors = new List<string>();
        string currentPath;
        try
        {
            currentPath = string.IsNullOrWhiteSpace(_document.Database.Filename)
                ? ""
                : Path.GetFullPath(_document.Database.Filename);
        }
        catch
        {
            currentPath = _document.Database.Filename ?? "";
        }

        foreach (var fileGroup in selected.GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var file = fileGroup.Key;
            var allowed = new HashSet<string>(
                fileGroup.Select(s => s.LayoutName),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                var isCurrent = !string.IsNullOrWhiteSpace(currentPath)
                    && string.Equals(Path.GetFullPath(file), currentPath, StringComparison.OrdinalIgnoreCase);
                List<RectangleFrameScanner.Result> results;
                if (isCurrent)
                {
                    results = RectangleFrameScanner.ScanScope(
                        _document,
                        TitleBlockScanScope.AllSpaces,
                        _settings.PaperMatchToleranceMm,
                        _settings.RecognizeFourLineRectangleFrames,
                        progress: null,
                        cancellationToken: default,
                        allowedLayoutNames: allowed);
                    TransformResultsToDcs(results);
                }
                else
                {
                    using var db = new Database(false, true);
                    db.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
                    db.CloseInput(true);
                    results = RectangleFrameScanner.ScanDatabase(
                        db,
                        file,
                        TitleBlockScanScope.AllSpaces,
                        allowed,
                        _settings.PaperMatchToleranceMm,
                        _settings.RecognizeFourLineRectangleFrames);
                }

                var fileStyle = fileGroup.FirstOrDefault()?.StyleSheet ?? "";
                foreach (var result in results)
                {
                    result.Job.StyleSheet = fileStyle;
                }

                allResults.AddRange(results);
            }
            catch (Exception ex)
            {
                errors.Add($"{file}: {ex.Message}");
            }
        }

        if (allResults.Count == 0)
        {
            LoadRows(allResults);
            _suppressSequenceOverlayFromMultiFile = true;
            ClearSequenceOverlay();
            MessageBox.Show(
                errors.Count > 0
                    ? "Сканирование завершено, но прямоугольные рамки не найдены.\n" + string.Join("\n", errors)
                    : "В выбранных пространствах не найдены прямоугольные рамки с подходящими пропорциями.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        LoadRows(allResults);

        _suppressSequenceOverlayFromMultiFile = true;
        ClearSequenceOverlay();

        if (errors.Count > 0)
        {
            MessageBox.Show(
                "Не удалось отсканировать некоторые DWG:\n" + string.Join("\n", errors),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private bool IsCurrentDocumentSource(string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            return false;
        }

        try
        {
            var current = _document.Database.Filename;
            if (string.IsNullOrWhiteSpace(current))
            {
                return string.Equals(sourceFile, _document.Name, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(sourceFile, _document.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ClearSequenceOverlay()
    {
        _overlayScheduleGeneration++;
        _overlay.Clear(repaint: false);
        _overlayPainted = false;
        _lastOverlayRebuildKey = null;
    }

    /// <summary>

    /// </summary>
    private void ScanCurrentDrawing()
    {
        _suppressSequenceOverlayFromMultiFile = false;
        var scope = PromptScanScope();
        if (scope == null)
        {
            return;
        }

        try
        {
            var results = ScanScopeWithProgress(scope.Value);
            if (results.Count == 0)
            {
                MessageBox.Show("В области сканирования не найдены прямоугольные рамки с подходящими пропорциями.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TransformResultsToDcs(results);
            _lastScanScope = scope;
            _scanSelectionIds = null;
            LoadRows(results);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("Распознавание отменено.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось отсканировать текущий чертёж: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>


    /// </summary>
    private void ScanSelectedObjects()
    {
        _suppressSequenceOverlayFromMultiFile = false;
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var prompt = ObjectSelectionPrompt.Prompt(
                _document.Editor,
                "\nВыберите прямоугольные рамки для пакетной печати (правой кнопкой — область сканирования): ",
                ObjectSelectionPrompt.RectangleFrameFilter(_settings.RecognizeFourLineRectangleFrames));
            if (prompt.Cancelled)
            {
                return;
            }

            List<RectangleFrameScanner.Result> results;
            if (prompt.Scope is { } scope)
            {
                results = ScanScopeWithProgress(scope);
                if (results.Count == 0)
                {
                    MessageBox.Show("В области сканирования не найдены прямоугольные рамки с подходящими пропорциями.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                TransformResultsToDcs(results);
                _lastScanScope = null;
                _scanSelectionIds = null;
                LoadRows(results, append: true);
                return;
            }

            if (prompt.SelectedIds is not { Length: > 0 } selectedIds)
            {
                return;
            }

            results = ScanSelectionWithProgress(selectedIds);
            if (results.Count == 0)
            {
                MessageBox.Show("Среди выбранных объектов не найдены прямоугольные рамки с подходящими пропорциями.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TransformResultsToDcs(results);
            if (_scanSelectionIds == null)
            {
                _scanSelectionIds = selectedIds.ToList();
            }
            else
            {
                var known = new HashSet<ObjectId>(_scanSelectionIds);
                foreach (var id in selectedIds)
                {
                    if (known.Add(id))
                    {
                        _scanSelectionIds.Add(id);
                    }
                }
            }

            _lastScanScope = null;
            LoadRows(results, append: true);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("Распознавание отменено.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Ошибка сканирования выбранной области: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }


    private List<RectangleFrameScanner.Result> ScanScopeWithProgress(TitleBlockScanScope scope)
    {
        using var session = RectangleScanProgressSession.Start(this);
        return RectangleFrameScanner.ScanScope(
            _document,
            scope,
            _settings.PaperMatchToleranceMm,
            _settings.RecognizeFourLineRectangleFrames,
            session.Progress,
            session.Token);
    }


    private List<RectangleFrameScanner.Result> ScanSelectionWithProgress(IEnumerable<ObjectId> selectedIds)
    {
        using var session = RectangleScanProgressSession.Start(this);
        return RectangleFrameScanner.ScanSelection(
            _document,
            selectedIds,
            _settings.PaperMatchToleranceMm,
            _settings.RecognizeFourLineRectangleFrames,
            session.Progress,
            session.Token);
    }

    private void TransformResultsToDcs(List<RectangleFrameScanner.Result> results)
    {
        try
        {
            var wcsToDcs = BatchPlotCommands.BuildWcsToDcsMatrix(_document.Editor);
            foreach (var result in results)
            {
                var job = result.Job;
                if (job.UsesUserCoordinateSystem)
                {

                    job.IsDcsWindow = false;
                    continue;
                }


                if (job.IsPaperSpace)
                {
                    if (result.CornerPoints != null)
                    {
                        job.CornerPoints = (double[])result.CornerPoints.Clone();
                    }

                    job.IsDcsWindow = true;
                    continue;
                }

                if (result.CornerPoints != null)
                {

                    job.CornerPoints = (double[])result.CornerPoints.Clone();
                    var corners = new[]
                    {
                        new Point3d(result.CornerPoints[0], result.CornerPoints[1], 0).TransformBy(wcsToDcs),
                        new Point3d(result.CornerPoints[2], result.CornerPoints[3], 0).TransformBy(wcsToDcs),
                        new Point3d(result.CornerPoints[4], result.CornerPoints[5], 0).TransformBy(wcsToDcs),
                        new Point3d(result.CornerPoints[6], result.CornerPoints[7], 0).TransformBy(wcsToDcs)
                    };
                    job.MinX = corners.Min(p => p.X);
                    job.MinY = corners.Min(p => p.Y);
                    job.MaxX = corners.Max(p => p.X);
                    job.MaxY = corners.Max(p => p.Y);
                }
                else
                {
                    BatchPlotCommands.TransformPlotWindow(job, wcsToDcs);
                }

                job.IsDcsWindow = true;
            }
        }
        catch (Exception ex)
        {
            _document.Editor.WriteMessage($"\nНе удалось преобразовать координаты рамки WCS→DCS; используются координаты WCS: {ex.Message}");
        }
    }

    private void SortRows()
    {
        if (_rows.Count == 0)
        {
            RefreshDisplayRows();
            UpdateVisuals();
            return;
        }

        _viewSortedByHeader = false;
        _sortMemberPath = "";

        var allRows = _rows.ToList();
        var sortedRows = SortRectangleRows(allRows);

        ReplaceBindingListContents(_rows, sortedRows);
        RefreshDisplayRows();
        RefreshFileNames();
        UpdateVisuals();
    }

    /// <summary>

    /// </summary>
    private List<Row> SortRectangleRows(IReadOnlyList<Row> rows)
    {
        var result = new List<Row>(rows.Count);
        var sourceGroups = rows
            .Select((row, index) => new { Row = row, Index = index })
            .GroupBy(item => GetSourceGroupKey(item.Row.Job.SourceFile), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => GetSourceFileSortName(group.Key), NaturalStringComparer.Instance)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Min(item => item.Index));

        foreach (var sourceGroup in sourceGroups)
        {
            var fileRows = sourceGroup.Select(item => item.Row).ToList();
            result.AddRange(SortRowsWithinSourceFile(fileRows));
        }

        return result;
    }

    /// <summary>


    /// </summary>
    private List<Row> SortRowsWithinSourceFile(List<Row> fileRows)
    {
        if (fileRows.Count <= 1)
        {
            return fileRows;
        }

        var horizontalFirst = _settings.SortOrderHorizontalFirst;
        var jobToRow = fileRows.ToDictionary(row => row.Job, row => row);

        if (_hasAttributeIdentity)
        {
            var byDrawingNumber = fileRows
                .OrderBy(row => row.Job.DrawingNumber ?? "", NaturalStringComparer.Instance)
                .ThenBy(row => row.Job.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                .Select(row => row.Job)
                .ToList();

            return SpatiallyBreakTiesWithinFile(byDrawingNumber)
                .Select(job => jobToRow[job])
                .ToList();
        }

        return SpatialSorter.SortByLayout(fileRows.Select(row => row.Job).ToList(), horizontalFirst)
            .Select(job => jobToRow[job])
            .ToList();
    }

    /// <summary>

    /// </summary>
    private List<PlotJob> SpatiallyBreakTiesWithinFile(List<PlotJob> sortedJobs)
    {
        if (sortedJobs.Count <= 1)
        {
            return sortedJobs;
        }

        var horizontalFirst = _settings.SortOrderHorizontalFirst;
        var result = new List<PlotJob>(sortedJobs.Count);
        var i = 0;
        while (i < sortedJobs.Count)
        {
            var anchor = sortedJobs[i];
            var j = i + 1;
            while (j < sortedJobs.Count
                && NaturalStringComparer.Instance.Compare(sortedJobs[j].DrawingNumber, anchor.DrawingNumber) == 0
                && string.Equals(sortedJobs[j].Title, anchor.Title, StringComparison.CurrentCultureIgnoreCase))
            {
                j++;
            }

            var group = sortedJobs.GetRange(i, j - i);
            if (group.Count > 1)
            {
                group = SpatialSorter.SortByLayout(group, horizontalFirst);
            }

            result.AddRange(group);
            i = j;
        }

        return result;
    }

    private static string GetSourceGroupKey(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(sourceFile);
        }
        catch
        {
            return sourceFile!.Trim();
        }
    }

    private static string GetSourceFileSortName(string sourceFileKey)
    {
        if (string.IsNullOrWhiteSpace(sourceFileKey))
        {
            return "";
        }

        try
        {
            return Path.GetFileName(sourceFileKey);
        }
        catch
        {
            return sourceFileKey;
        }
    }

    private void RefreshDisplayRows()
    {
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            ReplaceBindingListContents(_displayRows, _rows);
        }
        finally
        {
            _updating = wasUpdating;
        }

        UpdateDisplayIndexes();
    }

    /// <summary>


    /// </summary>
    private static void ReplaceBindingListContents<T>(BindingList<T> target, IEnumerable<T> values)
    {
        target.RaiseListChangedEvents = false;
        try
        {
            target.Clear();
            foreach (var value in values)
            {
                target.Add(value);
            }
        }
        finally
        {
            target.RaiseListChangedEvents = true;
            target.ResetBindings();
        }
    }

    private void UpdateDisplayIndexes()
    {

        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].SetNumber(i + 1);
        }
    }

    private void RefreshOutputPaths()
    {
        if (_updating)
        {
            return;
        }

        var directory = _outputDirectory.Text.Trim();
        foreach (var row in _rows)
        {
            row.Job.OutputPath = Path.Combine(directory, row.FileName);
        }
    }

    private void ApplyPrintSelectionToHighlightedRows(Row changedRow)
    {
        var targetRows = _pendingPrintToggleRows ?? HighlightedRows();
        _pendingPrintToggleRows = null;
        if (targetRows.Count <= 1 || !targetRows.Contains(changedRow))
        {
            return;
        }

        try
        {
            _updatingPrintSelection = true;

            foreach (var row in targetRows)
            {
                row.Selected = changedRow.Selected;
            }
        }
        finally
        {
            _updatingPrintSelection = false;
        }
    }

    private List<Row> HighlightedRows()
    {
        var rows = _grid.SelectedItems.OfType<Row>().Distinct().ToList();
        if (rows.Count == 0 && _grid.CurrentItem is Row current)
        {
            rows.Add(current);
        }
        return rows;
    }

    private void MarkHighlightedNotPrint()
    {
        foreach (var row in HighlightedRows())
        {
            row.Selected = false;
        }
        RemoveUnselectedRows();
        RefreshFileNames();
        UpdateVisuals();
    }

    private void BatchChangeHighlightedPaper()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var rows = HighlightedRows();
        if (TryGetCommonPaperOptions(rows, out var identicalOptions))
        {
            ApplyBatchPaperFromSharedOptions(rows, identicalOptions);
            return;
        }

        MessageBox.Show(
            "Для выбранных рамок нет общего варианта бумаги на разрешённых масштабах.",
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }



    /// <summary>


    /// </summary>
    private bool? ShowChildModalKeepingListVisible(Window dialog)
    {
        var result = CadDialog.ShowModal(dialog);
        CadWindowFocus.RestoreDialog(this);
        return result;
    }

    private void ApplyBatchPaperFromSharedOptions(IReadOnlyList<Row> rows, IReadOnlyList<PaperDetection> options)
    {
        var dialog = new SinglePlotPaperSelectionForm(options);
        if (ShowChildModalKeepingListVisible(dialog) != true)
        {
            return;
        }

        var selectedPaper = dialog.SelectedPaper;
        var paperChoice = PaperSizeDetector.FormatOption(selectedPaper);
        _suppressPaperEvents = true;
        try
        {
            foreach (var row in rows)
            {
                row.PaperChoice = paperChoice;
                ApplyPaper(row.Job, selectedPaper);
                row.RefreshFromJob();
            }
        }
        finally
        {
            _suppressPaperEvents = false;
        }

        RefreshFileNames();
        RefreshOutputPaths();
        UpdateVisuals();
    }

    /// <summary>

    /// </summary>
    private void ApplyBatchPaperByAspectRatioName(IReadOnlyList<Row> rows, IReadOnlyList<PaperDetection> aspectNameOptions)
    {
        var dialog = new SinglePlotPaperSelectionForm(aspectNameOptions);
        if (ShowChildModalKeepingListVisible(dialog) != true)
        {
            return;
        }

        var selectedName = dialog.SelectedPaper.PaperName;
        _suppressPaperEvents = true;
        try
        {
            foreach (var row in rows)
            {
                if (!TryGetJobDrawingSize(row.Job, out var width, out var height))
                {
                    continue;
                }

                var aspectOptions = PaperSizeDetector.DetectRectangleBatchAspectRatioCandidates(
                    width,
                    height);
                var match = aspectOptions.FirstOrDefault(paper =>
                    string.Equals(paper.PaperName, selectedName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    continue;
                }


                row.Options = aspectOptions;
                row.PaperChoice = PaperSizeDetector.FormatOption(match);
                ApplyPaper(row.Job, match);
                row.RefreshFromJob();
            }
        }
        finally
        {
            _suppressPaperEvents = false;
        }

        RefreshFileNames();
        RefreshOutputPaths();
        UpdateVisuals();
    }

    private static bool CanBatchChangePaper(IReadOnlyList<Row> rows)
        => TryGetCommonPaperOptions(rows, out _);

    private static bool TryGetCommonPaperOptions(IReadOnlyList<Row> rows, out IReadOnlyList<PaperDetection> options)
    {
        options = new PaperDetection[0];
        if (rows.Count == 0 || rows[0].Options.Count == 0)
        {
            return false;
        }

        var first = rows[0].Options;

        foreach (var row in rows.Skip(1))
        {
            if (!HasSamePaperOptions(first, row.Options))
            {
                return false;
            }
        }

        options = first;
        return true;
    }

    /// <summary>

    /// </summary>
    private static bool TryGetCommonAspectRatioPaperOptions(
        IReadOnlyList<Row> rows,
        out IReadOnlyList<PaperDetection> options)
    {
        options = new PaperDetection[0];
        if (rows.Count == 0)
        {
            return false;
        }

        HashSet<string>? commonNames = null;
        IReadOnlyList<PaperDetection>? firstAspect = null;
        foreach (var row in rows)
        {
            if (!TryGetJobDrawingSize(row.Job, out var width, out var height))
            {
                return false;
            }

            var aspect = PaperSizeDetector.DetectRectangleBatchAspectRatioCandidates(
                width,
                height);
            if (aspect.Count == 0)
            {
                return false;
            }

            firstAspect ??= aspect;
            var names = new HashSet<string>(
                aspect.Select(paper => paper.PaperName),
                StringComparer.OrdinalIgnoreCase);
            commonNames = commonNames == null
                ? names
                : new HashSet<string>(commonNames.Where(names.Contains), StringComparer.OrdinalIgnoreCase);
            if (commonNames.Count == 0)
            {
                return false;
            }
        }

        if (firstAspect == null || commonNames == null || commonNames.Count == 0)
        {
            return false;
        }


        options = firstAspect
            .Where(paper => commonNames.Contains(paper.PaperName))
            .GroupBy(paper => paper.PaperName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        return options.Count > 0;
    }


    private static bool TryGetJobDrawingSize(PlotJob job, out double width, out double height)
    {
        width = 0;
        height = 0;
        if (job.UsesUserCoordinateSystem)
        {
            width = Math.Abs(job.UcsMaxX - job.UcsMinX);
            height = Math.Abs(job.UcsMaxY - job.UcsMinY);
            if (width > 1e-9d && height > 1e-9d)
            {
                return true;
            }
        }

        if (job.CornerPoints is { Length: >= 8 } points)
        {
            width = Math.Sqrt(
                (points[2] - points[0]) * (points[2] - points[0])
                + (points[3] - points[1]) * (points[3] - points[1]));
            height = Math.Sqrt(
                (points[4] - points[2]) * (points[4] - points[2])
                + (points[5] - points[3]) * (points[5] - points[3]));
            if (width > 1e-9d && height > 1e-9d)
            {
                return true;
            }
        }

        width = Math.Abs(job.MaxX - job.MinX);
        height = Math.Abs(job.MaxY - job.MinY);
        return width > 1e-9d && height > 1e-9d;
    }

    private static bool HasSamePaperOptions(IReadOnlyList<PaperDetection> first, IReadOnlyList<PaperDetection> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var i = 0; i < first.Count; i++)
        {
            if (!IsSamePaperOption(first[i], second[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSamePaperOption(PaperDetection first, PaperDetection second)
    {
        const double tolerance = 0.001;
        return string.Equals(first.PaperName, second.PaperName, StringComparison.Ordinal)
            && string.Equals(first.ScaleText, second.ScaleText, StringComparison.Ordinal)
            && Math.Abs(first.PaperWidthMm - second.PaperWidthMm) <= tolerance
            && Math.Abs(first.PaperHeightMm - second.PaperHeightMm) <= tolerance
            && Math.Abs(first.ScaleValue - second.ScaleValue) <= tolerance
            && first.IsLong == second.IsLong
            && first.RequiresCustomPaper == second.RequiresCustomPaper;
    }

    private void DeleteHighlighted()
    {
        foreach (var row in HighlightedRows())
        {
            _rows.Remove(row);
        }
        RefreshDisplayRows();
        RefreshFileNames();
        UpdateVisuals();
    }

    private void RemoveUnselectedRows()
    {
        var removed = false;
        foreach (var row in _rows.Where(row => !row.Selected).ToList())
        {

            _rows.Remove(row);
            removed = true;
        }

        if (removed)
        {
            RefreshDisplayRows();
        }
    }

    private void PrintOrStop()
    {
        if (_printCts != null)
        {
            PrintSessionLog.Info("Нажата кнопка «Остановить».");
            _printCts.Cancel();
            return;
        }

        // Issue #9: every "Начать печать" starts a new %TEMP% log, and nothing may escape the click handler.
        using var session = PrintSessionScope.Begin(Dispatcher, "универсальная пакетная печать (" + SelectedOutputFormat + ")");
        try
        {
            PrintSessionLog.Info($"Окно: универсальная пакетная печать; формат={SelectedOutputFormat}; папка={_outputDirectory.Text}; "
                + $"чертёж={_document?.Name}; рамок={_rows.Count}; выбрано={_rows.Count(row => row.Selected)}");
            PrintSessionLog.Info("Настройки: " + PrintSessionLog.DescribeSettings(_settings));
            Print();
        }
        catch (Exception ex)
        {
            session.Result = "печать прервана исключением: " + ex.Message;
            PrintSessionLog.Error("Печать прервана", ex);
            MessageBox.Show(
                "Ошибка универсальной пакетной печати: " + ex.Message + "\nЖурнал печати: " + PrintSessionLog.LastPath,
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Print()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        RefreshOutputPaths();
        if (IsDwgOutput)
        {
            SplitDwgs();
            return;
        }

        var selected = _rows.Where(row => row.Selected).Select(row => row.Job).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Не выбрана ни одна прямоугольная рамка.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var directory = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("Выберите папку вывода.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var device = SelectedDevice();
        if (string.IsNullOrWhiteSpace(device))
        {
            MessageBox.Show($"Не найдено устройство вывода для {SelectedOutputFormat}.{(IsDwfOutput ? "" : AcadPlotterInstaller.PdfDeviceNotFoundHint)}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(directory);
        SaveCurrentPlotOptions();
        ApplyLeaveMarginSelection(selected);
        var originalPaths = selected.ToDictionary(job => job, job => job.OutputPath);
        string? temporaryDirectory = null;
        var mergedOutput = Path.Combine(directory, SourceStem() + ".pdf");
        var mergePdf = IsPdfOutput && _mergePdf.IsChecked == true;
        var mergedOutputPaths = new List<string>();
        var completed = 0;
        var printLogLines = new List<string>();



        void AppendPrintLog(string level, string message)
        {
            PrintSessionLog.Write(level, message);
            if (_settings.GeneratePrintLog)
            {
                printLogLines.Add(BatchPlotLogger.Format(level, message));
            }
        }

        string SavePrintLog()
        {
            return _settings.GeneratePrintLog
                ? BatchPlotLogger.SaveRunLog(printLogLines)
                : "";
        }

        static string BuildLogText(string path)
        {
            var text = "\nЖурнал печати: " + PrintSessionLog.LastPath;
            return string.IsNullOrWhiteSpace(path) ? text : text + $"\nЖурнал: {path}";
        }

        AppendPrintLog(
            "INFO",
            $"Начало универсальной пакетной печати; листов={selected.Count}; формат={SelectedOutputFormat}; устройство={device}; стиль={SelectedStyle()}");
        foreach (var job in selected)
        {
            PrintSessionLog.Info("Лист: " + PrintSessionLog.DescribeJob(job));
        }

        var wasTopmost = Topmost;
        BatchPlotHostProgress.Begin();
        BatchPrintProgressSession? progress = null;
        try
        {

            _printCts = new CancellationTokenSource();
            _printButton.Content = "Остановить";
            _printButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 40, 40));
            _printButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 30, 30));

            Visibility = System.Windows.Visibility.Visible;
            Topmost = true;
            Activate();
            progress = BatchPrintProgressSession.Start(
                this,
                selected.Count,
                () => _printCts?.Cancel());

            if (mergePdf && !_settings.KeepIndividualPdfsWhenMerging)
            {
                temporaryDirectory = Path.Combine(Path.GetTempPath(), "ZwcadBatchPlot", "RectangleMerge_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
                for (var i = 0; i < selected.Count; i++)
                {
                    selected[i].OutputPath = Path.Combine(temporaryDirectory, $"{i + 1:D5}.pdf");
                }
            }

            _status.Text = $"Печать... 0 из {selected.Count}";
            progress.Report(0, selected.Count, "Подготовка пользовательской бумаги и путей вывода…");
            Pump();


            CustomPaperBatchPreparer.Prepare(selected, device);
            var results = PdfRasterExport.PlotMany(
                selected, device, SelectedStyle(), _document, _settings,
                beforeJob: job =>
                {
                    completed++;
                    _status.Text = $"Печать... {completed} из {selected.Count}";
                    var finalOutput = originalPaths.TryGetValue(job, out var path) ? path : job.OutputPath;
                    var detail =
                        $"Лист {completed} из {selected.Count}\n" +
                        $"Пространство: {job.SpaceName}\n" +
                        $"Выходной файл: {System.IO.Path.GetFileName(finalOutput)}";
                    progress.Report(completed, selected.Count, detail);
                    AppendPrintLog(
                        "INFO",
                        $"Начало печати {completed}/{selected.Count}; источник={job.SourceFile}; пространство={job.SpaceName}; файл={finalOutput}");
                    Visibility = System.Windows.Visibility.Visible;
                    Activate();
                    Pump();
                },
                cancellationToken: _printCts.Token);

            foreach (var result in results)
            {
                var finalOutput = originalPaths.TryGetValue(result.Job, out var path)
                    ? path
                    : result.Job.OutputPath;
                AppendPrintLog(
                    result.Succeeded ? "INFO" : "ERROR",
                    result.Succeeded
                        ? $"Печать завершена; пространство={result.Job.SpaceName}; файл={finalOutput}"
                        : $"Ошибка печати; пространство={result.Job.SpaceName}; файл={finalOutput}; ошибка={result.Error}");
                if (!result.Succeeded)
                {
                    PrintSessionLog.Error("Лист " + result.Job.SpaceName + " -> " + finalOutput, result.Error!);
                }
            }

            var failures = results.Where(result => !result.Succeeded).ToList();
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", failures.Select(result => result.Error?.Message)));
            }

            if (mergePdf)
            {
                _status.Text = "Объединение PDF...";
                progress?.SetPhase("Объединение PDF…", "Подождите; после завершения окно будет закрыто автоматически.");
                Pump();
                var mergeInputs = selected.Select(job => new PdfMergeInput(
                    job.OutputPath,
                    Path.GetFileNameWithoutExtension(originalPaths[job]),
                    OutputPaperNameResolver.Resolve(
                        job,
                        _settings.LongPaperSnapToleranceMm),
                    job.PaperWidthMm,
                    job.PaperHeightMm)).ToList();
                var mergePlans = PdfDocumentService.PlanMerges(
                    mergeInputs,
                    mergedOutput,
                    _settings.MergePdfByPaperSize,
                    _settings.AddSequenceWhenPdfExists);
                foreach (var mergePlan in mergePlans)
                {
                    PdfDocumentService.Merge(
                        mergePlan.Inputs,
                        mergePlan.OutputPath,
                        _settings.UseFileNameAsPdfBookmark);
                    mergedOutputPaths.Add(mergePlan.OutputPath);
                    AppendPrintLog("INFO", $"PDF объединён; файл={mergePlan.OutputPath}");
                }
            }

            if (mergePdf && _settings.OpenMergedPdfAfterMerge)
            {
                OpenMergedPdfFiles(mergedOutputPaths);
            }
            else if (!mergePdf && _settings.OpenOutputDirectoryAfterBatchPrint)
            {
                RevealOutput(null, directory);
            }
            _status.Text = $"Готово; листов: {selected.Count}";
            AppendPrintLog("INFO", $"Универсальная пакетная печать завершена; успешно={selected.Count}; ошибок=0");
            var printLogPath = SavePrintLog();
            var printLogText = BuildLogText(printLogPath);
            MessageBox.Show(
                mergePdf
                    ? $"Печать и объединение завершены: листов — {selected.Count}, файлов PDF — {mergedOutputPaths.Count}.\n{string.Join("\n", mergedOutputPaths)}{printLogText}"
                    : $"Печать завершена; листов: {selected.Count}.\n{directory}{printLogText}",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = $"Остановлено (готово {completed} из {selected.Count})";
            AppendPrintLog("INFO", $"Печать отменена пользователем; начато={completed}/{selected.Count}");
            var printLogPath = SavePrintLog();
            MessageBox.Show($"Печать остановлена.\nГотово {completed} из {selected.Count}.{BuildLogText(printLogPath)}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка печати";
            PrintSessionLog.Error("Печать прервана", ex);
            AppendPrintLog("ERROR", "Ошибка универсальной пакетной печати: " + ex);
            var printLogPath = SavePrintLog();
            MessageBox.Show("Ошибка универсальной пакетной печати: " + ex.Message + BuildLogText(printLogPath), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Every cleanup step runs even if a previous one fails: an exception here would close the CAD (issue #9).
            _printCts?.Dispose();
            _printCts = null;

            RunPrintCleanup("Сброс кнопки печати", () =>
            {
                _printButton.Content = "Начать печать";
                _printButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215));
                _printButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 95, 170));
                Topmost = wasTopmost;
            });
            RunPrintCleanup("Закрытие окна хода печати", () => progress?.Dispose());
            progress = null;
            BatchPlotHostProgress.End();

            foreach (var pair in originalPaths)
            {
                pair.Key.OutputPath = pair.Value;
            }
            if (!string.IsNullOrWhiteSpace(temporaryDirectory))
            {
                try { Directory.Delete(temporaryDirectory, true); } catch { }
            }
            RunPrintCleanup("Обновление окна", UpdateVisuals);
        }
    }

    private static void RunPrintCleanup(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            PrintSessionLog.Error(step, ex);
        }
    }

    private void SplitDwgs()
    {
        var selected = _rows.Where(row => row.Selected).Select(row => row.Job).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Не выбрана ни одна прямоугольная рамка.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var directory = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("Выберите папку вывода.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"По выбранным рамкам будет создано файлов DWG: {selected.Count}.\n\nПапка вывода: {directory}\n\nПродолжить?",
            "Разделение DWG",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        SaveCurrentPlotOptions();
        Cursor = Cursors.Wait;
        IsEnabled = false;
        try
        {
            _status.Text = $"Создание DWG... 0 из {selected.Count}";
            var completed = 0;
            var explicitPaths = selected.ToDictionary(job => job, job => job.OutputPath);
            var results = DwgSplitService.SplitMany(
                selected,
                _document,
                _settings,
                beforeJob: _ =>
                {
                    completed++;
                    _status.Text = $"Создание DWG... {completed} из {selected.Count}";
                    Pump();
                },
                explicitOutputPaths: explicitPaths);

            var failures = results.Where(result => result.Error != null).ToList();
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", failures.Select(result => result.Error?.Message)));
            }

            foreach (var result in results)
            {
                result.Job.OutputPath = result.OutputPath;
            }
                if (_settings.OpenOutputDirectoryAfterBatchPrint)
            {
                RevealOutput(null, directory);
            }
            _status.Text = $"Разделение завершено; файлов: {selected.Count}";
            MessageBox.Show($"Разделение DWG завершено; файлов: {selected.Count}.\n{directory}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка разделения DWG";
            MessageBox.Show("Не удалось разделить DWG: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            Cursor = Cursors.Arrow;
            UpdateVisuals();
        }
    }

    private void ApplyLeaveMarginSelection(IEnumerable<PlotJob> jobs)
    {

        var leaveMargin = SupportsLeaveMargin && _leaveMargin.IsChecked == true;
        var marginMm = ReadMarginValue(_marginInput);
        foreach (var job in jobs)
        {

            job.LeavePaperMargin = leaveMargin;
            job.PaperMarginMm = marginMm;

            job.RequiresCustomPaperRegistration =
                job.DetectedRequiresCustomPaperRegistration || (leaveMargin && marginMm > 0);
            if (!leaveMargin || marginMm <= 0)
            {
                job.EffectivePaperWidthMm = 0;
                job.EffectivePaperHeightMm = 0;
                job.RequireExactPaperSize = false;
                job.UseExactWindowScale = false;
                job.CustomPaperWasAdded = false;
            }
        }
    }

    private void LoadPlotOptions()
    {
        _suppressComboEvents = true;
        _outputFormatCombo.Items.Clear();
        _outputFormatCombo.Items.Add("PDF");
        _outputFormatCombo.Items.Add("PNG");
        _outputFormatCombo.Items.Add("JPG");
        _outputFormatCombo.Items.Add("DWF");
        _outputFormatCombo.Items.Add("DWG");
        _outputFormatCombo.SelectedIndex = 0;
        RefreshSavePathModeOptions(preserveSelection: false);

        var pdfInstall = AcadPlotterInstaller.InstallBundledPlotter();
        var pngInstall = AcadPlotterInstaller.InstallPngPlotter();
        var jpgInstall = AcadPlotterInstaller.InstallJpgPlotter();
        var dwfInstall = AcadPlotterInstaller.InstallDwfPlotter();
        AcadPlotterInstaller.RefreshPlotterDevicesIfNeeded(
            pdfInstall.Written || pngInstall.Written || jpgInstall.Written || dwfInstall.Written);
        var validator = PlotSettingsValidator.Current;
        var devices = validator.GetPlotDeviceList()
            .Cast<object>()
            .Select(item => item?.ToString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        // nanoCAD: the printer chosen in the main window (issue #13).
        _pdfPlotDevice = AcadPlotterInstaller.ResolvePdfPlotDevice(devices, _settings.PdfPrinter);
#if NANOCAD
        // LA_dwf.pc3 is not installed in nanoCAD (issue #5): use its own DWF plotter.
        _dwfPlotDevice = PlotDeviceSelector.SelectDwfDevice(devices) ?? "";
#else
        _dwfPlotDevice = FindPlotDevice(
            devices,
            dwfInstall.DeviceName,
            value => value.IndexOf("DWF", StringComparison.OrdinalIgnoreCase) >= 0
                     && value.IndexOf("DWFx", StringComparison.OrdinalIgnoreCase) < 0,
            AcadPlotterInstaller.PreferredDwfPlotter,
            "DWF6 ePlot.pc3",
            "DWF6 ePlot.pc5",
            "ZWPLOT_DWF.pc5",
            "M_DWF.pc5");
#endif
        foreach (var style in PlotStyleManager.GetAvailableCtbStyles())
        {
            _style.Items.Add(style);
        }
        PlotStyleManager.RestoreSavedStyle(_style, _settings.LastStyleSheet);

        SaveCurrentPlotOptions();
        UpdateOutputFormatUi();
        _suppressComboEvents = false;
        _styleSelectionReady = true;
    }

    private static string FindPlotDevice(
        IReadOnlyList<string> devices,
        string installedPlotter,
        Func<string, bool> fallbackPredicate,
        params string[] preferred)
    {
        foreach (var expected in new[] { installedPlotter }
                     .Concat(preferred)
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var match = devices.FirstOrDefault(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }


        return devices.FirstOrDefault(fallbackPredicate) ?? "";
    }

    private void SetAll(bool selected)
    {
        foreach (var row in _rows)
        {
            row.Selected = selected;
        }
        RefreshFileNames();
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var selected = _rows.Count(row => row.Selected);
        var order = _settings.SortOrderHorizontalFirst ? "слева направо, сверху вниз" : "сверху вниз, слева направо";
        _status.Text = $"Найдено рамок: {_rows.Count}  |  Выбрано: {selected}  |  Формат: {SelectedOutputFormat}  |  Порядок: {order}  |  Вывод: {_outputDirectory.Text}";
        ScheduleOverlayIfRebuildNeeded();
    }

    /// <summary>

    /// </summary>
    private List<(PlotJob Job, string DrawingNumber)> CaptureOverlayRebuildKey()
    {
        var keys = new List<(PlotJob Job, string DrawingNumber)>();
        foreach (var row in _displayRows)
        {
            if (!row.Selected)
            {
                continue;
            }

            keys.Add((row.Job, row.Job.DrawingNumber ?? ""));
        }

        return keys;
    }

    /// <summary>

    /// </summary>
    private static bool OverlayRebuildKeysEqual(
        IReadOnlyList<(PlotJob Job, string DrawingNumber)>? left,
        IReadOnlyList<(PlotJob Job, string DrawingNumber)>? right)
    {
        if (left == null || right == null)
        {
            return left == null && right == null;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i].Job, right[i].Job)
                || !string.Equals(left[i].DrawingNumber, right[i].DrawingNumber, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>

    /// </summary>
    private void ScheduleOverlayIfRebuildNeeded()
    {
        var key = CaptureOverlayRebuildKey();
        if (_overlayPainted && OverlayRebuildKeysEqual(_lastOverlayRebuildKey, key))
        {
            return;
        }

        ScheduleOverlayRefresh();
    }

    /// <summary>

    /// </summary>
    private void ScheduleOverlayRefresh()
    {
        if (_suppressSequenceOverlayFromMultiFile)
        {
            ClearSequenceOverlay();
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        var generation = ++_overlayScheduleGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLoaded || generation != _overlayScheduleGeneration)
            {
                return;
            }

            ShowOverlayNow();
        }));
    }

    private void ShowOverlayNow()
    {
        try
        {
            var selectedJobs = _displayRows.Where(row => row.Selected).Select(row => row.Job).ToList();
            var highlightJob = (_highlightedJobIndex >= 0 && _highlightedJobIndex < _rows.Count)
                ? _rows[_highlightedJobIndex].Job
                : null;
            _overlay.Show(selectedJobs, highlightJob);
            _lastOverlayRebuildKey = CaptureOverlayRebuildKey();
            _overlayPainted = true;
        }
        catch
        {
            _overlay.Clear(repaint: false);
            _lastOverlayRebuildKey = null;
            _overlayPainted = false;
        }
    }

    private void ChooseOutputDirectory()
    {

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выберите папку вывода",
            SelectedPath = _outputDirectory.Text
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _outputDirectoryIsCustom = true;
            SetOutputDirectoryText(dialog.SelectedPath);
            SaveCurrentPlotOptions();
            RefreshOutputPaths();
        }
    }

    private void ApplyManuallyEnteredOutputDirectory()
    {
        if (!_outputDirectoryModified)
        {
            return;
        }

        _outputDirectoryModified = false;
        var directory = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            _outputDirectoryIsCustom = false;
            UpdateAutomaticOutputDirectory();
        }
        else
        {
            _outputDirectoryIsCustom = true;
            SetOutputDirectoryText(directory);
        }

        SaveCurrentPlotOptions();
        RefreshOutputPaths();
    }

    private void RefreshSavePathModeOptions(bool preserveSelection)
    {
        var selectedIndex = preserveSelection && _savePathModeCombo.SelectedIndex >= 0
            ? Math.Min(_savePathModeCombo.SelectedIndex, 1)
            : 0;
        var format = SelectedOutputFormat;
        var formatPathText = string.IsNullOrWhiteSpace(format)
            ? "Папка исходного файла/формат вывода"
            : "Папка исходного файла/" + format;

        var wasSuppressing = _suppressComboEvents;
        _suppressComboEvents = true;
        try
        {
            _savePathModeCombo.Items.Clear();
            _savePathModeCombo.Items.Add("Папка исходного файла");
            _savePathModeCombo.Items.Add(formatPathText);
            _savePathModeCombo.SelectedIndex = selectedIndex;
        }
        finally
        {
            _suppressComboEvents = wasSuppressing;
        }
    }

    private void ApplySelectedSavePathMode()
    {
        _outputDirectoryIsCustom = false;
        UpdateAutomaticOutputDirectory();
        SaveCurrentPlotOptions();
        RefreshOutputPaths();
        UpdateVisuals();
    }

    private void UpdateAutomaticOutputDirectory()
    {
        var subfolder = AutomaticOutputSubfolder;
        SetOutputDirectoryText(string.IsNullOrWhiteSpace(subfolder)
            ? SourceDirectory()
            : Path.Combine(SourceDirectory(), subfolder));
    }

    private void SetOutputDirectoryText(string text)
    {
        _suppressTextEvents = true;
        try
        {
            _outputDirectory.Text = text;
        }
        finally
        {
            _suppressTextEvents = false;
        }
        _outputDirectoryModified = false;
    }

    private void UpdateOutputFormatUi()
    {
        RefreshSavePathModeOptions(preserveSelection: true);
        if (!_outputDirectoryIsCustom)
        {
            UpdateAutomaticOutputDirectory();
        }

        var plotOutput = !IsDwgOutput;
        _style.IsEnabled = plotOutput;
        _styleSettingsButton.IsEnabled = plotOutput && _style.SelectedIndex >= 0;
        _leaveMargin.IsEnabled = SupportsLeaveMargin;
        _marginInput.IsEnabled = SupportsLeaveMargin && _leaveMargin.IsChecked == true;
        _mergePdf.IsEnabled = IsPdfOutput;
        RefreshFileNames();
        SaveCurrentPlotOptions();
        UpdateVisuals();
    }

    private string SourceDirectory()
    {
        var file = _document.Database.Filename;
        return string.IsNullOrWhiteSpace(file)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.GetDirectoryName(file) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private string SourceStem()
    {
        var value = Path.GetFileNameWithoutExtension(_document.Database.Filename);
        return string.IsNullOrWhiteSpace(value) ? Path.GetFileNameWithoutExtension(_document.Name) : value;
    }

    private string SelectedOutputFormat => _outputFormatCombo.SelectedItem?.ToString()?.Trim() ?? "PDF";
    private string SelectedOutputExtension => "." + SelectedOutputFormat.ToLowerInvariant();
    private bool IsPdfOutput => string.Equals(SelectedOutputFormat, "PDF", StringComparison.OrdinalIgnoreCase);
    private bool IsDwgOutput => string.Equals(SelectedOutputFormat, "DWG", StringComparison.OrdinalIgnoreCase);
    private bool IsDwfOutput => string.Equals(SelectedOutputFormat, "DWF", StringComparison.OrdinalIgnoreCase);

    private bool SupportsLeaveMargin => !IsDwgOutput;
    private string? AutomaticOutputSubfolder => _savePathModeCombo.SelectedIndex == 1
        ? FileNameSanitizer.Clean(SelectedOutputFormat)
        : null;

    private string SelectedDevice() => IsDwfOutput
        ? _dwfPlotDevice
        : _pdfPlotDevice;
    private string SelectedStyle() => _style.SelectedItem?.ToString() ?? "";

    private void SaveCurrentPlotOptions()
    {
        _settings.LastPlotDevice = _pdfPlotDevice;
        var style = PlotStyleManager.NormalizeStyleName(SelectedStyle());
        if (!string.IsNullOrEmpty(style))
        {
            _settings.LastStyleSheet = style;
        }
        _settings.MergePdf = _mergePdf.IsChecked == true;
        _settings.LeavePaperMargin = _leaveMargin.IsChecked == true;
        _settings.PaperMarginMm = ReadMarginValue(_marginInput);
        AppSettingsStore.Save(_settings);
    }


    private static void InitMarginCombo(ComboBox combo, double savedValue)
    {
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

    private static void ApplyPaper(PlotJob job, PaperDetection paper)
    {
        job.PaperName = paper.PaperName;
        job.PaperWidthMm = paper.PaperWidthMm;
        job.PaperHeightMm = paper.PaperHeightMm;
        job.PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm";
        job.ScaleText = paper.ScaleText;
        job.DetectedRequiresCustomPaperRegistration = paper.RequiresCustomPaper;
        job.RequiresCustomPaperRegistration = paper.RequiresCustomPaper;

        if (!paper.RequiresCustomPaper)
        {
            job.RequireExactPaperSize = false;
            job.UseExactWindowScale = false;
            job.CustomPaperWasAdded = false;
        }
    }

    private static void RevealOutput(string? file, string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = !string.IsNullOrWhiteSpace(file) && File.Exists(file)
                    ? $"/select,\"{Path.GetFullPath(file)}\""
                    : $"\"{Path.GetFullPath(directory)}\"",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static void OpenMergedPdfFiles(IEnumerable<string> outputFiles)
    {
        foreach (var outputFile in outputFiles.Where(File.Exists))
        {
            try
            {

                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(outputFile),
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
    }

    private void ShowSortSettings()
    {

        var dialog = new SortOrderDialog(
            _settings.SortOrderHorizontalFirst,
            showSortBasis: false,
            sortMode: TitleBlockSortMode.Spatial);
        if (ShowChildModalKeepingListVisible(dialog) != true) return;
        _settings.SortOrderHorizontalFirst = dialog.HorizontalFirst;
        AppSettingsStore.Save(_settings);
        SortRows();
    }

    private void ShowSettingsAtTab(int tabIndex)
    {
        while (true)
        {
            SettingsForm.InitialTabIndex = tabIndex;
            var form = new SettingsForm();
            if (CadDialog.ShowModal(form) != true)
            {
                break;
            }


            var updated = AppSettingsStore.Load();
            var recognitionSettingsChanged =
                Math.Abs(_settings.PaperMatchToleranceMm - updated.PaperMatchToleranceMm) > 1e-9
                || _settings.RecognizeFourLineRectangleFrames
                != updated.RecognizeFourLineRectangleFrames
                || !ScalesEqual(_settings.CustomScales, updated.CustomScales);
            _settings.PaperMatchToleranceMm = updated.PaperMatchToleranceMm;
            _settings.RecognizeFourLineRectangleFrames = updated.RecognizeFourLineRectangleFrames;
            _settings.CustomScales = updated.CustomScales;
            _settings.HideFrameBoundaryWhenPlotting = updated.HideFrameBoundaryWhenPlotting;
            _settings.PlotTransparency = updated.PlotTransparency;
            _settings.PlotObjectLineweights = updated.PlotObjectLineweights;
            _settings.GeneratePrintLog = updated.GeneratePrintLog;
            _settings.ConvertTextToGeometryWhenPlotting = updated.ConvertTextToGeometryWhenPlotting;
            _settings.LongPaperSnapToleranceMm = updated.LongPaperSnapToleranceMm;
            _settings.LongPaperNameFormat = updated.LongPaperNameFormat;
            _settings.SortOrderHorizontalFirst = updated.SortOrderHorizontalFirst;
            _settings.PdfFileNamePattern = updated.PdfFileNamePattern;
            _settings.AddSequenceWhenPdfExists = updated.AddSequenceWhenPdfExists;
            _settings.FileNameSequenceDigits = updated.FileNameSequenceDigits;
            _settings.AutoFileNameSequenceDigits = updated.AutoFileNameSequenceDigits;
            _settings.FileNameSequenceStartNumber = updated.FileNameSequenceStartNumber;

            if (!form.RequestPickDirectoryRowHeight
                && !form.RequestPickDirectoryTextAppearance
                && !form.RequestPickScaleFromCad
                && string.IsNullOrWhiteSpace(form.RequestedDirectoryColumnKey))
            {
                if (recognitionSettingsChanged)
                {

                    ReloadFrames();
                }
                else
                {

                    RefreshFileNames();
                }
                return;
            }

            tabIndex = form.SelectedTabIndex;
            Hide();
            Pump();
            try
            {
                var document = GetActiveCadDocument();
                if (document == null)
                {
                    MessageBox.Show(
                        "Нет доступного чертежа CAD. Откройте чертёж и повторите попытку.",
                        "Настройки пакетной печати",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    continue;
                }

                var settings = AppSettingsStore.Load();
                bool ok;
                string message;
                if (form.RequestPickScaleFromCad)
                {


                    ok = ScaleSettingsPicker.PromptScaleFromFrame(document, settings, out settings, out message);
                    _settings.CustomScales = settings.CustomScales;
                }
                else if (form.RequestPickDirectoryTextAppearance)
                {
                    ok = DirectoryTableGenerator.PromptTextAppearance(document, settings, out _, out message);
                }
                else if (form.RequestPickDirectoryRowHeight)
                {
                    ok = DirectoryTableGenerator.PromptRowHeight(document, settings, out _, out message);
                }
                else
                {
                    ok = DirectoryTableGenerator.PromptColumnSize(
                        document,
                        settings,
                        form.RequestedDirectoryColumnKey ?? "",
                        out _,
                        out message);
                }


                if (!ok)
                {
                    MessageBox.Show(message, "Настройки пакетной печати", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                Show();
                Activate();
            }
        }
    }

    private static void Pump()
    {

        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
    }


    private static bool ScalesEqual(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (Math.Abs(left[i] - right[i]) > 1e-9)
            {
                return false;
            }
        }

        return true;
    }

    private static Document? GetActiveCadDocument()
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
}
