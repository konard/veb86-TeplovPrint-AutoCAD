#if NANOCAD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace ZwcadBatchPlot;

/// <summary>A COM type library found in nanoCAD's folder and its registration state.</summary>
internal sealed class NanoCadTypeLibraryInfo
{
    public NanoCadTypeLibraryInfo(
        string path,
        Guid libId,
        ushort majorVersion,
        ushort minorVersion,
        bool isRegistered,
        string? registeredPath,
        bool definesPlotMembers)
    {
        Path = path;
        LibId = libId;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        IsRegistered = isRegistered;
        RegisteredPath = registeredPath;
        DefinesPlotMembers = definesPlotMembers;
    }

    public string Path { get; }
    public Guid LibId { get; }
    public ushort MajorVersion { get; }
    public ushort MinorVersion { get; }

    /// <summary>LoadRegTypeLib finds this LIBID and version.</summary>
    public bool IsRegistered { get; }

    /// <summary>Path registered for this LIBID and version (QueryPathOfRegTypeLib), if any.</summary>
    public string? RegisteredPath { get; }

    /// <summary>The library describes ActiveLayout and PlotToFile, i.e. the document/plot objects the plugin prints through.</summary>
    public bool DefinesPlotMembers { get; }

    public override string ToString()
        => $"{Path} {{{LibId}}} {MajorVersion}.{MinorVersion}: "
           + (IsRegistered ? "зарегистрирована" : "не зарегистрирована")
           + (string.IsNullOrEmpty(RegisteredPath) ? "" : " (в реестре: " + RegisteredPath + ")");
}

/// <summary>
/// nanoCAD 25 (issue #11): every IDispatch call into nanoCAD's COM document failed with TYPE_E_LIBNOTREGISTERED (0x8002801D):
/// nanoCAD's COM objects describe themselves (IDispatch.GetTypeInfo, GetIDsOfNames) through their type library, and the
/// library is looked up in the registry (LoadRegTypeLib). In that installation it was not registered, so the dynamic binder
/// failed on AcadDocument.ActiveLayout and Type.InvokeMember failed on AcadApplication.Preferences.
/// The type library is part of the module that is already loaded in nanoCAD's process: on the first such error it is
/// registered for the current user (RegisterTypeLibForUser, HKCU, no administrator rights) and the step is repeated once.
/// </summary>
internal static class NanoCadComTypeLibrary
{
    public const int TypeLibNotRegistered = unchecked((int)0x8002801D);

    private const int RegKindNone = 2;
    private static readonly string[] PlotMembers = { "ActiveLayout", "PlotToFile" };
    private static bool _registrationAttempted;

    /// <summary>The failure (or one of its causes) is TYPE_E_LIBNOTREGISTERED. The message may be a stale IErrorInfo text.</summary>
    public static bool IsNotRegistered(Exception? error)
    {
        for (var current = error; current != null; current = current.InnerException)
        {
            if (current.HResult == TypeLibNotRegistered)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs a COM step; on TYPE_E_LIBNOTREGISTERED registers nanoCAD's type library once and repeats the step.</summary>
    public static void Run(string step, Action action) => Run(step, action, TryRegisterOnce);

    internal static void Run(string step, Action action, Func<bool> register)
    {
        PlotStepException failure;
        try
        {
            PlotStepException.Run(step, action);
            return;
        }
        catch (PlotStepException ex) when (IsNotRegistered(ex))
        {
            failure = ex;
        }

        if (register())
        {
            PrintSessionLog.Info("Повтор шага после регистрации библиотеки типов nanoCAD: " + step);
            try
            {
                PlotStepException.Run(step, action);
                return;
            }
            catch (PlotStepException ex) when (IsNotRegistered(ex))
            {
                failure = ex;
            }
        }

        throw new InvalidOperationException(
            $"nanoCAD не нашёл зарегистрированную библиотеку типов своего COM API (TYPE_E_LIBNOTREGISTERED, 0x8002801D) на шаге {step}. "
            + "Печать в nanoCAD выполняется через COM API, поэтому без этой библиотеки она невозможна. "
            + "Восстановите установку nanoCAD (Параметры Windows → Приложения → nanoCAD → Изменить → Восстановить, от имени администратора) "
            + $"и перезапустите nanoCAD. Подробности в журнале печати: {PrintSessionLog.LastPath}",
            failure);
    }

    /// <summary>Libraries to register: not registered, describing the plot objects; one path per LIBID and version.</summary>
    public static IReadOnlyList<NanoCadTypeLibraryInfo> SelectForRegistration(IEnumerable<NanoCadTypeLibraryInfo> libraries)
    {
        return libraries
            .Where(library => !library.IsRegistered && library.DefinesPlotMembers)
            .GroupBy(library => (library.LibId, library.MajorVersion, library.MinorVersion))
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>Registers nanoCAD's type library for the current user; only the first call of the session does anything.</summary>
    private static bool TryRegisterOnce()
    {
        if (_registrationAttempted)
        {
            return false;
        }

        _registrationAttempted = true;
        try
        {
            return RegisterNanoCadTypeLibraries();
        }
        catch (Exception ex)
        {
            PrintSessionLog.Error("Регистрация библиотеки типов nanoCAD не выполнена", ex);
            return false;
        }
    }

    private static bool RegisterNanoCadTypeLibraries()
    {
        var folder = NanoCadFolder();
        PrintSessionLog.Write("WARN", "COM API nanoCAD: библиотека типов не зарегистрирована (TYPE_E_LIBNOTREGISTERED). Поиск библиотек типов в " + folder);
        var libraries = FindTypeLibraries(folder);
        foreach (var library in libraries.Where(l => l.DefinesPlotMembers))
        {
            PrintSessionLog.Info("Библиотека типов nanoCAD: " + library);
        }

        var selected = SelectForRegistration(libraries);
        if (selected.Count == 0)
        {
            PrintSessionLog.Write("WARN", $"Незарегистрированных библиотек типов nanoCAD с ActiveLayout/PlotToFile не найдено (проверено: {libraries.Count}).");
            return false;
        }

        var registered = false;
        foreach (var library in selected)
        {
            var hr = Register(library.Path);
            PrintSessionLog.Write(hr == 0 ? "INFO" : "WARN", $"RegisterTypeLibForUser({library.Path}) = 0x{hr:X8}");
            registered |= hr == 0;
        }

        return registered;
    }

    private static string NanoCadFolder()
    {
        using var process = Process.GetCurrentProcess();
        var exe = process.MainModule?.FileName;
        return string.IsNullOrEmpty(exe) ? AppContext.BaseDirectory : Path.GetDirectoryName(exe)!;
    }

    /// <summary>Type libraries of the modules nanoCAD loaded from its folder and the *.tlb files there.</summary>
    private static List<NanoCadTypeLibraryInfo> FindTypeLibraries(string folder)
    {
        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        using (var process = Process.GetCurrentProcess())
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (module.FileName is { } file && file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(file);
                }
            }
        }

        files.AddRange(Directory.EnumerateFiles(folder, "*.tlb"));

        var result = new List<NanoCadTypeLibraryInfo>();
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryDescribe(file) is { } library)
            {
                result.Add(library);
            }
        }

        return result;
    }

    private static NanoCadTypeLibraryInfo? TryDescribe(string path)
    {
        if (LoadTypeLibEx(path, RegKindNone, out var typeLib) != 0 || typeLib == null)
        {
            return null;
        }

        try
        {
            typeLib.GetLibAttr(out var attrPointer);
            ComTypes.TYPELIBATTR attr;
            try
            {
                attr = Marshal.PtrToStructure<ComTypes.TYPELIBATTR>(attrPointer);
            }
            finally
            {
                typeLib.ReleaseTLibAttr(attrPointer);
            }

            var libId = attr.guid;
            var major = unchecked((ushort)attr.wMajorVerNum);
            var minor = unchecked((ushort)attr.wMinorVerNum);
            var isRegistered = LoadRegTypeLib(ref libId, major, minor, 0, out var registeredLib) == 0;
            if (registeredLib != null)
            {
                Marshal.ReleaseComObject(registeredLib);
            }

            var registeredPath = QueryPathOfRegTypeLib(ref libId, major, minor, 0, out var queried) == 0 ? queried?.TrimEnd('\0') : null;
            var definesPlotMembers = PlotMembers.All(name => typeLib.IsName(name, 0));
            return new NanoCadTypeLibraryInfo(path, libId, major, minor, isRegistered, registeredPath, definesPlotMembers);
        }
        finally
        {
            Marshal.ReleaseComObject(typeLib);
        }
    }

    private static int Register(string path)
    {
        var hr = LoadTypeLibEx(path, RegKindNone, out var typeLib);
        if (hr != 0 || typeLib == null)
        {
            return hr;
        }

        try
        {
            return RegisterTypeLibForUser(typeLib, path, Path.GetDirectoryName(path));
        }
        finally
        {
            Marshal.ReleaseComObject(typeLib);
        }
    }

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadTypeLibEx(string szFile, int regKind, out ComTypes.ITypeLib? typeLib);

    [DllImport("oleaut32.dll")]
    private static extern int LoadRegTypeLib(ref Guid libId, ushort majorVersion, ushort minorVersion, int lcid, out ComTypes.ITypeLib? typeLib);

    [DllImport("oleaut32.dll")]
    private static extern int QueryPathOfRegTypeLib(ref Guid libId, ushort majorVersion, ushort minorVersion, int lcid, [MarshalAs(UnmanagedType.BStr)] out string? path);

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterTypeLibForUser(ComTypes.ITypeLib typeLib, string fullPath, string? helpDir);
}
#endif
