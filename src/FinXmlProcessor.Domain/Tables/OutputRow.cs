using FinXmlProcessor.Domain.Cells;

namespace FinXmlProcessor.Domain.Tables;

/// <summary>One mapped output row. Cells are ordered exactly like the target table columns.</summary>
public sealed record OutputRow(string TableId, long SourceOrdinal, string? SafeSourceIdentifier, IReadOnlyList<CellValue> Cells);
