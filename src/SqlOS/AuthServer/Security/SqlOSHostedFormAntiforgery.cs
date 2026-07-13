using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;

namespace SqlOS.AuthServer.Security;

internal sealed class SqlOSHostedFormAntiforgeryMetadata
{
    public static SqlOSHostedFormAntiforgeryMetadata Instance { get; } = new();

    private SqlOSHostedFormAntiforgeryMetadata()
    {
    }
}

internal sealed class SqlOSHostedFormAntiforgeryFilter(
    IAntiforgery antiforgery,
    IOptions<SqlOSAuthServerOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        if (!HasTrustedBrowserSource(httpContext.Request, options.Value))
        {
            return InvalidRequest();
        }

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return InvalidRequest();
        }

        return await next(context);
    }

    private static bool HasTrustedBrowserSource(HttpRequest request, SqlOSAuthServerOptions options)
    {
        var source = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(source))
        {
            source = request.Headers.Referer.ToString();
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

    private static IResult InvalidRequest()
        => Results.Text(
            "The hosted form could not be validated. Reload the page and try again.",
            contentType: "text/plain",
            statusCode: StatusCodes.Status400BadRequest);
}

internal sealed class SqlOSAntiforgeryAdditionalDataProvider : IAntiforgeryAdditionalDataProvider
{
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    public string GetAdditionalData(HttpContext context)
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool ValidateAdditionalData(HttpContext context, string additionalData)
    {
        if (!long.TryParse(
                additionalData,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var issuedAtSeconds))
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds);
        return age >= TimeSpan.Zero && age <= TokenLifetime;
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
            var antiforgery = httpContext.RequestServices.GetRequiredService<IAntiforgery>();
            var token = antiforgery.GetAndStoreTokens(httpContext).RequestToken
                ?? throw new InvalidOperationException("The hosted form antiforgery token could not be created.");
            var fieldName = httpContext.RequestServices.GetRequiredService<IOptions<AntiforgeryOptions>>()
                .Value.FormFieldName;
            var hiddenField = $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(fieldName)}\" value=\"{WebUtility.HtmlEncode(token)}\" />";
            responseHtml = PostFormPattern.Replace(responseHtml, match => match.Value + hiddenField);
        }

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "text/html; charset=utf-8";
        httpContext.Response.Headers.CacheControl = "no-store";
        await httpContext.Response.WriteAsync(responseHtml, httpContext.RequestAborted);
    }
}
