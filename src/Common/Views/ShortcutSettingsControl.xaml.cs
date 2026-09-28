using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZwcadBatchPlot;


public sealed partial class ShortcutSettingsControl : UserControl
{
    private readonly List<AliasRow> _rows = new();

    public event Action? OkRequested;
    public event Action? CancelRequested;


    public IReadOnlyDictionary<string, string> Aliases { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ShortcutSettingsControl(IReadOnlyDictionary<string, string> currentAliases)
    {
        InitializeComponent();
        BuildRows(currentAliases);
    }

    private void BuildRows(IReadOnlyDictionary<string, string> currentAliases)
    {
        foreach (var command in CommandAliasManager.AliasableCommands)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock
            {
                Text = command.DisplayName,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))
            });
            label.Children.Add(new TextBlock
            {
                Text = command.CommandName,
                Foreground = Brushes.DimGray
            });

            var input = new TextBox
            {
                MaxLength = 16,
                MinHeight = 22,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = FindCurrentAlias(currentAliases, command.CommandName)
            };

            Grid.SetColumn(label, 0);
            Grid.SetColumn(input, 1);
            grid.Children.Add(label);
            grid.Children.Add(input);
            RowsPanel.Children.Add(grid);
            _rows.Add(new AliasRow(command.CommandName, command.DisplayName, input));
        }
    }

    private static string FindCurrentAlias(IReadOnlyDictionary<string, string> currentAliases, string commandName)
    {
        foreach (var pair in currentAliases)
        {
            if (string.Equals(pair.Key, commandName, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value ?? "";
            }
        }

        return "";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var row in _rows)
        {
            var alias = row.Input.Text.Trim();
            if (alias.Length == 0)
            {
                continue;
            }

            if (!CommandAliasManager.IsValidAlias(alias))
            {
                errors.Add($"Сокращённая команда «{alias}» для «{row.DisplayName}» недопустима: начните с буквы, используйте только буквы и цифры, не более 16 символов.");
                continue;
            }

            if (!usedAliases.Add(alias))
            {
                errors.Add($"Сокращённая команда «{alias}» назначена нескольким командам.");
                continue;
            }

            aliases[row.CommandName] = alias;
        }

        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show(
                string.Join("\n", errors),
                "Настройки команд",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Aliases = aliases;
        OkRequested?.Invoke();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    private sealed class AliasRow
    {
        internal AliasRow(string commandName, string displayName, TextBox input)
        {
            CommandName = commandName;
            DisplayName = displayName;
            Input = input;
        }

        internal string CommandName { get; }
        internal string DisplayName { get; }
        internal TextBox Input { get; }
    }
}
