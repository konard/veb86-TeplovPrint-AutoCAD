using System;

namespace ZwcadBatchPlot;


public sealed class RectangleScanProgress
{

    public string Title { get; set; } = "Распознавание рамок…";


    public string Detail { get; set; } = "";


    public int Current { get; set; }


    public int Total { get; set; }
}
