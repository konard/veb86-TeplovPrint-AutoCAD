using System;
using System.Reflection;
#if ZWCAD
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#elif NANOCAD
using CadApp = HostMgd.ApplicationServices.Application;
#elif ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>



/// </summary>
public static class CadMenuInstaller
{
    /// <summary>


    /// </summary>
    private const string MenuName = "Teplov";
#if ZWCAD
    private const string PreferredMenuGroupName = "ZWCAD";
#else
    private const string PreferredMenuGroupName = "ACAD";
#endif

    /// <summary>


    /// </summary>
    public static void Install()
    {
        try
        {
            ShowMenuBar();

#if ACAD_CORE

            var acadApplication = GetAcadApplication();
            var menuBar = acadApplication == null ? null : GetProperty(acadApplication, "MenuBar");
            var menuGroups = acadApplication == null ? null : GetProperty(acadApplication, "MenuGroups");
#else

            var menuBar = CadApp.MenuBar;
            var menuGroups = CadApp.MenuGroups;
#endif
            if (menuBar == null || menuGroups == null)
            {
#if ZWCAD
                WriteMessage("\nПлагин пакетной печати загружен, но текущий CAD не предоставляет интерфейс строки меню.");
#endif
                return;
            }


            var menuGroup = InvokeItem(menuGroups, PreferredMenuGroupName)
                ?? InvokeItem(menuGroups, 0);
            if (menuGroup == null)
            {
#if ZWCAD
                WriteMessage("\nПлагин пакетной печати загружен, но группа меню по умолчанию недоступна.");
#endif
                return;
            }

            var menus = GetProperty(menuGroup, "Menus");
            if (menus == null)
            {
#if ZWCAD
                WriteMessage("\nПлагин пакетной печати загружен, но коллекция меню недоступна.");
#endif
                return;
            }

            var menu = GetOrCreateMenu(menus);
            if (menu == null)
            {
#if ZWCAD
                WriteMessage("\nПлагин пакетной печати загружен, но создать меню не удалось.");
#endif
                return;
            }



            var oldCount = Convert.ToInt32(GetProperty(menu, "Count") ?? 0);
            for (var i = 0; i < oldCount; i++)
            {
                var first = TryInvoke(menu, "Item", 0);
                if (first == null)
                {
                    break;
                }

                TryInvoke(first, "Delete");
            }


            AddMenuItem(menu, "Пакетная печать", "ZBP_SHOW_PANEL");
            AddMenuItem(menu, "Добавить формат", "ZBP_ADD_TITLE_BLOCK");
            AddMenuItem(menu, "Библиотека форматов", "ZBP_MANAGE_LIBRARY");
            AddSeparator(menu);

            AddMenuItem(menu, "Настройки", "ZBP_SETTINGS");
            AddMenuItem(menu, "Настройка сокращений", "ZBP_SHORTCUT_SETTINGS");

            AddMenuItem(menu, "О программе", "ZBP_ABOUT");


            if (!IsMenuOnMenuBar(menu))
            {
                var menuBarCount = Convert.ToInt32(GetProperty(menuBar, "Count") ?? 0);
                TryInvoke(menu, "InsertInMenuBar", menuBarCount + 1);
            }

            if (!IsMenuOnMenuBar(menu))
            {
                WriteMessage("\nПлагин пакетной печати загружен, но меню не удалось добавить в строку меню.");
                return;
            }

#if ZWCAD
            WriteMessage("\nМеню пакетной печати загружено.");
#endif
        }
        catch (Exception ex)
        {
            WriteMessage("\nНе удалось загрузить меню пакетной печати: " + ex.Message);
        }
    }

    /// <summary>


    /// </summary>
    private static object? GetOrCreateMenu(object menus)
    {
        var menu = TryGetMenu(menus);
        if (menu != null)
        {
            return menu;
        }

        return TryInvoke(menus, "Add", MenuName);
    }

    /// <summary>

    /// </summary>
    private static object? TryGetMenu(object menus)
    {
        try
        {
            return menus.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, menus, new object[] { MenuName });
        }
        catch (TargetInvocationException)
        {

            return null;
        }
        catch (ArgumentException)
        {

            return null;
        }
    }

    /// <summary>

    /// </summary>
    private static bool IsMenuOnMenuBar(object menu)
    {
        try
        {
            return Convert.ToBoolean(menu.GetType().InvokeMember("OnMenuBar", BindingFlags.GetProperty, null, menu, null));
        }
        catch (TargetInvocationException)
        {

            return false;
        }
        catch (ArgumentException)
        {

            return false;
        }
    }

    /// <summary>

    /// </summary>
    private static void ShowMenuBar()
    {
        try
        {
            CadApp.SetSystemVariable("MENUBAR", 1);
        }
        catch
        {
        }
    }

    /// <summary>

    /// </summary>
    private static void AddMenuItem(object menu, string label, string commandName)
    {
        var count = Convert.ToInt32(GetProperty(menu, "Count") ?? 0);
        TryInvoke(menu, "AddMenuItem", count, label, CreateMenuMacro(commandName));
    }

    /// <summary>

    /// </summary>
    private static void AddSeparator(object menu)
    {
        var count = Convert.ToInt32(GetProperty(menu, "Count") ?? 0);
        TryInvoke(menu, "AddSeparator", count);
    }

    /// <summary>

    /// </summary>
    private static string CreateMenuMacro(string commandName)
    {
        const char esc = (char)3;
        return new string(esc, 2) + "_" + commandName + " ";
    }

    /// <summary>

    /// </summary>
    private static object? InvokeItem(object collection, object index)
    {
        return TryInvoke(collection, "Item", index);
    }

#if ACAD_CORE
    /// <summary>


    /// </summary>
    private static object? GetAcadApplication()
    {
        var applicationTypeNames = new[]
        {
            "Autodesk.AutoCAD.ApplicationServices.Application, AcMgd",
            "Autodesk.AutoCAD.ApplicationServices.Core.Application, AcCoreMgd"
        };

        foreach (var typeName in applicationTypeNames)
        {
            var type = Type.GetType(typeName, throwOnError: false);
            if (type == null)
                continue;

            var acadApplication = GetStaticProperty(type, "AcadApplication");
            if (acadApplication != null)
                return acadApplication;
        }

        return null;
    }

    /// <summary>

    /// </summary>
    private static object? GetStaticProperty(Type target, string name)
    {
        try
        {
            return target.InvokeMember(name,
                BindingFlags.GetProperty | BindingFlags.Static | BindingFlags.Public,
                null, null, null);
        }
        catch
        {
            return null;
        }
    }
#endif

    /// <summary>

    /// </summary>
    private static object? GetProperty(object target, string name)
    {
        try
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>

    /// </summary>
    private static object? TryInvoke(object target, string name, params object[] args)
    {
        try
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>

    /// </summary>
    private static void WriteMessage(string message)
    {
        try
        {
            CadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(message);
        }
        catch
        {
        }
    }
}
