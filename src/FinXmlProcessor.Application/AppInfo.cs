using System.Reflection;
using System.Runtime.InteropServices;

namespace FinXmlProcessor.Application;

/// <summary>Central product identity. Change the name/bundle id in Directory.Build.props before the first signed release.</summary>
public static class AppInfo
{
    public const string ProductName = "FinXml Processor";
    public const string ShortName = "FinXmlProcessor";
    public const string BundleIdentifier = "com.example.finxmlprocessor";
    public const string ScheduleId = "daily-eastern-1900";
    public const string ReleasesUrl = "https://github.com/applyThoughts/finxml-processor/releases";

    public static string Version { get; } = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static string Platform { get; } = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    public static bool IsMacOS => OperatingSystem.IsMacOS();
}
