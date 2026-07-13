using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SqlOS.AuthServer.Configuration;

namespace SqlOS.AuthServer.Security;

internal sealed class SqlOSHostedFormAntiforgeryMetadata
{
    public static SqlOSHostedFormAntiforgeryMetadata Instance { get; } = new();

    private SqlOSHostedFormAntiforgeryMetadata()
    {
    }
}

internal sealed class SqlOSHostedFormAntiforgery
{
    internal const string FormFieldName = "__RequestVerificationToken";
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly SqlOSAuthServerOptions _options;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    public SqlOSHostedFormAntiforgery(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<SqlOSAuthServerOptions> options)
        : this(dataProtectionProvider, options, TimeProvider.System)
    {
    }

    internal SqlOSHostedFormAntiforgery(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<SqlOSAuthServerOptions> options,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        CookiePath = _options.BasePath.TrimEnd('/');
        var cookieSuffix = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(CookiePath)))[..16].ToLowerInvariant();
        CookieName = $"sqlos_auth_page_csrf_{cookieSuffix}";
        _protector = dataProtectionProvider.CreateProtector(
            "SqlOS.AuthServer.HostedFormAntiforgery.v1",
            CookiePath);
    }

    internal string CookieName { get; }
    internal string CookiePath { get; }

    internal string IssueRequestToken(HttpContext context)
    {
        var cookieSecret = context.Request.Cookies[CookieName];
        if (!IsValidCookieSecret(cookieSecret))
        {
            cookieSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(CookieName, cookieSecret, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                Path = CookiePath,
                MaxAge = TokenLifetime,
                Expires = _timeProvider.GetUtcNow().Add(TokenLifetime)
            });
        }

        var payload = new RequestTokenPayload(
            _timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            HashCookie(cookieSecret),
            WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)));
        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    internal async Task<bool> ValidateRequestAsync(HttpContext context)
    {
        if (!HasTrustedBrowserSource(context.Request, _options)
            || !context.Request.HasFormContentType)
        {
            return false;
        }

        var cookieSecret = context.Request.Cookies[CookieName];
        if (!IsValidCookieSecret(cookieSecret))
        {
            return false;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var requestToken = form[FormFieldName].ToString();
        return ValidateToken(cookieSecret, requestToken);
    }

    internal bool ValidateToken(string cookieSecret, string requestToken)
    {
        if (!IsValidCookieSecret(cookieSecret) || string.IsNullOrWhiteSpace(requestToken))
        {
            return false;
        }

        RequestTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RequestTokenPayload>(_protector.Unprotect(requestToken));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return false;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.CookieHash))
        {
            return false;
        }

        var age = _timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAtUnixSeconds);
        if (age < -ClockSkew || age > TokenLifetime)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                WebEncoders.Base64UrlDecode(payload.CookieHash),
                WebEncoders.Base64UrlDecode(HashCookie(cookieSecret)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool HasTrustedBrowserSource(HttpRequest request, SqlOSAuthServerOptions options)
    {
        var source = ReadSingleHeader(request.Headers.Origin);
        if (source == null && request.Headers.Origin.Count > 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            source = ReadSingleHeader(request.Headers.Referer);
            if (source == null && request.Headers.Referer.Count > 0)
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return true;
        }

        if (!Uri.TryCreate(source.Trim(), UriKind.Absolute, out var sourceUri))
        {
            return false;
        }

        var trustedValue = string.IsNullOrWhiteSpace(options.PublicOrigin)
            ? options.Issuer
            : options.PublicOrigin;
        if (!Uri.TryCreate(trustedValue, UriKind.Absolute, out var trustedUri))
        {
            return false;
        }

        return string.Equals(
            sourceUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            trustedUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadSingleHeader(StringValues values)
        => values.Count switch
        {
            0 => string.Empty,
            1 => values[0],
            _ => null
        };

    private static bool IsValidCookieSecret([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return WebEncoders.Base64UrlDecode(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string HashCookie(string cookieSecret)
        => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(cookieSecret)));

    private sealed record RequestTokenPayload(long IssuedAtUnixSeconds, string CookieHash, string Nonce);
}

internal sealed class SqlOSHostedFormAntiforgeryFilter(SqlOSHostedFormAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await antiforgery.ValidateRequestAsync(context.HttpContext))
        {
            return Results.Text(
                "The hosted form could not be validated. Reload the page and try again.",
                contentType: "text/plain",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }
}

internal sealed class SqlOSHostedHtmlResult(string html, int statusCode) : IResult
{
    private static readonly Regex PostFormPattern = new(
        "(<form\\b[^>]*\\bmethod\\s*=\\s*[\\\"']post[\\\"'][^>]*>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var responseHtml = html;
        if (PostFormPattern.IsMatch(responseHtml))
        {
            var antiforgery = httpContext.RequestServices.GetRequiredService<SqlOSHostedFormAntiforgery>();
            var token = antiforgery.IssueRequestToken(httpContext);
            var hiddenField = $"<input type=\"hidden\" name=\"{FormField()}\" value=\"{WebUtility.HtmlEncode(token)}\" />";
            responseHtml = PostFormPattern.Replace(responseHtml, match => match.Value + hiddenField);
        }

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "text/html; charset=utf-8";
        httpContext.Response.Headers.CacheControl = "no-store";
        await httpContext.Response.WriteAsync(responseHtml, httpContext.RequestAborted);
    }

    private static string FormField()
        => WebUtility.HtmlEncode(SqlOSHostedFormAntiforgery.FormFieldName);
}
