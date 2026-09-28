using System;
using System.Reflection;

namespace ZwcadBatchPlot;

/// <summary>





/// </summary>
internal static class LoadedAssemblyResolver
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requested = new AssemblyName(args.Name);
            var simpleName = requested.Name;
            if (string.IsNullOrWhiteSpace(simpleName))
            {
                return null;
            }


            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    return loaded;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
