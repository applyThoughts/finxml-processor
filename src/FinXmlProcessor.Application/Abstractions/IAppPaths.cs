namespace FinXmlProcessor.Application.Abstractions;

/// <summary>
/// Resolves per-user application directories. macOS: ~/Library/Application Support/FinXmlProcessor;
/// Windows: %LocalAppData%\FinXmlProcessor. Every file operation goes through these roots.
/// </summary>
public interface IAppPaths
{
    string Root { get; }

    string Settings { get; }

    string Profiles { get; }

    string Database { get; }

    string Staging { get; }

    string Quarantine { get; }

    string Reports { get; }

    string Logs { get; }

    /// <summary>Default output folder when the user has not selected one.</summary>
    string DefaultOutput { get; }

    /// <summary>Default input folder when the user has not selected one.</summary>
    string DefaultInput { get; }

    void EnsureCreated();

    /// <summary>Resolves a path and verifies it is inside the given root; throws otherwise. Used before any delete/move.</summary>
    string ResolveInside(string root, string relativeOrAbsolutePath);
}
