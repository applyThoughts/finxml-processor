using System.Xml.Linq;

namespace FinXmlProcessor.Domain.Sources;

/// <summary>
/// The current record only. The reader owns exactly one envelope at a time; consumers must not retain
/// <see cref="Fragment"/> beyond the mapping call, otherwise memory grows with the record count.
/// </summary>
public sealed record SourceRecordEnvelope(long SourceOrdinal, long ApproximateBytePosition, XElement Fragment);
