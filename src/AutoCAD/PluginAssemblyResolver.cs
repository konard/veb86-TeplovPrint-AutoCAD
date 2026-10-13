#if ACAD_CORE || NANOCAD
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;

namespace ZwcadBatchPlot;

/// <summary>



/// </summary>
internal static class PluginAssemblyResolver
{
    private static int _registered;


    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Design", "CA2255", Justification = "AutoCAD загружает сборку как плагин, поэтому зависимости необходимо разрешить до Initialize.")]
    [ModuleInitializer]
    internal static void Init() => Register();


    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        var pluginAssembly = typeof(PluginAssemblyResolver).Assembly;
        var context = AssemblyLoadContext.GetLoadContext(pluginAssembly) ?? AssemblyLoadContext.Default;
        context.Resolving += OnResolving;
        AppDomain.CurrentDomain.AssemblyResolve += OnAppDomainResolve;
    }


    private static Assembly? OnAppDomainResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            return Resolve(new AssemblyName(args.Name));
        }
        catch
        {
            return null;
        }
    }


    private static Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        return Resolve(name);
    }

    /// <summary>

    /// </summary>
    private static Assembly? Resolve(AssemblyName requested)
    {
        var simpleName = requested.Name;
        if (string.IsNullOrWhiteSpace(simpleName) || IsHostAssembly(simpleName))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(typeof(PluginAssemblyResolver).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var path = Path.Combine(dir, simpleName + ".dll");
            if (File.Exists(path))
            {
                try
                {
                    return Assembly.LoadFrom(path);
                }
                catch
                {

                }
            }
        }

        return FindLoaded(simpleName);
    }


    private static Assembly? FindLoaded(string simpleName)
    {
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }

        return null;
    }


    private static bool IsHostAssembly(string simpleName)
    {
        return simpleName.StartsWith("System", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("Autodesk.", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("accore", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("acdb", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("acmgd", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("hostmgd", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("hostdbmgd", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("Teigha", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase);
    }
}
#endif
