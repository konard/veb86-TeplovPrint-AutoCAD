using System;
using System.Windows;
using System.Windows.Controls;

namespace ZwcadBatchPlot;


public sealed partial class SortOrderControl : UserControl
{

    public bool HorizontalFirst => LeftToRightRadio.IsChecked == true;

    public TitleBlockSortMode SortMode => SortBasisPanel.Visibility == Visibility.Visible
        ? (SpatialSortRadio.IsChecked == true ? TitleBlockSortMode.Spatial : TitleBlockSortMode.DrawingNumber)
        : TitleBlockSortMode.Spatial;

    public event Action? OkRequested;
    public event Action? CancelRequested;

    public SortOrderControl(
        bool horizontalFirst = false,
        bool showSortBasis = false,
        TitleBlockSortMode sortMode = TitleBlockSortMode.DrawingNumber)
    {
        InitializeComponent();
        SortBasisPanel.Visibility = showSortBasis ? Visibility.Visible : Visibility.Collapsed;
        DialogTitleText.Text = showSortBasis ? "Выберите способ сортировки пакетной печати" : "Выберите порядок расположения листов";
        if (sortMode == TitleBlockSortMode.Spatial)
            SpatialSortRadio.IsChecked = true;
        else
            DrawingNumberSortRadio.IsChecked = true;

        if (horizontalFirst)
            LeftToRightRadio.IsChecked = true;
        else
            TopToBottomRadio.IsChecked = true;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => OkRequested?.Invoke();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
