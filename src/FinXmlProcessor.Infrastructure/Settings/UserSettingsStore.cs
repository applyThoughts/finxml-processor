using System.Text.Json;
using System.Text.Json.Nodes;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Infrastructure.Acquisition;
using FinXmlProcessor.Infrastructure.Delivery;
using FinXmlProcessor.Infrastructure.Paths;
using FinXmlProcessor.Infrastructure.Retention;
using FinXmlProcessor.Infrastructure.Scheduling;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Infrastructure.Settings;

/// <summary>
/// Reads and writes the user's appsettings.json in the settings folder. The file only ever contains non-secret
/// values; the configuration system reloads it on change so options monitors see edits immediately.
/// </summary>
public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly ILogger<UserSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UserSettingsStore(AppPaths paths, ILogger<UserSettingsStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string FilePath => _paths.SettingsFile;

    public sealed class UserSettings
    {
        public ProcessingOptions Processing { get; set; } = new();

        public ScheduleOptions Schedule { get; set; } = new();

        public SftpOptions Sftp { get; set; } = new();

        public DeliveryOptions Delivery { get; set; } = new();

        public RetentionOptions Retention { get; set; } = new();
    }

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new UserSettings();
        }

        await using FileStream stream = File.OpenRead(FilePath);
        try
        {
            return await JsonSerializer.DeserializeAsync<UserSettings>(stream, Json, cancellationToken).ConfigureAwait(false) ?? new UserSettings();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "User settings file is not valid JSON; defaults are used");
            return new UserSettings();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.Settings);
            // Preserve unknown top-level sections (e.g. Serilog) written by hand.
            JsonObject root = File.Exists(FilePath) && JsonNode.Parse(await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false)) is JsonObject existing ? existing : [];
            root[ProcessingOptions.SectionName] = JsonSerializer.SerializeToNode(settings.Processing, Json);
            root[ScheduleOptions.SectionName] = JsonSerializer.SerializeToNode(settings.Schedule, Json);
            root[SftpOptions.SectionName] = JsonSerializer.SerializeToNode(settings.Sftp, Json);
            root[DeliveryOptions.SectionName] = JsonSerializer.SerializeToNode(settings.Delivery, Json);
            root[RetentionOptions.SectionName] = JsonSerializer.SerializeToNode(settings.Retention, Json);
            string temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, root.ToJsonString(Json), cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
            _logger.LogInformation("User settings saved");
        }
        finally
        {
            _gate.Release();
        }
    }
}
