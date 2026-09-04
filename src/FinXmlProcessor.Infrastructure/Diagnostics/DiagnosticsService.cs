using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FinXmlProcessor.Application;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Infrastructure.Acquisition;
using FinXmlProcessor.Infrastructure.Delivery;
using FinXmlProcessor.Infrastructure.Logging;
using FinXmlProcessor.Infrastructure.Paths;
using FinXmlProcessor.Infrastructure.Scheduling;
using Microsoft.Extensions.Options;
using NodaTime;

namespace FinXmlProcessor.Infrastructure.Diagnostics;

/// <summary>Sanitized environment and configuration facts for the Settings page, the CLI and the diagnostic bundle.</summary>
public sealed class DiagnosticsService
{
    private readonly AppPaths _paths;
    private readonly IOptionsMonitor<ProcessingOptions> _processing;
    private readonly IOptionsMonitor<ScheduleOptions> _schedule;
    private readonly IOptionsMonitor<SftpOptions> _sftp;
    private readonly IOptionsMonitor<DeliveryOptions> _delivery;
    private readonly IScheduleService _scheduleService;
    private readonly IBackgroundAgentManager _agent;
    private readonly ISecretStore _secrets;
    private readonly IProcessingLock _lock;
    private readonly IProcessingClock _clock;

    public DiagnosticsService(AppPaths paths, IOptionsMonitor<ProcessingOptions> processing, IOptionsMonitor<ScheduleOptions> schedule, IOptionsMonitor<SftpOptions> sftp, IOptionsMonitor<DeliveryOptions> delivery, IScheduleService scheduleService, IBackgroundAgentManager agent, ISecretStore secrets, IProcessingLock processingLock, IProcessingClock clock)
    {
        _paths = paths;
        _processing = processing;
        _schedule = schedule;
        _sftp = sftp;
        _delivery = delivery;
        _scheduleService = scheduleService;
        _agent = agent;
        _secrets = secrets;
        _lock = processingLock;
        _clock = clock;
    }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> CollectAsync(CancellationToken cancellationToken)
    {
        var lines = new List<KeyValuePair<string, string>>
        {
            new("Application", $"{AppInfo.ProductName} {AppInfo.Version}"),
            new("Bundle identifier", AppInfo.BundleIdentifier),
            new("Platform", AppInfo.Platform),
            new(".NET runtime", Environment.Version.ToString()),
            new("Data folder", _paths.Root),
            new("Database", File.Exists(_paths.DatabaseFile) ? $"{_paths.DatabaseFile} ({new FileInfo(_paths.DatabaseFile).Length:N0} bytes)" : "not created yet"),
            new("Input folder", _processing.CurrentValue.InputDirectory ?? _paths.DefaultInput),
            new("Output folder", _processing.CurrentValue.OutputDirectory ?? _paths.DefaultOutput),
            new("Active profile", _processing.CurrentValue.ActiveProfileId),
            new("Max input size", $"{_processing.CurrentValue.MaxInputBytes:N0} bytes"),
            new("Secret store", _secrets.ProviderName),
            new("Delivery", $"{_delivery.CurrentValue.Provider}{(string.IsNullOrEmpty(_delivery.CurrentValue.LocalFolder) ? string.Empty : " -> " + _delivery.CurrentValue.LocalFolder)}"),
            new("SFTP", _sftp.CurrentValue.Enabled ? $"enabled ({_sftp.CurrentValue.Host}:{_sftp.CurrentValue.Port}, {_sftp.CurrentValue.AuthMethod} auth, host key {_sftp.CurrentValue.HostKeyAlgorithm})" : "disabled"),
        };

        Instant now = _clock.GetCurrentInstant();
        ScheduleOptions schedule = _schedule.CurrentValue;
        ScheduledOccurrence next = _scheduleService.NextOccurrence(now);
        lines.Add(new("Schedule", schedule.Enabled ? $"enabled, daily at {schedule.Time} America/New_York" : "disabled"));
        lines.Add(new("Next occurrence (Eastern)", next.BusinessTime.ToString("yyyy-MM-dd HH:mm o<g>", CultureInfo.InvariantCulture)));
        lines.Add(new("Next occurrence (this Mac)", next.Instant.InZone(DateTimeZoneProviders.Tzdb.GetSystemDefault()).ToString("yyyy-MM-dd HH:mm o<g>", CultureInfo.InvariantCulture)));
        lines.Add(new("Machine time zone", DateTimeZoneProviders.Tzdb.GetSystemDefault().Id));
        DueRunDecision due = await _scheduleService.EvaluateAsync(now, cancellationToken).ConfigureAwait(false);
        lines.Add(new("Due now", $"{due.IsDue} ({due.Reason})"));

        AgentStatus agent = await _agent.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        lines.Add(new("Background agent", agent.IsSupported ? $"installed={agent.IsInstalled}, loaded={agent.IsLoaded}" : "not supported on this platform"));
        foreach (string d in agent.Diagnostics)
        {
            lines.Add(new("  agent", d));
        }

        string? holder = await _lock.DescribeHolderAsync(cancellationToken).ConfigureAwait(false);
        lines.Add(new("Processing lock", holder ?? "free"));
        return lines;
    }

    /// <summary>
    /// Builds a ZIP containing sanitized diagnostics, redacted settings, recent logs and reports. Never includes
    /// input XML, output workbooks, private keys, secrets or the database itself.
    /// </summary>
    public async Task<string> ExportBundleAsync(string targetPath, CancellationToken cancellationToken)
    {
        IReadOnlyList<KeyValuePair<string, string>> facts = await CollectAsync(cancellationToken).ConfigureAwait(false);
        string temp = targetPath + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            await AddTextAsync(zip, "diagnostics.txt", string.Join('\n', facts.Select(f => $"{f.Key}: {f.Value}")), cancellationToken).ConfigureAwait(false);
            if (File.Exists(_paths.SettingsFile))
            {
                string settings = await File.ReadAllTextAsync(_paths.SettingsFile, cancellationToken).ConfigureAwait(false);
                await AddTextAsync(zip, "settings/appsettings.redacted.json", RedactSettingsJson(settings), cancellationToken).ConfigureAwait(false);
            }

            AddRecentFiles(zip, _paths.Logs, "logs", "*.log", 10, 5 * 1024 * 1024);
            AddRecentFiles(zip, _paths.Logs, "logs", "*.json", 10, 5 * 1024 * 1024);
            AddRecentFiles(zip, _paths.Reports, "reports", "*.json", 20, 2 * 1024 * 1024);
            AddRecentFiles(zip, _paths.Profiles, "profiles", "*.json", 20, 1024 * 1024);
        }

        File.Move(temp, targetPath, overwrite: true);
        return targetPath;
    }

    public static string RedactSettingsJson(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                WriteRedacted(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return LogRedaction.RedactText(json);
        }
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (LogRedaction.IsSensitiveName(property.Name))
                    {
                        writer.WriteStringValue(LogRedaction.Redacted);
                    }
                    else
                    {
                        WriteRedacted(property.Value, writer);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteRedacted(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(LogRedaction.RedactText(element.GetString() ?? string.Empty));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void AddRecentFiles(ZipArchive zip, string directory, string entryFolder, string pattern, int maxFiles, long maxBytesEach)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Take(maxFiles))
        {
            if (file.Length > maxBytesEach)
            {
                continue;
            }

            string content = LogRedaction.RedactText(File.ReadAllText(file.FullName));
            ZipArchiveEntry entry = zip.CreateEntry($"{entryFolder}/{file.Name}", CompressionLevel.Optimal);
            using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string content, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
