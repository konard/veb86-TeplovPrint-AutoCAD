using System;
using System.Collections.Generic;
using System.Windows.Forms;
#if AUTOCAD
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>





/// </summary>
public static class ArbitraryPaperPicker
{
    /// <summary>


    /// </summary>
    public const string HintText =
        "Рамка не соответствует форматам A4–A0 или их удлинённым вариантам. Введите масштаб, чтобы привести лист к обычному размеру и избежать печати чрезмерно большого чертежа.";

    /// <summary>


    /// </summary>
    public static IReadOnlyList<PaperDetection> DetectCandidatesOrPrompt(
        double width,
        double height,
        PaperSizeDetector.DetectionOptions options,
        Form? owner = null)
    {
        var candidates = PaperSizeDetector.DetectCandidates(width, height, options);
        if (candidates.Count > 0)
        {
            return candidates;
        }

        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var guessedScale = PaperSizeDetector.GuessScale(actualWidth, actualHeight);

        var allowAspectRatioPapers = !options.IncludeGenericDynamicTitleBlockPaper;
        using var scaleForm = new CustomScaleForm(actualWidth, actualHeight, guessedScale, HintText, allowAspectRatioPapers);
        var dialogResult = owner != null
            ? scaleForm.ShowDialog(owner)
            : ShowScaleDialog(scaleForm);
        if (dialogResult == DialogResult.OK
            && (scaleForm.SelectedStandardPaper != null || scaleForm.SelectedScale >= 1))
        {
            var chosen = CreatePaperFromScaleForm(scaleForm, actualWidth, actualHeight);
            if (string.Equals(chosen.PaperName, PaperSizeDetector.CustomPaperName, StringComparison.OrdinalIgnoreCase)
                || !allowAspectRatioPapers)
            {
                return new[] { chosen };
            }


            var ordered = new List<PaperDetection> { chosen };
            foreach (var paper in PaperSizeDetector.DetectByAspectRatio(actualWidth, actualHeight))
            {
                if (!string.Equals(paper.PaperName, chosen.PaperName, StringComparison.OrdinalIgnoreCase))
                {
                    ordered.Add(paper);
                }
            }

            return ordered;
        }


        return PaperSizeDetector.DetectCandidatesOrFallback(width, height, options);
    }

    /// <summary>



    /// </summary>
    public static PaperDetection CreatePaperFromScaleForm(CustomScaleForm form, double width, double height)
    {
        if (form.SelectedStandardPaper != null)
        {
            return form.SelectedStandardPaper;
        }

        var scale = form.SelectedScale;
        var paperWidthMm = width / scale;
        var paperHeightMm = height / scale;
        return new PaperDetection
        {
            PaperName = PaperSizeDetector.CustomPaperName,
            PaperWidthMm = paperWidthMm,
            PaperHeightMm = paperHeightMm,
            ScaleValue = scale,
            ScaleText = PaperSizeDetector.ToScaleText(scale),
            RequiresCustomPaper = true,
            Note = $"Пользовательская бумага: рассчитана по масштабу {PaperSizeDetector.ToScaleText(scale)}; размер {paperWidthMm:0.##} x {paperHeightMm:0.##} мм"
        };
    }

    private static DialogResult ShowScaleDialog(Form form)
    {
#if ACAD_CORE
        var result = form.ShowDialog();
#else
        var result = CadApp.ShowModalDialog(form);
#endif
        CadWindowFocus.ActivateCadWindow();
        return result;
    }
}
