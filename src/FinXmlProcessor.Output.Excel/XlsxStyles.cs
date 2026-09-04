using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using CellType = FinXmlProcessor.Domain.Cells.CellType;
using FinXmlProcessor.Domain.Tables;

namespace FinXmlProcessor.Output.Excel;

/// <summary>A small fixed style table plus one cell format per distinct profile-declared number format.</summary>
internal sealed class XlsxStyles
{
    private const uint FirstCustomNumberFormatId = 164;

    private readonly Dictionary<(CellType, string?), uint> _styleByTypeAndFormat;

    private XlsxStyles(Dictionary<(CellType, string?), uint> styleByTypeAndFormat, uint headerStyle, uint textStyle, uint warningStyle)
    {
        _styleByTypeAndFormat = styleByTypeAndFormat;
        HeaderStyle = headerStyle;
        TextStyle = textStyle;
        WarningStyle = warningStyle;
    }

    public uint HeaderStyle { get; }

    public uint TextStyle { get; }

    public uint WarningStyle { get; }

    public uint[] ColumnStyles(OutputTableDefinition table)
    {
        var result = new uint[table.Columns.Count];
        for (int i = 0; i < result.Length; i++)
        {
            OutputColumnDefinition column = table.Columns[i];
            if (!_styleByTypeAndFormat.TryGetValue((column.CellType, column.NumberFormat), out uint style))
            {
                style = _styleByTypeAndFormat[(column.CellType, null)];
            }

            result[i] = style;
        }

        return result;
    }

    public static XlsxStyles Create(WorkbookPart workbookPart, IReadOnlyList<OutputTableDefinition> tables)
    {
        var fonts = new Fonts(
            new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new FontSize { Val = 11 }, new Color { Rgb = "FF9C5700" }, new FontName { Val = "Calibri" }))
        { Count = 3 };

        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFD9E1F2" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFFFEB9C" }) { PatternType = PatternValues.Solid }))
        { Count = 4 };

        var borders = new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 };

        var numberFormats = new NumberingFormats();
        var formatIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint nextId = FirstCustomNumberFormatId;

        uint RegisterFormat(string code)
        {
            if (!formatIds.TryGetValue(code, out uint id))
            {
                id = nextId++;
                formatIds[code] = id;
                numberFormats.Append(new NumberingFormat { NumberFormatId = id, FormatCode = code });
            }

            return id;
        }

        var cellFormats = new CellFormats();
        var styles = new Dictionary<(CellType, string?), uint>();
        uint index = 0;

        uint Add(CellFormat format)
        {
            cellFormats.Append(format);
            return index++;
        }

        Add(new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 0 }); // 0 default
        uint header = Add(new CellFormat { FontId = 1, FillId = 2, BorderId = 0, NumberFormatId = 0, ApplyFont = true, ApplyFill = true });
        uint text = Add(new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 49, ApplyNumberFormat = true }); // "@"
        styles[(CellType.Text, null)] = text;
        styles[(CellType.Integer, null)] = Add(new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 1, ApplyNumberFormat = true }); // "0"
        styles[(CellType.Decimal, null)] = Add(new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 4, ApplyNumberFormat = true }); // "#,##0.00"
        styles[(CellType.Date, null)] = Add(new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = RegisterFormat("yyyy-mm-dd"), ApplyNumberFormat = true });
        styles[(CellType.DateTime, null)] = Add(new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = RegisterFormat("yyyy-mm-dd hh:mm:ss"), ApplyNumberFormat = true });
        styles[(CellType.Boolean, null)] = Add(new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 0 });
        uint warning = Add(new CellFormat { FontId = 2, FillId = 3, BorderId = 0, NumberFormatId = 0, ApplyFont = true, ApplyFill = true });

        foreach (OutputColumnDefinition column in tables.SelectMany(t => t.Columns))
        {
            if (string.IsNullOrWhiteSpace(column.NumberFormat) || column.CellType == CellType.Text || styles.ContainsKey((column.CellType, column.NumberFormat)))
            {
                continue;
            }

            uint formatId = RegisterFormat(column.NumberFormat);
            styles[(column.CellType, column.NumberFormat)] = Add(new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = formatId, ApplyNumberFormat = true });
        }

        numberFormats.Count = (uint)formatIds.Count;
        cellFormats.Count = index;

        var stylesheet = new Stylesheet();
        if (formatIds.Count > 0)
        {
            stylesheet.Append(numberFormats);
        }

        stylesheet.Append(fonts);
        stylesheet.Append(fills);
        stylesheet.Append(borders);
        stylesheet.Append(new CellStyleFormats(new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 0 }) { Count = 1 });
        stylesheet.Append(cellFormats);
        stylesheet.Append(new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 });

        WorkbookStylesPart stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = stylesheet;
        stylesPart.Stylesheet.Save();

        return new XlsxStyles(styles, header, text, warning);
    }
}
