using System;
using System.IO;
using System.Linq;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
#elif AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
internal static class DwgModelSplitter
{
    /// <summary>


    /// </summary>





    internal static void Split(
        Database sourceDatabase,
        string sourcePath,
        string outputPath,
        PlotJob job,
        DwgSplitService.SplitResult result)
    {
        var copyFrom = DwgDatabaseCleanup.ResolveSavedSourcePath(sourceDatabase, sourcePath, job);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Copy(copyFrom, outputPath, overwrite: true);

        using var db = new Database(false, true);
        db.ReadDwgFile(outputPath, FileOpenMode.OpenForReadAndWriteNoShare, true, "");
        db.CloseInput(true);

        var oldWorkingDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var model = (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForWrite);
                var layerLocks = DwgDatabaseCleanup.UnlockLayers(tr, db);
                CleanModelSpaceByWindow(tr, model, job, result);
                DwgDatabaseCleanup.RestoreLayerLocks(tr, layerLocks);
                tr.Commit();
            }

            if (result.KeptEntities == 0)
            {
                throw new InvalidOperationException(
                    "В области разделения нет объектов для сохранения; создание пустого DWG остановлено. Проверьте координаты ПСК/МСК рамки.");
            }

            DwgDatabaseCleanup.PurgeUnusedNamedObjects(db);
            db.SaveAs(outputPath, DwgVersion.Current);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
        }
    }


    private static void CleanModelSpaceByWindow(
        Transaction tr,
        BlockTableRecord model,
        PlotJob job,
        DwgSplitService.SplitResult result)
    {
        var ids = model.Cast<ObjectId>().ToList();
        foreach (var id in ids)
        {
            if (id.IsErased)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForWrite, false) is not Entity entity)
            {
                continue;
            }

            if (DwgDatabaseCleanup.IsTemporaryOverlayEntity(entity))
            {
                entity.Erase();
                result.RemovedEntities++;
                continue;
            }

            if (DwgSplitGeometry.ShouldKeepEntity(tr, entity, job, result))
            {
                result.KeptEntities++;
            }
            else
            {
                entity.Erase();
                result.RemovedEntities++;
            }
        }
    }
}
