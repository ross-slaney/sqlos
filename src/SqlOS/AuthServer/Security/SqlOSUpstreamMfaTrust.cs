using System.Security.Claims;
using System.Text.Json;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Security;

internal static class SqlOSUpstreamMfaTrust
{
    public const string AuthenticationMethod = "upstream_mfa";
    private const int MaxEvidenceValues = 32;
    private const int MaxEvidenceValueLength = 500;

    public static SqlOSUpstreamMfaDecision EvaluateOidc(
        SqlOSOidcConnection connection,
        ClaimsPrincipal validatedIdToken)
    {
        var amr = ReadClaimValues(validatedIdToken, "amr");
        var acr = ReadClaimValues(validatedIdToken, "acr");
        var present = amr.Values.Length > 0 || acr.Values.Length > 0 || amr.ExceededLimits || acr.ExceededLimits;
        if (amr.ExceededLimits || acr.ExceededLimits)
        {
            return SqlOSUpstreamMfaDecision.NotAccepted(
                present,
                "evidence_limits_exceeded",
                amr.Values,
                acr.Values);
        }

        if (!connection.TrustUpstreamMfa)
        {
            return SqlOSUpstreamMfaDecision.NotAccepted(
                present,
                present ? "trust_disabled" : "evidence_missing",
                amr.Values,
                acr.Values);
        }

        var acceptedAmr = ParseConfiguredValues(connection.AcceptedAmrValuesJson);
        var acceptedAcr = ParseConfiguredValues(connection.AcceptedAcrValuesJson);
        var matchedAmr = amr.Values.FirstOrDefault(value =>
            acceptedAmr.Contains(value, StringComparer.OrdinalIgnoreCase));
        if (matchedAmr != null)
        {
            return SqlOSUpstreamMfaDecision.AcceptedBy("amr", matchedAmr, amr.Values, acr.Values);
        }

        var matchedAcr = acr.Values.FirstOrDefault(value =>
            acceptedAcr.Contains(value, StringComparer.Ordinal));
        return matchedAcr != null
            ? SqlOSUpstreamMfaDecision.AcceptedBy("acr", matchedAcr, amr.Values, acr.Values)
            : SqlOSUpstreamMfaDecision.NotAccepted(
                present,
                present ? "evidence_unrecognized" : "evidence_missing",
                amr.Values,
                acr.Values);
    }

    public static SqlOSUpstreamMfaDecision EvaluateSaml(
        SqlOSSsoConnection connection,
        IReadOnlyList<string> authnContextClassRefs)
    {
        var evidence = NormalizeEvidence(authnContextClassRefs);
        if (evidence.ExceededLimits)
        {
            return SqlOSUpstreamMfaDecision.NotAccepted(
                true,
                "evidence_limits_exceeded",
                samlAuthnContextClassRefs: evidence.Values);
        }

        if (!connection.TrustUpstreamMfa)
        {
            return SqlOSUpstreamMfaDecision.NotAccepted(
                evidence.Values.Length > 0,
                evidence.Values.Length > 0 ? "trust_disabled" : "evidence_missing",
                samlAuthnContextClassRefs: evidence.Values);
        }

        var accepted = ParseConfiguredValues(connection.AcceptedAuthnContextClassRefsJson);
        var matched = evidence.Values.FirstOrDefault(value => accepted.Contains(value, StringComparer.Ordinal));
        return matched != null
            ? SqlOSUpstreamMfaDecision.AcceptedBy(
                "saml_authn_context",
                matched,
                samlAuthnContextClassRefs: evidence.Values)
            : SqlOSUpstreamMfaDecision.NotAccepted(
                evidence.Values.Length > 0,
                evidence.Values.Length > 0 ? "evidence_unrecognized" : "evidence_missing",
                samlAuthnContextClassRefs: evidence.Values);
    }

    private static EvidenceValues ReadClaimValues(ClaimsPrincipal principal, string type)
        => NormalizeEvidence(principal.Claims
            .Where(claim => string.Equals(claim.Type, type, StringComparison.Ordinal))
            .SelectMany(claim => ExpandClaimValue(claim.Value)));

    private static EvidenceValues NormalizeEvidence(IEnumerable<string> source)
    {
        var values = source
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var exceededLimits = values.Length > MaxEvidenceValues
            || values.Any(static value => value.Length > MaxEvidenceValueLength);
        return new EvidenceValues(
            values
                .Where(static value => value.Length <= MaxEvidenceValueLength)
                .Take(MaxEvidenceValues)
                .ToArray(),
            exceededLimits);
    }

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

    private sealed record EvidenceValues(string[] Values, bool ExceededLimits);
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
