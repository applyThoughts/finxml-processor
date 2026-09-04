using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace FinXmlProcessor.Infrastructure.Logging;

/// <summary>
/// Central redaction for structured logs: any property whose name suggests a secret is replaced, and any string
/// value that looks like a private key block, a URL with embedded credentials or a bearer token is masked.
/// </summary>
public static partial class LogRedaction
{
    public const string Redacted = "[redacted]";

    private static readonly string[] SensitiveNameFragments = ["password", "passwd", "secret", "passphrase", "token", "apikey", "api_key", "privatekey", "private_key", "credential", "authorization", "connectionstring"];

    public static bool IsSensitiveName(string name)
    {
        foreach (string fragment in SensitiveNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string result = PrivateKeyBlock().Replace(text, "-----BEGIN [redacted] PRIVATE KEY-----");
        result = UrlCredentials().Replace(result, "$1[redacted]@");
        result = BearerToken().Replace(result, "Bearer [redacted]");
        result = KeyValueSecret().Replace(result, "$1=[redacted]");
        return result;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\b(password|passwd|secret|passphrase|token|api[_-]?key)\s*[=:]\s*[^\s;,]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();
}

/// <summary>Serilog enricher applying <see cref="LogRedaction"/> to every event property.</summary>
public sealed class RedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties.ToList())
        {
            if (LogRedaction.IsSensitiveName(property.Key))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(property.Key, LogRedaction.Redacted));
                continue;
            }

            if (property.Value is ScalarValue { Value: string text })
            {
                string redacted = LogRedaction.RedactText(text);
                if (!ReferenceEquals(redacted, text) && !string.Equals(redacted, text, StringComparison.Ordinal))
                {
                    logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(property.Key, redacted));
                }
            }
        }
    }
}
