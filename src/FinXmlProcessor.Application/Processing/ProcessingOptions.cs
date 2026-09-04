namespace FinXmlProcessor.Application.Processing;

/// <summary>Bound from the "Processing" configuration section and editable from Settings. Nothing here is secret.</summary>
public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    /// <summary>Hard cap on input size. Default 1 GiB; expected inputs are 120–200 MB.</summary>
    public long MaxInputBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Reject inputs whose size or last-write time changes within this window (still being written).</summary>
    public int StabilityWindowMilliseconds { get; set; } = 1500;

    /// <summary>Accepted input extensions (case-insensitive).</summary>
    public string[] AllowedExtensions { get; set; } = [".xml"];

    /// <summary>Excel hard limit is 1,048,576 rows including the header. Lower values are used only by tests.</summary>
    public int MaxRowsPerSheet { get; set; } = 1_048_576;

    /// <summary>Maximum number of rejection issues retained on the job and in the report (counts are always exact).</summary>
    public int MaxRetainedRejectionIssues { get; set; } = 500;

    /// <summary>Maximum number of rows written to the "Rejected Records" sheet.</summary>
    public int MaxRejectedSheetRows { get; set; } = 100_000;

    /// <summary>Whether to write the "Rejected Records" sheet at all.</summary>
    public bool IncludeRejectedSheet { get; set; } = true;

    /// <summary>Minimum interval between progress notifications.</summary>
    public int ProgressIntervalMilliseconds { get; set; } = 250;

    /// <summary>Largest single record fragment accepted, in characters. Guards against a runaway record.</summary>
    public int MaxRecordFragmentChars { get; set; } = 4 * 1024 * 1024;

    /// <summary>Output folder; null means the application default output folder.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Input folder watched by "Run Now" and the scheduler; null means the application default input folder.</summary>
    public string? InputDirectory { get; set; }

    /// <summary>Filename pattern (glob) used when picking inputs from the input folder.</summary>
    public string InputPattern { get; set; } = "*.xml";

    /// <summary>Id of the active mapping profile.</summary>
    public string ActiveProfileId { get; set; } = "demo-fintech-v1";

    /// <summary>Whether a completed workbook is handed to the configured delivery provider automatically.</summary>
    public bool DeliverAutomatically { get; set; } = true;
}
