namespace FinXmlProcessor.Domain.Tables;

/// <summary>Drives redaction in logs, reports and rejection output. Never affects the workbook itself.</summary>
public enum SensitivityClassification
{
    /// <summary>May appear in logs, reports and rejection details unmasked.</summary>
    None = 0,

    /// <summary>Masked in logs and reports; may appear in rejection details only when explicitly allowed.</summary>
    Sensitive = 1,

    /// <summary>Never written to logs, reports or rejection details; only to the workbook.</summary>
    Restricted = 2,
}
