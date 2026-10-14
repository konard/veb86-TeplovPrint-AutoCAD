using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Reproduces, without nanoCAD and without WPF, how nanoCAD 24 loads a plugin and how WPF later
/// resolves the plugin assembly for <c>InitializeComponent()</c>.
///
/// nanoCAD 24 (hostdbmgd.dll, <c>CNetLoadCmd.LoadDLL</c>):
///   * subscribes <c>ResolveEventHandlerHelper.Resolver</c> to <c>AppDomain.AssemblyResolve</c>;
///   * loads the file with <c>Assembly.LoadFile(path)</c>, i.e. into its own isolated AssemblyLoadContext;
///   * handles <c>AppDomain.AssemblyLoad</c> (<c>CNetLoadCmd.OnAssemblyLoad</c>): every newly loaded copy of
///     a plugin gets its commands registered and <c>IExtensionApplication.Initialize()</c> called again
///     (counted here through <see cref="PluginCopies"/>).
///
/// WPF (dotnet/wpf release/6.0, BaseUriHelper.GetLoadedAssembly / SafeSecurityHelper.GetLoadedAssembly):
///   scans <c>AppDomain.GetAssemblies()</c> for name + exact version from the pack URI and otherwise falls back to
///   <c>Assembly.Load(asmName)</c>. Application.LoadComponent then throws
///   "The component '…' does not have a resource identified by the URI '…'" when the BAML stream comes from a
///   different Assembly instance than the component's type.
/// </summary>
internal sealed class NanoCadNetloadEmulation : IDisposable
{
    private readonly string _pluginPath;
    private readonly string _pluginName;

    public NanoCadNetloadEmulation(string pluginPath)
    {
        _pluginPath = pluginPath;
        _pluginName = AssemblyName.GetAssemblyName(pluginPath).Name!;
        AppDomain.CurrentDomain.AssemblyResolve += NanoCadResolver;
    }

    /// <summary>
    /// Copies of the plugin assembly loaded into the process. nanoCAD's AssemblyLoad handler registers the commands
    /// and calls IExtensionApplication.Initialize() for each of them, so every extra copy repeats the startup dialogs.
    /// </summary>
    public IReadOnlyList<Assembly> PluginCopies => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => string.Equals(a.GetName().Name, _pluginName, StringComparison.OrdinalIgnoreCase))
        .ToList();

    public void Dispose()
    {
        AppDomain.CurrentDomain.AssemblyResolve -= NanoCadResolver;
    }

    /// <summary>NETLOAD: <c>Assembly.LoadFile</c> + start of the plugin code (module initializer, resolvers from Initialize()).</summary>
    public Assembly Netload()
    {
        var plugin = Assembly.LoadFile(_pluginPath);
        RuntimeHelpers.RunModuleConstructor(plugin.ManifestModule.ModuleHandle);
        foreach (var resolver in new[] { "ZwcadBatchPlot.LoadedAssemblyResolver", "ZwcadBatchPlot.PluginAssemblyResolver" })
        {
            plugin.GetType(resolver, throwOnError: true)!
                .GetMethod("Register", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null);
        }

        return plugin;
    }

    /// <summary>WPF: BaseUriHelper.GetLoadedAssembly(assemblyName, assemblyVersion, assemblyKey).</summary>
    public static Assembly WpfGetLoadedAssembly(string assemblyName, string assemblyVersion)
    {
        var asmName = new AssemblyName(assemblyName) { CultureInfo = new CultureInfo(string.Empty) };
        if (!string.IsNullOrEmpty(assemblyVersion))
        {
            asmName.Version = new Version(assemblyVersion);
        }

        return WpfSafeSecurityHelperGetLoadedAssembly(asmName) ?? Assembly.Load(asmName);
    }

    /// <summary>WPF: SafeSecurityHelper.GetLoadedAssembly(AssemblyName).</summary>
    private static Assembly? WpfSafeSecurityHelperGetLoadedAssembly(AssemblyName assemblyName)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var reqVersion = assemblyName.Version;
        for (var i = assemblies.Length - 1; i >= 0; i--)
        {
            var cur = new AssemblyName(assemblies[i].FullName!);
            if (string.Equals(cur.Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)
                && (reqVersion == null || reqVersion.Equals(cur.Version))
                && cur.CultureInfo!.Equals(assemblyName.CultureInfo))
            {
                return assemblies[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Pack URI that the WPF markup compiler embedded into <c>InitializeComponent()</c> of the given XAML page,
    /// e.g. "/NcadBatchPlot;V1.0.0;component/common/views/batchplotform.xaml".
    /// </summary>
    public static string FindComponentUri(string pluginPath, string xamlPath)
    {
        using var stream = File.OpenRead(pluginPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var handle = MetadataTokens.UserStringHandle(1);
        while (!handle.IsNil)
        {
            var value = md.GetUserString(handle);
            if (value.StartsWith("/", StringComparison.Ordinal)
                && value.EndsWith(";component/" + xamlPath, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            handle = md.GetNextHandle(handle);
        }

        throw new InvalidOperationException($"Pack URI for {xamlPath} was not found in {pluginPath}.");
    }

    /// <summary>Port of nanoCAD 24 ResolveEventHandlerHelper.Resolver / CheckAndLoadAssembly (hostdbmgd.dll).</summary>
    private Assembly? NanoCadResolver(object? sender, ResolveEventArgs args)
    {
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (loaded.FullName == args.Name)
            {
                return loaded;
            }
        }

        // MAIN_ASSEMBLY_PATH (nanoCAD bin directory) cannot contain the plugin; LAST_LOADED_ASSEMBLY_PATH is the NETLOAD directory.
        return CheckAndLoadAssembly(Path.GetDirectoryName(_pluginPath)!, args.Name);
    }

    private static Assembly? CheckAndLoadAssembly(string path, string name)
    {
        try
        {
            var parts = name.Split(',');
            var file = Path.Combine(path, parts[0] + ".dll");
            if (!File.Exists(file))
            {
                return null;
            }

            var fileName = AssemblyName.GetAssemblyName(file).ToString().ToLower();
            return parts.All(part => fileName.IndexOf(part.ToLower(), StringComparison.Ordinal) >= 0)
                ? Assembly.LoadFile(file)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
