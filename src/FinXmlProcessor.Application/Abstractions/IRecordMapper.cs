using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Sources;
using FinXmlProcessor.Domain.Tables;

namespace FinXmlProcessor.Application.Abstractions;

/// <summary>Result of mapping one source record. Either rows were produced, or the record was rejected with issues.</summary>
public sealed record MappedRecord(IReadOnlyList<OutputRow> Rows, IReadOnlyList<RecordIssue> Issues, string? SafeIdentifier)
{
    public bool IsRejected => Issues.Any(i => i.Severity >= IssueSeverity.RecordRejected);
}

/// <summary>Converts one source record into zero or more typed output rows. Must not retain the envelope.</summary>
public interface IRecordMapper
{
    MappedRecord Map(SourceRecordEnvelope record);
}

/// <summary>Selected by the profile's mapper type so future business-specific mappers can be plugged in.</summary>
public interface IRecordMapperFactory
{
    string MapperType { get; }

    IRecordMapper Create(Profiles.CompiledProfile profile);
}
