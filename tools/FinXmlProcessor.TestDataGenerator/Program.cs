using System.CommandLine;
using System.Globalization;
using FinXmlProcessor.TestDataGenerator;

var output = new Option<string>("--output", "-o") { Description = "Output XML path.", Required = true };
var records = new Option<long>("--records", "-r") { Description = "Exact number of records.", DefaultValueFactory = _ => 1000 };
var approxBytes = new Option<string?>("--approx-size") { Description = "Approximate size, e.g. 200MB or 150000000. Overrides --records." };
var seed = new Option<int>("--seed") { Description = "Deterministic seed.", DefaultValueFactory = _ => 42 };
var missing = new Option<double>("--missing-rate") { Description = "Rate of records with a missing required field.", DefaultValueFactory = _ => 0 };
var badDate = new Option<double>("--invalid-date-rate") { DefaultValueFactory = _ => 0 };
var badDecimal = new Option<double>("--invalid-decimal-rate") { DefaultValueFactory = _ => 0 };
var duplicate = new Option<double>("--duplicate-rate") { DefaultValueFactory = _ => 0 };
var special = new Option<double>("--special-rate") { Description = "Rate of records containing special XML characters and Unicode.", DefaultValueFactory = _ => 0 };
var longField = new Option<double>("--long-field-rate") { DefaultValueFactory = _ => 0 };
var badStatus = new Option<double>("--invalid-status-rate") { DefaultValueFactory = _ => 0 };
var defaultNs = new Option<bool>("--default-namespace") { Description = "Serialize with a default namespace instead of a prefix." };
var truncate = new Option<bool>("--truncate") { Description = "Produce a truncated (malformed) document." };
var doctype = new Option<bool>("--doctype") { Description = "Include a DOCTYPE declaration (must be rejected by the processor)." };
var noIndent = new Option<bool>("--compact") { Description = "No indentation (smaller files)." };
var startDate = new Option<string>("--start-date") { Description = "Business date (yyyy-MM-dd).", DefaultValueFactory = _ => "2026-09-01" };

var root = new RootCommand("Streams a synthetic fintech-style XML file matching the demo-fintech-v1 profile. All values are fictitious.")
{
    output, records, approxBytes, seed, missing, badDate, badDecimal, duplicate, special, longField, badStatus, defaultNs, truncate, doctype, noIndent, startDate,
};

root.SetAction(parseResult =>
{
    var options = new GeneratorOptions
    {
        Records = parseResult.GetValue(records),
        ApproximateBytes = ParseSize(parseResult.GetValue(approxBytes)),
        Seed = parseResult.GetValue(seed),
        MissingRequiredRate = parseResult.GetValue(missing),
        InvalidDateRate = parseResult.GetValue(badDate),
        InvalidDecimalRate = parseResult.GetValue(badDecimal),
        DuplicateRate = parseResult.GetValue(duplicate),
        SpecialCharacterRate = parseResult.GetValue(special),
        LongFieldRate = parseResult.GetValue(longField),
        InvalidStatusRate = parseResult.GetValue(badStatus),
        DefaultNamespace = parseResult.GetValue(defaultNs),
        Truncate = parseResult.GetValue(truncate),
        IncludeDoctype = parseResult.GetValue(doctype),
        Indent = !parseResult.GetValue(noIndent),
        StartDate = DateOnly.ParseExact(parseResult.GetValue(startDate)!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    string path = parseResult.GetValue(output)!;
    GenerationSummary summary = SyntheticDataGenerator.GenerateFile(path, options);
    Console.WriteLine($"Wrote {summary.Records:N0} records, {summary.Bytes:N0} bytes to {path}");
    Console.WriteLine($"Expected anomalies: missing={summary.ExpectedMissingRequired:N0} invalidDate={summary.ExpectedInvalidDates:N0} invalidDecimal={summary.ExpectedInvalidDecimals:N0} duplicates={summary.ExpectedDuplicates:N0} longFields={summary.ExpectedLongFields:N0} invalidStatus={summary.ExpectedInvalidStatus:N0}");
    return 0;
});

return root.Parse(args).Invoke();

static long? ParseSize(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return null;
    }

    string t = text.Trim().ToUpperInvariant();
    long multiplier = 1;
    if (t.EndsWith("GB", StringComparison.Ordinal))
    {
        multiplier = 1024L * 1024 * 1024;
        t = t[..^2];
    }
    else if (t.EndsWith("MB", StringComparison.Ordinal))
    {
        multiplier = 1024L * 1024;
        t = t[..^2];
    }
    else if (t.EndsWith("KB", StringComparison.Ordinal))
    {
        multiplier = 1024L;
        t = t[..^2];
    }

    return (long)(double.Parse(t, CultureInfo.InvariantCulture) * multiplier);
}
