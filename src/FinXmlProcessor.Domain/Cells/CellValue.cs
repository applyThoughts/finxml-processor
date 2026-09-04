using System.Globalization;

namespace FinXmlProcessor.Domain.Cells;

/// <summary>
/// A single typed output cell. Immutable and allocation-light: only text holds a reference.
/// Identifiers are always <see cref="CellType.Text"/> so long account/reference numbers are never coerced to numbers.
/// </summary>
public readonly record struct CellValue
{
    private readonly string? _text;
    private readonly decimal _number;
    private readonly DateTime _dateTime;
    private readonly bool _boolean;

    private CellValue(CellType type, bool isBlank, string? text, decimal number, DateTime dateTime, bool boolean)
    {
        Type = type;
        IsBlank = isBlank;
        _text = text;
        _number = number;
        _dateTime = dateTime;
        _boolean = boolean;
    }

    public CellType Type { get; }

    public bool IsBlank { get; }

    public static CellValue Blank(CellType type = CellType.Text) => new(type, true, null, 0m, default, false);

    public static CellValue FromText(string? text) =>
        text is null ? Blank(CellType.Text) : new(CellType.Text, false, text, 0m, default, false);

    public static CellValue FromInteger(long value) => new(CellType.Integer, false, null, value, default, false);

    public static CellValue FromDecimal(decimal value) => new(CellType.Decimal, false, null, value, default, false);

    public static CellValue FromDate(DateOnly value) =>
        new(CellType.Date, false, null, 0m, value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), false);

    public static CellValue FromDateTime(DateTime value) => new(CellType.DateTime, false, null, 0m, value, false);

    public static CellValue FromBoolean(bool value) => new(CellType.Boolean, false, null, 0m, default, value);

    public string TextValue => Type == CellType.Text && !IsBlank ? _text! : throw NotOfType(CellType.Text);

    public long IntegerValue => Type == CellType.Integer && !IsBlank ? (long)_number : throw NotOfType(CellType.Integer);

    public decimal DecimalValue => Type == CellType.Decimal && !IsBlank ? _number : throw NotOfType(CellType.Decimal);

    public DateOnly DateValue => Type == CellType.Date && !IsBlank ? DateOnly.FromDateTime(_dateTime) : throw NotOfType(CellType.Date);

    public DateTime DateTimeValue => Type == CellType.DateTime && !IsBlank ? _dateTime : throw NotOfType(CellType.DateTime);

    public bool BooleanValue => Type == CellType.Boolean && !IsBlank ? _boolean : throw NotOfType(CellType.Boolean);

    /// <summary>Culture-invariant, round-trippable serialization used for duplicate keys, reports and tests.</summary>
    public string ToInvariantString()
    {
        if (IsBlank)
        {
            return string.Empty;
        }

        return Type switch
        {
            CellType.Text => _text!,
            CellType.Integer => ((long)_number).ToString(CultureInfo.InvariantCulture),
            CellType.Decimal => _number.ToString(CultureInfo.InvariantCulture),
            CellType.Date => DateOnly.FromDateTime(_dateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CellType.DateTime => _dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            CellType.Boolean => _boolean ? "true" : "false",
            _ => throw new InvalidOperationException($"Unknown cell type {Type}."),
        };
    }

    public override string ToString() => IsBlank ? $"<blank:{Type}>" : $"{Type}:{ToInvariantString()}";

    private InvalidOperationException NotOfType(CellType expected) =>
        new($"Cell is {Type}{(IsBlank ? " (blank)" : string.Empty)}, not {expected}.");
}
