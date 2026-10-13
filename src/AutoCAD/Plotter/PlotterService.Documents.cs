using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using HostMgd.PlottingServices;
using CadApp = HostMgd.ApplicationServices.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#endif



namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    
    private static Layout FindLayoutForJob(Transaction tr, Database db, PlotJob job)
    {
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        Layout? first = null;
        Layout? model = null;
        Layout? firstPaper = null;
        var availableLayouts = new List<string>();

        foreach (ObjectId id in blockTable)
        {
            var record = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!record.IsLayout)
            {
                continue;
            }

            var layout = (Layout)tr.GetObject(record.LayoutId, OpenMode.ForRead);
            availableLayouts.Add(layout.LayoutName);
            first ??= layout;
            if (layout.ModelType)
            {
                model ??= layout;
            }
            else
            {
                firstPaper ??= layout;
            }

            if (string.Equals(record.Name, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout.LayoutName, job.SpaceName, StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        if (!string.IsNullOrWhiteSpace(job.SpaceName))
        {
            throw new InvalidOperationException(
                $"Целевой лист «{job.SpaceName}» не найден. Доступные листы: {string.Join(", ", availableLayouts)}. Повторно отсканируйте чертёж.");
        }

        if (!job.IsPaperSpace && model != null)
        {
            return model;
        }

        if (job.IsPaperSpace && firstPaper != null)
        {
            return firstPaper;
        }

        return first ?? throw new InvalidOperationException("Не найден лист для печати.");
    }

    
    private static void ActivateLayout(Database db, PlotJob job)
    {
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = FindLayoutForJob(tr, db, job);
            LayoutManager.Current.CurrentLayout = layout.LayoutName;
            tr.Commit();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Не удалось активировать лист «{job.SpaceName}». Печать остановлена, чтобы не вывести неверную область.", ex);
        }
    }

    
    private static void EnsureSpaceRegenerated(Document doc, PlotJob job, ref string? lastSpaceKey)
    {
        var spaceKey = string.IsNullOrWhiteSpace(job.SpaceName) ? "__CURRENT__" : job.SpaceName.Trim();
        if (string.Equals(lastSpaceKey, spaceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ActivateLayout(doc.Database, job);
        doc.Editor.Regen();
        lastSpaceKey = spaceKey;
    }

    
    private static void PrepareOutputFile(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }

    
    private static Document OpenDocument(string file)
    {
#if ACAD_CORE
        CadApp.DocumentManager.AppContextOpenDocument(file);
        return FindOpenDocument(file)
            ?? CadApp.DocumentManager.MdiActiveDocument
            ?? throw new InvalidOperationException("AutoCAD открыл DWG, но плагин не получил объект документа: " + file);
#else
        return CadApp.DocumentManager.Open(file, false);
#endif
    }

    
    private static void CloseWithoutSave(Document doc)
    {
#if ACAD_CORE
        var close = doc.GetType().GetMethod("CloseAndDiscard");
        if (close == null)
        {
            return;
        }

        var parameters = close.GetParameters();
        if (parameters.Length == 0)
        {
            close.Invoke(doc, null);
        }
        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
        {
            close.Invoke(doc, new object?[] { null });
        }
#else
        doc.CloseAndDiscard();
#endif
    }

    
    private static Document? FindOpenDocument(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(file);
        foreach (Document doc in CadApp.DocumentManager)
        {
            var name = doc.Database.Filename;
            if (!string.IsNullOrWhiteSpace(name)
                && string.Equals(Path.GetFullPath(name), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return doc;
            }
        }

        return null;
    }

    
    private static bool IsCurrentDocumentJob(PlotJob job, Document currentDocument)
    {
        var currentFile = currentDocument.Database.Filename;
        if (string.IsNullOrWhiteSpace(currentFile))
        {
            return string.Equals(job.SourceFile, currentDocument.Name, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(job.SourceFile)
            && string.Equals(Path.GetFullPath(job.SourceFile), Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase);
    }

    
    private static void WaitForPlotIdle()
    {
        const int timeoutMs = 10 * 60 * 1000;
        var waited = 0;

        while (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            if (waited >= timeoutMs)
            {
                throw new InvalidOperationException("Текущее задание печати AutoCAD не завершается слишком долго.");
            }

            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(250);
            waited += 250;
        }
    }

    
    private static void TryPlotCleanup(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }

    
    private static void ValidatePlotOutput(string outputPath)
    {
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new IOException("Механизм печати не создал выходной файл: " + outputPath);
        }

        if (string.Equals(Path.GetExtension(outputPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            var signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            using var stream = File.OpenRead(outputPath);
            var actual = new byte[signature.Length];
            if (stream.Read(actual, 0, actual.Length) != actual.Length || !actual.SequenceEqual(signature))
            {
                throw new InvalidDataException("PNG создан, но имеет недопустимый формат; задание считается неудачным: " + outputPath);
            }
            return;
        }

        if (string.Equals(Path.GetExtension(outputPath), ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(outputPath), ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(outputPath);
            var signature = new byte[3];
            if (stream.Read(signature, 0, signature.Length) != signature.Length
                || signature[0] != 0xFF
                || signature[1] != 0xD8
                || signature[2] != 0xFF)
            {
                throw new InvalidDataException("JPG создан, но имеет недопустимый формат; задание считается неудачным: " + outputPath);
            }
            return;
        }

        if (string.Equals(Path.GetExtension(outputPath), ".dwf", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(outputPath);
            var signature = new byte[6];
            if (stream.Read(signature, 0, signature.Length) != signature.Length
                || !string.Equals(System.Text.Encoding.ASCII.GetString(signature), "(DWF V", StringComparison.Ordinal))
            {
                throw new InvalidDataException("DWF создан, но имеет недопустимый формат; задание считается неудачным: " + outputPath);
            }
            return;
        }

        using var pdf = PdfReader.Open(outputPath, PdfDocumentOpenMode.Import);
        if (pdf.PageCount == 0)
        {
            throw new InvalidDataException("PDF создан, но не содержит допустимых страниц; задание считается неудачным: " + outputPath);
        }
    }
}
