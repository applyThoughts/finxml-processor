using System.Globalization;
using System.Text;
using System.Xml;

namespace FinXmlProcessor.TestDataGenerator;

/// <summary>Options for the streaming synthetic generator. All rates are per record and deterministic for a seed.</summary>
public sealed class GeneratorOptions
{
    public int Seed { get; set; } = 42;

    /// <summary>Exact record count. Ignored when <see cref="ApproximateBytes"/> is set.</summary>
    public long Records { get; set; } = 1000;

    /// <summary>Generate until the output reaches roughly this size.</summary>
    public long? ApproximateBytes { get; set; }

    public double MissingRequiredRate { get; set; }

    public double InvalidDateRate { get; set; }

    public double InvalidDecimalRate { get; set; }

    public double DuplicateRate { get; set; }

    public double SpecialCharacterRate { get; set; }

    public double LongFieldRate { get; set; }

    public double InvalidStatusRate { get; set; }

    /// <summary>Use a default namespace (xmlns="...") instead of the "t" prefix. Same infoset, different serialization.</summary>
    public bool DefaultNamespace { get; set; }

    /// <summary>Stop writing part-way through a record and close the file, producing malformed XML.</summary>
    public bool Truncate { get; set; }

    /// <summary>Include a DOCTYPE declaration, which the processor must reject.</summary>
    public bool IncludeDoctype { get; set; }

    /// <summary>Whether to write indentation (larger, more human-readable files).</summary>
    public bool Indent { get; set; } = true;

    public DateOnly StartDate { get; set; } = new(2026, 9, 1);
}

public sealed record GenerationSummary(long Records, long Bytes, long ExpectedMissingRequired, long ExpectedInvalidDates, long ExpectedInvalidDecimals, long ExpectedDuplicates, long ExpectedLongFields, long ExpectedInvalidStatus)
{
    public long ExpectedRejected => ExpectedMissingRequired + ExpectedInvalidDates + ExpectedInvalidDecimals + ExpectedDuplicates + ExpectedLongFields + ExpectedInvalidStatus;
}

/// <summary>
/// Streams a synthetic fintech-style XML document matching the demo-fintech-v1 profile. Never buffers the dataset:
/// each record is written straight to the target stream. Every value is obviously fictitious.
/// </summary>
public static class SyntheticDataGenerator
{
    public const string Namespace = "urn:example:fintech:demo:v1";

    private static readonly string[] Counterparties =
    [
        "Fictional Grocers Ltd", "Example Utilities Co", "Demo Coffee Roasters", "Placeholder Insurance plc",
        "Sample Streaming Services", "Test Transit Authority", "Mock Bookshop", "Imaginary Airlines",
        "Prototype Pharmacy", "Dummy Hardware Store", "Specimen Telecom", "Example Payroll Services",
    ];

    private static readonly string[] Descriptions =
    [
        "Card purchase", "Direct debit", "Salary", "Transfer to savings", "Refund", "Subscription renewal",
        "ATM withdrawal", "Interest credit", "Fee reversal", "Standing order", "Merchant settlement", "Loan repayment",
    ];

    private static readonly string[] Currencies = ["USD", "USD", "USD", "EUR", "GBP", "CAD"];

    private static readonly string[] SpecialSnippets =
    [
        "Café & Bäckerei <Straße> \"quoted\" 'apos'", "Ünïcödé ✓ 日本語 テスト", "Tab\tand\r\nnewline",
        "=SUM(A1:A9)", "+1-555-0100", "@mention", "-42 leading dash", "  padded  ", "emoji 🧾💳",
    ];

    public static GenerationSummary Generate(Stream target, GeneratorOptions options)
    {
        var random = new Random(options.Seed);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = options.Indent,
            IndentChars = "  ",
            CloseOutput = false,
            NewLineChars = "\n",
            CheckCharacters = true,
        };

        string prefix = options.DefaultNamespace ? string.Empty : "t";
        long records = 0;
        long missing = 0, badDates = 0, badDecimals = 0, duplicates = 0, longFields = 0, badStatus = 0;
        var recentIds = new string[64];
        int recentCount = 0;
        bool truncated = false;

        // Not disposed on the truncation path on purpose: XmlWriter.Dispose auto-closes open elements, which would
        // repair the document we are deliberately leaving unterminated. CloseOutput is false, so nothing leaks.
        XmlWriter writer = XmlWriter.Create(target, settings);
        {
            writer.WriteStartDocument();
            if (options.IncludeDoctype)
            {
                writer.WriteDocType("TransactionBatch", null, null, "<!ENTITY demo \"demo\">");
            }

            writer.WriteStartElement(prefix, "TransactionBatch", Namespace);
            writer.WriteAttributeString("batchId", $"BATCH-{options.StartDate:yyyyMMdd}-01");
            writer.WriteStartElement(prefix, "Header", Namespace);
            writer.WriteElementString(prefix, "Generated", Namespace, options.StartDate.ToDateTime(new TimeOnly(18, 45)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteElementString(prefix, "Sender", Namespace, "DEMO-BANK");
            writer.WriteElementString(prefix, "Note", Namespace, "Synthetic data generated for testing. All identifiers and names are fictitious.");
            writer.WriteEndElement();
            writer.WriteStartElement(prefix, "Transactions", Namespace);

            while (true)
            {
                if (options.ApproximateBytes is long targetBytes)
                {
                    if (records % 512 == 0)
                    {
                        writer.Flush();
                        if (target.CanSeek && target.Position >= targetBytes)
                        {
                            break;
                        }
                    }
                }
                else if (records >= options.Records)
                {
                    break;
                }

                records++;
                long sequence = records;
                bool isDuplicate = recentCount > 0 && Chance(random, options.DuplicateRate);
                string transactionId = isDuplicate ? recentIds[random.Next(recentCount)] : $"TXN-{options.StartDate:yyyyMMdd}-{sequence:D9}";
                if (isDuplicate)
                {
                    duplicates++;
                }
                else
                {
                    recentIds[recentCount < recentIds.Length ? recentCount++ : random.Next(recentIds.Length)] = transactionId;
                }

                bool missingRequired = Chance(random, options.MissingRequiredRate);
                bool invalidDate = Chance(random, options.InvalidDateRate);
                bool invalidDecimal = Chance(random, options.InvalidDecimalRate);
                bool special = Chance(random, options.SpecialCharacterRate);
                bool longField = Chance(random, options.LongFieldRate);
                bool invalidStatus = Chance(random, options.InvalidStatusRate);
                if (missingRequired)
                {
                    missing++;
                }

                if (invalidDate)
                {
                    badDates++;
                }

                if (invalidDecimal)
                {
                    badDecimals++;
                }

                if (longField)
                {
                    longFields++;
                }

                if (invalidStatus)
                {
                    badStatus++;
                }

                writer.WriteStartElement(prefix, "Transaction", Namespace);
                writer.WriteAttributeString("sequence", sequence.ToString(CultureInfo.InvariantCulture));
                if (!missingRequired)
                {
                    writer.WriteElementString(prefix, "TransactionId", Namespace, longField ? transactionId + new string('X', 80) : transactionId);
                }

                writer.WriteStartElement(prefix, "Account", Namespace);
                writer.WriteElementString(prefix, "Reference", Namespace, $"ACC-{random.Next(1_000_000, 9_999_999):D10}");
                writer.WriteElementString(prefix, "Type", Namespace, random.Next(3) == 0 ? "SAVINGS" : "CHECKING");
                writer.WriteEndElement();

                DateTime posted = options.StartDate.ToDateTime(TimeOnly.MinValue).AddSeconds(random.Next(0, 86_400)).AddDays(-random.Next(0, 3));
                writer.WriteElementString(prefix, "PostedAt", Namespace, invalidDate ? "2026-13-45T25:61:00Z" : posted.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                writer.WriteElementString(prefix, "ValueDate", Namespace, posted.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

                writer.WriteStartElement(prefix, "Amount", Namespace);
                writer.WriteAttributeString("currency", Currencies[random.Next(Currencies.Length)]);
                writer.WriteString(invalidDecimal ? "12,34.5.6" : (random.Next(1, 2_000_000) / 100m * (random.Next(4) == 0 ? -1 : 1)).ToString("0.00", CultureInfo.InvariantCulture));
                writer.WriteEndElement();

                writer.WriteElementString(prefix, "Direction", Namespace, random.Next(2) == 0 ? "CREDIT" : "DEBIT");
                writer.WriteElementString(prefix, "Status", Namespace, invalidStatus ? "UNKNOWN" : random.Next(10) == 0 ? "PENDING" : "POSTED");

                writer.WriteStartElement(prefix, "Counterparty", Namespace);
                writer.WriteElementString(prefix, "Name", Namespace, special ? SpecialSnippets[random.Next(SpecialSnippets.Length)] : Counterparties[random.Next(Counterparties.Length)]);
                writer.WriteEndElement();

                string description = Descriptions[random.Next(Descriptions.Length)] + " #" + random.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
                if (special)
                {
                    description += " " + SpecialSnippets[random.Next(SpecialSnippets.Length)];
                }

                if (longField && random.Next(2) == 0)
                {
                    description += " " + new string('L', 600);
                }

                writer.WriteElementString(prefix, "Description", Namespace, description);
                writer.WriteElementString(prefix, "IsReversal", Namespace, random.Next(50) == 0 ? "true" : "false");
                writer.WriteStartElement(prefix, "BatchRef", Namespace);
                writer.WriteAttributeString("id", $"BATCH-{options.StartDate:yyyyMMdd}-01");
                writer.WriteEndElement();

                if (options.Truncate && records >= Math.Max(1, options.Records / 2))
                {
                    // Close the writer without ending elements: emits nothing further, leaving the document unterminated.
                    writer.Flush();
                    truncated = true;
                    break;
                }

                writer.WriteEndElement(); // Transaction
            }

            if (!truncated)
            {
                writer.WriteEndElement(); // Transactions
                writer.WriteStartElement(prefix, "Trailer", Namespace);
                writer.WriteElementString(prefix, "Count", Namespace, records.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
                writer.WriteEndElement(); // TransactionBatch
                writer.WriteEndDocument();
                writer.Dispose();
            }
            else
            {
                writer.Flush();
                target.Flush();
                return new GenerationSummary(records, target.CanSeek ? target.Position : -1, missing, badDates, badDecimals, duplicates, longFields, badStatus);
            }
        }

        target.Flush();
        return new GenerationSummary(records, target.CanSeek ? target.Position : -1, missing, badDates, badDecimals, duplicates, longFields, badStatus);
    }

    public static GenerationSummary GenerateFile(string path, GeneratorOptions options)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.SequentialScan);
        return Generate(stream, options);
    }

    private static bool Chance(Random random, double rate) => rate > 0 && random.NextDouble() < rate;
}
