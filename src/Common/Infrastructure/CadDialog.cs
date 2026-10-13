using System;
using System.ComponentModel;
using System.Windows;
#if NANOCAD
using CadApp = HostMgd.ApplicationServices.Application;
#elif AUTOCAD
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// Единая точка открытия модальных и немодальных окон CAD через
/// <c>CadApp.ShowModalDialog</c>, <c>CadApp.ShowModelessDialog</c> или
/// <c>form.ShowDialog(owner)</c>.
/// </summary>
internal static class CadDialog
{

    public static bool? ShowModal(Window window)
    {
        return ShowModal(window, owner: null);
    }

    /// <summary>
    /// Открывает модальное окно с указанным владельцем.
    /// </summary>
    public static bool? ShowModal(Window window, Window? owner)
    {
        if (owner != null)
        {

            window.Owner = owner;
            return window.ShowDialog();
        }

        AttachTopLevelFocusRestore(window);

        return CadApp.ShowModalWindow(window);
    }


    public static void ShowModeless(Window window)
    {
        AttachTopLevelFocusRestore(window);

        CadApp.ShowModelessWindow(window);
    }

    /// <summary>


    /// </summary>
    private static void AttachTopLevelFocusRestore(Window window)
    {
        void OnClosing(object? sender, CancelEventArgs e)
        {
            if (e.Cancel)
            {
                return;
            }

            CadWindowFocus.ActivateCadWindow();
        }

        void OnClosed(object? sender, EventArgs e)
        {
            window.Closing -= OnClosing;
            window.Closed -= OnClosed;
            CadWindowFocus.ActivateCadWindow();
        }

        window.Closing += OnClosing;
        window.Closed += OnClosed;
    }
}
