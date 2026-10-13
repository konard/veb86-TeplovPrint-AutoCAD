using System;
using System.Text;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#elif NANOCAD
using Teigha.DatabaseServices;
using Teigha.Geometry;
#else
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// Читает текстовые объекты через те же DXF-коды 0, 1 и 10, что и Lisp-функция <c>entget</c>.
/// Поддержка кодов выравнивания текста TCH_TEXT и TCH_MTEXT.
/// </summary>
internal static class TianzhengTextReader
{
    /// <summary>
    /// Возвращает отображаемый текст и точку позиционирования объектов TCH_TEXT/TCH_MTEXT.
    /// </summary>

    /// <param name="text">Отображаемый текст.</param>
    /// <param name="point">Точка позиционирования, используемая для проверки попадания в область поля.</param>
    /// <returns>true, если объект распознан как поддерживаемый текст и содержит непустое значение.</returns>
    public static bool TryGetText(Entity entity, out string text, out Point3d point)
    {
        text = "";
        point = Point3d.Origin;
        if (entity == null || entity.ObjectId.IsNull)
        {
            return false;
        }

        if (!CadEntGet.TryGet(entity.ObjectId, out var values))
        {
            return false;
        }

        var dxfName = "";
        var content = new StringBuilder();
        Point3d? insert = null;
        foreach (var value in values)
        {
            if (value.TypeCode == 0)
            {
                dxfName = Convert.ToString(value.Value) ?? "";
                continue;
            }

            if (value.TypeCode == 1 || value.TypeCode == 3)
            {
                var part = Convert.ToString(value.Value);
                if (!string.IsNullOrEmpty(part))
                {
                    content.Append(part);
                }

                continue;
            }

            if (value.TypeCode == 10 && insert == null && TryReadPoint(value.Value, out var found))
            {
                insert = found;
            }
        }

        if (!IsSingleOrMultiLine(dxfName) || content.Length == 0)
        {
            return false;
        }

        text = content.ToString();
        point = insert ?? GetExtentsCenter(entity);
        return true;
    }

    private static bool IsSingleOrMultiLine(string dxfName)
    {
        return dxfName.Equals("TCH_TEXT", StringComparison.OrdinalIgnoreCase)
            || dxfName.Equals("TCH_MTEXT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadPoint(object? raw, out Point3d point)
    {
        point = Point3d.Origin;
        if (raw is Point3d cadPoint)
        {
            point = cadPoint;
            return true;
        }

        if (raw is Point2d cadPoint2d)
        {
            point = new Point3d(cadPoint2d.X, cadPoint2d.Y, 0);
            return true;
        }

        return false;
    }

    private static Point3d GetExtentsCenter(Entity entity)
    {
        try
        {
            var extents = entity.GeometricExtents;
            return new Point3d(
                (extents.MinPoint.X + extents.MaxPoint.X) / 2d,
                (extents.MinPoint.Y + extents.MaxPoint.Y) / 2d,
                0);
        }
        catch
        {
            return Point3d.Origin;
        }
    }
}
