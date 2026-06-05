using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSMfaPolicyService
{
    private readonly ISqlOSAuthServerDbContext? _context;
    private readonly SqlOSSettingsService? _settingsService;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSMfaPolicyService(IOptions<SqlOSAuthServerOptions> options)
    {
        _options = options.Value;
    }

    public SqlOSMfaPolicyService(
        ISqlOSAuthServerDbContext context,
        SqlOSSettingsService settingsService,
        IOptions<SqlOSAuthServerOptions> options)
        : this(options)
    {
        _context = context;
        _settingsService = settingsService;
    }

    public async Task<SqlOSMfaPolicyEvaluation> EvaluateAsync(
        string userId,
        string? organizationId,
        string? authenticationMethod,
        CancellationToken cancellationToken = default)
    {
        var context = _context ?? throw new InvalidOperationException("MFA policy evaluation requires a database context.");
        var settingsService = _settingsService ?? throw new InvalidOperationException("MFA policy evaluation requires settings service.");

        await settingsService.EnsureDefaultMfaSettingsAsync(cancellationToken);
        var settings = await context.Set<SqlOSMfaSettings>()
            .AsNoTracking()
            .FirstAsync(x => x.Id == "default", cancellationToken);

        if (!_options.Mfa.Enabled || !settings.Enabled || !_options.Mfa.Totp.Enabled || !settings.TotpEnabled)
        {
            return SqlOSMfaPolicyEvaluation.Disabled();
        }

        var orgPolicy = string.IsNullOrWhiteSpace(organizationId)
            ? null
            : await context.Set<SqlOSOrganizationMfaPolicy>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        var userOverride = await context.Set<SqlOSUserMfaPolicyOverride>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var membership = string.IsNullOrWhiteSpace(organizationId)
            ? null
            : await context.Set<SqlOSMembership>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.OrganizationId == organizationId
                    && x.UserId == userId
                    && x.IsActive,
                    cancellationToken);

        var policyEnabled = orgPolicy?.IsEnabled ?? false;
        var requiredRoles = DeserializeList(policyEnabled ? orgPolicy!.RequiredRolesJson : settings.RequiredRolesJson, ["owner", "admin"]);
        var availableFactors = DeserializeList(policyEnabled ? orgPolicy!.AvailableFactorsJson : settings.AvailableFactorsJson, [SqlOSMfaFactorTypes.Totp, SqlOSMfaFactorTypes.RecoveryCode])
            .Where(static value =>
                string.Equals(value, SqlOSMfaFactorTypes.Totp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, SqlOSMfaFactorTypes.RecoveryCode, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (availableFactors.Length == 0)
        {
            availableFactors = [SqlOSMfaFactorTypes.Totp];
        }

        var requireAll = policyEnabled ? orgPolicy!.RequireMfaForAllUsers : settings.RequireForAllUsers;
        var requirePrivileged = policyEnabled ? orgPolicy!.RequireMfaForOwnersAndAdmins : settings.RequireForOwnersAndAdmins;
        var selfEnrollment = policyEnabled ? orgPolicy!.UserSelfEnrollmentEnabled : settings.UserSelfEnrollmentEnabled;
        var recoveryCodesEnabled = policyEnabled ? orgPolicy!.RecoveryCodesEnabled : settings.RecoveryCodesEnabled;

        if (userOverride?.RequireMfa is bool userRequires)
        {
            requireAll = userRequires;
        }

        if (userOverride?.UserSelfEnrollmentEnabled is bool userSelfEnrollment)
        {
            selfEnrollment = userSelfEnrollment;
        }

        var roleRequires = requirePrivileged
            && membership != null
            && requiredRoles.Contains(membership.Role, StringComparer.OrdinalIgnoreCase);
        var requiresMfa = requireAll || roleRequires;
        var reason = requireAll
            ? "all_users"
            : roleRequires
                ? "role"
                : null;

        var hasTotp = await context.Set<SqlOSUserAuthenticator>()
            .AsNoTracking()
            .AnyAsync(x =>
                x.UserId == userId
                && x.Type == SqlOSMfaFactorTypes.Totp
                && x.IsConfirmed
                && x.RevokedAt == null,
                cancellationToken);
        var recoveryCodeCount = await context.Set<SqlOSRecoveryCode>()
            .AsNoTracking()
            .CountAsync(x =>
                x.UserId == userId
                && x.ConsumedAt == null
                && x.RevokedAt == null,
                cancellationToken);

        var hasRecoveryCodes = recoveryCodeCount > 0;
        var canSelfEnroll = selfEnrollment && availableFactors.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase);
        var enrollmentRequired = requiresMfa && !SatisfiesStrongMfa(authenticationMethod) && !hasTotp;

        return new SqlOSMfaPolicyEvaluation(
            true,
            requiresMfa && !SatisfiesStrongMfa(authenticationMethod),
            canSelfEnroll,
            enrollmentRequired,
            hasTotp,
            hasRecoveryCodes,
            recoveryCodeCount,
            recoveryCodesEnabled,
            availableFactors,
            reason);
    }

    public bool SatisfiesStrongMfa(string? authenticationMethod)
    {
        if (string.IsNullOrWhiteSpace(authenticationMethod))
        {
            return false;
        }

        foreach (var method in SplitAuthenticationMethods(authenticationMethod))
        {
            if (string.Equals(method, "phone_otp", StringComparison.OrdinalIgnoreCase))
            {
                if (_options.PhoneOtp.SatisfiesMfa)
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(method, SqlOSMfaFactorTypes.Totp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, SqlOSMfaFactorTypes.RecoveryCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "passkey", StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "webauthn", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool SatisfiesAdminOwnerStrongMfa(string? authenticationMethod)
        => SplitAuthenticationMethods(authenticationMethod)
            .Any(method =>
                !string.Equals(method, "phone_otp", StringComparison.OrdinalIgnoreCase)
                && SatisfiesStrongMfa(method));

    public static string AddAuthenticationMethod(string authenticationMethod, string secondFactorMethod)
    {
        var existing = SplitAuthenticationMethods(authenticationMethod).ToList();
        if (!existing.Contains(secondFactorMethod, StringComparer.OrdinalIgnoreCase))
        {
            existing.Add(secondFactorMethod);
        }

        return string.Join("+", existing);
    }

    internal static IEnumerable<string> SplitAuthenticationMethods(string? authenticationMethod)
        => (authenticationMethod ?? string.Empty)
            .Split(['+', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] DeserializeList(string json, string[] fallback)
    {
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json) ?? fallback;
            var normalized = values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return normalized.Length == 0 ? fallback : normalized;
        }
        catch
        {
            return fallback;
        }
    }
}

public sealed record SqlOSMfaPolicyEvaluation(
    bool Enabled,
    bool RequiresMfa,
    bool CanSelfEnroll,
    bool EnrollmentRequired,
    bool HasTotp,
    bool HasRecoveryCodes,
    int RecoveryCodeCount,
    bool RecoveryCodesEnabled,
    IReadOnlyList<string> AvailableFactors,
    string? Reason)
{
    public static SqlOSMfaPolicyEvaluation Disabled()
        => new(false, false, false, false, false, false, 0, false, Array.Empty<string>(), null);
}
