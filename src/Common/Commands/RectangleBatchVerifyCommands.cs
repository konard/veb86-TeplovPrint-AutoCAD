using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using HostMgd.EditorInput;
using Teigha.Geometry;
using Teigha.Runtime;
using CadApp = HostMgd.ApplicationServices.Application;
#elif AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.Runtime;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
public sealed partial class BatchPlotCommands
{

    [CommandMethod("_ZBP_INTERNAL_VERIFY_RECTANGLE_BATCH")]
    public void VerifyRectangleBatch()
    {
        var reportPath = Path.Combine(
            Path.GetTempPath(),
            "zbp-rectangle-batch-verify-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        var log = new StringBuilder();
        var passed = 0;
        var failed = 0;

        void Case(string name, bool ok, string detail)
        {
            if (ok)
            {
                passed++;
                log.AppendLine("[PASS] " + name + " | " + detail);
            }
            else
            {
                failed++;
                log.AppendLine("[FAIL] " + name + " | " + detail);
            }
        }

        try
        {
            log.AppendLine("ZBP Rectangle Batch Verify");
            log.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            log.AppendLine("Process: " + System.Diagnostics.Process.GetCurrentProcess().ProcessName);
            log.AppendLine();

            RunPaperDetectionCases(Case);
            log.AppendLine();
            RunDrawingScanCases(Case);
        }
        catch (System.Exception ex)
        {
            failed++;
            log.AppendLine("[FAIL] fatal | " + ex);
        }

        log.AppendLine();
        log.AppendLine($"Summary: passed={passed} failed={failed} total={passed + failed}");
        File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);

        var doc = CadApp.DocumentManager.MdiActiveDocument;
        var ed = doc?.Editor;
        ed?.WriteMessage("\n[ZBP] Rectangle verify report: " + reportPath + "\n");
        ed?.WriteMessage($"[ZBP] passed={passed} failed={failed}\n");


        System.Diagnostics.Trace.WriteLine("ZBP_RECT_VERIFY_REPORT=" + reportPath);
        System.Diagnostics.Trace.WriteLine($"ZBP_RECT_VERIFY_SUMMARY=passed={passed};failed={failed}");
        Console.WriteLine("ZBP_RECT_VERIFY_REPORT=" + reportPath);
        Console.WriteLine($"ZBP_RECT_VERIFY_SUMMARY=passed={passed};failed={failed}");
        Console.WriteLine(log.ToString());
    }


    private static void RunPaperDetectionCases(Action<string, bool, string> Case)
    {
        var modelOptions = PaperSizeDetector.CreateRectangleBatchOptions(
            paperMatchToleranceMm: 1d,
            isPaperSpace: false,
            longPaperSnapToleranceMm: 3d,
            customScales: null);
        var layoutOptions = PaperSizeDetector.CreateRectangleBatchOptions(
            paperMatchToleranceMm: 1d,
            isPaperSpace: true,
            longPaperSnapToleranceMm: 3d,
            customScales: null);


        {
            var w = 420d * 100d;
            var h = 297d * 100d;
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(w, h, modelOptions);
            var first = list.FirstOrDefault();
            Case(
                "stage1_A3_1to100_default",
                first != null
                && string.Equals(first.PaperName, "A3", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 100d) < 0.001d,
                first == null
                    ? "empty"
                    : $"{first.PaperName} {first.ScaleText} count={list.Count}");
        }


        {
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(420d, 297d, layoutOptions);
            var first = list.FirstOrDefault();
            Case(
                "stage1_layout_A3_1to1_preferred",
                first != null
                && string.Equals(first.PaperName, "A3", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 1d) < 0.001d,
                first == null ? "empty" : $"{first.PaperName} {first.ScaleText}");
        }


        {
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(594d * 100d, 420d * 100d, modelOptions);
            var first = list.FirstOrDefault();
            Case(
                "stage1_A2_1to100_default",
                first != null
                && string.Equals(first.PaperName, "A2", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 100d) < 0.001d,
                first == null ? "empty" : $"{first.PaperName} {first.ScaleText}");
        }


        {
            var shortSide = 594d * 100d;
            var longSide = 841d * 10d / 8d * 100d;
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(longSide, shortSide, modelOptions);
            var first = list.FirstOrDefault();
            Case(
                "stage1_A1_plus_1_4",
                first != null
                && first.IsLong
                && first.PaperName.StartsWith("A1+", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 100d) < 0.001d,
                first == null ? "empty" : $"{first.PaperName} {first.ScaleText} IsLong={first.IsLong}");
        }


        {
            var h = 297d * 100d + 150d;
            var longAdjusted = h * (420d / 297d);
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(longAdjusted, h, modelOptions);
            var first = list.FirstOrDefault();
            var stage1Empty = PaperSizeDetector.DetectCandidates(longAdjusted, h, modelOptions).Count == 0;
            Case(
                "reject_unconfigured_scale_100_5",
                stage1Empty && list.Count == 0,
                $"stage1Empty={stage1Empty}; "
                + (first == null ? "empty" : $"{first.PaperName} {first.ScaleText} count={list.Count}"));
        }


        {
            const double scale = 143d;
            var w = 420d * scale;
            var h = 297d * scale;
            var stage1 = PaperSizeDetector.DetectCandidates(w, h, modelOptions);
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(w, h, modelOptions);
            Case(
                "reject_unconfigured_scale_143",
                stage1.Count == 0 && list.Count == 0,
                $"stage1={stage1.Count}; count={list.Count}");
        }


        {
            const double scale = 111.35d;
            var w = 841d * scale;
            var h = 594d * scale;
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(w, h, modelOptions);
            var first = list.FirstOrDefault();
            Case(
                "reject_unconfigured_scale_111_35",
                list.Count == 0,
                first == null
                    ? "empty"
                    : $"{first.PaperName} {first.ScaleText}; names={string.Join(",", list.Select(x => x.PaperName))}");
        }


        {
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(10000d, 10000d, modelOptions);
            Case(
                "reject_square_non_iso",
                list.Count == 0,
                $"count={list.Count}");
        }


        {
            var options = PaperSizeDetector.CreateRectangleBatchOptions(1d, false, 3d, new[] { 143d });
            var list = PaperSizeDetector.DetectRectangleBatchCandidates(420d * 143d, 297d * 143d, options);
            var first = list.FirstOrDefault();
            Case(
                "custom_scale_143_A3_defaults",
                first != null
                && string.Equals(first.PaperName, "A3", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(first.ScaleValue - 143d) < 0.001d,
                first == null ? "empty" : $"{first.PaperName} {first.ScaleText}");
        }


        {
            var a = PaperSizeDetector.DetectRectangleBatchAspectRatioCandidates(420d * 143d, 297d * 143d);
            var b = PaperSizeDetector.DetectRectangleBatchAspectRatioCandidates(420d * 200d, 297d * 200d);
            var common = a.Select(x => x.PaperName)
                .Intersect(b.Select(x => x.PaperName), StringComparer.OrdinalIgnoreCase)
                .ToList();
            Case(
                "batch_aspect_common_names_include_A3",
                common.Any(x => string.Equals(x, "A3", StringComparison.OrdinalIgnoreCase))
                && common.Any(x => string.Equals(x, "A2", StringComparison.OrdinalIgnoreCase)),
                "common=" + string.Join(",", common));
        }
    }


    private static void RunDrawingScanCases(Action<string, bool, string> Case)
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            Case("drawing_scan_has_document", false, "no active document");
            return;
        }

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);


                const string layerName = "ZBP_VERIFY_RECT";
                EnsureLayer(tr, doc.Database, layerName);

                ClearLayerEntities(tr, ms, layerName);


                AddContentRectangle(tr, ms, layerName, 0, 0, 42000, 29700, "A3-100");
                AddContentRectangle(tr, ms, layerName, 50000, 0, 50000 + 59400, 42000, "A2-100");
                AddContentRectangle(tr, ms, layerName, 120000, 0, 130000, 10000, "NONISO");

                tr.Commit();
            }

            var results = RectangleFrameScanner.ScanScope(
                doc,
                TitleBlockScanScope.ModelSpace,
                paperMatchToleranceMm: 1d,
                recognizeFourLineRectangles: false);

            var papers = results.Select(r => r.Job.PaperName + "@" + r.Job.ScaleText).ToList();
            Case(
                "scan_finds_at_least_A3_and_A2",
                results.Any(r => string.Equals(r.Job.PaperName, "A3", StringComparison.OrdinalIgnoreCase))
                && results.Any(r => string.Equals(r.Job.PaperName, "A2", StringComparison.OrdinalIgnoreCase)),
                $"count={results.Count}; " + string.Join("; ", papers));

            Case(
                "scan_rejects_or_ignores_noniso_square",
                !results.Any(r =>
                {
                    var w = Math.Abs(r.Job.MaxX - r.Job.MinX);
                    var h = Math.Abs(r.Job.MaxY - r.Job.MinY);

                    return Math.Abs(w - 10000d) < 1d && Math.Abs(h - 10000d) < 1d;
                }),
                $"count={results.Count}; " + string.Join("; ", papers));

            Case(
                "scan_each_has_paper_options",
                results.Count > 0 && results.All(r => r.PaperOptions.Count > 0),
                results.Count == 0
                    ? "no results"
                    : string.Join("; ", results.Select(r => r.Job.PaperName + " opts=" + r.PaperOptions.Count)));
        }
        catch (System.Exception ex)
        {
            Case("drawing_scan_exception", false, ex.ToString());
        }
    }

    private static void EnsureLayer(Transaction tr, Database db, string layerName)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(layerName))
        {
            return;
        }

        lt.UpgradeOpen();
        var layer = new LayerTableRecord { Name = layerName };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    private static void ClearLayerEntities(Transaction tr, BlockTableRecord space, string layerName)
    {
        var erase = new List<ObjectId>();
        foreach (ObjectId id in space)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity
                && string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase))
            {
                erase.Add(id);
            }
        }

        foreach (var id in erase)
        {
            var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite, false);
            entity.Erase();
        }
    }


    private static void AddContentRectangle(
        Transaction tr,
        BlockTableRecord space,
        string layerName,
        double minX,
        double minY,
        double maxX,
        double maxY,
        string tag)
    {
        var pl = new Polyline();
        pl.SetDatabaseDefaults();
        pl.Layer = layerName;
        pl.Closed = true;
        pl.AddVertexAt(0, new Point2d(minX, minY), 0, 0, 0);
        pl.AddVertexAt(1, new Point2d(maxX, minY), 0, 0, 0);
        pl.AddVertexAt(2, new Point2d(maxX, maxY), 0, 0, 0);
        pl.AddVertexAt(3, new Point2d(minX, maxY), 0, 0, 0);
        space.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);

        var midX = (minX + maxX) * 0.5d;
        var midY = (minY + maxY) * 0.5d;
        var line = new Line(
            new Point3d(midX - 100d, midY, 0),
            new Point3d(midX + 100d, midY, 0));
        line.SetDatabaseDefaults();
        line.Layer = layerName;
        space.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);

        var text = new DBText();
        text.SetDatabaseDefaults();
        text.Layer = layerName;
        text.Position = new Point3d(midX, midY + 200d, 0);
        text.Height = 200d;
        text.TextString = tag;
        space.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
    }
}
