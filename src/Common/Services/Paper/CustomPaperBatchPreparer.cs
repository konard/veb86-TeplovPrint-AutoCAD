using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
public static class CustomPaperBatchPreparer
{
    public sealed class PreparationResult
    {
        public IReadOnlyList<PmpCustomPaper.Registration> Registrations { get; set; }
            = Array.Empty<PmpCustomPaper.Registration>();
        public string AttachmentMessage { get; set; } = "";
    }

    public static PreparationResult Prepare(IReadOnlyList<PlotJob> jobs, string deviceName)
    {
        var isPdfDevice = string.Equals(
            deviceName,
            AcadPlotterInstaller.PreferredPdfPlotter,
            StringComparison.OrdinalIgnoreCase);
        var isDwfDevice = string.Equals(
            deviceName,
            AcadPlotterInstaller.PreferredDwfPlotter,
            StringComparison.OrdinalIgnoreCase);



        foreach (var job in jobs)
        {
            var expandsPaper = job.LeavePaperMargin
                               && job.PaperMarginMm > 0
                               && job.PaperWidthMm > 0
                               && job.PaperHeightMm > 0;
            if (expandsPaper)
            {
                job.EffectivePaperWidthMm = job.PaperWidthMm + job.PaperMarginMm * 2;
                job.EffectivePaperHeightMm = job.PaperHeightMm + job.PaperMarginMm * 2;
            }
            else
            {
                job.EffectivePaperWidthMm = 0;
                job.EffectivePaperHeightMm = 0;
            }





            job.RequiresCustomPaperRegistration =
                job.DetectedRequiresCustomPaperRegistration
                || IsLongPaper(job)
                || expandsPaper;
            job.RequireExactPaperSize = false;
            job.UseExactWindowScale = false;
            job.CustomPaperWasAdded = false;
        }

        var customJobs = jobs
            .Where(job => job.RequiresCustomPaperRegistration)
            .ToList();
        if (customJobs.Count == 0)
        {
            return new PreparationResult();
        }

        if (!isPdfDevice && !isDwfDevice)
        {


            foreach (var job in customJobs)
            {
                job.EffectivePaperWidthMm = 0;
                job.EffectivePaperHeightMm = 0;
                job.RequiresCustomPaperRegistration = false;
                job.RequireExactPaperSize = false;
                job.UseExactWindowScale = false;
                job.CustomPaperWasAdded = false;
            }

            return new PreparationResult();
        }

        var plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
        var preferredPlotter = isDwfDevice
            ? AcadPlotterInstaller.PreferredDwfPlotter
            : AcadPlotterInstaller.PreferredPdfPlotter;
        var pmpFileName = isDwfDevice ? "LA_dwf.pmp" : "LA_pdf.pmp";
        var outputKind = isDwfDevice ? "DWF" : "PDF";
        var installedPlotter = Path.Combine(plottersDirectory, preferredPlotter);
        var installedPmp = Path.Combine(plottersDirectory, "PMP Files", pmpFileName);
        if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
        {
            if (isDwfDevice)
            {
                var installedDevice = AcadPlotterInstaller.InstallDwfPlotter();
                if (!installedDevice.Installed
                    || !string.Equals(
                        installedDevice.DeviceName,
                        preferredPlotter,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Конфигурация плоттера LA_dwf неполна; невозможно зарегистрировать бумагу DWF с положительными или отрицательными полями.");
                }

                if (installedDevice.Written)
                {
                    AcadPlotterInstaller.RefreshPlotterDevicesIfNeeded(true);
                }
            }
            else
            {
                var installResult = AcadPlotterInstaller.InstallBundledPlotter();
                if (!installResult.Installed)
                {
                    throw new InvalidOperationException("Конфигурация плоттера LA_pdf неполна: " + installResult.Message);
                }

                if (installResult.Written)
                {
                    AcadPlotterInstaller.RefreshPlotterDevicesIfNeeded(true);
                }
            }

            plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
            installedPlotter = Path.Combine(plottersDirectory, preferredPlotter);
            installedPmp = Path.Combine(plottersDirectory, "PMP Files", pmpFileName);
        }

        if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
        {
            throw new FileNotFoundException(
                $"Плоттер LA_{outputKind.ToLowerInvariant()} или файл {pmpFileName} отсутствует; невозможно зарегистрировать бумагу с положительными или отрицательными полями.",
                installedPmp);
        }

        var requests = customJobs
            .Select(job => new PmpCustomPaper.PaperRequest
            {

                WidthMm = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm,
                HeightMm = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm
            })
            .ToList();
        var registrations = PmpCustomPaper.RegisterCustomPapers(installedPmp, requests)
            ?? throw new InvalidOperationException(
                $"Не удалось зарегистрировать в {pmpFileName} бумагу {outputKind} с полями. Печать остановлена, чтобы не использовать неверный формат.");

        var pmpUpdated = registrations.Any(registration => registration.WasAdded);
        var forceDeviceReload = pmpUpdated;
        var attachmentMessage = "";

#if AUTOCAD


        if (isPdfDevice)
        {
            var attachment = AcadPlotterInstaller.EnsureActivePdfPmpAttachment(
                installedPlotter,
                installedPmp,
                forceRewrite: pmpUpdated);
            if (!attachment.Success)
            {
                throw new InvalidOperationException("Не удалось связать LA_pdf.pc3 с пакетным PMP: " + attachment.Message);
            }

            forceDeviceReload |= attachment.Changed;
            attachmentMessage = attachment.Message;
        }
#endif

        foreach (var job in customJobs)
        {


            job.RequireExactPaperSize = true;
            job.UseExactWindowScale = true;
            job.CustomPaperWasAdded = false;
        }

        if (forceDeviceReload)
        {

            foreach (var firstJob in customJobs.GroupBy(job => job.IsPaperSpace).Select(group => group.First()))
            {
                firstJob.CustomPaperWasAdded = true;
            }
        }

        return new PreparationResult
        {
            Registrations = registrations,
            AttachmentMessage = attachmentMessage
        };
    }

    private static bool IsLongPaper(PlotJob job)
    {
        return job.PaperWidthMm > 0d
               && job.PaperHeightMm > 0d
               && !string.IsNullOrWhiteSpace(job.PaperName)
               && job.PaperName.IndexOf('+') > 0;
    }
}
