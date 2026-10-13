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
    
    private static PlotRotation ResolvePlotRotation(
        string deviceName,
        PlotRotation paperRotation,
        PlotJob job,
        Extents2d window)
    {
        if (IsRasterPlotDevice(deviceName))
        {

            return paperRotation;
        }

        return ResolveWindowRotation(paperRotation, job, window);
    }

    
    private static PlotRotation ResolveWindowRotation(
        PlotRotation paperRotation,
        PlotJob job,
        Extents2d window)
    {
        var paperWidth = job.PaperWidthMm;
        var paperHeight = job.PaperHeightMm;
        var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
        var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
        if (paperWidth <= 1e-9 || paperHeight <= 1e-9
            || windowWidth <= 1e-9 || windowHeight <= 1e-9)
        {
            return paperRotation;
        }

        var paperIsLandscape = paperWidth >= paperHeight;
        var windowIsLandscape = windowWidth >= windowHeight;
        if (paperIsLandscape == windowIsLandscape)
        {
            return paperRotation;
        }

        return ToggleQuarterTurn(paperRotation);
    }

    
    private static PlotRotation ToggleQuarterTurn(PlotRotation paperRotation)
    {
        return paperRotation switch
        {
            PlotRotation.Degrees000 => PlotRotation.Degrees090,
            PlotRotation.Degrees090 => PlotRotation.Degrees000,
            PlotRotation.Degrees180 => PlotRotation.Degrees270,
            PlotRotation.Degrees270 => PlotRotation.Degrees180,
            _ => paperRotation
        };
    }

    
    private static void PrepareEditorViewForPlot(Document doc, PlotJob job)
    {

        if (job.IsPaperSpace || job.IsDcsWindow)
        {
            return;
        }

        try
        {
            var corners = CadSelectionWindow.GetJobWorldCorners(job);
            var center = new Point3d(
                corners.Average(point => point.X),
                corners.Average(point => point.Y),
                corners.Average(point => point.Z));
            var width = job.UsesUserCoordinateSystem
                ? Math.Max(Math.Abs(job.UcsMaxX - job.UcsMinX), 1)
                : Math.Max(Math.Abs(job.MaxX - job.MinX), 1);
            var height = job.UsesUserCoordinateSystem
                ? Math.Max(Math.Abs(job.UcsMaxY - job.UcsMinY), 1)
                : Math.Max(Math.Abs(job.MaxY - job.MinY), 1);

            using var view = doc.Editor.GetCurrentView();

            view.ViewDirection = Vector3d.ZAxis;
            view.ViewTwist = job.UsesUserCoordinateSystem
                ? -Math.Atan2(job.UcsXAxisY, job.UcsXAxisX)
                : 0;
            view.Target = center;
            view.CenterPoint = Point2d.Origin;
            view.Width = width * 1.05;
            view.Height = height * 1.05;
            doc.Editor.SetCurrentView(view);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Не удалось нормализовать вид печати. Печать остановлена, чтобы избежать пустой или смещённой страницы.", ex);
        }
    }

    
    private static Extents2d GetPlotWindow(PlotJob job, Document? plotDocument)
    {

        if (job.IsDcsWindow)
        {
            return new Extents2d(
                Math.Min(job.MinX, job.MaxX),
                Math.Min(job.MinY, job.MaxY),
                Math.Max(job.MinX, job.MaxX),
                Math.Max(job.MinY, job.MaxY));
        }

        if (job.IsPaperSpace)
        {
            return new Extents2d(
                Math.Min(job.MinX, job.MaxX),
                Math.Min(job.MinY, job.MaxY),
                Math.Max(job.MinX, job.MaxX),
                Math.Max(job.MinY, job.MaxY));
        }

        if (plotDocument != null)
        {
            try
            {
                var view = plotDocument.Editor.GetCurrentView();
                var worldToDisplay = GetWorldToDisplayMatrix(view);

                var points = CadSelectionWindow.GetJobWorldCorners(job)
                    .Select(point => point.TransformBy(worldToDisplay))
                    .ToArray();

                return new Extents2d(
                    points.Min(p => p.X),
                    points.Min(p => p.Y),
                    points.Max(p => p.X),
                    points.Max(p => p.Y));
            }
            catch
            {
            }
        }

        return new Extents2d(
            Math.Min(job.MinX, job.MaxX),
            Math.Min(job.MinY, job.MaxY),
            Math.Max(job.MinX, job.MaxX),
            Math.Max(job.MinY, job.MaxY));
    }

    
    private static Matrix3d GetWorldToDisplayMatrix(ViewTableRecord view)
    {
        var matrix = Matrix3d.PlaneToWorld(view.ViewDirection);
        matrix = Matrix3d.Displacement(view.Target - Point3d.Origin) * matrix;
        matrix = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * matrix;
        return matrix.Inverse();
    }
}
