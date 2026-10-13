using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if NANOCAD
using Teigha.DatabaseServices;
#elif AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;


public sealed class DwgSpaceEntry
{
    public string FilePath { get; set; } = "";
    public string LayoutName { get; set; } = "";
    public bool IsModelSpace { get; set; }


    public string DisplayName => IsModelSpace ? "Модель" : LayoutName;

    public bool Selected { get; set; } = true;


    public string StyleSheet { get; set; } = "";
}


public static class DwgSpaceCatalog
{
    /// <summary>


    /// </summary>
    public static List<DwgSpaceEntry> ListSpaces(IEnumerable<string> files, ICollection<string>? errors = null)
    {
        var result = new List<DwgSpaceEntry>();
        if (files == null)
        {
            return result;
        }

        foreach (var raw in files)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string filePath;
            try
            {
                filePath = Path.GetFullPath(raw);
            }
            catch (Exception ex)
            {
                errors?.Add($"{raw}: {ex.Message}");
                continue;
            }

            try
            {
                result.AddRange(ListSpacesInFile(filePath));
            }
            catch (Exception ex)
            {
                errors?.Add($"{filePath}: {ex.Message}");
            }
        }

        return result;
    }

    private static List<DwgSpaceEntry> ListSpacesInFile(string filePath)
    {
        var entries = new List<(DwgSpaceEntry Entry, int TabOrder)>();
        using var db = new Database(false, true);
        db.ReadDwgFile(filePath, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout || owner.LayoutId.IsNull)
            {
                continue;
            }

            Layout layout;
            try
            {
                layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            }
            catch
            {
                continue;
            }

            var layoutName = layout.LayoutName ?? "";
            if (string.IsNullOrWhiteSpace(layoutName))
            {
                continue;
            }

            entries.Add((
                new DwgSpaceEntry
                {
                    FilePath = filePath,
                    LayoutName = layoutName,
                    IsModelSpace = layout.ModelType,
                    Selected = true
                },
                layout.TabOrder));
        }

        tr.Commit();


        entries.Sort((a, b) =>
        {
            if (a.Entry.IsModelSpace != b.Entry.IsModelSpace)
            {
                return a.Entry.IsModelSpace ? -1 : 1;
            }

            var tab = a.TabOrder.CompareTo(b.TabOrder);
            if (tab != 0)
            {
                return tab;
            }

            return string.Compare(a.Entry.LayoutName, b.Entry.LayoutName, StringComparison.OrdinalIgnoreCase);
        });

        return entries.Select(x => x.Entry).ToList();
    }
}
