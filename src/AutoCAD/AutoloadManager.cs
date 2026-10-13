using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using Microsoft.Win32;
#if NANOCAD
using Teigha.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

/// <summary>




/// </summary>
public static class AutoloadManager
{

    private const string AppKeyName = "AcadBatchPlot";

    private const string AppDescription = "Плагин пакетной печати AutoCAD";
#if ACAD_CORE

    private const string AcadRoot = @"Software\Autodesk\AutoCAD";

    private const string BundleName = "AcadBatchPlot.bundle";
#endif


    public static string CurrentDllPath => Assembly.GetExecutingAssembly().Location;

    /// <summary>

    /// </summary>


    public static IReadOnlyList<string> Install(string? dllPath = null)
    {
        dllPath = string.IsNullOrWhiteSpace(dllPath) ? CurrentDllPath : Path.GetFullPath(dllPath);
        var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: true)
            ?? throw new InvalidOperationException("Не найден раздел реестра автозагрузки текущего AutoCAD. Сначала один раз запустите AutoCAD.");

        if (!WriteAutoloadKey(applicationsRoot, dllPath))
        {
            throw new InvalidOperationException("Не удалось записать параметр автозагрузки текущего AutoCAD.");
        }

        return new[] { applicationsRoot };
    }

    /// <summary>

    /// </summary>

    public static int Uninstall()
    {
        var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: false);
        if (applicationsRoot == null)
        {
            return 0;
        }

        using var parent = Registry.CurrentUser.OpenSubKey(applicationsRoot, writable: true);
        if (parent == null)
        {
            return 0;
        }

        try
        {
            parent.DeleteSubKeyTree(AppKeyName, throwOnMissingSubKey: false);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>

    /// </summary>


    public static bool IsInstalled(out string dllPath)
    {
        dllPath = "";
        var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: false);
        if (applicationsRoot == null)
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(applicationsRoot + "\\" + AppKeyName);
        var loader = key?.GetValue("LOADER")?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(loader))
        {
            return false;
        }

        dllPath = loader;
        return true;
    }

#if ACAD_CORE
    /// <summary>


    /// </summary>
    private static string InstallCoreBundle(string? dllPath)
    {
        dllPath = string.IsNullOrWhiteSpace(dllPath) ? CurrentDllPath : Path.GetFullPath(dllPath);
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("DLL плагина отсутствует; установить автозагрузку невозможно.", dllPath);
        }


        RemoveRegistryAutoloadEntries();

        var sourceDir = Path.GetDirectoryName(dllPath)
            ?? throw new InvalidOperationException("Не удалось определить папку плагина; установить автозагрузку невозможно.");

        var bundlePath = GetBundlePath();

        var installFolderName = BuildInstallFolderName();
        var contentsPath = Path.Combine(bundlePath, "Contents", installFolderName);
        Directory.CreateDirectory(contentsPath);


        foreach (var pattern in new[] { "*.dll", "*.json", "*.config" })
        {
            foreach (var file in Directory.GetFiles(sourceDir, pattern, SearchOption.TopDirectoryOnly))
            {
                File.Copy(file, Path.Combine(contentsPath, Path.GetFileName(file)), overwrite: true);
            }
        }


        CopyDirectoryIfExists(Path.Combine(sourceDir, "runtimes"), Path.Combine(contentsPath, "runtimes"));
        CopyDirectoryIfExists(Path.Combine(sourceDir, "Plotters"), Path.Combine(contentsPath, "Plotters"));


        File.WriteAllText(Path.Combine(bundlePath, "PackageContents.xml"), BuildPackageContents(Path.GetFileName(dllPath), installFolderName));
        File.WriteAllText(Path.Combine(contentsPath, "AcadBatchPlot.mnu"), BuildMenuFile());
        return bundlePath;
    }

    /// <summary>

    /// </summary>
    private static string GetBundlePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Autodesk", "ApplicationPlugins", BundleName);
    }

    /// <summary>


    /// </summary>
    private static int DisableAndDeleteCoreBundle(string bundlePath)
    {
        var removed = 0;
        var packageContents = Path.Combine(bundlePath, "PackageContents.xml");
        if (File.Exists(packageContents))
        {
            TryDeleteFile(packageContents);
            if (!File.Exists(packageContents))
            {
                removed++;
            }
        }

        TryDeleteFile(Path.Combine(bundlePath, "Contents", "AcadBatchPlot.mnu"));
        TryDeleteDirectory(bundlePath);

        if (!Directory.Exists(bundlePath))
        {
            return Math.Max(removed, 1);
        }

        return removed;
    }

    /// <summary>

    /// </summary>
    private static string BuildInstallFolderName()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        return "v" + version + "-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
    }

    /// <summary>


    /// </summary>
    private static string BuildPackageContents(string dllName, string installFolderName)
    {
        var escapedInstallFolderName = SecurityElement.Escape(installFolderName);
        var version = SecurityElement.Escape(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<ApplicationPackage SchemaVersion=""1.0"" AutodeskProduct=""AutoCAD"" Name=""Teplov Print"" AppVersion=""{version}"" ProductCode=""{{7f2f2f2d-78d1-4df0-8c5d-acadba7c0011}}"" Description=""Плагин пакетной печати для AutoCAD"">
  <CompanyDetails Name=""lihao"" />
  <Components>
    <ComponentEntry AppName=""AcadBatchPlot"" AppType="".Net"" ModuleName=""./Contents/{escapedInstallFolderName}/{SecurityElement.Escape(dllName)}"" LoadOnAutoCADStartup=""True"" LoadOnCommandInvocation=""True"">
      <RuntimeRequirements OS=""Win64"" Platform=""AutoCAD*"" SeriesMin=""R24.1"" SeriesMax=""R24.1"" />
      <Commands GroupName=""AcadBatchPlot"">
        <Command Global=""SPDSPRINT"" Local=""SPDSPRINT"" />
        <Command Global=""SPDS_PRINT"" Local=""SPDS_PRINT"" />
        <Command Global=""ZBP_SHOW_PANEL"" Local=""ZBP_SHOW_PANEL"" />
        <Command Global=""ZBP_RECTANGLE_BATCH_PLOT"" Local=""ZBP_RECTANGLE_BATCH_PLOT"" />
        <Command Global=""ZBP_SINGLE_PLOT"" Local=""ZBP_SINGLE_PLOT"" />
        <Command Global=""ZBP_ADD_TITLE_BLOCK"" Local=""ZBP_ADD_TITLE_BLOCK"" />
        <Command Global=""ZBP_MANAGE_LIBRARY"" Local=""ZBP_MANAGE_LIBRARY"" />
        <Command Global=""ZBP_SETTINGS"" Local=""ZBP_SETTINGS"" />
        <Command Global=""ZBP_OPEN_CONFIG"" Local=""ZBP_OPEN_CONFIG"" />
        <Command Global=""ZBP_RELOAD_MENU"" Local=""ZBP_RELOAD_MENU"" />
      </Commands>
    </ComponentEntry>
    <ComponentEntry AppName=""AcadBatchPlotMenu"" AppType=""Mnu"" ModuleName=""./Contents/{escapedInstallFolderName}/AcadBatchPlot.mnu"">
      <RuntimeRequirements OS=""Win64"" Platform=""AutoCAD*"" SeriesMin=""R24.1"" SeriesMax=""R24.1"" />
    </ComponentEntry>
  </Components>
</ApplicationPackage>
";
    }

    /// <summary>

    /// </summary>
    private static string BuildMenuFile()
    {
        return @"***MENUGROUP=ACADBATCHPLOT
***POP1
**LA_BATCH_PLOT
ID_LA_BATCH_PLOT [Teplov]
ID_ZBP_SHOW_PANEL [Пакетная печать]ZBP_SHOW_PANEL
ID_ZBP_ADD_TITLE_BLOCK [Добавить формат]ZBP_ADD_TITLE_BLOCK
ID_ZBP_MANAGE_LIBRARY [Библиотека форматов]ZBP_MANAGE_LIBRARY
[--]
ID_ZBP_SETTINGS [Настройки]ZBP_SETTINGS
ID_ZBP_OPEN_CONFIG [Открыть папку настроек]ZBP_OPEN_CONFIG
ID_ZBP_RELOAD_MENU [Обновить меню]ZBP_RELOAD_MENU
";
    }


    private static void CopyDirectoryIfExists(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = GetRelativePath(sourceDir, file);
            var targetPath = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
    }


    private static string GetRelativePath(string basePath, string path)
    {
        var baseUri = new Uri(AppendDirectorySeparatorChar(Path.GetFullPath(basePath)));
        var pathUri = new Uri(Path.GetFullPath(path));
        return Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString())
            .Replace('/', Path.DirectorySeparatorChar);
    }


    private static string AppendDirectorySeparatorChar(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    /// <summary>


    /// </summary>
    private static int RemoveRegistryAutoloadEntries()
    {
        var removed = 0;
        using var root = Registry.CurrentUser.OpenSubKey(AcadRoot);
        if (root == null)
        {
            return removed;
        }

        foreach (var version in root.GetSubKeyNames())
        {
            using var versionKey = root.OpenSubKey(version);
            if (versionKey == null)
            {
                continue;
            }

            foreach (var profile in versionKey.GetSubKeyNames())
            {
                var applicationsPath = AcadRoot + "\\" + version + "\\" + profile + "\\Applications";
                using var applications = Registry.CurrentUser.OpenSubKey(applicationsPath, writable: true);
                if (applications == null)
                {
                    continue;
                }

                try
                {
                    applications.DeleteSubKeyTree(AppKeyName, throwOnMissingSubKey: false);
                    removed++;
                }
                catch
                {
                }
            }
        }

        return removed;
    }


    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            TryDeleteFile(file);
        }

        foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories)
                     .OrderByDescending(x => x.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch
            {
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch
        {
        }
    }


    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
#endif

    /// <summary>

    /// </summary>


    private static string? GetCurrentCadApplicationsRoot(bool createIfMissing)
    {
        var productRoot = HostApplicationServices.Current?.UserRegistryProductRootKey;
        if (string.IsNullOrWhiteSpace(productRoot))
        {
            return null;
        }

        productRoot = NormalizeHkcuRelativePath(productRoot!);
        if (string.IsNullOrWhiteSpace(productRoot))
        {
            return null;
        }

        var applicationsPath = productRoot + "\\Applications";
        if (createIfMissing)
        {
            using var created = Registry.CurrentUser.CreateSubKey(applicationsPath);
            return created != null ? applicationsPath : null;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(applicationsPath);
        return existing != null ? applicationsPath : null;
    }

    /// <summary>

    /// </summary>



    private static bool WriteAutoloadKey(string applicationsRoot, string dllPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(applicationsRoot + "\\" + AppKeyName);
        if (key == null)
        {
            return false;
        }

        key.SetValue("DESCRIPTION", AppDescription, RegistryValueKind.String);
        key.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
        key.SetValue("LOADER", dllPath, RegistryValueKind.String);
        key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
        return true;
    }

    /// <summary>

    /// </summary>


    private static string NormalizeHkcuRelativePath(string path)
    {
        var normalized = path.Replace('/', '\\').Trim('\\');
        const string hkcuPrefix = @"HKEY_CURRENT_USER\";
        if (normalized.StartsWith(hkcuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(hkcuPrefix.Length).Trim('\\');
        }

        return normalized;
    }
}
