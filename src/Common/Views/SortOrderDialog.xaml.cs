using System;

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
public sealed partial class SortOrderDialog : System.Windows.Window
{
    private readonly SortOrderControl? _wpfControl;


    public bool HorizontalFirst => _wpfControl?.HorizontalFirst ?? false;

    public TitleBlockSortMode SortMode => _wpfControl?.SortMode ?? TitleBlockSortMode.DrawingNumber;

    public SortOrderDialog(
        bool horizontalFirst = false,
        bool showSortBasis = false,
        TitleBlockSortMode sortMode = TitleBlockSortMode.DrawingNumber)
    {
        try
        {
            InitializeComponent();

            _wpfControl = new SortOrderControl(horizontalFirst, showSortBasis, sortMode);
            _wpfControl.OkRequested += () => { DialogResult = true; Close(); };
            _wpfControl.CancelRequested += () => { DialogResult = false; Close(); };
            Content = _wpfControl;


            if (showSortBasis)
            {
                MinHeight = 300;
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось открыть настройки сортировки:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
