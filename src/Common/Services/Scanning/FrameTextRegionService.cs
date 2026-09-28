using System;
using System.Collections.Generic;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// Универсальные резервные области номера и наименования листа.
/// Профиль хранится в мм бумаги относительно правого нижнего угла рамки,
/// поэтому одна настройка работает при разных масштабах модели и форматах бумаги.
/// </summary>
public static class FrameTextRegionService
{
    public static string GetProfileKey(FrameSourceKind source, string? blockName)
    {
        return source switch
        {
            FrameSourceKind.Spds => "SPDS",
            FrameSourceKind.Rectangle => "RECTANGLE",
            FrameSourceKind.Library => "LIBRARY:" + (blockName ?? "").Trim(),
            _ => source.ToString().ToUpperInvariant()
        };
    }

    public static FrameTextRegionProfile? FindProfile(
        AppSettings settings,
        FrameSourceKind source,
        string? blockName)
    {
        if (settings?.FrameTextRegionProfiles == null)
            return null;

        var key = GetProfileKey(source, blockName);
        return settings.FrameTextRegionProfiles.FirstOrDefault(profile =>
            profile.Enabled
            && string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase)
            && profile.TitleRegionMm?.HasArea() == true
            && profile.DrawingNumberRegionMm?.HasArea() == true);
    }

    public static void UpsertProfile(AppSettings settings, FrameTextRegionProfile profile)
    {
        settings.FrameTextRegionProfiles ??= new List<FrameTextRegionProfile>();
        var existing = settings.FrameTextRegionProfiles.FirstOrDefault(item =>
            string.Equals(item.Key, profile.Key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            settings.FrameTextRegionProfiles.Add(profile);
            return;
        }

        existing.Enabled = profile.Enabled;
        existing.TitleRegionMm = profile.TitleRegionMm;
        existing.DrawingNumberRegionMm = profile.DrawingNumberRegionMm;
    }

    public static bool RemoveProfile(AppSettings settings, FrameSourceKind source, string? blockName)
    {
        if (settings.FrameTextRegionProfiles == null)
            return false;

        var key = GetProfileKey(source, blockName);
        return settings.FrameTextRegionProfiles.RemoveAll(profile =>
            string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public static bool TryCreateProfile(
        PlotJob job,
        LocalRectangle frameWorld,
        LocalRectangle titleWorld,
        LocalRectangle numberWorld,
        out FrameTextRegionProfile profile,
        out string error)
    {
        profile = new FrameTextRegionProfile();
        error = "";

        if (!frameWorld.HasArea())
        {
            error = "Не удалось определить границы выбранной рамки.";
            return false;
        }

        if (job.PaperWidthMm <= 0 || job.PaperHeightMm <= 0)
        {
            error = "Для выбранной рамки не определён физический размер бумаги.";
            return false;
        }

        if (!titleWorld.HasArea() || !numberWorld.HasArea())
        {
            error = "Области номера и наименования должны иметь ненулевой размер.";
            return false;
        }

        var sx = (frameWorld.MaxX - frameWorld.MinX) / job.PaperWidthMm;
        var sy = (frameWorld.MaxY - frameWorld.MinY) / job.PaperHeightMm;
        if (Math.Abs(sx) <= 1e-9 || Math.Abs(sy) <= 1e-9)
        {
            error = "Не удалось вычислить масштаб рамки.";
            return false;
        }

        profile = new FrameTextRegionProfile
        {
            Key = GetProfileKey(job.FrameSource, job.BlockName),
            Enabled = true,
            TitleRegionMm = ToPaperRelative(titleWorld, frameWorld, sx, sy),
            DrawingNumberRegionMm = ToPaperRelative(numberWorld, frameWorld, sx, sy)
        };
        return true;
    }

    public static bool TryRead(
        Transaction tr,
        BlockTableRecord owner,
        LocalRectangle frameWorld,
        double paperWidthMm,
        double paperHeightMm,
        FrameTextRegionProfile? profile,
        CadTextExtractor.OwnerTextCache? ownerTextCache,
        out string drawingNumber,
        out string title)
    {
        drawingNumber = "";
        title = "";
        if (profile == null || !profile.Enabled || !frameWorld.HasArea())
            return false;
        if (paperWidthMm <= 0 || paperHeightMm <= 0)
            return false;

        var sx = (frameWorld.MaxX - frameWorld.MinX) / paperWidthMm;
        var sy = (frameWorld.MaxY - frameWorld.MinY) / paperHeightMm;
        if (Math.Abs(sx) <= 1e-9 || Math.Abs(sy) <= 1e-9)
            return false;

        var titleRegion = ToWorld(profile.TitleRegionMm, frameWorld, sx, sy);
        var numberRegion = ToWorld(profile.DrawingNumberRegionMm, frameWorld, sx, sy);

        if (titleRegion.HasArea())
            title = CadTextExtractor.ExtractWorldRegionText(tr, owner, titleRegion, ownerTextCache).Trim();
        if (numberRegion.HasArea())
            drawingNumber = CadTextExtractor.ExtractWorldRegionText(tr, owner, numberRegion, ownerTextCache).Trim();

        return !string.IsNullOrWhiteSpace(drawingNumber) || !string.IsNullOrWhiteSpace(title);
    }

    public static LocalRectangle FromJobWorldBounds(PlotJob job)
    {
        if (job.CornerPoints is { Length: >= 8 } points)
        {
            var xs = new[] { points[0], points[2], points[4], points[6] };
            var ys = new[] { points[1], points[3], points[5], points[7] };
            return LocalRectangle.FromPoints(xs.Min(), ys.Min(), xs.Max(), ys.Max());
        }

        return LocalRectangle.FromPoints(job.MinX, job.MinY, job.MaxX, job.MaxY);
    }

    private static LocalRectangle ToPaperRelative(
        LocalRectangle worldRegion,
        LocalRectangle frameWorld,
        double sx,
        double sy)
    {
        return LocalRectangle.FromPoints(
            (worldRegion.MinX - frameWorld.MaxX) / sx,
            (worldRegion.MinY - frameWorld.MinY) / sy,
            (worldRegion.MaxX - frameWorld.MaxX) / sx,
            (worldRegion.MaxY - frameWorld.MinY) / sy);
    }

    private static LocalRectangle ToWorld(
        LocalRectangle paperRegion,
        LocalRectangle frameWorld,
        double sx,
        double sy)
    {
        return LocalRectangle.FromPoints(
            frameWorld.MaxX + paperRegion.MinX * sx,
            frameWorld.MinY + paperRegion.MinY * sy,
            frameWorld.MaxX + paperRegion.MaxX * sx,
            frameWorld.MinY + paperRegion.MaxY * sy);
    }
}
