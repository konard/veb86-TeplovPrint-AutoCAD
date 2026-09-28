using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif



namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    private const int ExternalDocumentReadyTimeoutMs = 10000;

    
    public static Document PrepareJobDocument(PlotJob job, Document currentDocument, out bool openedByUs)
    {
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            openedByUs = false;
            if (CadApp.DocumentManager.MdiActiveDocument != currentDocument)
            {
                ActivateExternalDocument(currentDocument, currentDocument.Name);
            }

            return currentDocument;
        }

        var sourceFile = RequireExistingSourceFile(job.SourceFile);
        openedByUs = FindOpenDocument(sourceFile) == null;
        var doc = OpenExternalDocumentForPlot(sourceFile);
        ActivateExternalDocument(doc, sourceFile);
        TryResolveExternalXrefs(doc);
        return doc;
    }

    
    public static void ReleaseTemporaryDocument(Document? previousActive, Document document, bool openedByUs)
    {
        try
        {
            WaitForPlotIdle();
        }
        catch
        {
        }

        TryRestoreActiveDocument(previousActive);
        if (openedByUs && document != null && !document.IsDisposed && previousActive != document)
        {
            CloseWithoutSave(document);
        }

        var active = CadApp.DocumentManager.MdiActiveDocument;
        if (active != null && !active.IsDisposed)
        {
            try
            {
                HostApplicationServices.WorkingDatabase = active.Database;
            }
            catch
            {
            }
        }
    }

    
    private static void PlotExternalFileJobs(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        using var scope = AcquireExternalDocument(sourceFile);
        var doc = scope.Document;
        TryResolveExternalXrefs(doc);

        string? lastSpaceKey = null;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PrepareExternalSpace(doc, job, ref lastSpaceKey);
                beforeJob?.Invoke(job);

                ActivateExternalDocument(doc, sourceFile);
                using (doc.LockDocument())
                {
                    PlotDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, settings, doc);
                }

                results.Add(new PlotJobResult { Job = job });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }

    
    private static void PreviewExternalFile(PlotJob job, string deviceName, string styleSheet)
    {
        using var scope = AcquireExternalDocument(job.SourceFile);
        var doc = scope.Document;
        TryResolveExternalXrefs(doc);
        string? lastSpaceKey = null;
        PrepareExternalSpace(doc, job, ref lastSpaceKey);
        ActivateExternalDocument(doc, job.SourceFile);
        using (doc.LockDocument())
        {
            PreviewDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, doc);
        }
    }

    
    private static ExternalDocumentScope AcquireExternalDocument(string sourceFile)
    {
        sourceFile = RequireExistingSourceFile(sourceFile);
        var previousActive = CadApp.DocumentManager.MdiActiveDocument;
        var openedByUs = FindOpenDocument(sourceFile) == null;
        var doc = OpenExternalDocumentForPlot(sourceFile);
        ActivateExternalDocument(doc, sourceFile);
        return new ExternalDocumentScope(doc, previousActive, openedByUs);
    }

    
    private static string RequireExistingSourceFile(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            throw new InvalidOperationException("Для листа не указан путь DWG; невозможно открыть внешний файл.");
        }

        var fullPath = Path.GetFullPath(sourceFile);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Внешний файл чертежа не найден: " + fullPath, fullPath);
        }

        return fullPath;
    }

    
    private static Document OpenExternalDocumentForPlot(string sourceFile)
    {
        var existing = FindOpenDocument(sourceFile);
        if (existing != null)
        {
            return existing;
        }

        try
        {
            OpenDocument(sourceFile);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Не удалось открыть внешний чертёж: " + sourceFile + "\n" + ex.Message, ex);
        }

        var waited = 0;
        while (waited < ExternalDocumentReadyTimeoutMs)
        {
            var found = FindOpenDocument(sourceFile);
            if (found != null)
            {
                return found;
            }

            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(50);
            waited += 50;
        }

        throw new InvalidOperationException("Внешний чертёж не открыт. Его печать остановлена, чтобы не напечатать неверный документ: " + sourceFile);
    }

    
    private static void ActivateExternalDocument(Document doc, string sourceFile)
    {
        if (CadApp.DocumentManager.MdiActiveDocument != doc)
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
        }

        var waited = 0;
        while (CadApp.DocumentManager.MdiActiveDocument != doc && waited < ExternalDocumentReadyTimeoutMs)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(50);
            waited += 50;
        }

        if (CadApp.DocumentManager.MdiActiveDocument != doc)
        {
            throw new InvalidOperationException("Не удалось сделать внешний чертёж текущим документом: " + sourceFile);
        }

        HostApplicationServices.WorkingDatabase = doc.Database;
        PumpExternalDocumentDisplay(doc);
    }

    
    private static void TryRestoreActiveDocument(Document? previousActive)
    {
        if (previousActive == null || previousActive.IsDisposed)
        {
            return;
        }

        try
        {
            if (CadApp.DocumentManager.MdiActiveDocument != previousActive)
            {
                CadApp.DocumentManager.MdiActiveDocument = previousActive;
            }

            HostApplicationServices.WorkingDatabase = previousActive.Database;
        }
        catch
        {
        }
    }

    
    private static void TryResolveExternalXrefs(Document doc)
    {
        try
        {
            doc.Database.ResolveXrefs(true, false);
        }
        catch
        {
        }
    }

    
    private static void PrepareExternalSpace(Document doc, PlotJob job, ref string? lastSpaceKey)
    {
        var spaceKey = string.IsNullOrWhiteSpace(job.SpaceName) ? "__CURRENT__" : job.SpaceName.Trim();
        if (!string.Equals(lastSpaceKey, spaceKey, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using (doc.LockDocument())
                {
                    HostApplicationServices.WorkingDatabase = doc.Database;
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var layout = FindLayoutForJob(tr, doc.Database, job);
                        LayoutManager.Current.CurrentLayout = layout.LayoutName;
                        tr.Commit();
                    }

                    doc.Editor.Regen();
                }

                PumpExternalDocumentDisplay(doc);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Не удалось подготовить лист «{job.SpaceName}» внешнего чертежа. Печать этого листа остановлена, чтобы не вывести неверную область.",
                    ex);
            }

            lastSpaceKey = spaceKey;
        }

        if (!job.IsPaperSpace && !job.IsDcsWindow)
        {
            using (doc.LockDocument())
            {
                PrepareEditorViewForPlot(doc, job);
            }
        }
    }

    
    private static void PumpExternalDocumentDisplay(Document doc)
    {
        try
        {
            doc.Editor.UpdateScreen();
        }
        catch
        {
        }

        try
        {
            CadApp.UpdateScreen();
        }
        catch
        {
        }

        System.Windows.Forms.Application.DoEvents();
        Thread.Sleep(50);
        System.Windows.Forms.Application.DoEvents();
    }

    
    private sealed class ExternalDocumentScope : IDisposable
    {
        private readonly Document? _previousActive;
        private readonly bool _openedByUs;
        private bool _disposed;

        public ExternalDocumentScope(Document document, Document? previousActive, bool openedByUs)
        {
            Document = document;
            _previousActive = previousActive;
            _openedByUs = openedByUs;
        }

        public Document Document { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseTemporaryDocument(_previousActive, Document, _openedByUs);
        }
    }
}
