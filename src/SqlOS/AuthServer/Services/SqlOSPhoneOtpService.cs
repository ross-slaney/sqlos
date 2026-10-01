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
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain;
using SqlOS.Domain.Events;

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

    public async Task<SqlOSPhoneOtpStartResult> StartForAuthorizationRequestAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string phoneNumber,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsurePhoneOtpEnabledAsync(cancellationToken);

        return await CreateChallengeAsync(
            phoneNumber,
            authorizationRequestId: authorizationRequest?.Id,
            clientApplicationId: authorizationRequest?.ClientApplicationId,
            requestedOrganizationId: null,
            userId: null,
            userPhoneNumberId: null,
            sendWhenNoUser: false,
            purpose: PhoneOtpPurposes.Login,
            httpContext,
            cancellationToken);
    }

    public async Task<SqlOSPhoneOtpStartResult> StartForClientAsync(
        SqlOSPhoneOtpStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsurePhoneOtpEnabledAsync(cancellationToken);

        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, userId: null, cancellationToken);
        return await CreateChallengeAsync(
            request.PhoneNumber,
            authorizationRequestId: null,
            clientApplicationId: client.Id,
            requestedOrganizationId: request.OrganizationId,
            userId: null,
            userPhoneNumberId: null,
            sendWhenNoUser: false,
            purpose: PhoneOtpPurposes.Login,
            httpContext,
            cancellationToken);
    }

    public async Task<SqlOSPhoneOtpSignupStartResult> StartSignupForAuthorizationRequestAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string displayName,
        string phoneNumber,
        string? organizationName,
        JsonObject? customFields = null,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsurePhoneOtpEnabledAsync(cancellationToken);

        var trimmedDisplayName = RequireText(displayName, "Display name is required.");
        SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(authorizationRequest?.OrganizationId);
        var normalizedPhoneNumber = await EnsurePhoneNumberAvailableForSignupAsync(phoneNumber, cancellationToken);

        var challenge = await CreateChallengeAsync(
            normalizedPhoneNumber,
            authorizationRequestId: authorizationRequest?.Id,
            clientApplicationId: authorizationRequest?.ClientApplicationId,
            requestedOrganizationId: null,
            userId: null,
            userPhoneNumberId: null,
            sendWhenNoUser: true,
            purpose: PhoneOtpPurposes.Signup,
            httpContext,
            cancellationToken);

        var signupToken = (await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.PhoneOtpSignup,
            new PhoneOtpSignupPayload(
                _cryptoService.HashToken(challenge.ChallengeToken),
                authorizationRequest?.Id,
                authorizationRequest?.ClientApplication?.ClientId,
                authorizationRequest?.ClientApplicationId,
                trimmedDisplayName,
                challenge.PhoneNumber,
                string.IsNullOrWhiteSpace(organizationName) ? null : organizationName.Trim(),
                authorizationRequest?.OrganizationId,
                customFields),
            new TemporaryTokenBinding(ClientApplicationId: authorizationRequest?.ClientApplicationId),
            _options.ChallengeLifetime,
            cancellationToken)).RawToken;

        return new SqlOSPhoneOtpSignupStartResult(
            challenge.ChallengeToken,
            signupToken,
            challenge.PhoneNumber,
            challenge.MaskedPhoneNumber,
            challenge.Message,
            challenge.ExpiresAt,
            challenge.NextAllowedSendAt);
    }

    public async Task<SqlOSPhoneOtpSignupStartResult> StartSignupForClientAsync(
        SqlOSPhoneOtpSignupStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsurePhoneOtpEnabledAsync(cancellationToken);

        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, userId: null, cancellationToken);
        var trimmedDisplayName = RequireText(request.DisplayName, "Display name is required.");
        SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(request.OrganizationId);
        var normalizedPhoneNumber = await EnsurePhoneNumberAvailableForSignupAsync(request.PhoneNumber, cancellationToken);

        var challenge = await CreateChallengeAsync(
            normalizedPhoneNumber,
            authorizationRequestId: null,
            clientApplicationId: client.Id,
            requestedOrganizationId: null,
            userId: null,
            userPhoneNumberId: null,
            sendWhenNoUser: true,
            purpose: PhoneOtpPurposes.Signup,
            httpContext,
            cancellationToken);

        var signupToken = (await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.PhoneOtpSignup,
            new PhoneOtpSignupPayload(
                _cryptoService.HashToken(challenge.ChallengeToken),
                AuthorizationRequestId: null,
                ClientId: client.ClientId,
                ClientApplicationId: client.Id,
                DisplayName: trimmedDisplayName,
                PhoneNumber: challenge.PhoneNumber,
                OrganizationName: string.IsNullOrWhiteSpace(request.OrganizationName) ? null : request.OrganizationName.Trim(),
                OrganizationId: request.OrganizationId,
                CustomFields: request.CustomFields),
            new TemporaryTokenBinding(ClientApplicationId: client.Id, OrganizationId: request.OrganizationId),
            _options.ChallengeLifetime,
            cancellationToken)).RawToken;

        return new SqlOSPhoneOtpSignupStartResult(
            challenge.ChallengeToken,
            signupToken,
            challenge.PhoneNumber,
            challenge.MaskedPhoneNumber,
            challenge.Message,
            challenge.ExpiresAt,
            challenge.NextAllowedSendAt);
    }

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

        return await CreateChallengeAsync(
            phoneNumber,
            authorizationRequestId: null,
            clientApplicationId: null,
            requestedOrganizationId: null,
            userId: authenticatedUser.Id,
            userPhoneNumberId: null,
            sendWhenNoUser: true,
            purpose: PhoneOtpPurposes.Enrollment,
            httpContext,
            cancellationToken);
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

        var challenge = await VerifyChallengeAsync(
            new SqlOSPhoneOtpVerifyRequest(request.ChallengeToken, request.Code),
            expectedAuthorizationRequestId: null,
            requireAuthorizationRequestMatch: false,
            expectedPurpose: PhoneOtpPurposes.Enrollment,
            cancellationToken);

        if (!string.Equals(challenge.UserId, authenticatedUser.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        var phoneNumber = UnprotectPhoneNumber(challenge.PhoneNumberEncrypted);
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

    public async Task<SqlOSPhoneOtpVerificationResult> VerifyAsync(
        SqlOSPhoneOtpVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        await EnsurePhoneOtpEnabledAsync(cancellationToken);

        var challenge = await VerifyChallengeAsync(
            request,
            expectedAuthorizationRequestId,
            requireAuthorizationRequestMatch,
            expectedPurpose: PhoneOtpPurposes.Login,
            cancellationToken);

        if (challenge.User == null || !challenge.User.IsActive)
        {
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        if (challenge.UserPhoneNumberId != null)
        {
            await _context.LoadUserPartsAsync(challenge.User, SqlOSUserParts.PhoneNumbers, cancellationToken);
        }

        challenge.User.RecordPhoneSignIn(challenge.UserPhoneNumberId, DateTime.UtcNow);
        await _context.SaveChangesAsync(cancellationToken);

        var organizations = await _adminService.GetUserOrganizationsAsync(challenge.User.Id, cancellationToken);
        return new SqlOSPhoneOtpVerificationResult(challenge, challenge.User, organizations, "phone_otp");
    }

    public async Task<SqlOSPhoneOtpSignupVerificationResult> VerifySignupAsync(
        SqlOSPhoneOtpSignupVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        await EnsurePhoneOtpEnabledAsync(cancellationToken);

        var signupToken = request.SignupToken?.Trim()
            ?? throw new InvalidOperationException(PublicInvalidMessage);
        var token = await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.PhoneOtpSignup, signupToken, cancellationToken)
            ?? throw new InvalidOperationException(PublicInvalidMessage);
        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.PhoneOtpSignup)
            ?? throw new InvalidOperationException(PublicInvalidMessage);

        if (requireAuthorizationRequestMatch)
        {
            if (string.IsNullOrWhiteSpace(expectedAuthorizationRequestId))
            {
                if (!string.IsNullOrWhiteSpace(payload.AuthorizationRequestId))
                {
                    throw new InvalidOperationException(PublicInvalidMessage);
                }
            }
            else if (!string.Equals(payload.AuthorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(PublicInvalidMessage);
            }
        }

        var rawChallengeToken = request.ChallengeToken?.Trim()
            ?? throw new InvalidOperationException(PublicInvalidMessage);
        if (!string.Equals(payload.ChallengeTokenHash, _cryptoService.HashToken(rawChallengeToken), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        var challenge = await VerifyChallengeAsync(
            new SqlOSPhoneOtpVerifyRequest(rawChallengeToken, request.Code),
            expectedAuthorizationRequestId,
            requireAuthorizationRequestMatch,
            expectedPurpose: PhoneOtpPurposes.Signup,
            cancellationToken);

        if (challenge.User != null)
        {
            throw new InvalidOperationException("An account already exists for this phone number. Sign in with a phone code instead.");
        }

        var existingPhone = await _context.Set<SqlOSUserPhoneNumber>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PhoneNumberHash == challenge.PhoneNumberHash && x.RemovedAt == null, cancellationToken);
        if (existingPhone != null)
        {
            throw new InvalidOperationException("An account already exists for this phone number. Sign in with a phone code instead.");
        }

        return new SqlOSPhoneOtpSignupVerificationResult(
            signupToken,
            token.ClientApplicationId ?? payload.ClientApplicationId,
            payload.ClientId,
            payload.DisplayName,
            payload.PhoneNumber,
            payload.OrganizationName,
            token.OrganizationId ?? payload.OrganizationId,
            payload.CustomFields);
    }

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
        var record = account.AddVerifiedPhone(e164PhoneNumber, _cryptoService.ProtectSecret(e164PhoneNumber), DateTime.UtcNow);
        enrollment?.RecordEnrollment(account.Id, record.Id);
        await _context.SaveChangesAsync(cancellationToken);
        return record;
    }

    private async Task<SqlOSPhoneOtpChallenge> VerifyChallengeAsync(
        SqlOSPhoneOtpVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        string expectedPurpose,
        CancellationToken cancellationToken)
    {
        var rawChallengeToken = request.ChallengeToken?.Trim()
            ?? throw new InvalidOperationException(PublicInvalidMessage);
        var normalizedCode = NormalizeCode(request.Code);
        var challengeHash = _cryptoService.HashToken(rawChallengeToken);
        var challenge = await _context.Set<SqlOSPhoneOtpChallenge>()
            .Include(x => x.User)
            .Include(x => x.UserPhoneNumber)
            .Include(x => x.AuthorizationRequest)
            .ThenInclude(x => x!.ClientApplication)
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(x => x.ChallengeTokenHash == challengeHash, cancellationToken)
            ?? throw new InvalidOperationException(PublicInvalidMessage);

        if (!challenge.IsOpen(DateTime.UtcNow)
            || !challenge.IsFor(expectedPurpose)
            || (requireAuthorizationRequestMatch && !challenge.AnswersAuthorizationRequest(expectedAuthorizationRequestId)))
        {
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        if (!challenge.ProviderStarted)
        {
            await RejectChallengeAsync(challenge, SqlOSPhoneOtpChallenge.NotStartedReason, cancellationToken);
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        // The provider checks the code against the stored recipient, never a number from the request.
        var phoneNumber = UnprotectPhoneNumber(challenge.PhoneNumberEncrypted);
        var check = await _deliveryChannel.CheckAsync(
            phoneNumber,
            normalizedCode,
            new SqlOSOtpDeliveryContext(
                challenge.Purpose,
                challenge.ClientApplicationId,
                challenge.AuthorizationRequestId,
                challenge.IpAddress,
                challenge.UserAgent,
                challenge.ProviderChallengeId),
            cancellationToken);

        if (challenge.RegisterCheck(check) == PhoneOtpCheckOutcome.Rejected)
        {
            await RejectChallengeAsync(
                challenge,
                check.SanitizedError ?? check.ProviderStatus ?? SqlOSPhoneOtpChallenge.ProviderRejectedReason,
                cancellationToken);
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        challenge.Complete(DateTime.UtcNow);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent verification spent the challenge first; nothing this one staged may
            // reach a later save of the same unit of work.
            ((DbContext)_context).Entry(challenge).State = EntityState.Detached;
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        return challenge;
    }

    private async Task RejectChallengeAsync(
        SqlOSPhoneOtpChallenge challenge,
        string reason,
        CancellationToken cancellationToken)
    {
        challenge.RejectCode(reason, DateTime.UtcNow);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<SqlOSPhoneOtpStartResult> CreateChallengeAsync(
        string phoneNumber,
        string? authorizationRequestId,
        string? clientApplicationId,
        string? requestedOrganizationId,
        string? userId,
        string? userPhoneNumberId,
        bool sendWhenNoUser,
        string purpose,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePhoneNumber(phoneNumber);
        var now = DateTime.UtcNow;
        var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
        var phoneHash = _cryptoService.HashToken(normalized.E164);
        var maskedPhone = Masked.Phone(normalized.E164);
        var context = new PhoneOtpChallengeContext(authorizationRequestId, clientApplicationId, requestedOrganizationId);

        var phoneRecord = await _context.Set<SqlOSUserPhoneNumber>()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.PhoneNumberHash == phoneHash && x.RemovedAt == null && x.IsVerified, cancellationToken);

        var effectiveUserId = userId ?? phoneRecord?.UserId;
        var effectiveUserPhoneNumberId = userPhoneNumberId ?? phoneRecord?.Id;
        var admission = await _admission.AdmitPhoneCodeAsync(
            phoneHash,
            effectiveUserId,
            AdmissionOrigin.Of(httpContext),
            clientApplicationId,
            now,
            cancellationToken);
        if (!admission.Admitted)
        {
            _auditRecorder.Record(new PhoneOtpSendRateLimited(
                purpose,
                maskedPhone,
                ipAddress,
                admission.RefusedLimit ?? "phone",
                clientApplicationId,
                requestedOrganizationId));
            await _context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Too many sign-in code requests. Try again later.");
        }

        now = DateTime.UtcNow;

        var recentChallenges = await _context.Set<SqlOSPhoneOtpChallenge>()
            .Where(x => x.PhoneNumberHash == phoneHash && x.CreatedAt >= now.Subtract(_options.RateLimitWindow))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var latestContextChallenge = recentChallenges
            .FirstOrDefault(x => x.WasStartedIn(context, purpose) && !x.IsInvalidated);
        if (latestContextChallenge != null && latestContextChallenge.WasSentWithin(_options.ResendCooldown, now))
        {
            throw new InvalidOperationException($"Wait {(int)Math.Ceiling(_options.ResendCooldown.TotalSeconds)} seconds before requesting another code.");
        }

        var activeChallenges = await _context.Set<SqlOSPhoneOtpChallenge>()
            .Where(x => x.PhoneNumberHash == phoneHash
                && x.ConsumedAt == null
                && x.InvalidatedAt == null
                && x.ExpiresAt > now
                && x.AuthorizationRequestId == authorizationRequestId
                && x.ClientApplicationId == clientApplicationId
                && x.RequestedOrganizationId == requestedOrganizationId
                && x.Purpose == purpose)
            .ToListAsync(cancellationToken);

        foreach (var activeChallenge in activeChallenges)
        {
            activeChallenge.Supersede(now);
        }

        var issued = SqlOSPhoneOtpChallenge.Issue(
            new PhoneOtpChallengeRequest(
                normalized,
                purpose,
                context,
                effectiveUserId,
                effectiveUserPhoneNumberId,
                ipAddress,
                httpContext?.Request.Headers.UserAgent.ToString()),
            _cryptoService.ProtectSecret(normalized.E164),
            _options.ChallengeLifetime,
            now);
        var challenge = issued.Challenge;
        _context.Set<SqlOSPhoneOtpChallenge>().Add(challenge);
        await _context.SaveChangesAsync(cancellationToken);

        var shouldSend = (phoneRecord?.User != null && phoneRecord.User.IsActive) || sendWhenNoUser;
        if (shouldSend)
        {
            // The provider sends to the challenge's recipient, the only number the code goes to.
            var delivery = await _deliveryChannel.StartAsync(
                issued.Recipient.E164,
                new SqlOSOtpDeliveryContext(purpose, clientApplicationId, authorizationRequestId, ipAddress, challenge.UserAgent),
                cancellationToken);

            if (!delivery.Accepted)
            {
                challenge.FailDelivery(delivery, DateTime.UtcNow);
                await _context.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException("We couldn't send a sign-in code right now.");
            }

            challenge.RecordProviderStart(delivery);
        }
        else
        {
            challenge.RecordStartWithoutSending();
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new SqlOSPhoneOtpStartResult(
            issued.ChallengeToken,
            normalized.E164,
            maskedPhone,
            purpose == PhoneOtpPurposes.Signup
                ? $"Check {maskedPhone} for a sign-up code."
                : purpose == PhoneOtpPurposes.Enrollment
                    ? $"Check {maskedPhone} for a phone verification code."
                    : PublicStartMessage,
            challenge.ExpiresAt,
            challenge.LastSentAt.Add(_options.ResendCooldown));
    }

    private async Task<string> EnsurePhoneNumberAvailableForSignupAsync(
        string phoneNumber,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePhoneNumber(phoneNumber);
        var phoneHash = _cryptoService.HashToken(normalized.E164);
        var existingPhone = await _context.Set<SqlOSUserPhoneNumber>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PhoneNumberHash == phoneHash && x.RemovedAt == null, cancellationToken);
        if (existingPhone != null)
        {
            throw new InvalidOperationException("An account already exists for this phone number. Sign in with a phone code instead.");
        }

        return normalized.E164;
    }

    private async Task EnsurePhoneOtpEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!settings.PhoneOtpEnabled)
        {
            throw new InvalidOperationException("Phone sign-in is unavailable.");
        }
    }

    private PhoneNumber NormalizePhoneNumber(string phoneNumber)
    {
        var defaultRegion = string.IsNullOrWhiteSpace(_options.DefaultRegion) ? null : _options.DefaultRegion.Trim().ToUpperInvariant();
        if (!PhoneNumber.TryParse(phoneNumber, defaultRegion, out var normalized, out var error))
        {
            throw new InvalidOperationException(error);
        }

        if (!IsCountryAllowed(normalized.Region))
        {
            throw new InvalidOperationException("Phone number country is not allowed.");
        }

        return normalized;
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

    private string UnprotectPhoneNumber(string protectedPhoneNumber)
        => _cryptoService.UnprotectSecret(protectedPhoneNumber);


    private static string NormalizeCode(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(char.IsDigit)
            .ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        return normalized;
    }


    private static string RequireText(string? value, string message)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException(message);
        }

        return trimmed;
    }


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
