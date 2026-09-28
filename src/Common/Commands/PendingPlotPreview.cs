using System;
using System.Windows.Threading;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;


internal static class PendingPlotPreview
{
    private static readonly object Gate = new();
    private static Request? _pending;

    
    internal sealed class Request
    {
        public PlotJob Job { get; set; } = null!;
        public string DeviceName { get; set; } = "";
        public string StyleSheet { get; set; } = "";
        public Document Document { get; set; } = null!;
        public Action? OnFinally { get; set; }
        public Action<Exception>? OnError { get; set; }
    }

    
    public static void Start(Request request, Dispatcher dispatcher)
    {
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;
        var openedByUs = false;
        Document? previewDoc = null;
        try
        {
            previewDoc = PlotterService.PrepareJobDocument(request.Job, request.Document, out openedByUs);
            request.Document = previewDoc;
            var userFinally = request.OnFinally;
            var docToRelease = previewDoc;
            request.OnFinally = () =>
            {
                try
                {
                    userFinally?.Invoke();
                }
                finally
                {
                    dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        PlotterService.ReleaseTemporaryDocument(oldActive, docToRelease, openedByUs);
                    }));
                }
            };
            Queue(request);
            previewDoc.SendStringToExecute("_ZBP_INTERNAL_PREVIEW ", true, false, false);
        }
        catch
        {
            Take();
            if (previewDoc != null)
            {
                PlotterService.ReleaseTemporaryDocument(oldActive, previewDoc, openedByUs);
            }

            throw;
        }
    }

    
    public static void Queue(Request request)
    {
        lock (Gate)
        {
            _pending = request;
        }
    }

    
    public static Request? Take()
    {
        lock (Gate)
        {
            var request = _pending;
            _pending = null;
            return request;
        }
    }
}
