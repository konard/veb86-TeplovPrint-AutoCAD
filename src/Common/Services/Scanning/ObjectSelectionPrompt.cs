using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
#if NANOCAD
using Teigha.DatabaseServices;
using HostMgd.EditorInput;
#elif AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
#endif

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
internal sealed class ObjectScopeSelectionResult
{

    public ObjectId[]? SelectedIds { get; set; }


    public TitleBlockScanScope? Scope { get; set; }


    public bool Cancelled => SelectedIds == null && Scope == null;
}

/// <summary>


/// </summary>
internal static class ObjectSelectionPrompt
{

    public static SelectionFilter TitleBlockFilter()
        => ScanCandidateFilter.TitleBlockSelectionFilter();

    public static SelectionFilter SpdsFormatFilter()
        => ScanCandidateFilter.SpdsSelectionFilter();

    public static SelectionFilter AllSupportedFrameFilter(bool includeLines)
        => ScanCandidateFilter.AllSupportedFrameSelectionFilter(includeLines);

    /// <summary>

    /// </summary>

    public static SelectionFilter RectangleFrameFilter(bool includeLines)
        => ScanCandidateFilter.RectangleFrameSelectionFilter(includeLines);

    /// <summary>


    /// </summary>
    public static ObjectScopeSelectionResult Prompt(Editor editor, string message, SelectionFilter? filter)
    {
        using var menu = new ScopeContextMenu(editor.Document.Database);
        while (true)
        {
            using var interceptor = new RightClickMenuInterceptor(menu);
            Application.AddMessageFilter(interceptor);
            PromptSelectionResult? selection = null;
            try
            {
                var options = new PromptSelectionOptions { MessageForAdding = message };
                selection = filter != null
                    ? editor.GetSelection(options, filter)
                    : editor.GetSelection(options);
            }
            catch
            {

            }
            finally
            {
                Application.RemoveMessageFilter(interceptor);
            }

            if (menu.ChosenScope is { } scope)
            {
                return new ObjectScopeSelectionResult { Scope = scope };
            }

            if (selection is { Status: PromptStatus.OK, Value: not null })
            {
                var ids = selection.Value.GetObjectIds();
                if (ids != null && ids.Length > 0)
                {
                    return new ObjectScopeSelectionResult { SelectedIds = ids };
                }
            }


            if (interceptor.ConfirmRequested)
            {
                continue;
            }

            return new ObjectScopeSelectionResult();
        }
    }


    private static class KeyInjection
    {
        private const uint KeyeventfKeyup = 0x0002;
        private const byte VkEscape = 0x1B;
        private const byte VkReturn = 0x0D;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        public static void PressEscape()
        {
            keybd_event(VkEscape, 0, 0, UIntPtr.Zero);
            keybd_event(VkEscape, 0, KeyeventfKeyup, UIntPtr.Zero);
        }

        public static void PressEnter()
        {
            keybd_event(VkReturn, 0, 0, UIntPtr.Zero);
            keybd_event(VkReturn, 0, KeyeventfKeyup, UIntPtr.Zero);
        }
    }

    /// <summary>


    /// </summary>
    private sealed class RightClickMenuInterceptor : IMessageFilter, IDisposable
    {
        private readonly ScopeContextMenu _menu;
        private bool _rButtonDown;


        internal bool PickActivity { get; private set; }


        internal bool ConfirmRequested { get; private set; }

        public RightClickMenuInterceptor(ScopeContextMenu menu) => _menu = menu;

        public bool PreFilterMessage(ref Message m)
        {
            if (_menu.IsMenuVisible)
            {
                return false;
            }

            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_RBUTTONUP = 0x0205;
            switch (m.Msg)
            {
                case WM_LBUTTONDOWN:
                    PickActivity = true;
                    return false;
                case WM_RBUTTONDOWN:
                    _rButtonDown = true;
                    return true;
                case WM_RBUTTONUP when _rButtonDown:
                    _rButtonDown = false;
                    if (PickActivity)
                    {
                        ConfirmRequested = true;
                        KeyInjection.PressEnter();
                    }
                    else
                    {
                        _menu.ShowAtCursor();
                    }

                    return true;
                default:
                    return false;
            }
        }

        public void Dispose()
        {
        }
    }


    private sealed class ScopeContextMenu : IDisposable
    {
        private readonly ContextMenuStrip _menu;


        public TitleBlockScanScope? ChosenScope { get; private set; }

        public bool IsMenuVisible => _menu.Visible;

        public ScopeContextMenu(Database db)
        {
            _menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                ShowCheckMargin = false,
                AutoClose = true,
                Font = new System.Drawing.Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            };

            if (db.TileMode)
            {
                AddItem("Текущая модель", TitleBlockScanScope.CurrentSpace);
                AddItem("Все листы", TitleBlockScanScope.PaperLayouts);
                AddItem("Модель и листы", TitleBlockScanScope.AllSpaces);
            }
            else
            {
                AddItem("Пространство модели", TitleBlockScanScope.ModelSpace);
                AddItem("Все листы", TitleBlockScanScope.PaperLayouts);
                AddItem("Модель и листы", TitleBlockScanScope.AllSpaces);
                var layoutName = "";
                try
                {
                    layoutName = LayoutManager.Current.CurrentLayout ?? "";
                }
                catch
                {

                }

                AddItem(
                    string.IsNullOrWhiteSpace(layoutName) ? "Текущий лист" : $"Текущий лист ({layoutName})",
                    TitleBlockScanScope.CurrentSpace);
            }
        }

        private void AddItem(string text, TitleBlockScanScope scope)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (_, _) =>
            {
                ChosenScope = scope;
                KeyInjection.PressEscape();
                _menu.Close();
            };
            _menu.Items.Add(item);
        }

        public void ShowAtCursor() => _menu.Show(Cursor.Position);

        public void Dispose() => _menu.Dispose();
    }
}
