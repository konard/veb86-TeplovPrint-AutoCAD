using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace ZwcadBatchPlot;

/// <summary>


/// </summary>
internal static class CadWindowFocus
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    public static void ActivateCadWindow()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var windowHandle = process.MainWindowHandle;
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }

            if (IsIconic(windowHandle))
            {
                ShowWindowAsync(windowHandle, SwRestore);
            }

            BringWindowToTop(windowHandle);
            SetForegroundWindow(windowHandle);
        }
        catch
        {

        }
    }


    public static void HideForCadInput(Form form)
    {
        form.Hide();

        ActivateCadWindow();
        System.Windows.Forms.Application.DoEvents();
    }


    public static void HideForCadInput(Window window)
    {
        window.Hide();
        ActivateCadWindow();
        System.Windows.Forms.Application.DoEvents();
    }


    public static void RestoreDialog(Form form)
    {
        form.Visible = true;
        form.BringToFront();
        form.Activate();
    }


    public static void RestoreDialog(Window window)
    {
        window.Visibility = Visibility.Visible;
        window.BringToFrontHwnd();
        window.Activate();
    }

    private static void BringToFrontHwnd(this Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                BringWindowToTop(handle);
            }
        }
        catch
        {

        }
    }
}
