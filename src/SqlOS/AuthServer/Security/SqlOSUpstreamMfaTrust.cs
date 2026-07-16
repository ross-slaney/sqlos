using System.Security.Claims;
using System.Text.Json;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Security;

internal static class SqlOSUpstreamMfaTrust
{
    public const string AuthenticationMethod = "upstream_mfa";

    public static SqlOSUpstreamMfaDecision EvaluateOidc(
        SqlOSOidcConnection connection,
        ClaimsPrincipal validatedIdToken)
    {
        var amr = ReadClaimValues(validatedIdToken, "amr");
        var acr = ReadClaimValues(validatedIdToken, "acr");
        var present = amr.Length > 0 || acr.Length > 0;
        if (!connection.TrustUpstreamMfa)
        {
            return SqlOSUpstreamMfaDecision.NotAccepted(
                present,
                present ? "trust_disabled" : "evidence_missing",
                amr,
                acr);
        }

        var acceptedAmr = ParseConfiguredValues(connection.AcceptedAmrValuesJson);
        var acceptedAcr = ParseConfiguredValues(connection.AcceptedAcrValuesJson);
        var matchedAmr = amr.FirstOrDefault(value =>
            acceptedAmr.Contains(value, StringComparer.OrdinalIgnoreCase));
        if (matchedAmr != null)
        {
            return SqlOSUpstreamMfaDecision.AcceptedBy("amr", matchedAmr, amr, acr);
        }

        var matchedAcr = acr.FirstOrDefault(value =>
            acceptedAcr.Contains(value, StringComparer.Ordinal));
        return matchedAcr != null
            ? SqlOSUpstreamMfaDecision.AcceptedBy("acr", matchedAcr, amr, acr)
            : SqlOSUpstreamMfaDecision.NotAccepted(
                present,
                present ? "evidence_unrecognized" : "evidence_missing",
                amr,
                acr);
    }

    public static SqlOSUpstreamMfaDecision EvaluateSaml(
        SqlOSSsoConnection connection,
        IReadOnlyList<string> authnContextClassRefs)
    {
        var evidence = authnContextClassRefs
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!connection.TrustUpstreamMfa)
        {
            return SqlOSUpstreamMfaDecision.NotAccepted(
                evidence.Length > 0,
                evidence.Length > 0 ? "trust_disabled" : "evidence_missing",
                samlAuthnContextClassRefs: evidence);
        }

        var accepted = ParseConfiguredValues(connection.AcceptedAuthnContextClassRefsJson);
        var matched = evidence.FirstOrDefault(value => accepted.Contains(value, StringComparer.Ordinal));
        return matched != null
            ? SqlOSUpstreamMfaDecision.AcceptedBy(
                "saml_authn_context",
                matched,
                samlAuthnContextClassRefs: evidence)
            : SqlOSUpstreamMfaDecision.NotAccepted(
                evidence.Length > 0,
                evidence.Length > 0 ? "evidence_unrecognized" : "evidence_missing",
                samlAuthnContextClassRefs: evidence);
    }

    private static string[] ReadClaimValues(ClaimsPrincipal principal, string type)
        => principal.Claims
            .Where(claim => string.Equals(claim.Type, type, StringComparison.Ordinal))
            .SelectMany(claim => ExpandClaimValue(claim.Value))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<string> ExpandClaimValue(string value)
    {
        if (!value.TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            return [value];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch (JsonException)
        {
            return [value];
        }
    }

    private static string[] ParseConfiguredValues(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json) ?? [])
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

internal sealed record SqlOSUpstreamMfaDecision(
    bool EvidencePresent,
    bool Accepted,
    string Reason,
    string? AcceptedClaim,
    string? AcceptedValue,
    IReadOnlyList<string> AmrValues,
    IReadOnlyList<string> AcrValues,
    IReadOnlyList<string> SamlAuthnContextClassRefs)
{
    public static SqlOSUpstreamMfaDecision AcceptedBy(
        string claim,
        string value,
        IReadOnlyList<string>? amrValues = null,
        IReadOnlyList<string>? acrValues = null,
        IReadOnlyList<string>? samlAuthnContextClassRefs = null)
        => new(
            true,
            true,
            "accepted",
            claim,
            value,
            amrValues ?? [],
            acrValues ?? [],
            samlAuthnContextClassRefs ?? []);

    public static SqlOSUpstreamMfaDecision NotAccepted(
        bool evidencePresent,
        string reason,
        IReadOnlyList<string>? amrValues = null,
        IReadOnlyList<string>? acrValues = null,
        IReadOnlyList<string>? samlAuthnContextClassRefs = null)
        => new(
            evidencePresent,
            false,
            reason,
            null,
            null,
            amrValues ?? [],
            acrValues ?? [],
            samlAuthnContextClassRefs ?? []);
}
