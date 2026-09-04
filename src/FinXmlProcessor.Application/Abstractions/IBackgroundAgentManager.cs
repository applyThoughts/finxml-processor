namespace FinXmlProcessor.Application.Abstractions;

public sealed record AgentStatus(bool IsSupported, bool IsInstalled, bool IsLoaded, string? DefinitionPath, string? WorkerPath, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Manages the per-user background agent that invokes the worker. macOS uses a LaunchAgent; other platforms
/// provide a no-op implementation that reports "not supported" so the UI can explain the limitation.
/// </summary>
public interface IBackgroundAgentManager
{
    Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<AgentStatus> InstallOrUpdateAsync(CancellationToken cancellationToken);

    Task<AgentStatus> UninstallAsync(CancellationToken cancellationToken);

    /// <summary>Produces the agent definition text for preview/diagnostics without installing it.</summary>
    string RenderDefinition();
}
