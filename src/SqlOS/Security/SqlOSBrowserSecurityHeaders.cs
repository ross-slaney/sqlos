using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;

namespace SqlOS.Security;

internal sealed class SqlOSBrowserSecurityHeaders
{
    private readonly SqlOSBrowserSecurityOptions _options;

    public SqlOSBrowserSecurityHeaders(IOptions<SqlOSOptions> options)
    {
        _options = options.Value.BrowserSecurity;
    }

    public void ApplyBaseline(HttpResponse response)
    {
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "same-origin";
    }

    public string PrepareHtml(HttpContext context, string html)
    {
        ApplyBaseline(context.Response);
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        var policy = _options.ContentSecurityPolicy.Replace(
            SqlOSBrowserSecurityOptions.NoncePlaceholder,
            nonce,
            StringComparison.Ordinal);
        context.Response.Headers["Content-Security-Policy"] =
            $"{policy.Trim().TrimEnd(';')}; frame-ancestors 'none'";

        var encodedNonce = WebUtility.HtmlEncode(nonce);
        return html.Replace(
            SqlOSCspNonce.Attribute,
            $"nonce=\"{encodedNonce}\"",
            StringComparison.Ordinal);
    }
}
