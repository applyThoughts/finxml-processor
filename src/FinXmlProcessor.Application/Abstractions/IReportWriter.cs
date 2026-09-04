using FinXmlProcessor.Application.Reports;

namespace FinXmlProcessor.Application.Abstractions;

/// <summary>Writes one JSON report per run and a human-readable summary. Reports contain counts and sanitized errors only.</summary>
public interface IReportWriter
{
    Task<string> WriteAsync(ProcessingReport report, CancellationToken cancellationToken);

    Task<ProcessingReport?> ReadAsync(string reportPath, CancellationToken cancellationToken);

    string RenderText(ProcessingReport report);
}
