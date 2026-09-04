namespace FinXmlProcessor.Domain.Issues;

/// <summary>Stable machine-readable codes. Add new codes; never renumber or repurpose existing ones.</summary>
public static class IssueCodes
{
    // File-level (FILE-xxx)
    public const string FileNotFound = "FILE-001";
    public const string FileUnsupportedExtension = "FILE-002";
    public const string FileNotAccessible = "FILE-003";
    public const string FileUnstable = "FILE-004";
    public const string FileTooLarge = "FILE-005";
    public const string FileEmpty = "FILE-006";
    public const string FileDuplicate = "FILE-007";
    public const string FileUnsupportedFormat = "FILE-008";
    public const string FileHashMismatch = "FILE-009";

    // XML-level (XML-xxx)
    public const string XmlMalformed = "XML-001";
    public const string XmlSchemaViolation = "XML-002";
    public const string XmlDtdProhibited = "XML-003";
    public const string XmlRecordPathNotFound = "XML-004";
    public const string XmlRecordTooLarge = "XML-005";
    public const string XmlUnexpectedRoot = "XML-006";

    // Mapping / conversion (MAP-xxx)
    public const string MapRequiredMissing = "MAP-001";
    public const string MapInvalidInteger = "MAP-002";
    public const string MapInvalidDecimal = "MAP-003";
    public const string MapInvalidDate = "MAP-004";
    public const string MapInvalidDateTime = "MAP-005";
    public const string MapInvalidBoolean = "MAP-006";
    public const string MapSourceNotFound = "MAP-007";
    public const string MapTransformFailed = "MAP-008";

    // Validation (VAL-xxx)
    public const string ValRequired = "VAL-001";
    public const string ValMinLength = "VAL-002";
    public const string ValMaxLength = "VAL-003";
    public const string ValPattern = "VAL-004";
    public const string ValAllowedValues = "VAL-005";
    public const string ValDecimalRange = "VAL-006";
    public const string ValDateRange = "VAL-007";
    public const string ValRecordDuplicate = "VAL-008";

    // Output (OUT-xxx)
    public const string OutputSheetSplit = "OUT-001";
    public const string OutputPackageInvalid = "OUT-002";
    public const string OutputWriteFailed = "OUT-003";
    public const string OutputTextTruncated = "OUT-004";

    // Job-level (JOB-xxx)
    public const string JobCancelled = "JOB-001";
    public const string JobUnexpectedError = "JOB-002";
    public const string JobLockUnavailable = "JOB-003";
    public const string JobProfileInvalid = "JOB-004";
    public const string JobDeliveryFailed = "JOB-005";
    public const string JobConfigurationInvalid = "JOB-006";
}
