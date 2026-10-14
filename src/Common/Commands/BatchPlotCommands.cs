using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

public sealed partial class BatchPlotCommands : IExtensionApplication
{
    private static BatchPlotForm? _batchPlotForm;
    private static BatchPlotForm? _spdsBatchPlotForm;
    private static RectangleBatchPlotForm? _rectangleBatchPlotForm;


    static BatchPlotCommands()
    {
        LoadedAssemblyResolver.Register();
    }



    public void Initialize()
    {
        TraceLog.Write("Initialize: " + typeof(BatchPlotCommands).Assembly.Location);
        LoadedAssemblyResolver.Register();

        if (IsCoreConsole())
        {
            return;
        }

#if ACAD_CORE || NANOCAD
        PluginAssemblyResolver.Register();
#endif

#if NANOCAD
        // nanoCAD: plotter and menu integration is AutoCAD-specific, it must not block loading the commands.
        try
        {
            InitializeCadIntegration();
        }
        catch (System.Exception ex)
        {
            CadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                "\nTeplov Print: не удалось настроить плоттеры или меню: " + ex.Message);
        }
#else
        InitializeCadIntegration();
#endif
    }

    private static void InitializeCadIntegration()
    {
        var pdfInstall = AcadPlotterInstaller.InstallBundledPlotter();

        var pngInstall = AcadPlotterInstaller.InstallPngPlotter();
        var jpgInstall = AcadPlotterInstaller.InstallJpgPlotter();
        AcadPlotterInstaller.RefreshPlotterDevicesIfNeeded(
            pdfInstall.Written || pngInstall.Written || jpgInstall.Written);
        CadMenuInstaller.Install();

        CommandAliasManager.Apply(AppSettingsStore.Load().CommandAliases, out _);
    }

    public void Terminate()
    {
    }

    private static bool IsCoreConsole()
    {
        try
        {
            var processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            return string.Equals(processName, "accoreconsole", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }



    [CommandMethod("ZBP_ADD_TITLE_BLOCK")]
    public void AddTitleBlock() => AddTitleBlockCore();

    [CommandMethod("_ZBP_INTERNAL_ADD_TITLE_BLOCK")]
    public void AddTitleBlockLegacy() => AddTitleBlockCore();

    [CommandMethod("ZBP_SHOW_PANEL", CommandFlags.Session)]
    public void ShowBatchPlotWindow() => ShowBatchPlotWindowCore();

    [CommandMethod("_ZBP_INTERNAL_SHOW_PANEL", CommandFlags.Session)]
    public void ShowBatchPlotWindowLegacy() => ShowBatchPlotWindowCore();

    [CommandMethod("SPDSPRINT", CommandFlags.Session)]
    public void ShowSpdsBatchPlotWindow() => ShowSpdsBatchPlotWindowCore();

    [CommandMethod("SPDS_PRINT", CommandFlags.Session)]
    public void ShowSpdsBatchPlotWindowAlias() => ShowSpdsBatchPlotWindowCore();

    [CommandMethod("ZBP_SINGLE_PLOT", CommandFlags.Session)]
    public void SinglePlot() => SinglePlotCore();

    [CommandMethod("_ZBP_INTERNAL_SINGLE_PLOT", CommandFlags.Session)]
    public void SinglePlotLegacy() => SinglePlotCore();

    [CommandMethod("ZBP_RECTANGLE_BATCH_PLOT", CommandFlags.Session)]
    public void RectangleBatchPlot() => ShowRectangleBatchPlotCore();

    [CommandMethod("_ZBP_INTERNAL_RECTANGLE_BATCH_PLOT", CommandFlags.Session)]
    public void RectangleBatchPlotLegacy() => ShowRectangleBatchPlotCore();

    
    [CommandMethod("_ZBP_INTERNAL_PREVIEW", CommandFlags.NoHistory)]
    public void InternalPreview()
    {
        var request = PendingPlotPreview.Take();
        if (request == null)
        {
            return;
        }

        try
        {
            PlotterService.Preview(request.Job, request.DeviceName, request.StyleSheet, request.Document);
        }
        catch (System.Exception ex)
        {
            request.OnError?.Invoke(ex);
        }
        finally
        {
            request.OnFinally?.Invoke();
        }
    }

    [CommandMethod("ZBP_OPEN_CONFIG")]
    public void OpenConfigDirectory()
    {
        Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
        System.Diagnostics.Process.Start(TitleBlockLibraryStore.DefaultDirectory);
    }

    [CommandMethod("_ZBP_INTERNAL_OPEN_CONFIG")]
    public void OpenConfigDirectoryLegacy() => OpenConfigDirectory();

    [CommandMethod("ZBP_MANAGE_LIBRARY", CommandFlags.Session)]
    public void ManageLibrary()
    {
        var form = new TitleBlockLibraryManagerForm();
        CadDialog.ShowModal(form);
    }

    [CommandMethod("_ZBP_INTERNAL_MANAGE_LIBRARY", CommandFlags.Session)]
    public void ManageLibraryLegacy() => ManageLibrary();

    [CommandMethod("ZBP_SETTINGS", CommandFlags.Session)]
    public void ShowSettings()
    {
        while (true)
        {
            var form = new SettingsForm();
            if (CadDialog.ShowModal(form) != true)
            {
                return;
            }

            if (!form.RequestPickDirectoryRowHeight
                && !form.RequestPickDirectoryTextAppearance
                && !form.RequestPickScaleFromCad
                && string.IsNullOrWhiteSpace(form.RequestedDirectoryColumnKey))
            {
                return;
            }


            var doc = CadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show(
                    "Нет доступного чертежа CAD. Откройте чертёж и повторите попытку.",
                    "Настройки пакетной печати",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                continue;
            }

            var settings = AppSettingsStore.Load();
            bool ok;
            string message;
            if (form.RequestPickScaleFromCad)
            {
                ok = ScaleSettingsPicker.PromptScaleFromFrame(doc, settings, out settings, out message);
            }
            else if (form.RequestPickDirectoryTextAppearance)
            {
                ok = DirectoryTableGenerator.PromptTextAppearance(doc, settings, out _, out message);
            }
            else if (form.RequestPickDirectoryRowHeight)
            {
                ok = DirectoryTableGenerator.PromptRowHeight(doc, settings, out _, out message);
            }
            else
            {
                ok = DirectoryTableGenerator.PromptColumnSize(
                    doc,
                    settings,
                    form.RequestedDirectoryColumnKey ?? "",
                    out _,
                    out message);
            }


            if (!ok)
            {
                MessageBox.Show(message, "Настройки пакетной печати", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

        }
    }

    [CommandMethod("_ZBP_INTERNAL_SETTINGS", CommandFlags.Session)]
    public void ShowSettingsLegacy() => ShowSettings();

    [CommandMethod("ZBP_SHORTCUT_SETTINGS", CommandFlags.Session)]
    public void ShortcutSettings() => ShortcutSettingsCore();

    [CommandMethod("_ZBP_INTERNAL_SHORTCUT_SETTINGS", CommandFlags.Session)]
    public void ShortcutSettingsLegacy() => ShortcutSettingsCore();


    [CommandMethod("ZBP_ABOUT")]
    public void About()
    {
        var dialog = new AboutDialog();
        CadDialog.ShowModal(dialog);
    }

    [CommandMethod("_ZBP_INTERNAL_ABOUT")]
    public void AboutLegacy() => About();

    [CommandMethod("ZBP_INSTALL_AUTOLOAD")]
    public void InstallAutoload()
    {
        try
        {
            var roots = AutoloadManager.Install();
            MessageBox.Show(
                "Автозагрузка установлена.\n\nОна действует только для текущей версии CAD. При следующем запуске плагин пакетной печати загрузится автоматически.\n\nРасположение:\n" + string.Join("\n", roots),
                "Пакетная печать",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("Не удалось установить автозагрузку: " + ex.Message, "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [CommandMethod("_ZBP_INTERNAL_INSTALL_AUTOLOAD")]
    public void InstallAutoloadLegacy() => InstallAutoload();

    [CommandMethod("ZBP_UNINSTALL_AUTOLOAD")]
    public void UninstallAutoload()
    {
        try
        {
            var removed = AutoloadManager.Uninstall();
            MessageBox.Show(
                removed > 0
                    ? "Автозагрузка удалена для текущей версии CAD. Уже загруженный плагин останется доступен до закрытия CAD."
                    : "Установленная автозагрузка не найдена.",
                "Пакетная печать",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("Не удалось удалить автозагрузку: " + ex.Message, "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [CommandMethod("_ZBP_INTERNAL_UNINSTALL_AUTOLOAD")]
    public void UninstallAutoloadLegacy() => UninstallAutoload();


    //   AddTitleBlockCore     → AddTitleBlockCommands.cs
    //   SinglePlotCore        → SinglePlotCommands.cs
    //   TransformPlotWindow   → CoordinateUtils.cs
    //   BuildWcsToDcsMatrix   → CoordinateUtils.cs
    //   BuildUcsToDcsMatrix   → CoordinateUtils.cs



    private static void ShortcutSettingsCore()
    {
        var settings = AppSettingsStore.Load();
        var form = new ShortcutSettingsDialog(settings.CommandAliases);
        if (CadDialog.ShowModal(form) != true)
        {
            return;
        }

        settings.CommandAliases = CommandAliasManager.NormalizeAliases(
            form.Aliases.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
        AppSettingsStore.Save(settings);

        var applied = CommandAliasManager.Apply(settings.CommandAliases, out var message);
        MessageBox.Show(
            message,
            "Настройки команд",
            MessageBoxButton.OK,
            applied ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }



    private static void ShowBatchPlotWindowCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        if (_batchPlotForm is { IsLoaded: true })
        {
            _batchPlotForm.Activate();
            return;
        }

        var form = new BatchPlotForm(doc);
        _batchPlotForm = form;
        form.Closed += (_, _) =>
        {
            if (ReferenceEquals(_batchPlotForm, form))
            {
                _batchPlotForm = null;
            }
        };

        ShowModelessDialog(form);
    }

    // ---- CSoft СПДС: только интеллектуальные рамки SPDSFORMAT ----

    private static void ShowSpdsBatchPlotWindowCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        if (_spdsBatchPlotForm is { IsLoaded: true })
        {
            _spdsBatchPlotForm.Activate();
            return;
        }

        var form = new BatchPlotForm(doc, spdsMode: true);
        _spdsBatchPlotForm = form;
        form.Loaded += (_, _) => form.ScanSpdsOnOpen();
        form.Closed += (_, _) =>
        {
            if (ReferenceEquals(_spdsBatchPlotForm, form))
            {
                _spdsBatchPlotForm = null;
            }
        };

        ShowModelessDialog(form);
    }



    private static void ShowRectangleBatchPlotCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        if (_rectangleBatchPlotForm is { IsLoaded: true })
        {
            _rectangleBatchPlotForm.Activate();
            return;
        }

        var form = new RectangleBatchPlotForm(doc);
        _rectangleBatchPlotForm = form;
        form.Closed += (_, _) =>
        {
            if (ReferenceEquals(_rectangleBatchPlotForm, form))
            {
                _rectangleBatchPlotForm = null;
            }
        };
        ShowModelessDialog(form);
    }



    private static void RevealFileInExplorer(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{Path.GetFullPath(filePath)}\"",
                UseShellExecute = true
            });
        }
        catch
        {

        }
    }

    private static System.Windows.Forms.DialogResult ShowModalDialog(System.Windows.Forms.Form form)
    {
#if ACAD_CORE
        var result = form.ShowDialog();
#else
        var result = CadApp.ShowModalDialog(form);
#endif

        CadWindowFocus.ActivateCadWindow();
        return result;
    }

    /// <summary>

    /// </summary>
    private static System.Windows.Forms.DialogResult ShowModelessDialogAndWait(System.Windows.Forms.Form form)
    {
        form.TopMost = true;
#if ACAD_CORE
        form.Show();
#else
        CadApp.ShowModelessDialog(form);
#endif
        while (form.Visible)
        {
            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(15);
        }

        CadWindowFocus.ActivateCadWindow();
        return form.DialogResult;
    }

    private static bool? ShowModalDialog(Window window)
    {
        return CadDialog.ShowModal(window);
    }

    private static void ShowModelessDialog(Window window)
    {
        CadDialog.ShowModeless(window);
    }

    /// <summary>



    /// </summary>
    internal static TitleBlockScanScope? PromptScanScope(Window? owner = null)
    {
        TitleBlockScanScope? chosen = null;
        var dialog = new Window
        {
            Title = "Сканирование текущего чертежа",
            Width = 360,
            MinWidth = 340,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.ToolWindow,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 11
        };

        var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };

        void AddScopeButton(string content, TitleBlockScanScope scope)
        {
            var button = new Button
            {
                Content = content,
                MinHeight = 28,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 4, 12, 4)
            };
            try
            {
                if (System.Windows.Application.Current?.TryFindResource("PluginButtonStyle") is Style style)
                {
                    button.Style = style;
                }
            }
            catch
            {
            }

            button.Click += (_, _) =>
            {
                chosen = scope;
                dialog.DialogResult = true;
                dialog.Close();
            };
            panel.Children.Add(button);
        }

        AddScopeButton("Сканировать модель и все листы", TitleBlockScanScope.AllSpaces);
        AddScopeButton("Сканировать все листы", TitleBlockScanScope.PaperLayouts);
        AddScopeButton("Сканировать текущее пространство", TitleBlockScanScope.CurrentSpace);
        AddScopeButton("Сканировать пространство модели", TitleBlockScanScope.ModelSpace);


        if (panel.Children[panel.Children.Count - 1] is FrameworkElement last)
        {
            last.Margin = new Thickness(0);
        }

        dialog.Content = panel;

        var result = CadDialog.ShowModal(dialog, owner);
        owner?.Activate();
        if (result != true)
        {
            return null;
        }

        return chosen;
    }

    internal static bool TryGetRegion(Editor editor, string firstPrompt, string secondPrompt, Matrix3d inverseBlockTransform, out LocalRectangle region)
    {
        region = new LocalRectangle();
        var first = editor.GetPoint(new PromptPointOptions(firstPrompt));
        if (first.Status != PromptStatus.OK)
        {
            return false;
        }

        var cornerOptions = new PromptCornerOptions(secondPrompt, first.Value);
        var second = editor.GetCorner(cornerOptions);
        if (second.Status != PromptStatus.OK)
        {
            return false;
        }

        var p1 = first.Value.TransformBy(inverseBlockTransform);
        var p2 = second.Value.TransformBy(inverseBlockTransform);
        region = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
        return true;
    }

    private static OptionalRegionStatus TryGetOptionalRegion(
        Editor editor,
        string firstPrompt,
        string secondPrompt,
        Matrix3d inverseBlockTransform,
        out LocalRectangle region)
    {
        region = new LocalRectangle();
        var firstOptions = new PromptPointOptions(firstPrompt)
        {
            AllowNone = true
        };
        var first = editor.GetPoint(firstOptions);
        if (first.Status == PromptStatus.None)
        {
            return OptionalRegionStatus.None;
        }

        if (first.Status != PromptStatus.OK)
        {
            return OptionalRegionStatus.Cancel;
        }

        var cornerOptions = new PromptCornerOptions(secondPrompt, first.Value);
        var second = editor.GetCorner(cornerOptions);
        if (second.Status != PromptStatus.OK)
        {
            return OptionalRegionStatus.Cancel;
        }

        var p1 = first.Value.TransformBy(inverseBlockTransform);
        var p2 = second.Value.TransformBy(inverseBlockTransform);
        region = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
        return OptionalRegionStatus.Selected;
    }

    private static bool TryGetBlockExtents(Database database, ObjectId blockReferenceId, out Extents3d extents)
    {
        extents = default;
        using var tr = database.TransactionManager.StartTransaction();
        var blockRef = (BlockReference)tr.GetObject(blockReferenceId, OpenMode.ForRead);

        try
        {
            var direct = blockRef.GeometricExtents;
            if (HasValidExtents(direct))
            {
                extents = direct;
                return true;
            }
        }
        catch
        {
        }

        var hasExtents = false;
        var combined = default(Extents3d);
        var definition = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
        foreach (ObjectId entityId in definition)
        {
            try
            {
                var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                if (entity == null)
                {
                    continue;
                }

                var transformed = TransformWorldExtents(entity.GeometricExtents, blockRef.BlockTransform);
                if (!HasValidExtents(transformed))
                {
                    continue;
                }

                if (!hasExtents)
                {
                    combined = transformed;
                    hasExtents = true;
                }
                else
                {
                    combined.AddExtents(transformed);
                }
            }
            catch
            {
            }
        }

        if (!hasExtents || !HasValidExtents(combined))
        {
            return false;
        }

        extents = combined;
        return true;
    }

    private static bool HasValidExtents(Extents3d extents)
    {
        var values = new[]
        {
            extents.MinPoint.X, extents.MinPoint.Y,
            extents.MaxPoint.X, extents.MaxPoint.Y
        };
        return values.All(value => !double.IsNaN(value) && !double.IsInfinity(value))
            && extents.MaxPoint.X - extents.MinPoint.X > 1e-6
            && extents.MaxPoint.Y - extents.MinPoint.Y > 1e-6;
    }

    private static Extents3d TransformWorldExtents(Extents3d extents, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, extents.MinPoint.Z).TransformBy(transform),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, extents.MinPoint.Z).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, extents.MinPoint.Z).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, extents.MaxPoint.Z).TransformBy(transform)
        };

        return new Extents3d(
            new Point3d(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
            new Point3d(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
    }

    private static Extents3d TransformRegion(LocalRectangle region, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(region.MinX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MinX, region.MaxY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MaxY, 0).TransformBy(transform)
        };

        var minX = Math.Min(Math.Min(points[0].X, points[1].X), Math.Min(points[2].X, points[3].X));
        var minY = Math.Min(Math.Min(points[0].Y, points[1].Y), Math.Min(points[2].Y, points[3].Y));
        var maxX = Math.Max(Math.Max(points[0].X, points[1].X), Math.Max(points[2].X, points[3].X));
        var maxY = Math.Max(Math.Max(points[0].Y, points[1].Y), Math.Max(points[2].Y, points[3].Y));
        return new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
    }

    private static LocalRectangle TransformExtents(Extents3d extents, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform)
        };

        return LocalRectangle.FromPoints(
            points.Min(p => p.X),
            points.Min(p => p.Y),
            points.Max(p => p.X),
            points.Max(p => p.Y));
    }

    private static LocalRectangle ToFrameRelative(LocalRectangle region, LocalRectangle referenceFrame)
        => TitleBlockRegionConverter.ToFrameRelative(region, referenceFrame);

    /// <summary>

    /// </summary>
    private static LocalRectangle ToFrameRightBottomRelative(LocalRectangle region, LocalRectangle referenceFrame)
        => TitleBlockRegionConverter.ToFrameRightBottomRelative(region, referenceFrame);

    private static bool IsGenericDynamicPaperName(string paperName)
    {
        return !string.IsNullOrWhiteSpace(paperName)
               && paperName.EndsWith("+", StringComparison.Ordinal);
    }

    private static string GetGenericDynamicPaperBaseName(string paperName)
    {
        return IsGenericDynamicPaperName(paperName)
            ? paperName.Substring(0, paperName.Length - 1)
            : "";
    }

    private static void AddBlockLog(string message)
    {
        if (!BatchPlotLogger.IsEnabled)
        {
            return;
        }

        try
        {
            var logDirectory = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "AddTitleBlock_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private enum OptionalRegionStatus
    {
        Cancel,
        None,
        Selected
    }
}
