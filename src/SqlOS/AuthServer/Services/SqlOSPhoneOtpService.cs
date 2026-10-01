using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain.Events;
using SqlOS.Hosting;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSPhoneOtpService
{
    private const string PublicInvalidMessage = "The sign-in code is invalid or expired.";
    internal const string PublicStartMessage = "If an account exists for that phone number, check your messages for a sign-in code.";
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ISqlOSOtpDeliveryChannel _deliveryChannel;
    private readonly IAdmissionGate _admission;
    private readonly IAuditRecorder _auditRecorder;
    private readonly SqlOSAuthServerOptions _authOptions;
    private readonly SqlOSPhoneOtpOptions _options;

    public SqlOSPhoneOtpService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService,
        ISqlOSOtpDeliveryChannel deliveryChannel,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSDeliveryAdmissionService? deliveryAdmissionService = null)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
        _deliveryChannel = deliveryChannel;
        _admission = SqlOSAdmissionGate.Create(
            context,
            adminService,
            cryptoService,
            options,
            deliveryAdmissionService ?? new SqlOSDeliveryAdmissionService());
        _auditRecorder = new SqlOSAuditRecorder(context);
        _authOptions = options.Value;
        _options = options.Value.PhoneOtp;
    }

    public bool IsRuntimeConfigured => _options.IsConfigured;

    /// <summary>The phone-code options: lifetimes, cooldown, limits and allowed countries.</summary>
    internal SqlOSPhoneOtpOptions Options => _options;

    /// <summary>The admission gate phone codes pass (the delivery buckets).</summary>
    internal IAdmissionGate Admission => _admission;

    /// <summary>Records the failures phone codes audit without a state change.</summary>
    internal IAuditRecorder AuditRecorder => _auditRecorder;

    /// <summary>The provider that sends codes and checks them against the stored recipient.</summary>
    internal ISqlOSOtpDeliveryChannel DeliveryChannel => _deliveryChannel;

    /// <summary>The identity processes the phone-code facades below delegate to.</summary>
    private SqlOSIdentityProcesses Processes => new(_context, _adminService, _cryptoService, _settingsService, _authOptions)
    {
        PhoneCodes = this
    };

    public async Task<SqlOSPhoneOtpStartResult> StartForAuthorizationRequestAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string phoneNumber,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => Started(await Processes.StartPhoneOtpSignIn().ExecuteAsync(
            new StartPhoneOtpSignInCommand(
                phoneNumber,
                LoginTarget.ForBrowser(authorizationRequest, invitationToken: null),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.Hosted)),
            cancellationToken));

    public async Task<SqlOSPhoneOtpStartResult> StartForClientAsync(
        SqlOSPhoneOtpStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => Started(await Processes.StartPhoneOtpSignIn().ExecuteAsync(
            new StartPhoneOtpSignInCommand(
                request.PhoneNumber,
                new LoginTarget.DirectLogin(request.ClientId, request.OrganizationId),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken));

    public async Task<SqlOSPhoneOtpSignupStartResult> StartSignupForAuthorizationRequestAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string displayName,
        string phoneNumber,
        string? organizationName,
        JsonObject? customFields = null,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => Started(await Processes.StartPhoneOtpSignUp().ExecuteAsync(
            new StartPhoneOtpSignUpCommand(
                displayName,
                phoneNumber,
                organizationName,
                customFields,
                LoginTarget.ForBrowser(authorizationRequest, invitationToken: null),
                CarriesInvitation: false,
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.Hosted)),
            cancellationToken));

    public async Task<SqlOSPhoneOtpSignupStartResult> StartSignupForClientAsync(
        SqlOSPhoneOtpSignupStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => Started(await Processes.StartPhoneOtpSignUp().ExecuteAsync(
            new StartPhoneOtpSignUpCommand(
                request.DisplayName,
                request.PhoneNumber,
                request.OrganizationName,
                request.CustomFields,
                new LoginTarget.DirectLogin(request.ClientId, request.OrganizationId),
                CarriesInvitation: false,
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken));

    public async Task<SqlOSPhoneOtpStartResult> StartEnrollmentAsync(
        SqlOSUser? authenticatedUser,
        string phoneNumber,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsurePhoneOtpEnabledAsync(cancellationToken);
        if (authenticatedUser == null)
        {
            throw new InvalidOperationException("Sign in before changing phone numbers.");
        }

        var issue = await new PhoneCodeChallenges(_context, this).IssueAsync(
            new PhoneCodeIssueRequest(
                phoneNumber,
                PhoneOtpPurposes.Enrollment,
                new PhoneOtpChallengeContext(null, null, null),
                authenticatedUser.Id,
                SendWhenNoAccount: true,
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.Hosted)),
            _cryptoService.Clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        return issue switch
        {
            PhoneCodeIssue.Issued issued => issued.Result,
            PhoneCodeIssue.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown phone-code issue '{issue.GetType().Name}'.")
        };
    }

    public async Task<SqlOSUserPhoneNumber> VerifyEnrollmentAsync(
        SqlOSUser? authenticatedUser,
        SqlOSPhoneOtpEnrollmentVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsurePhoneOtpEnabledAsync(cancellationToken);
        if (authenticatedUser == null)
        {
            throw new InvalidOperationException("Sign in before changing phone numbers.");
        }

        var check = await new PhoneCodeChallenges(_context, this).VerifyAsync(
            new PhoneCodeVerifyRequest(request.ChallengeToken, request.Code, PhoneOtpPurposes.Enrollment, new ChallengeBinding(null, RequireMatch: false)),
            _cryptoService.Clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        var challenge = check switch
        {
            PhoneCodeCheck.Verified verified => verified.Challenge,
            PhoneCodeCheck.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown phone-code check '{check.GetType().Name}'.")
        };

        if (!string.Equals(challenge.UserId, authenticatedUser.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        var phoneNumber = Unprotect(challenge.PhoneNumberEncrypted);
        return await AddVerifiedPhoneNumberAsync(authenticatedUser, phoneNumber, enrollment: challenge, cancellationToken);
    }

    public async Task<SqlOSPhoneOtpVerificationResult> VerifyAsync(
        SqlOSPhoneOtpVerifyRequest request,
        CancellationToken cancellationToken = default)
        => await VerifyAsync(
            request,
            expectedAuthorizationRequestId: null,
            requireAuthorizationRequestMatch: false,
            cancellationToken);

    /// <summary>
    /// Verifies a sign-in code without completing the sign-in: the phone-code sign-in
    /// (<see cref="VerifyPhoneOtpSignIn"/>) with only its credential.
    /// </summary>
    public async Task<SqlOSPhoneOtpVerificationResult> VerifyAsync(
        SqlOSPhoneOtpVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.VerifyPhoneOtpSignIn(httpContext: null).ExecuteAsync(
            new VerifyPhoneOtpSignInCommand(
                request.ChallengeToken,
                request.Code,
                LoginTarget.CredentialOnly.Instance,
                new ChallengeBinding(expectedAuthorizationRequestId, requireAuthorizationRequestMatch)),
            cancellationToken);
        var signedIn = outcome switch
        {
            PhoneCodeSignInOutcome.SignedIn success => success,
            PhoneCodeSignInOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown phone-code sign-in outcome '{outcome.GetType().Name}'.")
        };

        var organizations = await _adminService.GetUserOrganizationsAsync(signedIn.Evidence.UserId, cancellationToken);
        return new SqlOSPhoneOtpVerificationResult(signedIn.Challenge, signedIn.Evidence.User, organizations, signedIn.Evidence.AuthenticationMethod);
    }

    /// <summary>
    /// Verifies a phone-code sign-up's token and code without creating the account (the step the
    /// phone-code sign-up runs inside its transaction).
    /// </summary>
    public async Task<SqlOSPhoneOtpSignupVerificationResult> VerifySignupAsync(
        SqlOSPhoneOtpSignupVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        await EnsurePhoneOtpEnabledAsync(cancellationToken);
        var check = await PhoneOtpSignupTokens.VerifyAsync(
            _context,
            this,
            request.SignupToken,
            request.ChallengeToken,
            request.Code,
            new ChallengeBinding(expectedAuthorizationRequestId, requireAuthorizationRequestMatch),
            _cryptoService.Clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        return check switch
        {
            PhoneOtpSignupCheck.Verified verified => verified.Result,
            PhoneOtpSignupCheck.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown phone-code sign-up check '{check.GetType().Name}'.")
        };
    }

    private static SqlOSPhoneOtpStartResult Started(PhoneCodeStartOutcome outcome) => outcome switch
    {
        PhoneCodeStartOutcome.Sent sent => sent.Result,
        PhoneCodeStartOutcome.Refused refused => throw refused.Refusal.ToException(),
        _ => throw new InvalidOperationException($"Unknown phone-code start outcome '{outcome.GetType().Name}'.")
    };

    private static SqlOSPhoneOtpSignupStartResult Started(PhoneCodeSignUpStartOutcome outcome) => outcome switch
    {
        PhoneCodeSignUpStartOutcome.Sent sent => sent.Result,
        PhoneCodeSignUpStartOutcome.Refused refused => throw refused.Refusal.ToException(),
        _ => throw new InvalidOperationException($"Unknown phone-code sign-up start outcome '{outcome.GetType().Name}'.")
    };

    public async Task ConsumeSignupTokenAsync(
        string signupToken,
        CancellationToken cancellationToken = default)
    {
        var rawSignupToken = signupToken?.Trim()
            ?? throw new InvalidOperationException(PublicInvalidMessage);
        _ = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.PhoneOtpSignup, rawSignupToken, cancellationToken)
            ?? throw new InvalidOperationException(PublicInvalidMessage);
    }

    public Task<SqlOSUserPhoneNumber> AddVerifiedPhoneNumberAsync(
        SqlOSUser user,
        string e164PhoneNumber,
        CancellationToken cancellationToken = default)
        => AddVerifiedPhoneNumberAsync(user, e164PhoneNumber, enrollment: null, cancellationToken);

    /// <summary>
    /// Adds a verified number to <paramref name="user"/>'s account. An enrollment challenge that
    /// proved the number records the enrollment in the same save.
    /// </summary>
    private async Task<SqlOSUserPhoneNumber> AddVerifiedPhoneNumberAsync(
        SqlOSUser user,
        string e164PhoneNumber,
        SqlOSPhoneOtpChallenge? enrollment,
        CancellationToken cancellationToken)
    {
        // A number belongs to one account at a time (the unique index on active numbers is the
        // guarantee; this check gives the 7.x refusal).
        var phoneHash = _cryptoService.HashToken(e164PhoneNumber);
        var holder = await _context.Set<SqlOSUserPhoneNumber>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PhoneNumberHash == phoneHash && x.RemovedAt == null, cancellationToken);
        if (holder != null && !string.Equals(holder.UserId, user.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An account already exists for this phone number.");
        }

        var account = await _context.GetUserAsync(user.Id, SqlOSUserParts.PhoneNumbers, cancellationToken);
        var record = account.AddVerifiedPhone(
            e164PhoneNumber,
            _cryptoService.ProtectSecret(e164PhoneNumber),
            _cryptoService.Clock.GetUtcNow().UtcDateTime);
        enrollment?.RecordEnrollment(account.Id, record.Id);
        await _context.SaveChangesAsync(cancellationToken);
        return record;
    }

    private async Task EnsurePhoneOtpEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!settings.PhoneOtpEnabled)
        {
            throw new InvalidOperationException("Phone sign-in is unavailable.");
        }
    }

    /// <summary>
    /// <paramref name="phoneNumber"/> in E.164, parsed with the host's default region and allowed by
    /// its country lists, or why not.
    /// </summary>
    internal PhoneNumberCheck Normalize(string? phoneNumber)
    {
        var defaultRegion = string.IsNullOrWhiteSpace(_options.DefaultRegion) ? null : _options.DefaultRegion.Trim().ToUpperInvariant();
        if (!PhoneNumber.TryParse(phoneNumber, defaultRegion, out var normalized, out var error))
        {
            return new PhoneNumberCheck.Invalid(new IdentityRefusal("phone_number_invalid", error));
        }

        return IsCountryAllowed(normalized.Region)
            ? new PhoneNumberCheck.Valid(normalized)
            : new PhoneNumberCheck.Invalid(new IdentityRefusal("phone_country_not_allowed", "Phone number country is not allowed."));
    }

    /// <summary>The number as the challenge stores it, protected with the host's data protection.</summary>
    internal string Protect(string e164PhoneNumber) => _cryptoService.ProtectSecret(e164PhoneNumber);

    /// <summary>The number a challenge stores.</summary>
    internal string Unprotect(string protectedPhoneNumber) => _cryptoService.UnprotectSecret(protectedPhoneNumber);

    private bool IsCountryAllowed(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return false;
        }

        var denied = NormalizeCountryList(_options.CountryDenyList);
        if (denied.Contains(region, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var allowed = NormalizeCountryList(_options.CountryAllowList);
        return allowed.Length == 0 || allowed.Contains(region, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] NormalizeCountryList(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed record SqlOSPhoneOtpVerificationResult(
    SqlOSPhoneOtpChallenge Challenge,
    SqlOSUser User,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    string AuthenticationMethod);

public sealed record SqlOSPhoneOtpSignupVerificationResult(
    string SignupToken,
    string? ClientApplicationId,
    string? ClientId,
    string DisplayName,
    string PhoneNumber,
    string? OrganizationName,
    string? OrganizationId,
    JsonObject? CustomFields);
