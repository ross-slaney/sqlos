using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Domain;

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

    /// <summary>
    /// Normalizes a domain an organization claims; the rules live in <see cref="DomainName"/>.
    /// Throws <see cref="InvalidOperationException"/> with the rule's message when the domain
    /// cannot be claimed.
    /// </summary>
    public static string NormalizeDomain(string? value, SqlOSSsoPortalOptions options)
        => DomainName.TryParse(value, ToDomainNameRules(options), out var domainName, out var error)
            ? domainName.Value
            : throw new InvalidOperationException(error);

    internal static DomainNameRules ToDomainNameRules(SqlOSSsoPortalOptions options)
        => new(options.ReservedDomainRoots, options.AllowLocalhostDomainVerification);

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
