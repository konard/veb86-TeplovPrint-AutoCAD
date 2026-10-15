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
    private const double MediaMatchToleranceMm = 3d;
    private const double ExactMediaToleranceMm = 0.05d;

    
    public sealed class PlotJobResult
    {
        public PlotJob Job { get; set; } = new();
        public Exception? Error { get; set; }
        public bool Succeeded => Error == null;
    }

    
    private sealed class MediaChoice
    {
        public string Name { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double Error { get; set; }
        public bool IsFullBleed { get; set; }
        public bool UseClosestBySize { get; set; }
        public bool RequiresExactSize { get; set; }
        public double SizeToleranceMm { get; set; } = MediaMatchToleranceMm;
        public bool FromCachedCatalog { get; set; }
        public PlotRotation PreferredRotation { get; set; }
    }

    
    private sealed class MediaCatalogItem
    {
        public string Name { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public bool IsFullBleed { get; set; }
    }

    
    private sealed class CachedMediaCatalogException : Exception
    {
        public CachedMediaCatalogException(Exception innerException)
            : base("Сохранённая конфигурация бумаги больше недействительна.", innerException)
        {
        }
    }

    /// <summary>Every media/rotation attempt to configure the plot settings of a sheet was refused by the CAD.</summary>
    private sealed class PlotSettingsRejectedException : InvalidOperationException
    {
        public PlotSettingsRejectedException(string message)
            : base(message)
        {
        }

        public PlotSettingsRejectedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private static readonly object MediaCatalogCacheLock = new();
    private static readonly Dictionary<string, IReadOnlyList<MediaCatalogItem>> MediaCatalogCache =
        new(StringComparer.OrdinalIgnoreCase);

    
    private sealed class ValidatedPlot : IDisposable
    {
        public PlotInfo Info { get; set; } = new();
        public PlotSettings Settings { get; set; } = null!;
        public MediaChoice Media { get; set; } = new();
        public PlotRotation Rotation { get; set; }

        /// <summary>False when Settings is the layout itself (nanoCAD, issue #7): it belongs to the database.</summary>
        public bool OwnsSettings { get; set; } = true;

        
        public void Dispose()
        {
            if (OwnsSettings)
            {
                Settings.Dispose();
            }
        }
    }

    
    public static List<PlotJobResult> PlotMany(
        IReadOnlyList<PlotJob> jobs,
        string deviceName,
        string styleSheet,
        Document currentDocument,
        AppSettings settings,
        Action<PlotJob>? beforeJob = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PlotJobResult>();
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;
        PrintSessionLog.Info($"PlotMany: заданий={jobs.Count}; устройство={deviceName}; стиль={styleSheet}; "
            + $"чертёж={DocumentNameForLog(currentDocument)}; активный={DocumentNameForLog(oldActive)}; {DescribeCadForLog()}");

        PrintSessionLog.Step("EnsureTextGeometryMode(" + deviceName + ")");
        EnsureTextGeometryMode(deviceName, settings.ConvertTextToGeometryWhenPlotting);
        using var variables = PlotSystemVariables.Apply(settings.PlotTransparency);
        try
        {
            foreach (var group in jobs.GroupBy(job => GetGroupKey(job, currentDocument)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupJobs = group.ToList();
                try
                {
                    if (group.Key == "__CURRENT__")
                    {
                        PlotDocumentJobs(currentDocument, groupJobs, deviceName, styleSheet, settings, beforeJob, results, cancellationToken);
                    }
                    else
                    {
                        PlotExternalFileJobs(
                            groupJobs,
                            groupJobs[0].SourceFile,
                            deviceName,
                            styleSheet,
                            settings,
                            beforeJob,
                            results,
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    foreach (var job in groupJobs.Where(job => !results.Any(x => ReferenceEquals(x.Job, job))))
                    {
                        results.Add(new PlotJobResult { Job = job, Error = ex });
                    }
                }
            }
        }
        finally
        {
            if (oldActive != null && !oldActive.IsDisposed)
            {
                CadApp.DocumentManager.MdiActiveDocument = oldActive;
            }
        }

        return results;
    }

    
    public static void Plot(PlotJob job, string deviceName, string styleSheet, Document currentDocument, AppSettings settings)
    {
        var result = PlotMany(new[] { job }, deviceName, styleSheet, currentDocument, settings).FirstOrDefault();
        if (result?.Error != null)
        {
            throw result.Error;
        }
    }

    
    public static void Preview(PlotJob job, string deviceName, string styleSheet, Document currentDocument)
    {
        var settings = AppSettingsStore.Load();
        EnsureTextGeometryMode(deviceName, settings.ConvertTextToGeometryWhenPlotting);
        using var variables = PlotSystemVariables.Apply(settings.PlotTransparency);
        var active = CadApp.DocumentManager.MdiActiveDocument;
        if (active != null && IsCurrentDocumentJob(job, active))
        {
            currentDocument = active;
        }

        if (!IsCurrentDocumentJob(job, currentDocument))
        {
            PreviewExternalFile(job, deviceName, styleSheet);
            return;
        }

        using (currentDocument.LockDocument())
        {
            string? lastSpaceKey = null;
            EnsureSpaceRegenerated(currentDocument, job, ref lastSpaceKey);

            PreviewDatabase(currentDocument.Database, currentDocument.Name, job, deviceName, styleSheet, currentDocument);
        }
    }

    /// <summary>A separate call, so "?." does not make the compiler treat the caller's document as maybe-null.</summary>
    private static string DocumentNameForLog(Document? doc) => doc?.Name ?? "<нет>";

    /// <summary>CAD version and the plot-related system variables for the print log (issue #9).</summary>
    private static string DescribeCadForLog()
    {
        var text = new List<string>();
        foreach (var name in new[] { "ACADVER", "PRODUCT", "LOCALE", "BACKGROUNDPLOT", "PUBLISHCOLLATE", "PDFSHX", "PLOTTRANSPARENCYOVERRIDE", "TILEMODE", "CTAB" })
        {
            try
            {
                text.Add(name + "=" + CadApp.GetSystemVariable(name));
            }
            catch (Exception ex)
            {
                text.Add(name + "=<" + ex.GetType().Name + ">");
            }
        }

        return string.Join("; ", text);
    }

    private static string GetGroupKey(PlotJob job, Document currentDocument)
    {
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            return "__CURRENT__";
        }

        return string.IsNullOrWhiteSpace(job.SourceFile) ? "" : Path.GetFullPath(job.SourceFile);
    }

    
    private static void EnsureTextGeometryMode(string deviceName, bool convertToGeometry)
    {
        WaitForPlotIdle();
        var result = AcadPlotterInstaller.ApplyTextGeometryMode(deviceName, convertToGeometry);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }

        if (result.Changed)
        {

            InvalidateMediaCatalog(deviceName);
        }
    }

    
    private static void PlotDocumentJobs(
        Document doc,
        IReadOnlyList<PlotJob> jobs,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {

        string? lastSpaceKey = null;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                beforeJob?.Invoke(job);
                using (doc.LockDocument())
                {
                    EnsureSpaceRegenerated(doc, job, ref lastSpaceKey);
                    PlotDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, settings, doc);
                }

                results.Add(new PlotJobResult { Job = job });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }

    
    private static void PlotSideDatabaseJobs(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        using var db = new Database(false, true);
        db.ReadDwgFile(sourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);
        db.ResolveXrefs(true, false);

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                beforeJob?.Invoke(job);
                PlotDatabase(db, Path.GetFileName(sourceFile), job, deviceName, styleSheet, settings, null);
                results.Add(new PlotJobResult { Job = job });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }
}
