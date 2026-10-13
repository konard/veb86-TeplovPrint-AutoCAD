using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
#if NANOCAD
using Teigha.DatabaseServices;
using HostMgd.PlottingServices;
using CadApp = HostMgd.ApplicationServices.Application;
#elif AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.PlottingServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
internal static class PlotStyleManager
{
    public static IReadOnlyList<string> GetAvailableCtbStyles()
    {
        return PlotSettingsValidator.Current.GetPlotStyleSheetList()
            .Cast<object>()
            .Select(value => value?.ToString() ?? "")
            .Where(value => value.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>

    /// </summary>
    public static string NormalizeStyleName(string? styleSheet)
    {
        var name = (styleSheet ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }

        try
        {
            name = Path.GetFileName(name);
        }
        catch
        {

        }

        return name.Trim();
    }

    /// <summary>

    /// </summary>
    public static string ResolveJobStyle(PlotJob? job, string? fallback)
    {
        if (job != null && !string.IsNullOrWhiteSpace(job.StyleSheet))
        {
            return NormalizeStyleName(job.StyleSheet);
        }

        return NormalizeStyleName(fallback);
    }


    /// <summary>

    /// </summary>
    public static bool StyleNamesEqual(string? left, string? right)
    {
        var a = NormalizeStyleName(left);
        var b = NormalizeStyleName(right);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Path.GetFileNameWithoutExtension(a),
            Path.GetFileNameWithoutExtension(b),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>

    /// </summary>
    public static string? FindSavedStyle(IEnumerable<string> styles, string? savedStyleSheet)
    {
        var saved = NormalizeStyleName(savedStyleSheet);
        if (string.IsNullOrEmpty(saved))
        {
            return null;
        }

        return styles.FirstOrDefault(value => StyleNamesEqual(value, saved));
    }

    /// <summary>


    /// </summary>
    public static string ResolvePreferredStyle(IEnumerable<string> styles, string? savedStyleSheet)
    {
        var list = styles as IList<string> ?? styles.ToList();
        var matched = FindSavedStyle(list, savedStyleSheet);
        if (!string.IsNullOrEmpty(matched))
        {
            return matched!;
        }

        var monochrome = list.FirstOrDefault(value =>
            value.IndexOf("monochrome", StringComparison.OrdinalIgnoreCase) >= 0);
        if (!string.IsNullOrEmpty(monochrome))
        {
            return monochrome!;
        }

        return list.FirstOrDefault() ?? "";
    }

    /// <summary>

    /// </summary>

    public static string RestoreSavedStyle(ComboBox combo, string? savedStyleSheet)
    {
        var available = combo.Items
            .Cast<object>()
            .Select(item => item?.ToString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var preferred = ResolvePreferredStyle(available, savedStyleSheet);
        if (string.IsNullOrEmpty(preferred))
        {
            return "";
        }

        if (TrySelectStyle(combo, preferred))
        {
            return combo.SelectedItem?.ToString() ?? preferred;
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
            return combo.SelectedItem?.ToString() ?? "";
        }

        return "";
    }

    private static bool TrySelectStyle(ComboBox combo, string saved)
    {
        if (string.IsNullOrEmpty(saved))
        {
            return false;
        }

        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (StyleNamesEqual(combo.Items[i]?.ToString(), saved))
            {
                combo.SelectedIndex = i;
                return true;
            }
        }

        return false;
    }

    public static void EditSelectedStyle(Window owner, string? styleSheet)
    {
        var selectedStyle = styleSheet ?? "";
        if (string.IsNullOrWhiteSpace(selectedStyle))
        {
            MessageBox.Show(
                owner,
                "Сначала выберите стиль печати.",
                "Настройка стиля печати",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var stylePath = ResolveStylePath(selectedStyle) ?? "";
        if (!string.IsNullOrWhiteSpace(stylePath) && TryOpenStyleFile(stylePath))
        {
            return;
        }


        if (TryOpenCadPlotStyleManager())
        {
            var hint = string.IsNullOrWhiteSpace(stylePath)
                ? $"Стиль «{selectedStyle}» не найден в путях поиска текущего CAD. Открыт диспетчер стилей печати; дважды щёлкните файл для изменения."
                : $"Не удалось запустить редактор таблицы стилей. Открыт диспетчер стилей печати; дважды щёлкните «{selectedStyle}».";
            MessageBox.Show(
                owner,
                hint,
                "Настройка стиля печати",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(
            owner,
            $"Не удалось открыть стиль печати «{selectedStyle}». Выполните STYLESMANAGER в CAD и проверьте пути поиска таблиц стилей.",
            "Настройка стиля печати",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    
    private static string? ResolveStylePath(string styleSheet)
    {
        var raw = (styleSheet ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            if (Path.IsPathRooted(raw) && File.Exists(raw))
            {
                return Path.GetFullPath(raw);
            }
        }
        catch
        {

        }

        var fileName = NormalizeStyleName(raw);
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var fromPlotConfig = TryResolveStylePathFromPlotConfig(fileName);
        if (!string.IsNullOrWhiteSpace(fromPlotConfig))
        {
            return fromPlotConfig;
        }

        foreach (var directory in AcadPlotterInstaller.GetPlotStyleSearchDirectories())
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {

            }
        }


        return TryFindStyleFileWithCad(fileName);
    }

    
    private static string? TryResolveStylePathFromPlotConfig(string fileName)
    {
        try
        {
            PlotConfigManager.RefreshList(RefreshCode.RefreshStyleList);
        }
        catch
        {

        }

        try
        {
            var styles = fileName.EndsWith(".stb", StringComparison.OrdinalIgnoreCase)
                ? PlotConfigManager.NamedPlotStyles
                : PlotConfigManager.ColorDependentPlotStyles;


            foreach (var entry in (IEnumerable)styles)
            {
                if (entry is string text)
                {
                    var resolved = MatchExistingStyleFile(text, fileName);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved;
                    }

                    continue;
                }

                if (entry is PlotConfigInfo info)
                {
                    string? resolved = null;
                    try
                    {
                        resolved = MatchExistingStyleFile(info.FullPath, fileName)
                            ?? MatchExistingStyleFile(info.DeviceName, fileName);
                    }
                    catch
                    {

                    }

                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved;
                    }
                }
            }
        }
        catch
        {

        }

        return null;
    }

    
    private static string? MatchExistingStyleFile(string? entry, string fileName)
    {
        if (string.IsNullOrWhiteSpace(entry) || !StyleNamesEqual(entry, fileName))
        {
            return null;
        }

        try
        {
            if (!Path.IsPathRooted(entry) || !File.Exists(entry))
            {
                return null;
            }

            return Path.GetFullPath(entry);
        }
        catch
        {
            return null;
        }
    }

    
    private static string? TryFindStyleFileWithCad(string fileName)
    {
        var document = CadApp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            return null;
        }

        try
        {
            var resolved = HostApplicationServices.Current.FindFile(
                fileName,
                document.Database,
                FindFileHint.Default);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                return Path.GetFullPath(resolved);
            }
        }
        catch
        {

        }

        return null;
    }

    
    private static bool TryOpenStyleFile(string stylePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = stylePath,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return TryRevealStyleFile(stylePath);
        }
    }

    
    private static bool TryOpenCadPlotStyleManager()
    {
        var document = CadApp.DocumentManager.MdiActiveDocument;
        if (document != null)
        {
            try
            {
                document.SendStringToExecute("_.STYLESMANAGER ", true, false, false);
                return true;
            }
            catch
            {
            }
        }

        var firstDirectory = AcadPlotterInstaller.GetPlotStyleSearchDirectories().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstDirectory) || !Directory.Exists(firstDirectory))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = firstDirectory,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRevealStyleFile(string stylePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{Path.GetFullPath(stylePath)}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
