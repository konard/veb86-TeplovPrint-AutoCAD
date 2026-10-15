using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ZwcadBatchPlot;

/// <summary>
/// Журнал печати в %TEMP%\TeplovPrint-print.log (issue #9). Создаётся заново при каждом «Начать печать»,
/// поэтому всегда содержит только последнюю печать. Каждая строка сразу записывается на диск (файл открывается
/// и закрывается на каждую строку): если CAD аварийно закроется в нативном коде, в журнале останется последний
/// выполнявшийся шаг. Пока печать идёт, сюда же попадают сообщения <see cref="TraceLog"/>, шаги
/// <see cref="PlotStepException.Run"/>, необработанные и первичные (first-chance) исключения.
/// Не зависит от CAD и WPF, поэтому проверяется тестами без хоста.
/// </summary>
public static class PrintSessionLog
{
    public const string FileName = "TeplovPrint-print.log";

    private const int MaxFirstChanceEntries = 200;

    private static readonly object Sync = new();
    private static string? _currentPath;
    private static int _firstChanceCount;
    private static bool _handlersAttached;

    [ThreadStatic]
    private static bool _writing;

    /// <summary>%TEMP%\TeplovPrint-print.log.</summary>
    public static string DefaultPath => Path.Combine(Path.GetTempPath(), FileName);

    /// <summary>Журнал текущей печати; null, если печать не идёт.</summary>
    public static string? CurrentPath => _currentPath;

    /// <summary>Журнал последней начатой печати (остаётся после <see cref="Stop"/>), чтобы показать путь пользователю.</summary>
    public static string LastPath { get; private set; } = DefaultPath;

    public static bool IsActive => _currentPath != null;

    /// <summary>Начинает новый журнал в %TEMP%, заменяя предыдущий.</summary>
    public static string Start(string title) => StartAt(DefaultPath, title);

    /// <summary>Начинает новый журнал по указанному пути, заменяя предыдущий файл.</summary>
    public static string StartAt(string path, string title)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Не указан путь журнала.", nameof(path));
        }

        lock (Sync)
        {
            _currentPath = path;
            LastPath = path;
            _firstChanceCount = 0;
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, BuildHeader(title), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Print log was not created: " + ex.Message, "TeplovPrint");
            }

            AttachHandlers();
        }

        return path;
    }

    /// <summary>Добавляет строку "время [уровень] сообщение" и сразу сбрасывает её на диск.</summary>
    public static void Write(string level, string message)
    {
        var path = _currentPath;
        if (path == null || _writing)
        {
            return;
        }

        _writing = true;
        try
        {
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] [T{2}] {3}{4}",
                DateTime.Now,
                level,
                Environment.CurrentManagedThreadId,
                message,
                Environment.NewLine);
            lock (Sync)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Журнал не должен ломать печать.
        }
        finally
        {
            _writing = false;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    /// <summary>Шаг печати. Записывается до выполнения, чтобы при аварийном закрытии CAD было видно, где оно произошло.</summary>
    public static void Step(string step) => Write("STEP", step);

    public static void Error(string context, Exception error) => Write("ERROR", context + ": " + Describe(error));

    /// <summary>Тип, HRESULT, сообщение и стек исключения со всеми вложенными исключениями.</summary>
    public static string Describe(Exception? error)
    {
        if (error == null)
        {
            return "(нет исключения)";
        }

        var text = new StringBuilder();
        var depth = 0;
        for (var current = error; current != null && depth < 10; current = current.InnerException, depth++)
        {
            if (depth > 0)
            {
                text.AppendLine().Append("  ---> ");
            }

            text.Append(current.GetType().FullName)
                .Append(" 0x").Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture))
                .Append(": ").Append(current.Message);
            if (current is PlotStepException step)
            {
                text.Append(" [шаг: ").Append(step.Step).Append(']');
            }

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                text.AppendLine().Append(current.StackTrace);
            }
        }

        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                text.AppendLine().Append("  [aggregate] ").Append(Describe(inner));
            }
        }

        return text.ToString();
    }

    /// <summary>Лист печати одной строкой: номер, имя, источник, пространство, рамка, бумага, параметры масштаба, файл.</summary>
    public static string DescribeJob(PlotJob job)
    {
        if (job == null)
        {
            return "(нет листа)";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}_{1}; источник={2}; пространство={3} (лист={4}); блок={5}; рамка=({6:0.###},{7:0.###})-({8:0.###},{9:0.###}){10}{11}; "
            + "бумага={12} {13:0.##}x{14:0.##} мм (эфф. {15:0.##}x{16:0.##}); поля={17} {18:0.##} мм; точный масштаб={19}; точный формат={20}; "
            + "пользовательский формат={21}; стиль={22}; файл={23}",
            job.DrawingNumber,
            job.Title,
            job.SourceFile,
            job.SpaceName,
            job.IsPaperSpace,
            job.BlockName,
            job.MinX,
            job.MinY,
            job.MaxX,
            job.MaxY,
            job.IsDcsWindow ? " DCS" : "",
            job.UsesUserCoordinateSystem ? " UCS" : "",
            job.PaperName,
            job.PaperWidthMm,
            job.PaperHeightMm,
            job.EffectivePaperWidthMm,
            job.EffectivePaperHeightMm,
            job.LeavePaperMargin,
            job.PaperMarginMm,
            job.UseExactWindowScale,
            job.RequireExactPaperSize,
            job.RequiresCustomPaperRegistration,
            job.StyleSheet,
            job.OutputPath);
    }

    /// <summary>Простые свойства настроек "имя=значение; ..." (строки, числа, флаги, перечисления).</summary>
    public static string DescribeSettings(object? settings)
    {
        if (settings == null)
        {
            return "(нет настроек)";
        }

        var text = new StringBuilder();
        foreach (var property in settings.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (property.GetIndexParameters().Length > 0
                || !(type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)))
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(settings);
            }
            catch (Exception ex)
            {
                value = "<" + ex.GetType().Name + ">";
            }

            if (text.Length > 0)
            {
                text.Append("; ");
            }

            text.Append(property.Name).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>Завершает журнал печати: итог записывается, обработчики исключений снимаются.</summary>
    public static void Stop(string result)
    {
        if (_currentPath == null)
        {
            return;
        }

        Write("END", result);
        lock (Sync)
        {
            _currentPath = null;
            DetachHandlers();
        }
    }

    private static string BuildHeader(string title)
    {
        var text = new StringBuilder();
        text.AppendLine("Teplov Print — журнал печати (заменяется при каждом «Начать печать»)");
        text.AppendLine("Печать: " + title);
        text.AppendLine("Начало: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        AppendSafe(text, "Плагин", DescribePlugin);
        AppendSafe(text, "Процесс CAD", DescribeHost);
        AppendSafe(text, ".NET", () => RuntimeInformation.FrameworkDescription + " (CLR " + Environment.Version + ")");
        AppendSafe(text, "ОС", () => Environment.OSVersion.VersionString + ", " + RuntimeInformation.OSDescription + ", "
            + (Environment.Is64BitProcess ? "64-bit процесс" : "32-bit процесс"));
        AppendSafe(text, "Культура", () => CultureInfo.CurrentCulture.Name + " / UI " + CultureInfo.CurrentUICulture.Name);
        AppendSafe(text, "Папка TEMP", Path.GetTempPath);
        AppendSafe(text, "Трассировка TEPLOV_PRINT_TRACE", () => TraceLog.Enabled ? "включена" : "выключена");
        text.AppendLine(new string('-', 80));
        return text.ToString();
    }

    private static string DescribePlugin()
    {
        var assembly = typeof(PrintSessionLog).Assembly;
        var name = assembly.GetName();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return $"{name.Name} {informational ?? name.Version?.ToString()} ({assembly.Location})";
    }

    private static string DescribeHost()
    {
        using var process = Process.GetCurrentProcess();
        var module = process.MainModule;
        var version = module?.FileVersionInfo;
        return $"{process.ProcessName} PID {process.Id}, {module?.FileName}, версия {version?.FileVersion} ({version?.ProductName} {version?.ProductVersion})";
    }

    private static void AppendSafe(StringBuilder text, string name, Func<string> value)
    {
        string line;
        try
        {
            line = value();
        }
        catch (Exception ex)
        {
            line = "не удалось определить: " + ex.GetType().Name + ": " + ex.Message;
        }

        text.Append(name).Append(": ").AppendLine(line);
    }

    private static void AttachHandlers()
    {
        if (_handlersAttached)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        _handlersAttached = true;
    }

    private static void DetachHandlers()
    {
        if (!_handlersAttached)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        _handlersAttached = false;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Write("FATAL", "Необработанное исключение (CAD будет закрыт: " + e.IsTerminating + "): "
            + Describe(e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture))));
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (_writing || _currentPath == null || _firstChanceCount >= MaxFirstChanceEntries)
        {
            return;
        }

        var count = System.Threading.Interlocked.Increment(ref _firstChanceCount);
        if (count > MaxFirstChanceEntries)
        {
            return;
        }

        var error = e.Exception;
        Write("FIRST-CHANCE", $"{error.GetType().FullName} 0x{error.HResult:X8}: {error.Message}"
            + (count == MaxFirstChanceEntries ? " (дальнейшие first-chance исключения не записываются)" : ""));
    }
}
