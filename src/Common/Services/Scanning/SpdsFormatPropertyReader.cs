using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

public static class SpdsFormatPropertyReader
{
    public static SpdsFormatInfo TryRead(Entity entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var info = new SpdsFormatInfo { ObjectId = entity.ObjectId, Handle = SafeHandle(entity) };
        var dxfPropertiesFound = false;
        try
        {
            dxfPropertiesFound = TryReadDxf(entity, info);
        }
        catch (Exception ex)
        {
            info.ReadError = "DXF: " + ex.Message;
        }

        try
        {
            ReadPublishedProperties(entity, info);
            if (info.HasPublishedProperties)
            {
                info.ReadMethod = dxfPropertiesFound ? "DXF + COM/reflection" : "COM/reflection";
                return info;
            }
        }
        catch (Exception ex)
        {
            info.ReadError = AppendError(info.ReadError, "COM/reflection: " + ex.Message);
        }

        info.ReadMethod = dxfPropertiesFound ? "DXF" : "fallback";
        return info;
    }

    private static bool TryReadDxf(Entity entity, SpdsFormatInfo info)
    {
        // Используем общий CadEntGet: он динамически находит native-функции
        // AutoCAD/ZWCAD и возвращает тот же набор DXF-пар, что Lisp entget.
        // Это особенно важно для CSoft SPDSFORMAT: Sheet, DrawingName1 и
        // остальные поля не являются XData и часто недоступны через COM.
        if (entity.ObjectId.IsNull || entity.ObjectId.IsErased)
            return false;

        if (!CadEntGet.TryGet(entity.ObjectId, out var values) || values.Length == 0)
            return false;

        return ParseDxfProperties(values, info);
    }

    internal static bool ParseDxfProperties(IReadOnlyList<TypedValue> values, SpdsFormatInfo info)
    {
        var found = false;
        for (var i = 0; i < values.Count; i++)
        {
            var keyValue = values[i];
            if (keyValue.TypeCode != 301 || !(keyValue.Value is string key) || string.IsNullOrWhiteSpace(key))
                continue;

            // СПДС GraphiCS в зависимости от версии записывает значение как
            // 300, обычную строку 1/1000, либо после служебных групп. Ищем
            // первое значение до следующего имени свойства.
            for (var valueIndex = i + 1; valueIndex < values.Count; valueIndex++)
            {
                var value = values[valueIndex];
                if (value.TypeCode == 301)
                    break;
                if (!IsPropertyValueCode(value.TypeCode))
                    continue;

                found |= Assign(info, key.Trim(), value.Value);
                i = valueIndex;
                break;
            }
        }

        return found;
    }

    private static bool IsPropertyValueCode(int typeCode)
        => typeCode == 1       // обычная строка DXF
           || typeCode == 40   // double
           || typeCode == 70   // short integer
           || typeCode == 90   // long integer
           || typeCode == 290  // bool
           || typeCode == 300  // arbitrary string
           || typeCode == 1000;

    private static bool Assign(SpdsFormatInfo info, string key, object? value)
    {
        switch (key.ToUpperInvariant())
        {
            case "FORMAT": info.Format = ToText(value); return true;
            case "MULTIPLICITY": info.Multiplicity = Math.Max(1, ToInt(value, 1)); return true;
            case "ORIENTATION": info.Orientation = ToText(value); return true;
            case "SCALE": info.Scale = ToDouble(value); return true;
            case "DIMSCALE": info.DimScale = ToDouble(value); return true;
            case "ORIGINX": info.OriginX = ToDouble(value); info.HasOriginX = true; return true;
            case "ORIGINY": info.OriginY = ToDouble(value); info.HasOriginY = true; return true;
            case "SHEET": info.Sheet = ToText(value); return true;
            case "SHEETCOUNT": info.SheetCount = ToText(value); return true;
            case "DESIGNATION": info.Designation = ToText(value); return true;
            case "DRAWING TYPE":
                info.DrawingName = PreferNonEmpty(info.DrawingName, ToText(value));
                return true;
            case "DRAWINGNAME1":
                info.DrawingName = PreferNonEmpty(info.DrawingName, ToText(value));
                return true;
            case "STAGE": info.Stage = ToText(value); return true;
            case "PAPERBOUND": info.PaperBound = ToBool(value); return true;
            case "STD VERSION": info.StdVersion = ToText(value); return true;
            default: return false;
        }
    }

    private static void ReadPublishedProperties(object entity, SpdsFormatInfo info)
    {
        info.Format = PreferNonEmpty(info.Format, ReadFirstString(entity, "Format"));
        info.Multiplicity = Math.Max(1, ReadInt(entity, "Multiplicity", info.Multiplicity));
        info.Orientation = PreferNonEmpty(info.Orientation, ReadFirstString(entity, "Orientation"));
        info.Scale = PreferPositive(info.Scale, ReadDouble(entity, "Scale", info.Scale));
        info.DimScale = PreferPositive(info.DimScale, ReadDouble(entity, "DimScale", info.DimScale));
        info.OriginX = ReadDouble(entity, "OriginX", info.OriginX);
        info.OriginY = ReadDouble(entity, "OriginY", info.OriginY);
        info.HasOriginX |= ReadProperty(entity, "OriginX") != null;
        info.HasOriginY |= ReadProperty(entity, "OriginY") != null;
        // В разных версиях СПДС эти же видимые в палитре поля могут иметь
        // разные внутренние имена. Основные варианты идут первыми.
        info.Sheet = PreferNonEmpty(info.Sheet, ReadFirstString(entity, "Sheet", "SheetNumber"));
        info.SheetCount = PreferNonEmpty(info.SheetCount, ReadFirstString(entity, "SheetCount"));
        info.Designation = PreferNonEmpty(info.Designation, ReadFirstString(entity, "Designation"));
        info.DrawingName = PreferNonEmpty(info.DrawingName, ReadFirstString(entity, "DrawingName1", "DrawingName", "SheetName"));
        info.Stage = PreferNonEmpty(info.Stage, ReadFirstString(entity, "Stage"));
        info.PaperBound = ToBool(ReadProperty(entity, "PaperBound"));
        info.StdVersion = PreferNonEmpty(info.StdVersion, ReadFirstString(entity, "Std version", "StdVersion"));
    }

    private static string ReadString(object target, string name) => ToText(ReadProperty(target, name));
    private static string ReadFirstString(object target, params string[] names)
        => names.Select(name => ReadString(target, name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static int ReadInt(object target, string name, int fallback) => ToInt(ReadProperty(target, name), fallback);

    private static string PreferNonEmpty(string current, string value)
        => string.IsNullOrWhiteSpace(value) ? current : value;

    private static double PreferPositive(double current, double value)
        => value > 0 ? value : current;

    private static double ReadDouble(object target, string name, double fallback)
    {
        var value = ReadProperty(target, name);
        return value == null ? fallback : ToDouble(value);
    }

    private static object? ReadProperty(object target, string name)
    {
        var type = target.GetType();
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property != null && property.CanRead)
        {
            try { return property.GetValue(target, null); }
            catch { }
        }

        try
        {
            var acadObject = type.GetProperty("AcadObject", BindingFlags.Instance | BindingFlags.Public)?.GetValue(target, null);
            return acadObject?.GetType().InvokeMember(
                name,
                BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                null,
                acadObject,
                null);
        }
        catch
        {
            return null;
        }
    }

    private static string SafeHandle(Entity entity)
    {
        try { return entity.Handle.ToString(); }
        catch { return ""; }
    }

    private static string ToText(object? value) => value?.ToString()?.Trim() ?? "";

    private static int ToInt(object? value, int fallback)
    {
        try { return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private static double ToDouble(object? value)
    {
        try { return value == null ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return 0d; }
    }

    private static bool ToBool(object? value)
    {
        try { return value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    private static string AppendError(string current, string next)
        => string.IsNullOrWhiteSpace(current) ? next : current + "; " + next;

}
