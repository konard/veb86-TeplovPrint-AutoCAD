using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZwcadBatchPlot;

public static class CsvExporter
{
    public static void ExportJobs(string path, IEnumerable<PlotJob> jobs, AppSettings? settings = null)
    {
        settings ??= AppSettingsStore.Load();
        var lines = new List<string>
        {
            "Печатать,Номер листа,Наименование,Информация 1,Информация 2,Формат,Масштаб,Фактический размер,Бумага,Имя блока,Пространство,Файл,Выходной PDF,Описание распознавания"
        };

        foreach (var job in jobs)
        {
            lines.Add(string.Join(",",
                Csv(job.Selected ? "Да" : "Нет"),
                Csv(job.DrawingNumber),
                Csv(job.Title),
                Csv(job.Info1),
                Csv(job.Info2),
                Csv(FileNameSanitizer.NormalizeLongPaperFraction(
                    OutputPaperNameResolver.Resolve(job, settings.LongPaperSnapToleranceMm),
                    settings.LongPaperNameFormat)),
                Csv(job.ScaleText),
                Csv(job.SizeText),
                Csv(job.PaperSizeText),
                Csv(job.BlockName),
                Csv(job.SpaceName),
                Csv(job.SourceFile),
                Csv(job.OutputPath),
                Csv(job.DetectionNote)));
        }

        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Csv(string value)
    {
        value ??= "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
