using NcadBatchPlot.Tests;

var graph = new PluginCallGraph(args[0]);
if (args[1] == "callers")
{
    foreach (var caller in graph.FindTransitiveCallers(args[2]).OrderBy(c => c))
        Console.WriteLine(caller);
}
else
{
    var path = graph.FindPath(args[1], args[2]);
    Console.WriteLine(path == null ? "(no path)" : string.Join(Environment.NewLine + "  -> ", path));
}
