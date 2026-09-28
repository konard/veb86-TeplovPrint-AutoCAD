using System;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

public sealed partial class BatchPlotCommands
{
    /// <summary>


    /// </summary>
    internal static void TransformPlotWindow(PlotJob job, Matrix3d toTarget)
    {
        var corners = new[]
        {
            new Point3d(job.MinX, job.MinY, 0).TransformBy(toTarget),
            new Point3d(job.MaxX, job.MinY, 0).TransformBy(toTarget),
            new Point3d(job.MinX, job.MaxY, 0).TransformBy(toTarget),
            new Point3d(job.MaxX, job.MaxY, 0).TransformBy(toTarget)
        };
        job.MinX = corners.Min(p => p.X);
        job.MinY = corners.Min(p => p.Y);
        job.MaxX = corners.Max(p => p.X);
        job.MaxY = corners.Max(p => p.Y);
        job.IsDcsWindow = true;
    }

    /// <summary>



    /// </summary>
    internal static Matrix3d BuildWcsToDcsMatrix(Editor editor)
    {

        if (!editor.Document.Database.TileMode)
        {
            return Matrix3d.Identity;
        }

        var view = editor.GetCurrentView();


        var wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
        wcsToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcsToDcs;
        wcsToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * wcsToDcs;


        return wcsToDcs.Inverse();
    }

    /// <summary>

    /// = UCS→WCS × WCS→DCS
    /// </summary>
    private static Matrix3d BuildUcsToDcsMatrix(Editor editor)
    {

        var ucsToWcs = editor.CurrentUserCoordinateSystem;

        try
        {
            var wcsToDcs = BuildWcsToDcsMatrix(editor);


            return ucsToWcs.PreMultiplyBy(wcsToDcs);
        }
        catch (System.Exception ex)
        {

            editor.WriteMessage($"\nНе удалось выполнить преобразование ПСК→DCS; используется ПСК→МСК: {ex.Message}");
            return ucsToWcs;
        }
    }
}
