using FinXmlProcessor.Domain.Cells;

namespace FinXmlProcessor.Domain.Tables;

public sealed record OutputColumnDefinition(
    string Id,
    string Heading,
    CellType CellType,
    bool Required = false,
    double? Width = null,
    string? NumberFormat = null,
    SensitivityClassification Sensitivity = SensitivityClassification.None,
    bool AllowInRejectionOutput = true)
{
    /// <summary>True when the value may be included verbatim in a rejected-records sheet or report.</summary>
    public bool IsSafeForRejectionOutput => Sensitivity == SensitivityClassification.None ||
                                            (Sensitivity == SensitivityClassification.Sensitive && AllowInRejectionOutput);
}
