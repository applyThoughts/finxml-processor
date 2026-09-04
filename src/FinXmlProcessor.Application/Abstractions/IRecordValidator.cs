using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Tables;

namespace FinXmlProcessor.Application.Abstractions;

/// <summary>Validates mapped rows without any UI or infrastructure dependency. Returns sanitized issues only.</summary>
public interface IRecordValidator
{
    void Validate(IReadOnlyList<OutputRow> rows, long sourceOrdinal, ICollection<RecordIssue> issues);
}
