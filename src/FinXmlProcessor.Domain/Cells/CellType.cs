namespace FinXmlProcessor.Domain.Cells;

/// <summary>The typed representation a mapped value is expected to have in the output table.</summary>
public enum CellType
{
    Text = 0,
    Integer = 1,
    Decimal = 2,
    Date = 3,
    DateTime = 4,
    Boolean = 5,
}
