using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ZwcadBatchPlot;

public sealed partial class AboutControl : UserControl
{
    public event Action? OkRequested;

    public AboutControl()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => OkRequested?.Invoke();

    private sealed class AboutViewModel
    {
        public string VersionText
        {
            get
            {
                var version = typeof(AboutControl).Assembly.GetName().Version;
                if (version == null)
                {
                    return "";
                }


                return version.Revision > 0 ? "V" + version.ToString(4) : "V" + version.ToString(3);
            }
        }
    }
}
