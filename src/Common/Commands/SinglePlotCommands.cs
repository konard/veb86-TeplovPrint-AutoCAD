using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
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

public sealed partial class BatchPlotCommands
{
    private static void SinglePlotCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var editor = doc.Editor;
        string? customPmpPath = null;
        PmpCustomPaper.Registration? customPaperRegistration = null;
        var isArbitraryPaper = false;
        var forceCustomPaperReload = false;
        try
        {
            var first = editor.GetPoint(new PromptPointOptions("\nУкажите первый угол внешней рамки листа: "));
            if (first.Status != PromptStatus.OK)
            {
                return;
            }

            var second = editor.GetCorner(new PromptCornerOptions("\nУкажите противоположный угол внешней рамки листа: ", first.Value));
            if (second.Status != PromptStatus.OK)
            {
                return;
            }



            //


            //


            var ucsP1 = first.Value;
            var ucsP2 = second.Value;


            var ucsToDcs = BuildUcsToDcsMatrix(editor);


            var corners = new[]
            {
                new Point3d(ucsP1.X, ucsP1.Y, 0).TransformBy(ucsToDcs),
                new Point3d(ucsP2.X, ucsP1.Y, 0).TransformBy(ucsToDcs),
                new Point3d(ucsP1.X, ucsP2.Y, 0).TransformBy(ucsToDcs),
                new Point3d(ucsP2.X, ucsP2.Y, 0).TransformBy(ucsToDcs)
            };


            var minX = corners.Min(p => p.X);
            var minY = corners.Min(p => p.Y);
            var maxX = corners.Max(p => p.X);
            var maxY = corners.Max(p => p.Y);
            var width = maxX - minX;
            var height = maxY - minY;
            if (width <= 1e-6 || height <= 1e-6)
            {
                MessageBox.Show("Ширина или высота выбранной рамки недопустима.", "Печать одного листа", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }


            var plottersDir = AcadPlotterInstaller.GetPlottersDirectory();
            var installedPlotter = Path.Combine(plottersDir, AcadPlotterInstaller.PreferredPdfPlotter);
            var installedPmp = Path.Combine(plottersDir, "PMP Files", "LA_pdf.pmp");
            // nanoCAD plots through its own PDF plotter, LA_pdf is not installed there (issue #5).
            if (AcadPlotterInstaller.UsesBundledLaPlotters
                && (!File.Exists(installedPlotter) || !File.Exists(installedPmp)))
            {
                var installResult = AcadPlotterInstaller.InstallBundledPlotter();
                if (!installResult.Installed)
                    throw new InvalidOperationException("Конфигурация плоттера LA_pdf неполна: " + installResult.Message);


                plottersDir = AcadPlotterInstaller.GetPlottersDirectory();
                installedPlotter = Path.Combine(plottersDir, AcadPlotterInstaller.PreferredPdfPlotter);
                installedPmp = Path.Combine(plottersDir, "PMP Files", "LA_pdf.pmp");
                if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
                    throw new InvalidOperationException("После установки LA_pdf файлы PC3/PC5 или PMP не найдены.");
            }

            var candidates = PaperSizeDetector.DetectCandidates(width, height);
            if (candidates.Count == 0)
            {
                isArbitraryPaper = true;

                var guessedScale = PaperSizeDetector.GuessScale(width, height);
                using var scaleForm = new CustomScaleForm(width, height, guessedScale);
                if (ShowModalDialog(scaleForm) != System.Windows.Forms.DialogResult.OK)
                {
                    return;
                }


                if (scaleForm.SelectedStandardPaper != null)
                {
                    isArbitraryPaper = false;
                    candidates = new List<PaperDetection> { scaleForm.SelectedStandardPaper };
                }
                else
                {
                    var scale = scaleForm.SelectedScale;
                    var paperW = scaleForm.PaperWidthMm;
                    var paperH = scaleForm.PaperHeightMm;

#if NANOCAD
                    // nanoCAD has no LA_pdf.pmp to register the paper in (issue #5):
                    // the closest format of the nanoCAD PDF plotter is used and the frame is scaled to fit.
                    isArbitraryPaper = false;
                    var customPaperName = PaperSizeDetector.CustomPaperName;
                    editor.WriteMessage($"\nПользовательская бумага {paperW:0.######} x {paperH:0.######} мм не регистрируется в nanoCAD; будет выбран ближайший формат PDF-плоттера nanoCAD.");
#else
                    try
                    {
                        customPmpPath = Path.Combine(plottersDir, "PMP Files", "LA_pdf.pmp");
                        if (!File.Exists(customPmpPath))
                            throw new FileNotFoundException("Файл LA_pdf.pmp отсутствует; невозможно зарегистрировать пользовательскую бумагу.", customPmpPath);

                        customPaperRegistration = PmpCustomPaper.RegisterCustomPaper(customPmpPath, paperW, paperH)
                            ?? throw new InvalidOperationException("Не удалось зарегистрировать пользовательскую бумагу в LA_pdf.pmp.");

                        forceCustomPaperReload = customPaperRegistration.WasAdded;
#if AUTOCAD
                        var attachment = AcadPlotterInstaller.EnsureActivePdfPmpAttachment(
                            installedPlotter,
                            customPmpPath,
                            forceRewrite: customPaperRegistration.WasAdded);
                        if (!attachment.Success)
                        {
                            throw new InvalidOperationException("Не удалось связать LA_pdf.pc3 с текущим PMP: " + attachment.Message);
                        }
                        forceCustomPaperReload |= attachment.Changed;
                        editor.WriteMessage("\nОбновление связи плоттера AutoCAD: " + attachment.Message);
#endif
                        editor.WriteMessage($"\nРегистрация пользовательской бумаги: pmp={customPmpPath}, имя={customPaperRegistration.PaperName}, добавлено={customPaperRegistration.WasAdded}, перезагрузка={forceCustomPaperReload}, ширина={paperW:0.######}, высота={paperH:0.######}");
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Не удалось зарегистрировать пользовательскую бумагу. Печать остановлена, чтобы не использовать неверный формат.", ex);
                    }

                    var customPaperName = customPaperRegistration.PaperName;
#endif
                    candidates = new List<PaperDetection>
                    {
                        new()
                        {
                            PaperName = customPaperName,
                            PaperWidthMm = paperW,
                            PaperHeightMm = paperH,
                            ScaleValue = scale,
                            ScaleText = PaperSizeDetector.ToScaleText(scale),
                            Note = $"Пользовательская бумага {paperW:0.##} x {paperH:0.##} мм"
                        }
                    };
                }
            }

            var sourceFile = string.IsNullOrWhiteSpace(doc.Database.Filename)
                ? doc.Name
                : doc.Database.Filename;

            var settings = AppSettingsStore.Load();
            var styles = PlotStyleManager.GetAvailableCtbStyles();
            var (deviceName, defaultStyleSheet) = ResolveSinglePlotOptions(settings, styles);
            var form = new SinglePlotForm(
                sourceFile,
                width,
                height,
                candidates,
                styles,
                defaultStyleSheet);
            if (CadDialog.ShowModal(form) != true)
            {
                return;
            }

            var paper = form.SelectedPaper;
            var outputPath = form.OutputPath;
            var styleSheet = form.SelectedStyle;
            var layoutName = LayoutManager.Current.CurrentLayout;
            var isPaperSpace = !doc.Database.TileMode;
            var baseName = Path.GetFileNameWithoutExtension(sourceFile);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "Drawing";
            }

            var job = new PlotJob
            {
                IsManualWindow = true,
                IsDcsWindow = true,
                SourceFile = sourceFile,
                SpaceName = layoutName,
                IsPaperSpace = isPaperSpace,
                DrawingNumber = baseName,
                Title = baseName,
                PaperName = paper.PaperName,
                ScaleText = paper.ScaleText,
                SizeText = $"{width:0.##} x {height:0.##}",
                PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                DetectionNote = "Печать одного листа: рамка указана пользователем",
                PaperWidthMm = paper.PaperWidthMm,
                PaperHeightMm = paper.PaperHeightMm,
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY,
                OutputPath = outputPath,
                LeavePaperMargin = form.LeavePaperMargin,
                PaperMarginMm = form.PaperMarginMm,
                RequireExactPaperSize = isArbitraryPaper,
                UseExactWindowScale = isArbitraryPaper,

                CustomPaperWasAdded = forceCustomPaperReload
            };

            if (form.IsPreview)
            {
                SaveLastPlotOptions(settings, deviceName, styleSheet);
                PlotterService.Preview(job, deviceName, styleSheet, doc);
                editor.WriteMessage("\nОткрыт предварительный просмотр одного листа.");
            }
            else
            {
                SaveLastPlotOptions(settings, deviceName, styleSheet);
                PlotterService.Plot(job, deviceName, styleSheet, doc, settings);
                editor.WriteMessage($"\nПечать одного листа завершена: {outputPath}");
                RevealFileInExplorer(outputPath);
                MessageBox.Show(
                    $"Печать одного листа завершена.\nБумага: {paper.PaperName} {paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} мм\nФайл: {outputPath}",
                    "Печать одного листа",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\nОшибка печати одного листа: " + ex.Message);
            MessageBox.Show("Ошибка печати одного листа: " + ex.Message, "Печать одного листа", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (customPmpPath != null && customPaperRegistration?.WasAdded == true)
            {
                PmpCustomPaper.RemoveCustomPaper(customPmpPath, customPaperRegistration.PaperName);
            }
        }
    }

    /// <summary>


    /// </summary>
    private static (string DeviceName, string StyleSheet) ResolveSinglePlotOptions(
        AppSettings settings,
        IReadOnlyList<string> styles)
    {
        using var plotSettings = new PlotSettings(true);
        var validator = PlotSettingsValidator.Current;
        var devices = validator.GetPlotDeviceList()
            .Cast<object>()
            .Select(value => value?.ToString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
#if NANOCAD
        // LA_pdf.pc3 is an AutoCAD plotter: nanoCAD prints through the printer chosen in the main window (issue #13)
        // or its internal PDF plotter (issue #5).
        var device = NullIfEmpty(AcadPlotterInstaller.ResolvePdfPlotDevice(devices, settings.PdfPrinter))
            ?? throw new InvalidOperationException("Не найден доступный PDF-плоттер." + AcadPlotterInstaller.PdfDeviceNotFoundHint);
#else
        var device = FindPlotOption(devices, AcadPlotterInstaller.PreferredPdfPlotter)
            ?? FindPlotOption(devices, settings.LastPlotDevice)
            ?? devices.FirstOrDefault(value => value.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? throw new InvalidOperationException("Не найден доступный PDF-плоттер.");
#endif

        var style = PlotStyleManager.ResolvePreferredStyle(styles, settings.LastStyleSheet);
        return (device, style);
    }

    private static void SaveLastPlotOptions(AppSettings settings, string deviceName, string styleSheet)
    {

        var style = PlotStyleManager.NormalizeStyleName(styleSheet);
        settings.LastPlotDevice = deviceName;
        if (!string.IsNullOrEmpty(style))
        {
            settings.LastStyleSheet = style;
        }
        AppSettingsStore.Save(settings);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? FindPlotOption(System.Collections.Generic.IEnumerable<string> values, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return null;
        }

        return values.FirstOrDefault(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            ?? values.FirstOrDefault(value => value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
