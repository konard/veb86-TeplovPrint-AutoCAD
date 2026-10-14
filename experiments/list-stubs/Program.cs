var filter = args.Length > 1 ? args[1] : "";
foreach (var s in NcadBatchPlot.Tests.NanoCadApiStubs.FindStubs(args[0]).Where(s => s.Contains(filter)).OrderBy(s => s))
    Console.WriteLine(s);
