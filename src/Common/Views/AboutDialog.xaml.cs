using System;

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
public sealed partial class AboutDialog : System.Windows.Window
{
    public AboutDialog()
    {
        try
        {
            InitializeComponent();
            Control.OkRequested += () => { DialogResult = true; Close(); };
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось открыть окно «О программе»:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
