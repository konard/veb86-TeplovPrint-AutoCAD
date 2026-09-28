using System;
using System.Collections.Generic;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
#endif

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
internal static class ScanCandidateFilter
{

    public static string[] TitleBlockDxfNames() => new[] { "INSERT" };

    /// <summary>

    /// </summary>

    public static string[] RectangleFrameDxfNames(bool includeLines)
    {
        return includeLines
            ? new[] { "INSERT", "LWPOLYLINE", "POLYLINE", "3DPOLYLINE", "LINE" }
            : new[] { "INSERT", "LWPOLYLINE", "POLYLINE", "3DPOLYLINE" };
    }


    public static SelectionFilter TitleBlockSelectionFilter()
        => CreateStartTypeFilter(TitleBlockDxfNames());

    public static SelectionFilter SpdsSelectionFilter()
        => CreateStartTypeFilter(new[] { SpdsFormatScanner.SpdsDxfName });

    public static SelectionFilter AllSupportedFrameSelectionFilter(bool includeLines)
    {
        var names = new List<string>(RectangleFrameDxfNames(includeLines));
        names.Add(SpdsFormatScanner.SpdsDxfName);
        return CreateStartTypeFilter(names);
    }



    public static SelectionFilter RectangleFrameSelectionFilter(bool includeLines)
        => CreateStartTypeFilter(RectangleFrameDxfNames(includeLines));



    public static bool IsTitleBlockCandidate(DBObject? obj) => obj is BlockReference;

    /// <summary>

    /// </summary>


    public static bool IsRectangleFrameCandidate(DBObject? obj, bool includeLines)
    {
        if (obj is BlockReference or Polyline or Polyline2d or Polyline3d)
        {
            return true;
        }

        return includeLines && obj is Line;
    }

    /// <summary>

    /// </summary>




    public static void CollectFromSpace(
        Transaction tr,
        BlockTableRecord owner,
        Func<DBObject, bool> isCandidate,
        ICollection<ObjectId> results)
    {
        foreach (ObjectId id in owner)
        {
            DBObject? obj;
            try
            {
                obj = tr.GetObject(id, OpenMode.ForRead, false);
            }
            catch
            {
                continue;
            }

            if (obj == null || !isCandidate(obj))
            {
                continue;
            }

            results.Add(id);
        }
    }

    /// <summary>

    /// </summary>



    public static List<ObjectId> CollectInLayouts(
        Database db,
        Func<Layout, BlockTableRecord, bool> shouldIncludeLayout,
        Func<DBObject, bool> isCandidate)
    {
        var results = new List<ObjectId>();
        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout || owner.LayoutId.IsNull)
            {
                continue;
            }

            Layout? layout;
            try
            {
                layout = tr.GetObject(owner.LayoutId, OpenMode.ForRead, false) as Layout;
            }
            catch
            {
                continue;
            }

            if (layout == null || !shouldIncludeLayout(layout, owner))
            {
                continue;
            }

            CollectFromSpace(tr, owner, isCandidate, results);
        }

        tr.Commit();
        return results;
    }


    private static SelectionFilter CreateStartTypeFilter(IReadOnlyList<string> dxfNames)
    {
        return new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, string.Join(",", dxfNames))
        });
    }
}
