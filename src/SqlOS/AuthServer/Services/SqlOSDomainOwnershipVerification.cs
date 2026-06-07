using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;

namespace SqlOS.AuthServer.Services;

public static class SqlOSDomainOwnershipVerification
{
    public const string VerificationTokenPrefix = "sqlos-domain-verification=";

    public static string CreateVerificationToken(SqlOSCryptoService cryptoService)
        => $"{VerificationTokenPrefix}{cryptoService.GenerateOpaqueToken(24)}";

    public static string CreateVerificationToken(SqlOSCryptoService cryptoService, SqlOSSsoPortalOptions options)
        => $"{NormalizeValuePrefix(options.DomainVerificationRecordValuePrefix)}{cryptoService.GenerateOpaqueToken(24)}";

    public static SqlOSDomainOwnershipRecord BuildOwnershipRecord(
        string domain,
        string verificationToken,
        SqlOSSsoPortalOptions options)
        => new(
            "TXT",
            $"{NormalizeRecordPrefix(options.DomainVerificationRecordPrefix)}.{domain}",
            BuildVerificationValue(verificationToken, options));

    public static string BuildVerificationValue(string verificationToken, SqlOSSsoPortalOptions options)
        => $"{NormalizeValuePrefix(options.DomainVerificationRecordValuePrefix)}{ExtractVerificationTokenSuffix(verificationToken)}";

    public static string NormalizeDomain(string? value, SqlOSSsoPortalOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Domain is required.");
        }

        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            candidate = uri.Host;
        }

        var atIndex = candidate.LastIndexOf('@');
        if (atIndex >= 0)
        {
            candidate = candidate[(atIndex + 1)..];
        }

        candidate = candidate.Trim().Trim('[', ']').Trim('.').Trim().ToLowerInvariant();
        if (candidate.StartsWith("*.", StringComparison.Ordinal) || candidate.Contains('*', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Wildcard domains cannot be verified.");
        }

        string asciiDomain;
        try
        {
            asciiDomain = new IdnMapping().GetAscii(candidate).ToLowerInvariant();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Domain is not a valid DNS name.", ex);
        }

        if (IPAddress.TryParse(asciiDomain, out _))
        {
            throw new InvalidOperationException("IP addresses cannot be verified as organization domains.");
        }

        if (string.Equals(asciiDomain, "localhost", StringComparison.Ordinal))
        {
            if (options.AllowLocalhostDomainVerification)
            {
                return asciiDomain;
            }

            throw new InvalidOperationException("Localhost domain verification is disabled.");
        }

        if (!asciiDomain.Contains('.', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Domain must include a public DNS suffix.");
        }

        ValidateLabels(asciiDomain);
        foreach (var root in options.ReservedDomainRoots)
        {
            var normalizedRoot = NormalizeReservedRoot(root);
            if (string.IsNullOrWhiteSpace(normalizedRoot))
            {
                continue;
            }

            if (string.Equals(asciiDomain, normalizedRoot, StringComparison.Ordinal)
                || asciiDomain.EndsWith($".{normalizedRoot}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Domain is reserved by the SqlOS host: {normalizedRoot}.");
            }
        }

        return asciiDomain;
    }

    public static bool IsLocalhostDomain(string domain)
        => string.Equals(domain, "localhost", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeTxtValue(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains('"', StringComparison.Ordinal))
        {
            return trimmed;
        }

        var builder = new StringBuilder();
        var inQuote = false;
        var escaped = false;
        foreach (var ch in trimmed)
        {
            if (escaped)
            {
                builder.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && inQuote)
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (inQuote)
            {
                builder.Append(ch);
            }
        }

        return builder.Length == 0 ? trimmed.Trim('"') : builder.ToString();
    }

    private static void ValidateLabels(string domain)
    {
        if (domain.Length > 253)
        {
            throw new InvalidOperationException("Domain name is too long.");
        }

        var labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
        {
            throw new InvalidOperationException("Domain must include a public DNS suffix.");
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
            {
                throw new InvalidOperationException("Domain contains an invalid DNS label.");
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                throw new InvalidOperationException("Domain labels cannot start or end with a hyphen.");
            }

            if (!label.All(static ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                throw new InvalidOperationException("Domain contains characters that are not valid in DNS labels.");
            }
        }
    }

    private static string NormalizeRecordPrefix(string? value)
    {
        var prefix = string.IsNullOrWhiteSpace(value) ? "_sqlos-verify" : value.Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return "_sqlos-verify";
        }

        return prefix.ToLowerInvariant();
    }

    private static string NormalizeValuePrefix(string? value)
    {
        var prefix = string.IsNullOrWhiteSpace(value) ? "sqlos-domain-verification" : value.Trim().TrimEnd('=');
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return VerificationTokenPrefix;
        }

        return $"{prefix}=";
    }

    private static string ExtractVerificationTokenSuffix(string value)
    {
        var token = value.Trim();
        var separatorIndex = token.IndexOf('=');
        return separatorIndex >= 0 && separatorIndex < token.Length - 1
            ? token[(separatorIndex + 1)..]
            : token;
    }

    private static string? NormalizeReservedRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var root = value.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            return new IdnMapping().GetAscii(root).ToLowerInvariant();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Reserved domain root is invalid: {value}.", ex);
        }
    }
}

public sealed class SqlOSDnsOverHttpsDomainVerifier : ISqlOSDomainDnsVerifier
{
    private static readonly string[] Endpoints =
    [
        "https://cloudflare-dns.com/dns-query?name={0}&type=TXT",
        "https://dns.google/resolve?name={0}&type=TXT"
    ];

    private readonly HttpClient _httpClient;

    public SqlOSDnsOverHttpsDomainVerifier(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<bool> HasTxtRecordValueAsync(
        string recordName,
        string expectedValue,
        CancellationToken cancellationToken = default)
    {
        var normalizedExpected = SqlOSDomainOwnershipVerification.NormalizeTxtValue(expectedValue);
        foreach (var endpoint in Endpoints)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                string.Format(CultureInfo.InvariantCulture, endpoint, Uri.EscapeDataString(recordName.TrimEnd('.'))));
            request.Headers.Accept.ParseAdd("application/dns-json");

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (ContainsExpectedTxtValue(content, normalizedExpected))
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                continue;
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return false;
    }

    private static bool ContainsExpectedTxtValue(string content, string normalizedExpected)
    {
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("Answer", out var answers)
            || answers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var answer in answers.EnumerateArray())
        {
            if (answer.TryGetProperty("type", out var type) && type.GetInt32() != 16)
            {
                continue;
            }

            if (!answer.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var normalizedData = SqlOSDomainOwnershipVerification.NormalizeTxtValue(data.GetString() ?? string.Empty);
            if (string.Equals(normalizedData, normalizedExpected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
