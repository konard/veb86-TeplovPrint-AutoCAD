using System;
using System.Collections.Generic;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

public static class DirectoryTableGenerator
{
    public static bool PromptAndGenerate(Document document, IReadOnlyList<PlotJob> jobs, AppSettings settings, out string message)
    {
        message = "";
        var selected = jobs.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            message = "Не выбран ни один лист.";
            return false;
        }

        if (GetEnabledColumns(settings).Count == 0)
        {
            message = "Для ведомости не включено ни одного поля. Включите столбцы в настройках.";
            return false;
        }

        var editor = document.Editor;
        var pointResult = editor.GetPoint(new PromptPointOptions("\nУкажите верхнюю левую точку ведомости чертежей: "));
        if (pointResult.Status != PromptStatus.OK)
        {
            message = "Создание ведомости отменено.";
            return false;
        }

        Generate(document, selected, settings, pointResult.Value);
        message = $"Ведомость чертежей создана; строк: {selected.Count}.";
        return true;
    }

    public static void Generate(Document document, IReadOnlyList<PlotJob> jobs, AppSettings settings, Point3d origin)
    {
        var columns = GetEnabledColumns(settings);
        if (columns.Count == 0)
        {
            return;
        }

        var headerRows = settings.DirectoryDrawHeader ? 1 : 0;
        var totalRows = jobs.Count + headerRows;
        var totalCols = columns.Count;
        var defaultRowHeight = Math.Max(1, settings.DirectoryRowHeight);
        var textHeight = Math.Max(1, settings.DirectoryTextHeight);
        var widthFactor = settings.DirectoryTextWidthFactor > 1e-6 ? settings.DirectoryTextWidthFactor : 1.0;

        using (document.LockDocument())
        using (var tr = document.Database.TransactionManager.StartTransaction())
        {
            var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
            var textStyleId = EnsureTextStyleId(tr, document.Database, settings.DirectoryTextStyleName, widthFactor);
            var layerName = EnsureLayer(tr, document.Database, settings.DirectoryLayerName);


            var tb = new Table { Position = origin };
            tb.SetDatabaseDefaults();
            tb.Layer = layerName;

            if (settings.DirectoryColorIndex > 0 && settings.DirectoryColorIndex <= 256)
            {
                tb.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)settings.DirectoryColorIndex);
            }

            tb.SetSize(totalRows, totalCols);
            tb.GenerateLayout();


            try
            {
                if (tb.Cells[0, 0].IsMerged == true)
                {
                    tb.UnmergeCells(tb.Cells[0, 0].GetMergeRange());
                }
            }
            catch { }


            if (settings.DirectoryDrawHeader)
            {
                for (var col = 0; col < totalCols; col++)
                {
                    var cell = tb.Cells[0, col];
                    var rawText = ToCadDirectoryText(columns[col].Header);

                    cell.TextString = FormatTextWithWidthFactor(rawText, widthFactor);
                    cell.TextHeight = textHeight;
                    cell.Alignment = columns[col].Centered ? CellAlignment.MiddleCenter : CellAlignment.MiddleLeft;
                    if (!textStyleId.IsNull)
                    {
                        cell.TextStyleId = textStyleId;
                    }
                }
            }


            for (var r = 0; r < jobs.Count; r++)
            {
                var tableRow = r + headerRows;
                for (var col = 0; col < totalCols; col++)
                {
                    var column = columns[col];
                    var value = GetColumnValue(column.Key, jobs[r], r, settings);
                    var cell = tb.Cells[tableRow, col];
                    var rawText = ToCadDirectoryText(value);


                    cell.TextString = FormatTextWithWidthFactor(rawText, widthFactor);
                    cell.TextHeight = textHeight;
                    cell.Alignment = column.Centered ? CellAlignment.MiddleCenter : CellAlignment.MiddleLeft;
                    if (!textStyleId.IsNull)
                    {
                        cell.TextStyleId = textStyleId;
                    }
                }
            }


            for (var r = 0; r < totalRows; r++)
            {
                tb.Rows[r].Height = defaultRowHeight;
            }


            var maxColWidths = new double[totalCols];
            for (var col = 0; col < totalCols; col++)
            {
                for (var row = 0; row < totalRows; row++)
                {
                    var text = tb.Cells[row, col].TextString;
                    if (string.IsNullOrEmpty(text)) continue;

                    using (var mt = new MText())
                    {
                        mt.Contents = text;
                        mt.TextHeight = textHeight;
                        if (!textStyleId.IsNull) mt.TextStyleId = textStyleId;
                        mt.Width = 0;
                        mt.LineSpacingFactor = 1.0;

                        double actW = mt.ActualWidth;
                        if (actW > maxColWidths[col])
                        {
                            maxColWidths[col] = actW;
                        }
                    }
                }
            }


#pragma warning disable 0618
            try
            {
                tb.HorizontalCellMargin = textHeight * 0.2;
                tb.VerticalCellMargin = textHeight * 0.1;
            }
            catch { }
#pragma warning restore 0618


            double absoluteColPadding = textHeight * 1.5;

            for (var c = 0; c < totalCols; c++)
            {
                double autoFitWidth = maxColWidths[c] + absoluteColPadding;

                double configuredWidth = columns[c].Width;
                tb.Columns[c].Width = Math.Max(configuredWidth, autoFitWidth);
            }


            if (!settings.DirectoryDrawGridLines)
            {
                try
                {
                    for (var r = 0; r < totalRows; r++)
                    {
                        for (var c = 0; c < totalCols; c++)
                        {
                            tb.Cells[r, c].Borders.Top.IsVisible = false;
                            tb.Cells[r, c].Borders.Bottom.IsVisible = false;
                            tb.Cells[r, c].Borders.Left.IsVisible = false;
                            tb.Cells[r, c].Borders.Right.IsVisible = false;
                        }
                    }
                }
                catch { }
            }

            tb.GenerateLayout();
            try { tb.RecomputeTableBlock(true); } catch { }

            space.AppendEntity(tb);
            tr.AddNewlyCreatedDBObject(tb, true);

            tr.Commit();
        }
    }

    public static bool PromptColumnSize(
        Document document,
        AppSettings settings,
        string columnKey,
        out AppSettings updated,
        out string message)
    {
        updated = settings;
        var column = settings.DirectoryColumns.FirstOrDefault(x =>
            string.Equals(x.Key, columnKey, StringComparison.OrdinalIgnoreCase));
        if (column == null)
        {
            message = "Не найден столбец ведомости для настройки.";
            return false;
        }

        var editor = document.Editor;
        var first = editor.GetPoint(new PromptPointOptions($"\nУкажите первый угол ячейки «{column.Header}»: "));
        if (first.Status != PromptStatus.OK)
        {
            message = "Настройка ширины столбца отменена.";
            return false;
        }

        var second = editor.GetCorner(new PromptCornerOptions($"\nУкажите противоположный угол ячейки «{column.Header}»: ", first.Value));
        if (second.Status != PromptStatus.OK)
        {
            message = "Настройка ширины столбца отменена.";
            return false;
        }

        var width = Math.Abs(second.Value.X - first.Value.X);
        if (width <= 1e-6)
        {
            message = "Ширина выбранной области равна нулю; ширина столбца не изменена.";
            return false;
        }

        column.Width = width;
        AppSettingsStore.Save(updated);
        message = $"Ширина столбца «{column.Header}» установлена: {width:0.##}.";
        return true;
    }

    public static bool PromptRowHeight(Document document, AppSettings settings, out AppSettings updated, out string message)
    {
        updated = settings;
        var options = new PromptDistanceOptions("\nУкажите две точки, задающие высоту строки ведомости: ")
        {
            UseDefaultValue = false,
            Only2d = true
        };
        var result = document.Editor.GetDistance(options);
        if (result.Status != PromptStatus.OK)
        {
            message = "Настройка высоты строки отменена.";
            return false;
        }

        var height = Math.Abs(result.Value);
        if (height <= 1e-6)
        {
            message = "Измеренная высота строки равна нулю; настройка не изменена.";
            return false;
        }

        updated.DirectoryRowHeight = height;
        AppSettingsStore.Save(updated);
        message = $"Высота строки ведомости установлена: {height:0.##}.";
        return true;
    }

    public static bool PromptTextAppearance(
        Document document,
        AppSettings settings,
        out AppSettings updated,
        out string message)
    {
        updated = settings;
        if (document == null)
        {
            message = "Нет доступного чертежа CAD.";
            return false;
        }

        try
        {
            var options = new PromptEntityOptions("\nВыберите текст-образец для ведомости чертежей: ");
            options.SetRejectMessage("\nВыберите однострочный, многострочный текст или текст атрибута.");
            options.AddAllowedClass(typeof(DBText), false);
            options.AddAllowedClass(typeof(MText), false);

            var selection = document.Editor.GetEntity(options);
            if (selection.Status != PromptStatus.OK)
            {
                message = "Выбор стиля текста ведомости отменён.";
                return false;
            }

            using var tr = document.Database.TransactionManager.StartTransaction();
            if (tr.GetObject(selection.ObjectId, OpenMode.ForRead, false) is not Entity entity)
            {
                message = "Выбранный объект не является допустимым текстом.";
                return false;
            }

            double textHeight;
            double widthFactor;
            ObjectId textStyleId;
            if (entity is DBText dbText)
            {
                textHeight = dbText.Height;
                widthFactor = dbText.WidthFactor;
                textStyleId = dbText.TextStyleId;
            }
            else if (entity is MText mText)
            {
                textHeight = mText.TextHeight;
                textStyleId = mText.TextStyleId;
                widthFactor = 1d;
                if (!textStyleId.IsNull
                    && tr.GetObject(textStyleId, OpenMode.ForRead, false) is TextStyleTableRecord mTextStyle
                    && mTextStyle.XScale > 0)
                {
                    widthFactor = mTextStyle.XScale;
                }
            }
            else
            {
                message = "Выберите однострочный, многострочный текст или текст атрибута.";
                return false;
            }

            var textStyleName = "";
            if (!textStyleId.IsNull
                && tr.GetObject(textStyleId, OpenMode.ForRead, false) is TextStyleTableRecord textStyle)
            {
                textStyleName = textStyle.Name ?? "";
            }

            settings.DirectoryColorIndex = Math.Max(0, Math.Min(256, entity.ColorIndex));
            if (textHeight > 1e-6)
            {
                settings.DirectoryTextHeight = textHeight;
            }
            if (widthFactor > 1e-6)
            {
                settings.DirectoryTextWidthFactor = widthFactor;
            }
            settings.DirectoryTextStyleName = textStyleName;
            settings.DirectoryLayerName = string.IsNullOrWhiteSpace(entity.Layer) ? "0" : entity.Layer;

            tr.Commit();
            AppSettingsStore.Save(settings);
            updated = settings;
            message =
                $"Параметры ведомости считаны из выбранного текста: цвет {settings.DirectoryColorIndex}, " +
                $"высота {settings.DirectoryTextHeight:0.##}, коэффициент ширины {settings.DirectoryTextWidthFactor:0.##}, " +
                $"стиль «{(string.IsNullOrWhiteSpace(settings.DirectoryTextStyleName) ? "По умолчанию" : settings.DirectoryTextStyleName)}», " +
                $"слой «{settings.DirectoryLayerName}».";
            return true;
        }
        catch (Exception ex)
        {
            message = "Не удалось прочитать стиль текста. Убедитесь, что активный чертёж всё ещё открыт: " + ex.Message;
            return false;
        }
    }

    private static List<DirectoryColumnSetting> GetEnabledColumns(AppSettings settings)
    {
        return (settings.DirectoryColumns ?? new List<DirectoryColumnSetting>())
            .Where(x => x.Enabled && x.Width > 0)
            .Select(x => x.Clone())
            .ToList();
    }

    private static string GetColumnValue(string key, PlotJob job, int rowIndex, AppSettings settings)
    {
        return key switch
        {
            "Sequence" => (rowIndex + 1).ToString(),
            "DrawingNumber" => job.DrawingNumber,
            "Title" => job.Title,
            "PaperName" => FileNameSanitizer.NormalizeLongPaperFraction(
                OutputPaperNameResolver.Resolve(job, settings.LongPaperSnapToleranceMm),
                settings.LongPaperNameFormat),
            "Date" => job.Date,
            "Revision" => job.Revision,
            "Phase" => job.Phase,
            "Info1" => job.Info1,
            "Info2" => job.Info2,
            _ => ""
        } ?? "";
    }

    private static string ToCadDirectoryText(string value)
    {
        return (value ?? "").Replace('\u2215', '/');
    }

    /// <summary>

    /// </summary>
    private static string FormatTextWithWidthFactor(string text, double factor)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return $"\\W{factor:0.##};{text}";
    }

    private static string EnsureLayer(Transaction tr, Database db, string? configuredName)
    {
        var layerName = string.IsNullOrWhiteSpace(configuredName) ? "0" : configuredName!.Trim();
        try
        {
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(layerName))
            {
                return layerName;
            }

            table.UpgradeOpen();
            var record = new LayerTableRecord { Name = layerName };
            table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return layerName;
        }
        catch
        {
            return "0";
        }
    }

    private static ObjectId EnsureTextStyleId(Transaction tr, Database db, string? textStyleName, double widthFactor)
    {
        if (string.IsNullOrWhiteSpace(textStyleName))
        {
            return ObjectId.Null;
        }

        try
        {
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            var styleName = textStyleName!.Trim();
            if (table.Has(styleName))
            {
                return table[styleName];
            }

            if (!string.Equals(styleName, "Arial", StringComparison.OrdinalIgnoreCase))
            {
                return ObjectId.Null;
            }

            table.UpgradeOpen();
            var record = new TextStyleTableRecord
            {
                Name = styleName,
                FileName = "arial.ttf",
                XScale = 1.0
            };
            var id = table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return id;
        }
        catch
        {
            return ObjectId.Null;
        }
    }
}
