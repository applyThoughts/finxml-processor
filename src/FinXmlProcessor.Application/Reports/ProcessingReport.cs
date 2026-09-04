using System.Text.Json.Serialization;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Jobs;

namespace FinXmlProcessor.Application.Reports;

/// <summary>Per-run report. Contains counts, timings and sanitized issues only. Never raw financial values.</summary>
public sealed class ProcessingReport
{
    public const int CurrentReportVersion = 1;

    [JsonPropertyName("reportVersion")]
    public int ReportVersion { get; set; } = CurrentReportVersion;

    [JsonPropertyName("jobId")]
    public Guid JobId { get; set; }

    [JsonPropertyName("rerunOfJobId")]
    public Guid? RerunOfJobId { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
    public JobStatus Status { get; set; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = string.Empty;

    [JsonPropertyName("businessDate")]
    public DateOnly? BusinessDate { get; set; }

    [JsonPropertyName("source")]
    public SourceInfo Source { get; set; } = new();

    [JsonPropertyName("profile")]
    public ProfileInfo Profile { get; set; } = new();

    [JsonPropertyName("timestamps")]
    public Timestamps Times { get; set; } = new();

    [JsonPropertyName("timingsMs")]
    public Timings TimingsMs { get; set; } = new();

    [JsonPropertyName("counts")]
    public ProcessingCounts Counts { get; set; } = ProcessingCounts.Empty;

    [JsonPropertyName("output")]
    public OutputInfo? Output { get; set; }

    [JsonPropertyName("delivery")]
    public DeliveryInfo? Delivery { get; set; }

    [JsonPropertyName("issueCodeCounts")]
    public Dictionary<string, long> IssueCodeCounts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Capped list of sanitized issues (fatal and warnings first, then a sample of rejections).</summary>
    [JsonPropertyName("issues")]
    public List<ReportIssue> Issues { get; set; } = [];

    [JsonPropertyName("issuesTruncated")]
    public bool IssuesTruncated { get; set; }

    [JsonPropertyName("application")]
    public ApplicationInfo Application { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];

    public sealed class SourceInfo
    {
        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = "local";
    }

    public sealed class ProfileInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("isSynthetic")]
        public bool IsSynthetic { get; set; }
    }

    public sealed class Timestamps
    {
        [JsonPropertyName("createdUtc")]
        public DateTimeOffset CreatedUtc { get; set; }

        [JsonPropertyName("startedUtc")]
        public DateTimeOffset? StartedUtc { get; set; }

        [JsonPropertyName("finishedUtc")]
        public DateTimeOffset? FinishedUtc { get; set; }
    }

    public sealed class Timings
    {
        [JsonPropertyName("acquisition")]
        public long Acquisition { get; set; }

        [JsonPropertyName("validation")]
        public long Validation { get; set; }

        [JsonPropertyName("parsingAndMapping")]
        public long ParsingAndMapping { get; set; }

        [JsonPropertyName("workbookWrite")]
        public long WorkbookWrite { get; set; }

        [JsonPropertyName("delivery")]
        public long Delivery { get; set; }

        [JsonPropertyName("total")]
        public long Total { get; set; }
    }

    public sealed class OutputInfo
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; set; }

        [JsonPropertyName("sheets")]
        public List<string> Sheets { get; set; } = [];
    }

    public sealed class DeliveryInfo
    {
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; set; }

        [JsonPropertyName("deliveredPath")]
        public string? DeliveredPath { get; set; }

        [JsonPropertyName("deliveredSha256")]
        public string? DeliveredSha256 { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    public sealed class ReportIssue
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("severity")]
        [JsonConverter(typeof(JsonStringEnumConverter<IssueSeverity>))]
        public IssueSeverity Severity { get; set; }

        [JsonPropertyName("field")]
        public string? Field { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("sourceOrdinal")]
        public long? SourceOrdinal { get; set; }

        public static ReportIssue From(RecordIssue issue) => new()
        {
            Code = issue.Code,
            Severity = issue.Severity,
            Field = issue.FieldId,
            Message = issue.Message,
            SourceOrdinal = issue.SourceOrdinal,
        };
    }

    public sealed class ApplicationInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = string.Empty;
    }
}
