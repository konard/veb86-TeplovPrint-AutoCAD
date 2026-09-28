using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;


public sealed class FieldBoxSelectInitialState
{
    public LocalRectangle TitleRegion { get; set; } = new();
    public LocalRectangle DrawingNumberRegion { get; set; } = new();
    public LocalRectangle DateRegion { get; set; } = new();
    public LocalRectangle RevisionRegion { get; set; } = new();
    public LocalRectangle PhaseRegion { get; set; } = new();
    public LocalRectangle Info1Region { get; set; } = new();
    public LocalRectangle Info2Region { get; set; } = new();
    public string PaperName { get; set; } = "";
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }
}

/// <summary>



/// </summary>
public sealed class FieldBoxSelectDialog : Form
{
    private readonly Editor _editor;
    private readonly Matrix3d _inverseBlockTransform;
    private readonly Matrix3d _blockTransform;
    private readonly TransientFrameMarkers _markers;


    private readonly Dictionary<string, (Point3d Corner1, Point3d Corner2)> _fieldCorners = new(StringComparer.Ordinal);


    private (Point3d Corner1, Point3d Corner2) _printAreaCorners;

    private readonly Label _printAreaStatus;
    private readonly Label _titleStatus;
    private readonly Label _numberStatus;
    private readonly Label _dateStatus;
    private readonly Label _revisionStatus;
    private readonly Label _phaseStatus;
    private readonly Label _info1Status;
    private readonly Label _info2Status;


    public LocalRectangle ReferenceFrame { get; set; }

    public LocalRectangle TitleRegion { get; private set; } = new();
    public LocalRectangle DrawingNumberRegion { get; private set; } = new();
    public LocalRectangle DateRegion { get; private set; } = new();
    public LocalRectangle RevisionRegion { get; private set; } = new();
    public LocalRectangle PhaseRegion { get; private set; } = new();
    public LocalRectangle Info1Region { get; private set; } = new();
    public LocalRectangle Info2Region { get; private set; } = new();



    private readonly ComboBox _paperName = new();
    private readonly PaperSizeDetector.DetectionOptions _paperDetectionOptions;
    private IReadOnlyList<PaperDetection> _paperOptions = Array.Empty<PaperDetection>();


    private readonly ComboBox _libraryTemplateCombo = new();
    private readonly Label _libraryTemplateHint = new();
    private List<TitleBlockDefinition> _compatibleLibraryEntries = new();
    private bool _applyingLibraryTemplate;

    private PaperDetection? SelectedPaper =>
        _paperName.SelectedIndex >= 0 && _paperName.SelectedIndex < _paperOptions.Count
            ? _paperOptions[_paperName.SelectedIndex]
            : null;

    public string PaperName => SelectedPaper?.PaperName ?? "";
    public double PaperWidthMm => SelectedPaper?.PaperWidthMm ?? 0d;
    public double PaperHeightMm => SelectedPaper?.PaperHeightMm ?? 0d;

    public FieldBoxSelectDialog(Editor editor, Matrix3d inverseBlockTransform,
        Matrix3d blockTransform, TransientFrameMarkers markers, LocalRectangle referenceFrame,
        IReadOnlyList<PaperDetection> paperOptions,
        PaperSizeDetector.DetectionOptions paperDetectionOptions,
        FieldBoxSelectInitialState? initialState = null)
    {
        _editor = editor;
        _inverseBlockTransform = inverseBlockTransform;
        _blockTransform = blockTransform;
        _markers = markers;
        _paperDetectionOptions = paperDetectionOptions;
        ReferenceFrame = referenceFrame;



        var worldFrame = RectangleGeometry.TransformRectangle(referenceFrame, blockTransform);
        _printAreaCorners = (
            new Point3d(worldFrame.MinX, worldFrame.MinY, 0),
            new Point3d(worldFrame.MaxX, worldFrame.MaxY, 0));

            Text = "Настройка полей рамки и формата";
        UiLayout.ConfigureForm(this, 460, 488, 430, 462);

        ClientSize = new Size(UiLayout.Scale(460), UiLayout.Scale(466));
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;

        TopMost = true;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(8), UiLayout.Scale(12), UiLayout.Scale(8))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));


        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(52)));
        for (var i = 1; i < 10; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        }

        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(36)));

        table.Controls.Add(MakeLabel("Использовать шаблон рамки"), 0, 0);
        table.Controls.Add(MakeLibraryTemplateRow(), 1, 0);


        table.Controls.Add(MakeLabel("Область печати"), 0, 1);
        _printAreaStatus = MakeStatusLabel();
        UpdatePrintAreaStatus();
        table.Controls.Add(MakePrintAreaRow(_printAreaStatus, SelectPrintArea), 1, 1);


        table.Controls.Add(MakeLabel("Наименование *"), 0, 2);
        _titleStatus = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_titleStatus, SelectTitle, ClearTitle), 1, 2);

        table.Controls.Add(MakeLabel("№ листа *"), 0, 3);
        _numberStatus = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_numberStatus, SelectNumber, ClearNumber), 1, 3);


        table.Controls.Add(MakeLabel("Дата"), 0, 4);
        _dateStatus = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_dateStatus, SelectDate, ClearDate), 1, 4);


        table.Controls.Add(MakeLabel("Редакция"), 0, 5);
        _revisionStatus = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_revisionStatus, SelectRevision, ClearRevision), 1, 5);


        table.Controls.Add(MakeLabel("Стадия"), 0, 6);
        _phaseStatus = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_phaseStatus, SelectPhase, ClearPhase), 1, 6);


        table.Controls.Add(MakeLabel("Сведения 1"), 0, 7);
        _info1Status = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_info1Status, SelectInfo1, ClearInfo1), 1, 7);

        table.Controls.Add(MakeLabel("Сведения 2"), 0, 8);
        _info2Status = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_info2Status, SelectInfo2, ClearInfo2), 1, 8);

        if (initialState != null)
        {
            ApplyInitialState(initialState);
        }


        table.Controls.Add(MakeLabel("Формат"), 0, 9);
        table.Controls.Add(MakePaperRow(), 1, 9);
        ApplyPaperOptions(
            paperOptions,
            initialState?.PaperName,
            initialState?.PaperWidthMm ?? 0d,
            initialState?.PaperHeightMm ?? 0d);
        RefreshLibraryTemplateList();


        var hint = new Label
        {
            Text = "Можно использовать существующую рамку с совместимым форматом. Наименование и номер листа обязательны. Нажмите «Выбрать область» для ручного изменения; выбранные области показаны красной временной рамкой.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Font = new Font(Font.FontFamily, Math.Max(Font.Size - 1, 7))
        };
        table.SetColumnSpan(hint, 2);
        table.Controls.Add(hint, 0, 10);


        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, UiLayout.Scale(4), 0, 0)
        };
        var ok = UiLayout.CreateButton("ОК", 76);
        var skip = UiLayout.CreateButton("Пропустить", 76);
        var cancel = UiLayout.CreateButton("Отмена", 76);
        ok.Click += (_, _) =>
        {
            if (!ValidateRequiredFields())
            {
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };
        skip.Click += (_, _) =>
        {

            if (!ValidateRequiredFields())
            {
                return;
            }

            ClearOptionalField("Дата", r => DateRegion = r, _dateStatus);
            ClearOptionalField("Редакция", r => RevisionRegion = r, _revisionStatus);
            ClearOptionalField("Стадия", r => PhaseRegion = r, _phaseStatus);
            ClearOptionalField("Информация 1", r => Info1Region = r, _info1Status);
            ClearOptionalField("Информация 2", r => Info2Region = r, _info2Status);
            DialogResult = DialogResult.OK;
            Close();
        };
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(skip);
        buttons.Controls.Add(cancel);
        table.SetColumnSpan(buttons, 2);
        table.Controls.Add(buttons, 0, 11);

        Controls.Add(table);
    }

    private Control MakeLibraryTemplateRow()
    {
        _libraryTemplateCombo.Dock = DockStyle.Fill;
        _libraryTemplateCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _libraryTemplateCombo.DropDownWidth = UiLayout.Scale(360);
        _libraryTemplateCombo.SelectedIndexChanged += (_, _) => OnLibraryTemplateSelected();

        _libraryTemplateHint.Dock = DockStyle.Fill;
        _libraryTemplateHint.TextAlign = ContentAlignment.MiddleLeft;
        _libraryTemplateHint.ForeColor = Color.DimGray;
        _libraryTemplateHint.AutoEllipsis = true;
        _libraryTemplateHint.Font = new Font(Font.FontFamily, Math.Max(Font.Size - 1, 7));

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(14)));
        panel.Controls.Add(_libraryTemplateCombo, 0, 0);
        panel.Controls.Add(_libraryTemplateHint, 0, 1);

        return panel;
    }

    /// <summary>


    /// </summary>
    private void RefreshLibraryTemplateList()
    {
        if (_applyingLibraryTemplate)
        {
            return;
        }

        var previousBlockName = (_libraryTemplateCombo.SelectedItem as LibraryTemplateItem)?.Definition?.BlockName;
        _compatibleLibraryEntries = LoadCompatibleLibraryEntries();

        _applyingLibraryTemplate = true;
        try
        {
            _libraryTemplateCombo.BeginUpdate();
            try
            {
                _libraryTemplateCombo.Items.Clear();
        _libraryTemplateCombo.Items.Add(new LibraryTemplateItem(null, "(не использовать)"));
                foreach (var entry in _compatibleLibraryEntries)
                {
                    _libraryTemplateCombo.Items.Add(new LibraryTemplateItem(entry, FormatLibraryEntryDisplay(entry)));
                }

                var restoreIndex = 0;
                if (!string.IsNullOrWhiteSpace(previousBlockName))
                {
                    for (var i = 1; i < _libraryTemplateCombo.Items.Count; i++)
                    {
                        if (_libraryTemplateCombo.Items[i] is LibraryTemplateItem item
                            && item.Definition != null
                            && string.Equals(item.Definition.BlockName, previousBlockName, StringComparison.OrdinalIgnoreCase))
                        {
                            restoreIndex = i;
                            break;
                        }
                    }
                }

                _libraryTemplateCombo.SelectedIndex = _libraryTemplateCombo.Items.Count > 0 ? restoreIndex : -1;
            }
            finally
            {
                _libraryTemplateCombo.EndUpdate();
            }

            var hasCompatible = _compatibleLibraryEntries.Count > 0;
            _libraryTemplateCombo.Enabled = hasCompatible;
            _libraryTemplateHint.Text = hasCompatible
            ? $"Доступно совместимых шаблонов рамок: {_compatibleLibraryEntries.Count}. При выборе поля и формат будут применены сразу."
            : "Нет шаблонов рамок, совместимых с текущим форматом. Выберите поля вручную.";
            _libraryTemplateHint.ForeColor = hasCompatible ? Color.DimGray : Color.DarkOrange;
        }
        finally
        {
            _applyingLibraryTemplate = false;
        }
    }

    private List<TitleBlockDefinition> LoadCompatibleLibraryEntries()
    {
        if (!TryGetCurrentPaperSizeMm(out var widthMm, out var heightMm))
        {
            return new List<TitleBlockDefinition>();
        }

        var toleranceMm = _paperDetectionOptions.LongPaperShortSideToleranceMm;
        try
        {
            var library = TitleBlockLibraryStore.Load();
            return library.Blocks
                .Where(b => b != null
                    && !string.IsNullOrWhiteSpace(b.BlockName)
                    && PaperSizeDetector.ArePhysicalSizesCompatible(
                        b.PaperWidthMm,
                        b.PaperHeightMm,
                        widthMm,
                        heightMm,
                        toleranceMm))
                .OrderBy(b => b.BlockName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.PaperName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<TitleBlockDefinition>();
        }
    }

    private bool TryGetCurrentPaperSizeMm(out double widthMm, out double heightMm)
    {
        widthMm = 0d;
        heightMm = 0d;
        var paper = SelectedPaper ?? (_paperOptions.Count > 0 ? _paperOptions[0] : null);
        if (paper == null || paper.PaperWidthMm <= 0d || paper.PaperHeightMm <= 0d)
        {
            return false;
        }

        widthMm = paper.PaperWidthMm;
        heightMm = paper.PaperHeightMm;
        return true;
    }

    private static string FormatLibraryEntryDisplay(TitleBlockDefinition entry)
    {
        var sizeText = entry.PaperWidthMm > 0d && entry.PaperHeightMm > 0d
            ? $"{entry.PaperWidthMm:0.##}×{entry.PaperHeightMm:0.##} mm"
            : "";
        if (string.IsNullOrWhiteSpace(entry.PaperName))
        {
            return string.IsNullOrWhiteSpace(sizeText)
                ? entry.BlockName
                : $"{entry.BlockName} · {sizeText}";
        }

        return string.IsNullOrWhiteSpace(sizeText)
            ? $"{entry.BlockName} · {entry.PaperName}"
            : $"{entry.BlockName} · {entry.PaperName} {sizeText}";
    }

    private void OnLibraryTemplateSelected()
    {
        if (_applyingLibraryTemplate)
        {
            return;
        }

        if (_libraryTemplateCombo.SelectedItem is not LibraryTemplateItem item || item.Definition == null)
        {
            return;
        }

        ApplyLibraryTemplate(item.Definition);
    }

    /// <summary>


    /// </summary>
    private void ApplyLibraryTemplate(TitleBlockDefinition template)
    {
        _applyingLibraryTemplate = true;
        try
        {
            var frame = ReferenceFrame;
            var mode = template.CoordinateMode;

            ApplyConvertedField("Наименование", template.TitleRegion, mode, frame, r => TitleRegion = r, _titleStatus);
            ApplyConvertedField("Номер листа", template.DrawingNumberRegion, mode, frame, r => DrawingNumberRegion = r, _numberStatus);
            ApplyConvertedField("Дата", template.DateRegion, mode, frame, r => DateRegion = r, _dateStatus);
            ApplyConvertedField("Редакция", template.RevisionRegion, mode, frame, r => RevisionRegion = r, _revisionStatus);
            ApplyConvertedField("Стадия", template.PhaseRegion, mode, frame, r => PhaseRegion = r, _phaseStatus);
            ApplyConvertedField("Информация 1", template.Info1Region, mode, frame, r => Info1Region = r, _info1Status);
            ApplyConvertedField("Информация 2", template.Info2Region, mode, frame, r => Info2Region = r, _info2Status);

            EnsureAndSelectPaperFromTemplate(template);
            RefreshAllMarkers();
        _libraryTemplateHint.Text = $"Применено: {FormatLibraryEntryDisplay(template)} (области и формат можно изменить вручную)";
            _libraryTemplateHint.ForeColor = Color.Green;
        }
        finally
        {
            _applyingLibraryTemplate = false;
        }
    }

    private void ApplyConvertedField(
        string fieldName,
        LocalRectangle storedRegion,
        string? coordinateMode,
        LocalRectangle referenceFrame,
        Action<LocalRectangle> setRegion,
        Label statusLabel)
    {
        LocalRectangle absolute;
        if (!storedRegion.HasArea())
        {
            absolute = new LocalRectangle();
        }
        else if (string.Equals(coordinateMode, "World", StringComparison.OrdinalIgnoreCase))
        {

            var p1 = new Point3d(storedRegion.MinX, storedRegion.MinY, 0).TransformBy(_inverseBlockTransform);
            var p2 = new Point3d(storedRegion.MaxX, storedRegion.MaxY, 0).TransformBy(_inverseBlockTransform);
            absolute = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
        }
        else
        {
            absolute = TitleBlockRegionConverter.FromStoredRelative(storedRegion, referenceFrame, coordinateMode);
        }

        setRegion(absolute);
        UpdateStatus(statusLabel, absolute);

        if (!absolute.HasArea())
        {
            _fieldCorners.Remove(fieldName);
            _markers.Remove(fieldName);
            return;
        }

        var worldRegion = RectangleGeometry.TransformRectangle(absolute, _blockTransform);
        _fieldCorners[fieldName] = (
            new Point3d(worldRegion.MinX, worldRegion.MinY, 0),
            new Point3d(worldRegion.MaxX, worldRegion.MaxY, 0));
    }

    private void EnsureAndSelectPaperFromTemplate(TitleBlockDefinition template)
    {
        if (string.IsNullOrWhiteSpace(template.PaperName)
            || template.PaperWidthMm <= 0d
            || template.PaperHeightMm <= 0d)
        {
            return;
        }

        var options = _paperOptions.ToList();
        var existingIndex = options.FindIndex(x =>
            string.Equals(x.PaperName, template.PaperName, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(x.PaperWidthMm - template.PaperWidthMm) <= 0.01d
            && Math.Abs(x.PaperHeightMm - template.PaperHeightMm) <= 0.01d);

        if (existingIndex < 0)
        {
            var width = Math.Abs(_printAreaCorners.Corner2.X - _printAreaCorners.Corner1.X);
            var height = Math.Abs(_printAreaCorners.Corner2.Y - _printAreaCorners.Corner1.Y);
            var scaleX = template.PaperWidthMm > 0 ? width / template.PaperWidthMm : 0d;
            var scaleY = template.PaperHeightMm > 0 ? height / template.PaperHeightMm : 0d;
            var scale = scaleX > 0 && scaleY > 0 ? (scaleX + scaleY) / 2d : Math.Max(scaleX, scaleY);
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            {
                scale = 1d;
            }

            options.Insert(0, new PaperDetection
            {
                PaperName = template.PaperName,
                PaperWidthMm = template.PaperWidthMm,
                PaperHeightMm = template.PaperHeightMm,
                ScaleValue = scale,
                ScaleText = scale >= 1d ? "1:" + scale.ToString("0.###") : (1d / scale).ToString("0.###") + ":1",
                IsLong = template.PaperName.IndexOf('+') > 0,
                RequiresCustomPaper = string.Equals(
                    template.PaperName,
                    PaperSizeDetector.CustomPaperName,
                    StringComparison.OrdinalIgnoreCase),
                Note = "Скопировано из существующей рамки"
            });
        }

        ApplyPaperOptions(options, template.PaperName, template.PaperWidthMm, template.PaperHeightMm);
    }

    private sealed class LibraryTemplateItem
    {
        public LibraryTemplateItem(TitleBlockDefinition? definition, string display)
        {
            Definition = definition;
            Display = display;
        }

        public TitleBlockDefinition? Definition { get; }
        public string Display { get; }
        public override string ToString() => Display;
    }

    private void ApplyInitialState(FieldBoxSelectInitialState state)
    {
        TitleRegion = state.TitleRegion;
        DrawingNumberRegion = state.DrawingNumberRegion;
        DateRegion = state.DateRegion;
        RevisionRegion = state.RevisionRegion;
        PhaseRegion = state.PhaseRegion;
        Info1Region = state.Info1Region;
        Info2Region = state.Info2Region;

        AddInitialField("Наименование", TitleRegion, _titleStatus);
        AddInitialField("Номер листа", DrawingNumberRegion, _numberStatus);
        AddInitialField("Дата", DateRegion, _dateStatus);
        AddInitialField("Редакция", RevisionRegion, _revisionStatus);
        AddInitialField("Стадия", PhaseRegion, _phaseStatus);
        AddInitialField("Информация 1", Info1Region, _info1Status);
        AddInitialField("Информация 2", Info2Region, _info2Status);
    }

    private void AddInitialField(string fieldName, LocalRectangle region, Label statusLabel)
    {
        UpdateStatus(statusLabel, region);
        if (!region.HasArea())
        {
            return;
        }


        var worldRegion = RectangleGeometry.TransformRectangle(region, _blockTransform);
        _fieldCorners[fieldName] = (
            new Point3d(worldRegion.MinX, worldRegion.MinY, 0),
            new Point3d(worldRegion.MaxX, worldRegion.MaxY, 0));
    }

    /// <summary>


    /// </summary>
    private void RefreshAllMarkers()
    {

        _markers.SetBox("Внешняя рамка", _printAreaCorners.Corner1, _printAreaCorners.Corner2, null, refresh: false);
        foreach (var kv in _fieldCorners)
        {
            _markers.SetBox(kv.Key, kv.Value.Corner1, kv.Value.Corner2, kv.Key, refresh: false);
        }

        _markers.RefreshDisplay();
    }

    /// <summary>


    /// </summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
    }

    /// <summary>



    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshAllMarkers();
        _markers.RefreshDisplay(regenerate: false);
    }

    /// <summary>

    /// </summary>
    private void SelectPrintArea()
    {
        double detectedWidth = 0;
        double detectedHeight = 0;
        var picked = false;
        BeginCadPick();
        try
        {
            var first = _editor.GetPoint(new PromptPointOptions("\nУкажите первый угол внешней границы печати: "));
            if (first.Status != PromptStatus.OK)
            {
                return;
            }

            var cornerOptions = new PromptCornerOptions("\nУкажите противоположный угол внешней границы печати: ", first.Value);
            var second = _editor.GetCorner(cornerOptions);
            if (second.Status != PromptStatus.OK)
            {
                return;
            }


            _printAreaCorners = (first.Value, second.Value);


            var p1 = first.Value.TransformBy(_inverseBlockTransform);
            var p2 = second.Value.TransformBy(_inverseBlockTransform);
            ReferenceFrame = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);


            _markers.SetBox("Внешняя рамка", first.Value, second.Value, null);
            UpdatePrintAreaStatus();

            detectedWidth = Math.Abs(second.Value.X - first.Value.X);
            detectedHeight = Math.Abs(second.Value.Y - first.Value.Y);
            picked = true;
        }
        finally
        {
            EndCadPick();
        }

        if (!picked)
        {
            return;
        }


        ApplyPaperOptions(ArbitraryPaperPicker.DetectCandidatesOrPrompt(
            detectedWidth,
            detectedHeight,
            _paperDetectionOptions,
            this));

        RefreshLibraryTemplateList();
        if (_libraryTemplateCombo.SelectedItem is LibraryTemplateItem stillSelected
            && stillSelected.Definition != null)
        {
            ApplyLibraryTemplate(stillSelected.Definition);
        }
    }

    /// <summary>

    /// </summary>
    private void UpdatePrintAreaStatus()
    {
        var w = Math.Abs(_printAreaCorners.Corner2.X - _printAreaCorners.Corner1.X);
        var h = Math.Abs(_printAreaCorners.Corner2.Y - _printAreaCorners.Corner1.Y);
        _printAreaStatus.Text = $"{(int)w} × {(int)h} (нажмите «Выбрать», чтобы изменить)";
        _printAreaStatus.ForeColor = Color.Green;
    }

    private bool ValidateRequiredFields()
    {
        if (!TitleRegion.HasArea() || !DrawingNumberRegion.HasArea())
        {
            MessageBox.Show(this,
            "Наименование и номер листа обязательны. Сначала выберите для них области.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(PaperName))
        {
            MessageBox.Show(this,
            "Название формата не может быть пустым.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private void SelectTitle() { if (TryBoxSelect("Наименование", out var r)) { TitleRegion = r; UpdateStatus(_titleStatus, TitleRegion); } }
    private void ClearTitle() { TitleRegion = new LocalRectangle(); _fieldCorners.Remove("Наименование"); _markers.Remove("Наименование"); UpdateStatus(_titleStatus, TitleRegion); }

    private void SelectNumber() { if (TryBoxSelect("Номер листа", out var r)) { DrawingNumberRegion = r; UpdateStatus(_numberStatus, DrawingNumberRegion); } }
    private void ClearNumber() { DrawingNumberRegion = new LocalRectangle(); _fieldCorners.Remove("Номер листа"); _markers.Remove("Номер листа"); UpdateStatus(_numberStatus, DrawingNumberRegion); }

    private void SelectDate() { if (TryBoxSelect("Дата", out var r)) { DateRegion = r; UpdateStatus(_dateStatus, DateRegion); } }
    private void ClearDate() { ClearOptionalField("Дата", r => DateRegion = r, _dateStatus); }

    private void SelectRevision() { if (TryBoxSelect("Редакция", out var r)) { RevisionRegion = r; UpdateStatus(_revisionStatus, RevisionRegion); } }
    private void ClearRevision() { ClearOptionalField("Редакция", r => RevisionRegion = r, _revisionStatus); }

    private void SelectPhase() { if (TryBoxSelect("Стадия", out var r)) { PhaseRegion = r; UpdateStatus(_phaseStatus, PhaseRegion); } }
    private void ClearPhase() { ClearOptionalField("Стадия", r => PhaseRegion = r, _phaseStatus); }

    private void SelectInfo1() { if (TryBoxSelect("Информация 1", out var r)) { Info1Region = r; UpdateStatus(_info1Status, Info1Region); } }
    private void ClearInfo1() { ClearOptionalField("Информация 1", r => Info1Region = r, _info1Status); }

    private void SelectInfo2() { if (TryBoxSelect("Информация 2", out var r)) { Info2Region = r; UpdateStatus(_info2Status, Info2Region); } }
    private void ClearInfo2() { ClearOptionalField("Информация 2", r => Info2Region = r, _info2Status); }

    private void ClearOptionalField(string fieldName, Action<LocalRectangle> setRegion, Label statusLabel)
    {
        setRegion(new LocalRectangle());
        _fieldCorners.Remove(fieldName);
        _markers.Remove(fieldName);
        UpdateStatus(statusLabel, new LocalRectangle());
    }

    private bool TryBoxSelect(string fieldName, out LocalRectangle region)
    {
        region = new LocalRectangle();
        BeginCadPick();
        try
        {
        var firstPrompt = $"Выберите первый угол области «{fieldName}» или нажмите правую кнопку, чтобы пропустить: ";
        var secondPrompt = $"Выберите противоположный угол области «{fieldName}»: ";

            var first = _editor.GetPoint(new PromptPointOptions(firstPrompt));
            if (first.Status != PromptStatus.OK)
            {
                return false;
            }

            var cornerOptions = new PromptCornerOptions(secondPrompt, first.Value);
            var second = _editor.GetCorner(cornerOptions);
            if (second.Status != PromptStatus.OK)
            {
                return false;
            }

            var p1 = first.Value.TransformBy(_inverseBlockTransform);
            var p2 = second.Value.TransformBy(_inverseBlockTransform);
            region = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);


            _fieldCorners[fieldName] = (first.Value, second.Value);



            _markers.SetBox(fieldName, first.Value, second.Value, fieldName);
            return true;
        }
        finally
        {
            EndCadPick();
        }
    }

    /// <summary>


    /// </summary>
    private void BeginCadPick()
    {
        Enabled = false;
        Opacity = 0;
        CadWindowFocus.ActivateCadWindow();
        System.Windows.Forms.Application.DoEvents();
    }


    private void EndCadPick()
    {
        Opacity = 1;
        Enabled = true;
        BringToFront();
        Activate();
    }

    private static void UpdateStatus(Label label, LocalRectangle region)
    {
        if (region.HasArea())
        {
        label.Text = $"Выбрано: ({region.MinX:0.###},{region.MinY:0.###})-({region.MaxX:0.###},{region.MaxY:0.###})";
            label.ForeColor = Color.Green;
        }
        else
        {
        label.Text = "(не выбрано)";
            label.ForeColor = Color.DimGray;
        }
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = true
    };

    private static Label MakeStatusLabel() => new()
    {
            Text = "(не выбрано)",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DimGray,
        AutoEllipsis = true
    };

    private static Control MakeFieldRow(Label statusLabel, Action select, Action clear)
    {

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("Выбрать", 60) + UiLayout.Scale(8)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("Очистить", 60) + UiLayout.Scale(2)));

        var selectBtn = UiLayout.CreateButton("Выбрать", 70);
        selectBtn.Dock = DockStyle.Fill;
        selectBtn.Click += (_, _) => select();

        var clearBtn = UiLayout.CreateButton("Очистить", 70);
        clearBtn.Dock = DockStyle.Fill;
        clearBtn.Click += (_, _) => clear();

        panel.Controls.Add(statusLabel, 0, 0);
        panel.Controls.Add(selectBtn, 1, 0);
        panel.Controls.Add(clearBtn, 2, 0);

        return panel;
    }

    /// <summary>

    /// </summary>
    private static Control MakePrintAreaRow(Label statusLabel, Action select)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("Выбрать", 60) + UiLayout.Scale(8)));

        var selectBtn = UiLayout.CreateButton("Выбрать", 70);
        selectBtn.Dock = DockStyle.Fill;
        selectBtn.Click += (_, _) => select();

        panel.Controls.Add(statusLabel, 0, 0);
        panel.Controls.Add(selectBtn, 1, 0);

        return panel;
    }


    private Control MakePaperRow()
    {
        _paperName.Dock = DockStyle.Fill;
        _paperName.DropDownStyle = ComboBoxStyle.DropDownList;
        _paperName.DropDownWidth = UiLayout.Scale(330);
        _paperName.SelectedIndexChanged += (_, _) =>
        {
            if (!_applyingLibraryTemplate)
            {
                RefreshLibraryTemplateList();
            }
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(110)));

        panel.Controls.Add(_paperName, 0, 0);
        var arbitraryButton = UiLayout.CreateButton("Произвольный…", 110);
        arbitraryButton.Dock = DockStyle.Right;
        arbitraryButton.Click += (_, _) => PickArbitraryPaper();
        panel.Controls.Add(arbitraryButton, 1, 0);

        return panel;
    }

    /// <summary>



    /// </summary>
    private void PickArbitraryPaper()
    {
        var width = Math.Abs(_printAreaCorners.Corner2.X - _printAreaCorners.Corner1.X);
        var height = Math.Abs(_printAreaCorners.Corner2.Y - _printAreaCorners.Corner1.Y);
        if (width <= 1e-6 || height <= 1e-6)
        {
            return;
        }

        var guessedScale = PaperSizeDetector.GuessScale(width, height);
        using var scaleForm = new CustomScaleForm(
            width,
            height,
            guessedScale,
            ArbitraryPaperPicker.HintText,
            allowAspectRatioPapers: !_paperDetectionOptions.IncludeGenericDynamicTitleBlockPaper);
        if (scaleForm.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var arbitrary = ArbitraryPaperPicker.CreatePaperFromScaleForm(scaleForm, width, height);


        var options = _paperOptions.ToList();
        var existingIndex = options.FindIndex(x =>
            string.Equals(x.PaperName, arbitrary.PaperName, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(x.PaperWidthMm - arbitrary.PaperWidthMm) <= 0.01d
            && Math.Abs(x.PaperHeightMm - arbitrary.PaperHeightMm) <= 0.01d);
        if (existingIndex >= 0)
        {
            options[existingIndex] = arbitrary;
        }
        else
        {
            options.Add(arbitrary);
        }

        ApplyPaperOptions(options, arbitrary.PaperName, arbitrary.PaperWidthMm, arbitrary.PaperHeightMm);
        RefreshLibraryTemplateList();
    }

    private void ApplyPaperOptions(
        IReadOnlyList<PaperDetection> paperOptions,
        string? preferredPaperName = null,
        double preferredPaperWidthMm = 0d,
        double preferredPaperHeightMm = 0d)
    {
        if (paperOptions.Count == 0)
        {
        throw new ArgumentException("Требуется хотя бы один вариант формата.", nameof(paperOptions));
        }

        _paperOptions = paperOptions;
        var suppressLibraryRefresh = _applyingLibraryTemplate;
        _applyingLibraryTemplate = true;
        _paperName.BeginUpdate();
        try
        {
            _paperName.Items.Clear();
            foreach (var paper in _paperOptions)
            {
                _paperName.Items.Add(PaperSizeDetector.FormatOption(paper));
            }

            var preferredIndex = -1;
            if (!string.IsNullOrWhiteSpace(preferredPaperName))
            {
                for (var i = 0; i < _paperOptions.Count; i++)
                {
                    var paper = _paperOptions[i];
                    if (string.Equals(paper.PaperName, preferredPaperName, StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(paper.PaperWidthMm - preferredPaperWidthMm) <= 0.01d
                        && Math.Abs(paper.PaperHeightMm - preferredPaperHeightMm) <= 0.01d)
                    {
                        preferredIndex = i;
                        break;
                    }
                }
            }

            _paperName.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
        }
        finally
        {
            _paperName.EndUpdate();
            _applyingLibraryTemplate = suppressLibraryRefresh;
        }
    }
}
