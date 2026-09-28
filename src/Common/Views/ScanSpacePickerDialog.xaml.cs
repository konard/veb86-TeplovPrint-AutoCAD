using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
public sealed partial class ScanSpacePickerDialog : Window
{
    private sealed class FileGroup
    {
        public string FilePath { get; set; } = "";
        public CheckBox HeaderCheck { get; set; } = null!;
        public ComboBox StyleCombo { get; set; } = null!;
        public List<SpaceRow> Rows { get; } = new();
        public bool SuppressHeaderSync;
        public bool SuppressChildSync;
    }

    private sealed class SpaceRow
    {
        public DwgSpaceEntry Entry { get; set; } = null!;
        public CheckBox Check { get; set; } = null!;
        public FileGroup Group { get; set; } = null!;
    }

    private readonly List<FileGroup> _groups = new();
    private readonly List<DwgSpaceEntry> _selected = new();


    public IReadOnlyList<DwgSpaceEntry> SelectedSpaces => _selected;

    private readonly string _defaultStyleSheet;
    private readonly List<string> _availableStyles;

    public ScanSpacePickerDialog(
        IEnumerable<DwgSpaceEntry> spaces,
        string defaultStyleSheet,
        IEnumerable<string>? availableStyles = null)
    {
        InitializeComponent();
        _defaultStyleSheet = defaultStyleSheet ?? "";
        _availableStyles = (availableStyles ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_availableStyles.Count == 0)
        {
            try
            {
                _availableStyles = PlotStyleManager.GetAvailableCtbStyles().ToList();
            }
            catch
            {
                _availableStyles = new List<string>();
            }
        }

        BuildContent(spaces?.ToList() ?? new List<DwgSpaceEntry>());
    }

    private void BuildContent(List<DwgSpaceEntry> spaces)
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(top, Dock.Top);
        var selectAll = new Button
        {
            Content = "Выбрать всё",
            MinWidth = 76,
            Style = TryFindResource("PluginButtonStyle") as Style,
            Margin = new Thickness(0, 0, 6, 0)
        };
        selectAll.Click += (_, __) => SetAllSelected(true);
        var selectNone = new Button
        {
            Content = "Снять выбор",
            MinWidth = 76,
            Style = TryFindResource("PluginButtonStyle") as Style
        };
        selectNone.Click += (_, __) => SetAllSelected(false);
        top.Children.Add(selectAll);
        top.Children.Add(selectNone);
        top.Children.Add(new TextBlock
        {
            Text = "Выберите модель и листы для сканирования. После подтверждения текущий список будет очищен.",
            Foreground = Brushes.DimGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(top);

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var cancel = new Button
        {
            Content = "Отмена",
            MinWidth = 76,
            Style = TryFindResource("PluginButtonStyle") as Style,
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, __) =>
        {
            DialogResult = false;
            Close();
        };
        var start = new Button
        {
            Content = "Начать сканирование",
            MinWidth = 104,
            Style = TryFindResource("PluginButtonStyle") as Style,
            IsDefault = true
        };
        start.Click += (_, __) => ConfirmStart();
        bottom.Children.Add(cancel);
        bottom.Children.Add(start);
        root.Children.Add(bottom);

        var listHost = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD2, 0xD8)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Padding = new Thickness(4)
        };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var stack = new StackPanel();
        scroll.Content = stack;
        listHost.Child = scroll;
        root.Children.Add(listHost);

        foreach (var fileGroup in spaces.GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var group = new FileGroup { FilePath = fileGroup.Key };
            var headerRow = new DockPanel
            {
                Margin = new Thickness(2, 4, 2, 2),
                LastChildFill = true
            };

            var styleCombo = new ComboBox
            {
                Width = 168,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Стиль печати"
            };
            foreach (var style in _availableStyles)
            {
                styleCombo.Items.Add(style);
            }

            PlotStyleManager.RestoreSavedStyle(styleCombo, _defaultStyleSheet);
            group.StyleCombo = styleCombo;

            var styleLabel = new TextBlock
            {
                Text = "Стиль печати",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = Brushes.DimGray
            };
            DockPanel.SetDock(styleCombo, Dock.Right);
            DockPanel.SetDock(styleLabel, Dock.Right);
            headerRow.Children.Add(styleCombo);
            headerRow.Children.Add(styleLabel);

            var header = new CheckBox
            {
                Content = Path.GetFileName(fileGroup.Key),
                IsThreeState = true,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                ToolTip = fileGroup.Key
            };
            group.HeaderCheck = header;
            header.Checked += (_, __) => OnHeaderChanged(group, true);
            header.Unchecked += (_, __) => OnHeaderChanged(group, false);

            header.Indeterminate += (_, __) =>
            {
                if (group.SuppressHeaderSync)
                {
                    return;
                }

                header.IsChecked = true;
            };
            headerRow.Children.Add(header);
            stack.Children.Add(headerRow);

            foreach (var entry in fileGroup)
            {
                var row = new SpaceRow { Entry = entry, Group = group };
                var child = new CheckBox
                {
                    Content = entry.DisplayName,
                    IsChecked = entry.Selected,
                    Margin = new Thickness(22, 1, 2, 1),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                row.Check = child;
                child.Checked += (_, __) => OnChildChanged(row, true);
                child.Unchecked += (_, __) => OnChildChanged(row, false);
                group.Rows.Add(row);
                stack.Children.Add(child);
            }

            _groups.Add(group);
            SyncHeaderFromChildren(group);
        }

        Content = root;
    }

    private void OnHeaderChanged(FileGroup group, bool selected)
    {
        if (group.SuppressHeaderSync)
        {
            return;
        }

        group.SuppressChildSync = true;
        try
        {
            foreach (var row in group.Rows)
            {
                row.Entry.Selected = selected;
                row.Check.IsChecked = selected;
            }
        }
        finally
        {
            group.SuppressChildSync = false;
        }
    }

    private void OnChildChanged(SpaceRow row, bool selected)
    {
        if (row.Group.SuppressChildSync)
        {
            return;
        }

        row.Entry.Selected = selected;
        SyncHeaderFromChildren(row.Group);
    }

    private static void SyncHeaderFromChildren(FileGroup group)
    {
        group.SuppressHeaderSync = true;
        try
        {
            var total = group.Rows.Count;
            var checkedCount = group.Rows.Count(r => r.Check.IsChecked == true);
            if (checkedCount == 0)
            {
                group.HeaderCheck.IsChecked = false;
            }
            else if (checkedCount == total)
            {
                group.HeaderCheck.IsChecked = true;
            }
            else
            {
                group.HeaderCheck.IsChecked = null;
            }
        }
        finally
        {
            group.SuppressHeaderSync = false;
        }
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var group in _groups)
        {
            group.SuppressHeaderSync = true;
            group.SuppressChildSync = true;
            try
            {
                foreach (var row in group.Rows)
                {
                    row.Entry.Selected = selected;
                    row.Check.IsChecked = selected;
                }

                group.HeaderCheck.IsChecked = selected;
            }
            finally
            {
                group.SuppressHeaderSync = false;
                group.SuppressChildSync = false;
            }
        }
    }

    private void ConfirmStart()
    {
        _selected.Clear();
        foreach (var group in _groups)
        {
            var style = group.StyleCombo.SelectedItem?.ToString()
                ?? PlotStyleManager.NormalizeStyleName(_defaultStyleSheet);
            foreach (var row in group.Rows)
            {
                row.Entry.Selected = row.Check.IsChecked == true;
                row.Entry.StyleSheet = style;
                if (row.Entry.Selected)
                {
                    _selected.Add(row.Entry);
                }
            }
        }

        if (_selected.Count == 0)
        {
            MessageBox.Show("Выберите хотя бы модель или один лист.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }
}
