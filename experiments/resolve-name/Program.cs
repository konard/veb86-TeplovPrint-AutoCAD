// Shows what nanoCAD's AppDomain.AssemblyResolve handler receives when WPF resolves the pack URI
// "/NcadBatchPlot;V1.0.0;component/..." (BaseUriHelper.GetLoadedAssembly -> Assembly.Load(asmName)),
// and whether WPF's own loaded-assembly scan (SafeSecurityHelper.GetLoadedAssembly) can match version "1.0.0".
using System.Globalization;
using System.Reflection;

foreach (var uriVersion in new[] { "1.0.0", "1.0.0.0" })
{
    var asmName = new AssemblyName("NcadBatchPlotProbe") { CultureInfo = new CultureInfo(string.Empty), Version = new Version(uriVersion) };
    Console.WriteLine($"URI version {uriVersion}: AssemblyName.FullName = '{asmName.FullName}'");
    Console.WriteLine($"  new Version(\"{uriVersion}\").Equals(1.0.0.0) = {new Version(uriVersion).Equals(new Version(1, 0, 0, 0))}");
    ResolveEventHandler h = (_, e) => { Console.WriteLine($"  AssemblyResolve args.Name = '{e.Name}'"); return null; };
    AppDomain.CurrentDomain.AssemblyResolve += h;
    try { Assembly.Load(asmName); } catch (FileNotFoundException) { }
    AppDomain.CurrentDomain.AssemblyResolve -= h;
}
