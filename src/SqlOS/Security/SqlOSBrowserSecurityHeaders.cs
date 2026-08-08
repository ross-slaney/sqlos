using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;

namespace SqlOS.Security;

internal sealed class SqlOSBrowserSecurityHeaders
{
    private static readonly Regex InlineElementPattern = new(
        "<(script|style)(?![^>]*\\bnonce\\s*=)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly SqlOSBrowserSecurityOptions _options;

    public SqlOSBrowserSecurityHeaders(IOptions<SqlOSOptions> options)
    {
        _options = options.Value.BrowserSecurity;
    }

    public void ApplyBaseline(HttpResponse response)
    {
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    public string PrepareHtml(HttpContext context, string html)
        => PrepareHtml(context, html, "DENY", "'none'", allowSameOriginChildFrames: false);

    public string PrepareDashboardHtml(HttpContext context, string html)
        => PrepareHtml(context, html, "DENY", "'none'", allowSameOriginChildFrames: true);

    public string PrepareSameOriginEmbeddedHtml(HttpContext context, string html)
        => PrepareHtml(context, html, "SAMEORIGIN", "'self'", allowSameOriginChildFrames: false);

    private string PrepareHtml(
        HttpContext context,
        string html,
        string xFrameOptions,
        string frameAncestors,
        bool allowSameOriginChildFrames)
    {
        ApplyBaseline(context.Response);
        context.Response.Headers["X-Frame-Options"] = xFrameOptions;
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        var policy = _options.ContentSecurityPolicy.Replace(
            SqlOSBrowserSecurityOptions.NoncePlaceholder,
            nonce,
            StringComparison.Ordinal);
        var normalizedPolicy = policy.Trim().TrimEnd(';');
        if (allowSameOriginChildFrames)
        {
            normalizedPolicy = EnsureDirectiveIncludesSource(normalizedPolicy, "frame-src", "'self'");
        }
        context.Response.Headers["Content-Security-Policy"] =
            $"{normalizedPolicy}; frame-ancestors {frameAncestors}";

        var encodedNonce = WebUtility.HtmlEncode(nonce);
        return InlineElementPattern.Replace(
            html,
            match => $"<{match.Groups[1].Value} nonce=\"{encodedNonce}\"");
    }

    private static string EnsureDirectiveIncludesSource(string policy, string directiveName, string source)
    {
        var directives = policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var directiveIndex = directives.FindIndex(directive =>
        {
            var separator = directive.IndexOfAny([' ', '\t']);
            var name = separator < 0 ? directive : directive[..separator];
            return name.Equals(directiveName, StringComparison.OrdinalIgnoreCase);
        });

        if (directiveIndex < 0)
        {
            directives.Add($"{directiveName} {source}");
            return string.Join("; ", directives);
        }

        var tokens = directives[directiveIndex]
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        tokens.RemoveAll(token => token.Equals("'none'", StringComparison.OrdinalIgnoreCase));
        if (!tokens.Contains(source, StringComparer.OrdinalIgnoreCase))
        {
            tokens.Add(source);
        }
        directives[directiveIndex] = string.Join(' ', tokens);
        return string.Join("; ", directives);
    }
}
