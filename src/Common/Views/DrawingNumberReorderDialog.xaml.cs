using System;

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
public sealed partial class DrawingNumberReorderDialog : System.Windows.Window
{
    private readonly DrawingNumberReorderControl? _wpfControl;

    public string Prefix => _wpfControl?.Prefix ?? "";
    public string Suffix => _wpfControl?.Suffix ?? "";
    public int StartNumber => _wpfControl?.StartNumber ?? 1;

    public int Digits => _wpfControl?.Digits ?? 0;

    public bool HorizontalFirst => _wpfControl?.HorizontalFirst ?? false;


    public bool Accepted { get; private set; }


    public event Action? PreviewRequested;

    public DrawingNumberReorderDialog(int jobCount, string detectedPrefix = "", bool horizontalFirst = false)
    {
        try
        {
            InitializeComponent();

            _wpfControl = new DrawingNumberReorderControl(jobCount, detectedPrefix, horizontalFirst);

            _wpfControl.OkRequested += () => { Accepted = true; Close(); };
            _wpfControl.CancelRequested += () => { Accepted = false; Close(); };
            _wpfControl.PreviewRequested += () => PreviewRequested?.Invoke();
            Content = _wpfControl;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось открыть окно перенумерации листов:\n{ex}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
