using System.Text;
using System.Xml.Linq;
using FinXmlProcessor.Application.Profiles;

namespace FinXmlProcessor.Application.Mapping;

/// <summary>The closed set of supported text transforms. No scripting, no expressions.</summary>
public static class TextTransforms
{
    /// <summary>Applies the field transforms in order. <paramref name="record"/> is only needed for concat.</summary>
    public static string? Apply(string? text, IReadOnlyList<CompiledTransform> transforms, XElement? record)
    {
        foreach (CompiledTransform transform in transforms)
        {
            switch (transform.Kind)
            {
                case TransformKind.Upper:
                    text = text?.ToUpperInvariant();
                    break;
                case TransformKind.Lower:
                    text = text?.ToLowerInvariant();
                    break;
                case TransformKind.Trim:
                    text = text?.Trim();
                    break;
                case TransformKind.NormalizeWhitespace:
                    text = text is null ? null : NormalizeWhitespace(text);
                    break;
                case TransformKind.Constant:
                    text = transform.Value;
                    break;
                case TransformKind.Concat:
                    text = Concat(text, transform, record);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown transform {transform.Kind}.");
            }
        }

        return text;
    }

    public static string NormalizeWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string Concat(string? primary, CompiledTransform transform, XElement? record)
    {
        var parts = new List<string>(1 + transform.Sources.Count);
        if (primary is not null)
        {
            parts.Add(primary);
        }

        if (record is not null)
        {
            foreach (XmlPath source in transform.Sources)
            {
                string? part = source.Evaluate(record);
                if (part is not null)
                {
                    parts.Add(part);
                }
            }
        }

        return string.Join(transform.Separator, parts);
    }
}
