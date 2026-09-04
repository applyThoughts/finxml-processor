using System.Diagnostics;
using System.Globalization;
using System.Security;
using FinXmlProcessor.Application;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Infrastructure.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Infrastructure.Agent;

/// <summary>
/// Per-user macOS LaunchAgent that invokes <c>finxml schedule run-due</c> at load and on an interval. launchd
/// coalesces missed intervals after sleep, and run-due is idempotent, so wake/reboot catch-up needs no extra state.
/// </summary>
public sealed class LaunchAgentManager : IBackgroundAgentManager
{
    public const string Label = AppInfo.BundleIdentifier + ".worker";
    public const string ExpectedAppPath = "/Applications/FinXml Processor.app";
    private readonly IAppPaths _paths;
    private readonly IOptionsMonitor<ScheduleOptions> _schedule;
    private readonly ILogger<LaunchAgentManager> _logger;
    private readonly string? _workerPathOverride;

    public LaunchAgentManager(IAppPaths paths, IOptionsMonitor<ScheduleOptions> schedule, ILogger<LaunchAgentManager> logger)
        : this(paths, schedule, logger, null)
    {
    }

    public LaunchAgentManager(IAppPaths paths, IOptionsMonitor<ScheduleOptions> schedule, ILogger<LaunchAgentManager> logger, string? workerPathOverride)
    {
        _paths = paths;
        _schedule = schedule;
        _logger = logger;
        _workerPathOverride = workerPathOverride;
    }

    public string DefinitionPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    /// <summary>The worker binary shipped inside the app bundle.</summary>
    public string WorkerPath => _workerPathOverride ?? Path.Combine(ExpectedAppPath, "Contents", "MacOS", "finxml");

    public string RenderDefinition() => RenderPlist(WorkerPath, _paths.Logs, _schedule.CurrentValue.AgentIntervalSeconds);

    public static string RenderPlist(string workerPath, string logsDirectory, int intervalSeconds)
    {
        string interval = Math.Clamp(intervalSeconds, 60, 3600).ToString(CultureInfo.InvariantCulture);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{Label}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{SecurityElement.Escape(workerPath)}</string>
                <string>schedule</string>
                <string>run-due</string>
                <string>--quiet</string>
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>StartInterval</key>
              <integer>{interval}</integer>
              <key>ProcessType</key>
              <string>Background</string>
              <key>LowPriorityIO</key>
              <true/>
              <key>StandardOutPath</key>
              <string>{SecurityElement.Escape(Path.Combine(logsDirectory, "launchagent.out.log"))}</string>
              <key>StandardErrorPath</key>
              <string>{SecurityElement.Escape(Path.Combine(logsDirectory, "launchagent.err.log"))}</string>
              <key>EnvironmentVariables</key>
              <dict>
                <key>DOTNET_gcServer</key>
                <string>0</string>
              </dict>
            </dict>
            </plist>

            """;
    }

    public async Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        if (!OperatingSystem.IsMacOS())
        {
            return new AgentStatus(false, false, false, DefinitionPath, WorkerPath, ["LaunchAgent scheduling is only available on macOS."]);
        }

        bool installed = File.Exists(DefinitionPath);
        diagnostics.Add(installed ? $"Agent definition present at {DefinitionPath}." : "Agent definition is not installed.");
        if (!File.Exists(WorkerPath))
        {
            diagnostics.Add($"Worker executable not found at {WorkerPath}. Install the app in /Applications before enabling automation.");
        }

        bool loaded = false;
        if (installed)
        {
            (int code, string output) = await RunLaunchctlAsync(["print", $"gui/{GetUid()}/{Label}"], cancellationToken).ConfigureAwait(false);
            loaded = code == 0;
            diagnostics.Add(loaded ? "Agent is loaded in launchd." : "Agent is not loaded in launchd." + (string.IsNullOrWhiteSpace(output) ? string.Empty : $" ({output.Trim().Split('\n')[0]})"));
        }

        return new AgentStatus(true, installed, loaded, DefinitionPath, WorkerPath, diagnostics);
    }

    public async Task<AgentStatus> InstallOrUpdateAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("LaunchAgent scheduling is only available on macOS.");
        }

        if (!File.Exists(WorkerPath))
        {
            throw new InvalidOperationException($"Worker executable not found at {WorkerPath}. Move the app to /Applications and try again.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DefinitionPath)!);
        Directory.CreateDirectory(_paths.Logs);
        string temp = DefinitionPath + ".tmp";
        await File.WriteAllTextAsync(temp, RenderDefinition(), cancellationToken).ConfigureAwait(false);
        File.Move(temp, DefinitionPath, overwrite: true);
        await RunLaunchctlAsync(["bootout", $"gui/{GetUid()}/{Label}"], cancellationToken).ConfigureAwait(false);
        (int code, string output) = await RunLaunchctlAsync(["bootstrap", $"gui/{GetUid()}", DefinitionPath], cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            _logger.LogError("launchctl bootstrap failed ({Code}): {Output}", code, output);
            throw new InvalidOperationException($"launchctl bootstrap failed (exit {code}). {output.Trim()}");
        }

        _logger.LogInformation("LaunchAgent {Label} installed", Label);
        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentStatus> UninstallAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("LaunchAgent scheduling is only available on macOS.");
        }

        await RunLaunchctlAsync(["bootout", $"gui/{GetUid()}/{Label}"], cancellationToken).ConfigureAwait(false);
        if (File.Exists(DefinitionPath))
        {
            File.Delete(DefinitionPath);
        }

        _logger.LogInformation("LaunchAgent {Label} uninstalled", Label);
        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetUid()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/id", "-u") { RedirectStandardOutput = true, UseShellExecute = false });
            string output = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return output.Length > 0 ? output : "501";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "501";
        }
    }

    private static async Task<(int ExitCode, string Output)> RunLaunchctlAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("/bin/launchctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using Process process = Process.Start(psi)!;
            string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, stdout + stderr);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (-1, ex.Message);
        }
    }
}

/// <summary>Development/no-op implementation for Windows and Linux.</summary>
public sealed class NoOpBackgroundAgentManager : IBackgroundAgentManager
{
    private readonly IAppPaths _paths;
    private readonly IOptionsMonitor<ScheduleOptions> _schedule;

    public NoOpBackgroundAgentManager(IAppPaths paths, IOptionsMonitor<ScheduleOptions> schedule)
    {
        _paths = paths;
        _schedule = schedule;
    }

    public Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AgentStatus(false, false, false, null, null, ["Background scheduling is managed by a macOS LaunchAgent and is not available on this platform. Use 'finxml schedule run-due' from an external scheduler instead."]));

    public Task<AgentStatus> InstallOrUpdateAsync(CancellationToken cancellationToken) => GetStatusAsync(cancellationToken);

    public Task<AgentStatus> UninstallAsync(CancellationToken cancellationToken) => GetStatusAsync(cancellationToken);

    public string RenderDefinition() => LaunchAgentManager.RenderPlist("/Applications/FinXml Processor.app/Contents/MacOS/finxml", _paths.Logs, _schedule.CurrentValue.AgentIntervalSeconds);
}
