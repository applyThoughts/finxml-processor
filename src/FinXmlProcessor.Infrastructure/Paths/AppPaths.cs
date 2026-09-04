using FinXmlProcessor.Application;
using FinXmlProcessor.Application.Abstractions;

namespace FinXmlProcessor.Infrastructure.Paths;

/// <summary>
/// Per-user application directories. The root can be overridden with the FINXML_HOME environment variable
/// (used by tests and by operators who want a non-default location).
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string HomeEnvironmentVariable = "FINXML_HOME";

    public AppPaths()
        : this(ResolveDefaultRoot())
    {
    }

    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Settings = Path.Combine(Root, "settings");
        Profiles = Path.Combine(Root, "profiles");
        Database = Path.Combine(Root, "database");
        Staging = Path.Combine(Root, "staging");
        Quarantine = Path.Combine(Root, "quarantine");
        Reports = Path.Combine(Root, "reports");
        Logs = Path.Combine(Root, "logs");
        DefaultOutput = Path.Combine(Root, "output");
        DefaultInput = Path.Combine(Root, "input");
    }

    public string Root { get; }

    public string Settings { get; }

    public string Profiles { get; }

    public string Database { get; }

    public string Staging { get; }

    public string Quarantine { get; }

    public string Reports { get; }

    public string Logs { get; }

    public string DefaultOutput { get; }

    public string DefaultInput { get; }

    public string DatabaseFile => Path.Combine(Database, "history.sqlite");

    public string SettingsFile => Path.Combine(Settings, "appsettings.json");

    public void EnsureCreated()
    {
        foreach (string dir in new[] { Root, Settings, Profiles, Database, Staging, Quarantine, Reports, Logs, DefaultOutput, DefaultInput })
        {
            Directory.CreateDirectory(dir);
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                // Least privilege: only the owning user may read the application data.
                File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Best effort; a shared or unusual filesystem may refuse.
            }
        }
    }

    public string ResolveInside(string root, string relativeOrAbsolutePath)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.IsPathRooted(relativeOrAbsolutePath)
            ? Path.GetFullPath(relativeOrAbsolutePath)
            : Path.GetFullPath(Path.Combine(fullRoot, relativeOrAbsolutePath));
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison) && !string.Equals(candidate, fullRoot, comparison))
        {
            throw new UnauthorizedAccessException($"Path is outside the expected root '{Path.GetFileName(fullRoot)}'.");
        }

        return candidate;
    }

    public static string ResolveDefaultRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", AppInfo.ShortName);
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.ShortName);
        }

        string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(xdg, AppInfo.ShortName);
    }
}
