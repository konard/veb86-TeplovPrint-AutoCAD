using System;
using System.Collections.Generic;

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
public sealed partial class ShortcutSettingsDialog : System.Windows.Window
{
    private readonly ShortcutSettingsControl? _wpfControl;


    public IReadOnlyDictionary<string, string> Aliases =>
        _wpfControl?.Aliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ShortcutSettingsDialog(IReadOnlyDictionary<string, string> currentAliases)
    {
        try
        {
            InitializeComponent();

            _wpfControl = new ShortcutSettingsControl(currentAliases);
            _wpfControl.OkRequested += () => { DialogResult = true; Close(); };
            _wpfControl.CancelRequested += () => { DialogResult = false; Close(); };
            Content = _wpfControl;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось открыть настройки команд:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
