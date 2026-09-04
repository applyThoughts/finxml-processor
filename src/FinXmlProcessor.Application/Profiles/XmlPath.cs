using System.Xml.Linq;

namespace FinXmlProcessor.Application.Profiles;

/// <summary>
/// A resolved, namespace-aware relative path: zero or more child element steps, optionally ending in an attribute.
/// <c>IsSelf</c> selects the record element text. This is deliberately not XPath: no predicates, no axes, no wildcards.
/// </summary>
public sealed class XmlPath
{
    private XmlPath(XName[] elements, XName? attribute, bool isSelf, string text)
    {
        Elements = elements;
        Attribute = attribute;
        IsSelf = isSelf;
        Text = text;
    }

    public IReadOnlyList<XName> Elements { get; }

    public XName? Attribute { get; }

    public bool IsSelf { get; }

    /// <summary>The original profile text, for messages. Never contains data.</summary>
    public string Text { get; }

    public static XmlPath Parse(string path, IReadOnlyDictionary<string, string> namespaces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path == ".")
        {
            return new XmlPath([], null, true, path);
        }

        string[] segments = path.Split('/');
        var elements = new List<XName>(segments.Length);
        XName? attribute = null;
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0)
            {
                throw new ProfileValidationException($"Path '{path}' contains an empty segment.");
            }

            if (segment[0] == '@')
            {
                if (i != segments.Length - 1)
                {
                    throw new ProfileValidationException($"Path '{path}': an attribute must be the last segment.");
                }

                attribute = ResolveQName(segment[1..], namespaces, path, isAttribute: true);
            }
            else
            {
                elements.Add(ResolveQName(segment, namespaces, path, isAttribute: false));
            }
        }

        return new XmlPath([.. elements], attribute, false, path);
    }

    public static XName ResolveQName(string qname, IReadOnlyDictionary<string, string> namespaces, string context, bool isAttribute)
    {
        int colon = qname.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            // Unprefixed attributes are never in a namespace; unprefixed elements use the default namespace if declared.
            if (!isAttribute && namespaces.TryGetValue(string.Empty, out string? defaultNs))
            {
                return XName.Get(qname, defaultNs);
            }

            return XName.Get(qname);
        }

        string prefix = qname[..colon];
        string local = qname[(colon + 1)..];
        if (!namespaces.TryGetValue(prefix, out string? ns))
        {
            throw new ProfileValidationException($"Path '{context}' uses undeclared namespace prefix '{prefix}'.");
        }

        return XName.Get(local, ns);
    }

    /// <summary>Evaluates the path against a record element. Returns null when the target does not exist.</summary>
    public string? Evaluate(XElement record)
    {
        if (IsSelf)
        {
            return record.Value;
        }

        XElement current = record;
        foreach (XName step in Elements)
        {
            XElement? next = current.Element(step);
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        if (Attribute is not null)
        {
            return current.Attribute(Attribute)?.Value;
        }

        return current.Value;
    }

    public override string ToString() => Text;
}
