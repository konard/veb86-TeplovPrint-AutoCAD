using System;
using System.Globalization;
using System.IO;
using System.Text;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.Runtime;
using CadApp = HostMgd.ApplicationServices.Application;
#elif AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Runtime;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;


public sealed partial class BatchPlotCommands
{
    /// <summary>

    /// </summary>
    [CommandMethod("_ZBP_INTERNAL_PROFILE_RECTANGLE_SCAN")]
    public void ProfileRectangleScan()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        var report = new StringBuilder();
        report.AppendLine("ZBP Rectangle Scan Profile");
        report.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        report.AppendLine("Process: " + System.Diagnostics.Process.GetCurrentProcess().ProcessName);
        report.AppendLine("Drawing: " + (doc?.Database.Filename ?? doc?.Name ?? "(none)"));
        report.AppendLine();

        if (doc == null)
        {
            report.AppendLine("ERROR: no active document");
            WriteProfileReport(report);
            return;
        }

        try
        {
            // Pass 1: four-line ON (product default path)
            report.AppendLine("=== Pass A: RecognizeFourLineRectangleFrames=true, Scope=ModelSpace ===");
            report.AppendLine(RunProfilePass(doc, recognizeFourLines: true));
            report.AppendLine();

            // Pass 2: four-line OFF (contrast)
            report.AppendLine("=== Pass B: RecognizeFourLineRectangleFrames=false, Scope=ModelSpace ===");
            report.AppendLine(RunProfilePass(doc, recognizeFourLines: false));
            report.AppendLine();

            // Pass 3: four-line ON, all spaces (if layouts exist)
            report.AppendLine("=== Pass C: RecognizeFourLineRectangleFrames=true, Scope=AllSpaces ===");
            report.AppendLine(RunProfilePass(doc, recognizeFourLines: true, TitleBlockScanScope.AllSpaces));
        }
        catch (System.Exception ex)
        {
            report.AppendLine("ERROR: " + ex);
        }

        WriteProfileReport(report);
    }

    private static string RunProfilePass(
        Document doc,
        bool recognizeFourLines,
        TitleBlockScanScope scope = TitleBlockScanScope.ModelSpace)
    {
        RectangleFrameScanner.EnableProfiling = true;
        try
        {
            var results = RectangleFrameScanner.ScanScope(
                doc,
                scope,
                paperMatchToleranceMm: null,
                recognizeFourLineRectangles: recognizeFourLines);
            var profile = RectangleFrameScanner.LastProfile;
            if (profile == null)
            {
                return "no profile captured; results=" + results.Count;
            }

            return profile.FormatReport();
        }
        finally
        {
            RectangleFrameScanner.EnableProfiling = false;
        }
    }

    private static void WriteProfileReport(StringBuilder report)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "zbp-rectangle-scan-profile-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        File.WriteAllText(path, report.ToString(), Encoding.UTF8);
        Console.WriteLine("ZBP_RECT_PROFILE_REPORT=" + path);
        Console.WriteLine(report.ToString());
        try
        {
            CadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n[ZBP] profile: " + path + "\n");
        }
        catch
        {
        }
    }
}
