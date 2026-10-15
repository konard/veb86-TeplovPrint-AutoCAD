using System.Reflection;

namespace NcadBatchPlot.Tests;

internal static class TestPaths
{
    public static string NanoCadApiDir => Get("NanoCadApiDir");

    public static string NanoCad25ApiDir => Get("NanoCad25ApiDir");

    public static string PluginPath => Get("PluginPath");

    public static string PluginProjectPath => Get("PluginProjectPath");

    private static string Get(string key)
    {
        var value = typeof(TestPaths).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == key)
            .Value!;
        return Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar));
    }
}
