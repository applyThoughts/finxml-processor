using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Naming;
using FinXmlProcessor.Application.Reports;
using FinXmlProcessor.Domain.Jobs;

namespace FinXmlProcessor.Infrastructure.Reports;

public sealed class JsonReportWriter : IReportWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAppPaths _paths;

    public JsonReportWriter(IAppPaths paths)
    {
        _paths = paths;
    }

    public async Task<string> WriteAsync(ProcessingReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.Reports);
        DateOnly date = report.BusinessDate ?? DateOnly.FromDateTime(report.Times.CreatedUtc.UtcDateTime);
        string path = Path.Combine(_paths.Reports, OutputNaming.ReportFileName(date, report.JobId));
        string temp = path + ".tmp";
        await using (FileStream stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, report, Json, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
        return path;
    }

    public async Task<ProcessingReport?> ReadAsync(string reportPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(reportPath))
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(reportPath);
        return await JsonSerializer.DeserializeAsync<ProcessingReport>(stream, Json, cancellationToken).ConfigureAwait(false);
    }

    public string RenderText(ProcessingReport report)
    {
        var sb = new StringBuilder();
        ProcessingCounts c = report.Counts;
        sb.AppendLine(CultureInfo.InvariantCulture, $"{report.Application.Name} {report.Application.Version} — processing report");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Job:            {report.JobId:D}{(report.RerunOfJobId is Guid r ? $" (rerun of {r:D})" : string.Empty)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Outcome:        {report.Outcome} (status {report.Status}, trigger {report.Trigger})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Business date:  {report.BusinessDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source:         {report.Source.FileName} ({report.Source.SizeBytes:N0} bytes, sha256 {report.Source.Sha256 ?? "-"})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Profile:        {report.Profile.Id} {report.Profile.Version}{(report.Profile.IsSynthetic ? " [synthetic demo]" : string.Empty)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Started:        {report.Times.StartedUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "-"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Finished:       {report.Times.FinishedUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "-"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Records:        seen {c.RecordsSeen:N0}, accepted {c.RecordsAccepted:N0}, rejected {c.RecordsRejected:N0}, duplicates {c.RecordDuplicates:N0}, warnings {c.WarningCount:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Rows written:   {c.RowsWritten:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Timings (ms):   validation {report.TimingsMs.Validation}, parse+map {report.TimingsMs.ParsingAndMapping}, workbook {report.TimingsMs.WorkbookWrite}, delivery {report.TimingsMs.Delivery}, total {report.TimingsMs.Total}");
        if (report.Output is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Output:         {report.Output.Path} ({report.Output.SizeBytes:N0} bytes, sha256 {report.Output.Sha256 ?? "-"})");
        }

        if (report.Delivery is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Delivery:       {report.Delivery.Provider} {(report.Delivery.Succeeded ? "succeeded" : "FAILED")} {report.Delivery.DeliveredPath ?? report.Delivery.Error ?? string.Empty}");
        }

        if (report.IssueCodeCounts.Count > 0)
        {
            sb.AppendLine("Issue codes:");
            foreach ((string code, long count) in report.IssueCodeCounts.OrderByDescending(kv => kv.Value))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {code,-10} {count:N0}");
            }
        }

        if (report.Issues.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Issues ({report.Issues.Count}{(report.IssuesTruncated ? ", truncated" : string.Empty)}):");
            foreach (ProcessingReport.ReportIssue issue in report.Issues.Take(50))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{issue.Severity}] {issue.Code}{(issue.SourceOrdinal is long o ? $" record {o}" : string.Empty)}{(issue.Field is null ? string.Empty : $" {issue.Field}")}: {issue.Message}");
            }
        }

        foreach (string note in report.Notes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Note: {note}");
        }

        return sb.ToString();
    }
}
