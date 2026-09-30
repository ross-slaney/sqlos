using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// Normalizes an HTML response into a stable, readable tree: one element per line, indented by
/// depth, attributes in the order the server wrote them, text trimmed with whitespace collapsed,
/// and comments dropped. Inline <c>&lt;style&gt;</c>, <c>&lt;script&gt;</c>, and inline SVG bodies
/// become a content digest (<c>{script-sha256:…}</c>), so a change to them is visible without
/// pages of CSS in every transcript. <c>data:</c> URIs become <c>{data-uri:mime}</c> because
/// some embed per-run secrets (a TOTP QR code). CSP nonces and hidden-input values are
/// registered with the scrubber.
/// </summary>
public static partial class HtmlCanonicalizer
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"
    };

    private static readonly HashSet<string> DigestedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "svg"
    };

    private static readonly HashSet<string> WhitespaceSensitiveElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "pre", "textarea"
    };

    public static string Render(string html, TranscriptValueSink sink)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);
        var builder = new StringBuilder();
        if (document.Doctype is { } doctype)
        {
            builder.Append("<!DOCTYPE ").Append(doctype.Name).Append(">\n");
        }

        if (document.DocumentElement is { } root)
        {
            WriteElement(builder, root, sink, depth: 0);
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static void WriteElement(StringBuilder builder, IElement element, TranscriptValueSink sink, int depth)
    {
        var name = element.LocalName;
        var indent = new string(' ', depth * 2);
        builder.Append(indent).Append('<').Append(name);
        RegisterHiddenInput(element, sink);
        foreach (var attribute in element.Attributes)
        {
            builder.Append(' ').Append(attribute.Name);
            builder.Append("=\"").Append(RenderAttributeValue(element, attribute, sink)).Append('"');
        }

        if (VoidElements.Contains(name))
        {
            builder.Append(" />\n");
            return;
        }

        builder.Append('>');
        if (DigestedElements.Contains(name))
        {
            var content = name.Equals("svg", StringComparison.OrdinalIgnoreCase) ? element.InnerHtml : element.TextContent;
            if (content.Length > 0)
            {
                builder.Append('{').Append(name.ToLowerInvariant()).Append("-sha256:").Append(Digest(content)).Append('}');
            }

            builder.Append("</").Append(name).Append(">\n");
            return;
        }

        if (WhitespaceSensitiveElements.Contains(name))
        {
            InspectText(element.TextContent, sink);
            builder.Append(EscapeText(element.TextContent)).Append("</").Append(name).Append(">\n");
            return;
        }

        var children = element.ChildNodes
            .Where(node => node is IElement || (node is IText text && !string.IsNullOrWhiteSpace(text.Data)))
            .ToList();
        if (children.Count == 0)
        {
            builder.Append("</").Append(name).Append(">\n");
            return;
        }

        // Short text-only content stays on the element's line: <h1>Sign in</h1>.
        if (children.All(node => node is IText))
        {
            var text = CollapseWhitespace(string.Concat(children.Cast<IText>().Select(node => node.Data)));
            InspectText(text, sink);
            if (text.Length <= 100)
            {
                builder.Append(EscapeText(text)).Append("</").Append(name).Append(">\n");
                return;
            }
        }

        builder.Append('\n');
        foreach (var child in children)
        {
            if (child is IElement childElement)
            {
                WriteElement(builder, childElement, sink, depth + 1);
            }
            else if (child is IText text)
            {
                InspectText(text.Data, sink);
                builder.Append(indent).Append("  ").Append(EscapeText(CollapseWhitespace(text.Data))).Append('\n');
            }
        }

        builder.Append(indent).Append("</").Append(name).Append(">\n");
    }

    private static string RenderAttributeValue(IElement element, IAttr attribute, TranscriptValueSink sink)
    {
        var value = attribute.Value;
        if (attribute.Name.Equals("nonce", StringComparison.OrdinalIgnoreCase))
        {
            sink.Register(value, "csp-nonce");
            return EscapeAttribute(value);
        }

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return CanonicalJson.DataUri(value);
        }

        if (attribute.Name is "href" or "action" or "src" or "content" or "formaction")
        {
            sink.Inspect(value);
            var refresh = MetaRefresh().Match(value);
            if (refresh.Success)
            {
                sink.Inspect(refresh.Groups["url"].Value);
            }
        }

        return EscapeAttribute(value);
    }

    private static void RegisterHiddenInput(IElement element, TranscriptValueSink sink)
    {
        if (!element.LocalName.Equals("input", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var name = element.GetAttribute("name");
        var value = element.GetAttribute("value");
        if (name == null || string.IsNullOrEmpty(value))
        {
            return;
        }

        if (ValueRoles.KindFor(name) is { } kind)
        {
            sink.Register(value, kind);
        }
        else if (string.Equals(element.GetAttribute("type"), "hidden", StringComparison.OrdinalIgnoreCase))
        {
            sink.Inspect(value);
        }
    }

    /// <summary>
    /// Pages sometimes print a URL as text (the <c>otpauth://</c> enrollment URI next to a QR
    /// code); its query parameters carry the same roles as in a link.
    /// </summary>
    private static void InspectText(string text, TranscriptValueSink sink)
    {
        foreach (Match url in UrlInText().Matches(text))
        {
            sink.Inspect(url.Value);
        }
    }

    private static string Digest(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n", StringComparison.Ordinal))))[..16].ToLowerInvariant();

    private static string CollapseWhitespace(string text)
        => Whitespace().Replace(text, " ").Trim();

    private static string EscapeText(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EscapeAttribute(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[a-z][a-z0-9+.\-]*://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInText();

    [GeneratedRegex(@"url=(?<url>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefresh();
}
