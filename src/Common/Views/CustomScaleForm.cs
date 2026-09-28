using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

/// <summary>






/// </summary>
public sealed class CustomScaleForm : Form
{

    private const double PaperShortSideMinMm = 100d;
    private const double PaperShortSideMaxMm = 900d;


    public const string AspectRatioHintText =
        "Пропорции рамки близки к стандартному или удлинённому формату, но масштаб отсутствует в списке. Выберите формат для автоматического расчёта или задайте масштаб вручную.";

    private readonly TextBox _scaleInput = new();
    private readonly Label _scalePrefixLabel;
    private readonly Label _paperSizeLabel;
    private readonly ComboBox? _paperCombo;
    private readonly IReadOnlyList<PaperDetection> _aspectPapers;
    private readonly double _drawingWidth;
    private readonly double _drawingHeight;
    private string _customScaleText;


    public PaperDetection? SelectedStandardPaper { get; private set; }

    public double SelectedScale { get; private set; }

    public double PaperWidthMm => SelectedStandardPaper?.PaperWidthMm ?? _drawingWidth / SelectedScale;

    public double PaperHeightMm => SelectedStandardPaper?.PaperHeightMm ?? _drawingHeight / SelectedScale;

    /// <param name="allowAspectRatioPapers">


    /// </param>
    public CustomScaleForm(
        double drawingWidth,
        double drawingHeight,
        int guessedScale,
        string? hintText = null,
        bool allowAspectRatioPapers = true)
    {
        _drawingWidth = drawingWidth;
        _drawingHeight = drawingHeight;
        _customScaleText = guessedScale > 0 ? guessedScale.ToString() : "100";
        _aspectPapers = allowAspectRatioPapers
            ? PaperSizeDetector.DetectByAspectRatio(drawingWidth, drawingHeight)
            : new PaperDetection[0];
        var hasAspectPapers = _aspectPapers.Count > 0;

        Text = "Пользовательский масштаб печати";
        UiLayout.ConfigureForm(this, 420, hasAspectPapers ? 220 : 180, 360, hasAspectPapers ? 200 : 170);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        ClientSize = new Size(UiLayout.Scale(420), UiLayout.Scale(hasAspectPapers ? 230 : 190));

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = hasAspectPapers ? 7 : 6,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(8), UiLayout.Scale(12), UiLayout.Scale(8))
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(22)));
        if (hasAspectPapers)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        }

        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(26)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(14)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(32)));

        var info = new Label
        {
            Text = hasAspectPapers
                ? $"Размер чертежа: {drawingWidth:0.##} x {drawingHeight:0.##} мм (пропорции близки к стандартному или удлинённому формату; масштаб отсутствует в списке)"
                : $"Размер чертежа: {drawingWidth:0.##} x {drawingHeight:0.##} мм (не соответствует стандартным форматам A0–A4)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };



        var scaleLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty };
        _scalePrefixLabel = new Label { Text = "Масштаб печати  1 : ", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
        _scaleInput.Width = UiLayout.Scale(160);
        _scaleInput.Text = _customScaleText;
        _scaleInput.TextChanged += (_, _) => UpdatePaperSize();
        scaleLayout.Controls.Add(_scalePrefixLabel);
        scaleLayout.Controls.Add(_scaleInput);

        _paperSizeLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(0, 120, 212)
        };

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = hasAspectPapers
                ? AspectRatioHintText
                : (string.IsNullOrWhiteSpace(hintText) ? "" : hintText)
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, UiLayout.Scale(6), 0, 0)
        };
        var ok = UiLayout.CreateButton("ОК", 76);
        var cancel = UiLayout.CreateButton("Отмена", 76);
        ok.Click += (_, _) => Confirm();
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        var row = 0;
        panel.Controls.Add(info, 0, row++);
        if (hasAspectPapers)
        {
            _paperCombo = CreatePaperCombo();
            panel.Controls.Add(CreatePaperChoiceRow(_paperCombo), 0, row++);
        }

        panel.Controls.Add(scaleLayout, 0, row++);
        panel.Controls.Add(_paperSizeLabel, 0, row++);
        panel.Controls.Add(hint, 0, row++);
        panel.Controls.Add(new Label(), 0, row++);
        panel.Controls.Add(buttons, 0, row);
        Controls.Add(panel);

        AcceptButton = ok;
        CancelButton = cancel;

        if (_paperCombo != null)
        {
            ApplyPaperChoice();
        }
        else
        {
            UpdatePaperSize();
        }
    }


    private ComboBox CreatePaperCombo()
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = UiLayout.Scale(300),
            Anchor = AnchorStyles.Left
        };
        foreach (var paper in _aspectPapers)
        {
            combo.Items.Add(new PaperChoiceItem(PaperSizeDetector.FormatOption(paper), paper));
        }

        combo.Items.Add(new PaperChoiceItem("Пользовательский размер | ручной ввод масштаба", null));
        var preferred = PaperSizeDetector.IndexOfPreferredAspectRatioPaper(
            _drawingWidth,
            _drawingHeight,
            _aspectPapers);
        combo.SelectedIndex = preferred >= 0 ? preferred : combo.Items.Count - 1;
        combo.SelectedIndexChanged += (_, _) => ApplyPaperChoice();
        return combo;
    }

    private static Control CreatePaperChoiceRow(ComboBox combo)
    {
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty };
        layout.Controls.Add(new Label
        {
            Text = "Формат бумаги  ",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft
        });
        layout.Controls.Add(combo);
        return layout;
    }

    /// <summary>

    /// </summary>
    private void ApplyPaperChoice()
    {
        var standard = GetSelectedChoicePaper();
        if (standard == null)
        {
            _scalePrefixLabel.Text = "Масштаб печати  1 : ";
            _scaleInput.ReadOnly = false;
            _scaleInput.Text = _customScaleText;
            UpdatePaperSize();
            return;
        }

        if (!_scaleInput.ReadOnly)
        {
            _customScaleText = _scaleInput.Text;
        }

        _scalePrefixLabel.Text = "Масштаб печати  ";
        _scaleInput.ReadOnly = true;
        _scaleInput.Text = standard.ScaleText;
        ShowStandardPaperSize(standard);
    }

    private void UpdatePaperSize()
    {
        var standard = GetSelectedChoicePaper();
        if (standard != null)
        {
            ShowStandardPaperSize(standard);
            return;
        }


        if (!PaperSizeDetector.TryParseScale(_scaleInput.Text, out var scale) || scale < 1)
        {
            _paperSizeLabel.Text = "Введите знаменатель масштаба ≥ 1, например 100";
            return;
        }

        var pw = _drawingWidth / scale;
        var ph = _drawingHeight / scale;
        var shortSide = Math.Min(pw, ph);
        var rangeText = shortSide >= PaperShortSideMinMm && shortSide <= PaperShortSideMaxMm
            ? ""
            : " (вне обычного диапазона A4–A0; страница может оказаться слишком большой или маленькой)";
        _paperSizeLabel.Text = $"Бумага: {pw:0.##} x {ph:0.##} мм (пользовательский размер){rangeText}";
        _paperSizeLabel.ForeColor = rangeText.Length == 0 ? Color.FromArgb(0, 120, 212) : Color.OrangeRed;
    }

    private void ShowStandardPaperSize(PaperDetection paper)
    {
        _paperSizeLabel.Text = $"Бумага: {paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} мм ({paper.PaperName})";
        _paperSizeLabel.ForeColor = Color.FromArgb(0, 120, 212);
    }

    private PaperDetection? GetSelectedChoicePaper()
    {
        return _paperCombo?.SelectedItem is PaperChoiceItem item ? item.Paper : null;
    }

    private void Confirm()
    {
        var standard = GetSelectedChoicePaper();
        if (standard != null)
        {
            SelectedStandardPaper = standard;
            SelectedScale = standard.ScaleValue;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        if (!PaperSizeDetector.TryParseScale(_scaleInput.Text, out var scale) || scale < 1)
        {
            MessageBox.Show(
                "Не удалось распознать масштаб. Введите число (100 означает 1:100) или значение вида 1:143; масштаб должен быть не меньше 1.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }


        var paperWidthMm = _drawingWidth / scale;
        var paperHeightMm = _drawingHeight / scale;
        var shortSideMm = Math.Min(paperWidthMm, paperHeightMm);
        if (shortSideMm < PaperShortSideMinMm || shortSideMm > PaperShortSideMaxMm)
        {
            var confirm = MessageBox.Show(
                $"При масштабе {PaperSizeDetector.ToScaleText(scale)} размер бумаги составит {paperWidthMm:0.##} x {paperHeightMm:0.##} мм, "
                + "что выходит за обычный диапазон A4–A0 (короткая сторона 100–900 мм). Всё равно использовать этот масштаб?",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
            {
                _scaleInput.Focus();
                _scaleInput.SelectAll();
                return;
            }
        }

        SelectedStandardPaper = null;
        SelectedScale = scale;
        DialogResult = DialogResult.OK;
        Close();
    }


    private sealed class PaperChoiceItem
    {
        public PaperChoiceItem(string display, PaperDetection? paper)
        {
            Display = display;
            Paper = paper;
        }

        public string Display { get; }

        public PaperDetection? Paper { get; }

        public override string ToString()
        {
            return Display;
        }
    }
}
