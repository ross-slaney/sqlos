using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using PhoneNumbers;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSPhoneOtpService
{
    private const string PublicInvalidMessage = "The sign-in code is invalid or expired.";
    private const string PublicStartMessage = "If an account exists for that phone number, check your messages for a sign-in code.";
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ISqlOSOtpDeliveryChannel _deliveryChannel;
    private readonly SqlOSDeliveryAdmissionService _deliveryAdmission;
    private readonly SqlOSPhoneOtpOptions _options;
    private readonly PhoneNumberUtil _phoneNumberUtil = PhoneNumberUtil.GetInstance();

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
        _deliveryAdmission = deliveryAdmissionService ?? new SqlOSDeliveryAdmissionService();
        _options = options.Value.PhoneOtp;
    }

    public bool IsRuntimeConfigured => _options.IsConfigured;

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
            purpose: "login",
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
        return await CreateChallengeAsync(
            request.PhoneNumber,
            authorizationRequestId: null,
            clientApplicationId: client.Id,
            requestedOrganizationId: request.OrganizationId,
            userId: null,
            userPhoneNumberId: null,
            sendWhenNoUser: false,
            purpose: "login",
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
            purpose: "signup",
            httpContext,
            cancellationToken);

        var signupToken = await _cryptoService.CreateTemporaryTokenAsync(
            "phone_otp_signup",
            userId: null,
            clientApplicationId: authorizationRequest?.ClientApplicationId,
            organizationId: null,
            payload: new PhoneOtpSignupPayload(
                _cryptoService.HashToken(challenge.ChallengeToken),
                authorizationRequest?.Id,
                authorizationRequest?.ClientApplication?.ClientId,
                authorizationRequest?.ClientApplicationId,
                trimmedDisplayName,
                challenge.PhoneNumber,
                string.IsNullOrWhiteSpace(organizationName) ? null : organizationName.Trim(),
                authorizationRequest?.OrganizationId,
                customFields),
            lifetime: _options.ChallengeLifetime,
            cancellationToken);

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
            purpose: "signup",
            httpContext,
            cancellationToken);

        var signupToken = await _cryptoService.CreateTemporaryTokenAsync(
            "phone_otp_signup",
            userId: null,
            clientApplicationId: client.Id,
            organizationId: request.OrganizationId,
            payload: new PhoneOtpSignupPayload(
                _cryptoService.HashToken(challenge.ChallengeToken),
                AuthorizationRequestId: null,
                ClientId: client.ClientId,
                ClientApplicationId: client.Id,
                DisplayName: trimmedDisplayName,
                PhoneNumber: challenge.PhoneNumber,
                OrganizationName: string.IsNullOrWhiteSpace(request.OrganizationName) ? null : request.OrganizationName.Trim(),
                OrganizationId: request.OrganizationId,
                CustomFields: request.CustomFields),
            lifetime: _options.ChallengeLifetime,
            cancellationToken);

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
            purpose: "enrollment",
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
            expectedPurpose: "enrollment",
            cancellationToken);

        if (!string.Equals(challenge.UserId, authenticatedUser.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        var phoneNumber = UnprotectPhoneNumber(challenge.PhoneNumberEncrypted);
        var record = await AddVerifiedPhoneNumberAsync(authenticatedUser, phoneNumber, cancellationToken);
        await RecordPhoneOtpAuditAsync(
            "phone_otp.phone_added",
            challenge.MaskedPhoneNumber,
            "enrollment",
            challenge.IpAddress,
            new { userId = authenticatedUser.Id, phoneNumberId = record.Id },
            cancellationToken);
        return record;
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
            expectedPurpose: "login",
            cancellationToken);

        if (challenge.User == null || !challenge.User.IsActive)
        {
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        if (challenge.UserPhoneNumber != null)
        {
            challenge.UserPhoneNumber.LastUsedAt = DateTime.UtcNow;
            challenge.UserPhoneNumber.UpdatedAt = DateTime.UtcNow;
        }

        challenge.User.UpdatedAt = DateTime.UtcNow;
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
        var token = await _cryptoService.FindTemporaryTokenAsync("phone_otp_signup", signupToken, cancellationToken)
            ?? throw new InvalidOperationException(PublicInvalidMessage);
        var payload = _cryptoService.DeserializePayload<PhoneOtpSignupPayload>(token)
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
            expectedPurpose: "signup",
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
        _ = await _cryptoService.ConsumeTemporaryTokenAsync("phone_otp_signup", rawSignupToken, cancellationToken)
            ?? throw new InvalidOperationException(PublicInvalidMessage);
    }

    public async Task<SqlOSUserPhoneNumber> AddVerifiedPhoneNumberAsync(
        SqlOSUser user,
        string e164PhoneNumber,
        CancellationToken cancellationToken = default)
    {
        var phoneHash = _cryptoService.HashToken(e164PhoneNumber);
        var existing = await _context.Set<SqlOSUserPhoneNumber>()
            .FirstOrDefaultAsync(x => x.PhoneNumberHash == phoneHash && x.RemovedAt == null, cancellationToken);
        if (existing != null && !string.Equals(existing.UserId, user.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An account already exists for this phone number.");
        }

        var now = DateTime.UtcNow;
        if (existing != null)
        {
            existing.IsVerified = true;
            existing.VerifiedAt ??= now;
            existing.PhoneNumber = e164PhoneNumber;
            existing.DisplayValueEncrypted = _cryptoService.ProtectSecret(e164PhoneNumber);
            existing.UpdatedAt = now;
            await _context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var hasActivePhone = await _context.Set<SqlOSUserPhoneNumber>()
            .AnyAsync(x => x.UserId == user.Id && x.RemovedAt == null, cancellationToken);
        var record = new SqlOSUserPhoneNumber
        {
            Id = _cryptoService.GenerateId("phn"),
            UserId = user.Id,
            PhoneNumber = e164PhoneNumber,
            PhoneNumberHash = phoneHash,
            DisplayValueEncrypted = _cryptoService.ProtectSecret(e164PhoneNumber),
            IsPrimary = !hasActivePhone,
            IsVerified = true,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        _context.Set<SqlOSUserPhoneNumber>().Add(record);
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

        if (!IsChallengeActive(challenge)
            || !string.Equals(challenge.Purpose, expectedPurpose, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        if (requireAuthorizationRequestMatch)
        {
            if (string.IsNullOrWhiteSpace(expectedAuthorizationRequestId))
            {
                if (!string.IsNullOrWhiteSpace(challenge.AuthorizationRequestId))
                {
                    throw new InvalidOperationException(PublicInvalidMessage);
                }
            }
            else if (!string.Equals(challenge.AuthorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(PublicInvalidMessage);
            }
        }

        if (!challenge.ProviderStarted)
        {
            await RejectChallengeAsync(challenge, "not_started", cancellationToken);
            throw new InvalidOperationException(PublicInvalidMessage);
        }

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

        challenge.AttemptCount++;
        challenge.ProviderStatus = check.ProviderStatus;
        if (!string.IsNullOrWhiteSpace(check.ProviderChallengeId))
        {
            challenge.ProviderChallengeId = check.ProviderChallengeId;
        }

        if (!check.Approved)
        {
            await RejectChallengeAsync(challenge, check.SanitizedError ?? check.ProviderStatus ?? "provider_rejected", cancellationToken);
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        challenge.ConsumedAt = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException(PublicInvalidMessage);
        }

        await RecordPhoneOtpAuditAsync(
            "phone_otp.verify_succeeded",
            challenge.MaskedPhoneNumber,
            challenge.Purpose,
            challenge.IpAddress,
            new
            {
                challenge.UserId,
                challenge.ClientApplicationId,
                challenge.AuthorizationRequestId,
                providerStatus = check.ProviderStatus
            },
            cancellationToken);

        return challenge;
    }

    private async Task RejectChallengeAsync(
        SqlOSPhoneOtpChallenge challenge,
        string reason,
        CancellationToken cancellationToken)
    {
        challenge.InvalidatedAt = DateTime.UtcNow;
        challenge.InvalidatedReason = reason.Length > 120 ? reason[..120] : reason;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordPhoneOtpAuditAsync(
            "phone_otp.verify_failed",
            challenge.MaskedPhoneNumber,
            challenge.Purpose,
            challenge.IpAddress,
            new
            {
                challenge.ClientApplicationId,
                challenge.AuthorizationRequestId,
                reason = challenge.InvalidatedReason
            },
            cancellationToken);
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
        var maskedPhone = MaskPhoneNumber(normalized.E164);

        var phoneRecord = await _context.Set<SqlOSUserPhoneNumber>()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.PhoneNumberHash == phoneHash && x.RemovedAt == null && x.IsVerified, cancellationToken);

        var effectiveUserId = userId ?? phoneRecord?.UserId;
        var effectiveUserPhoneNumberId = userPhoneNumberId ?? phoneRecord?.Id;
        await EnsureSendAllowedAsync(
            phoneHash,
            effectiveUserId,
            clientApplicationId,
            requestedOrganizationId,
            purpose,
            maskedPhone,
            ipAddress,
            now,
            cancellationToken);
        now = DateTime.UtcNow;

        var recentChallenges = await _context.Set<SqlOSPhoneOtpChallenge>()
            .Where(x => x.PhoneNumberHash == phoneHash && x.CreatedAt >= now.Subtract(_options.RateLimitWindow))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var latestContextChallenge = recentChallenges
            .FirstOrDefault(x => string.Equals(x.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal)
                && string.Equals(x.ClientApplicationId, clientApplicationId, StringComparison.Ordinal)
                && string.Equals(x.RequestedOrganizationId, requestedOrganizationId, StringComparison.Ordinal)
                && string.Equals(x.Purpose, purpose, StringComparison.Ordinal)
                && x.InvalidatedAt == null);

        if (latestContextChallenge != null && latestContextChallenge.LastSentAt > now.Subtract(_options.ResendCooldown))
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
            activeChallenge.InvalidatedAt = now;
            activeChallenge.InvalidatedReason = "superseded";
        }

        var rawChallengeToken = _cryptoService.GenerateOpaqueToken();
        var challenge = new SqlOSPhoneOtpChallenge
        {
            Id = _cryptoService.GenerateId("potp"),
            ChallengeTokenHash = _cryptoService.HashToken(rawChallengeToken),
            PhoneNumberHash = phoneHash,
            PhoneNumberEncrypted = _cryptoService.ProtectSecret(normalized.E164),
            MaskedPhoneNumber = maskedPhone,
            Purpose = purpose,
            UserId = effectiveUserId,
            UserPhoneNumberId = effectiveUserPhoneNumberId,
            AuthorizationRequestId = authorizationRequestId,
            ClientApplicationId = clientApplicationId,
            RequestedOrganizationId = requestedOrganizationId,
            ProviderStarted = false,
            Provider = "twilio_verify",
            AttemptCount = 0,
            CreatedAt = now,
            ExpiresAt = now.Add(_options.ChallengeLifetime),
            LastSentAt = now,
            IpAddress = ipAddress,
            UserAgent = httpContext?.Request.Headers.UserAgent.ToString()
        };

        _context.Set<SqlOSPhoneOtpChallenge>().Add(challenge);
        await _context.SaveChangesAsync(cancellationToken);

        var shouldSend = (phoneRecord?.User != null && phoneRecord.User.IsActive) || sendWhenNoUser;
        if (shouldSend)
        {
            var delivery = await _deliveryChannel.StartAsync(
                normalized.E164,
                new SqlOSOtpDeliveryContext(purpose, clientApplicationId, authorizationRequestId, ipAddress, challenge.UserAgent),
                cancellationToken);

            if (!delivery.Accepted)
            {
                challenge.InvalidatedAt = DateTime.UtcNow;
                challenge.InvalidatedReason = "delivery_failed";
                challenge.ProviderStatus = delivery.ProviderStatus;
                await _context.SaveChangesAsync(cancellationToken);
                await RecordPhoneOtpAuditAsync(
                    "phone_otp.send_failed",
                    maskedPhone,
                    purpose,
                    ipAddress,
                    new { clientApplicationId, requestedOrganizationId, providerStatus = delivery.ProviderStatus },
                    cancellationToken);
                throw new InvalidOperationException("We couldn't send a sign-in code right now.");
            }

            challenge.ProviderStarted = true;
            challenge.Provider = delivery.Provider;
            challenge.ProviderChallengeId = delivery.ProviderChallengeId;
            challenge.ProviderStatus = delivery.ProviderStatus;
            await _context.SaveChangesAsync(cancellationToken);
        }

        await RecordPhoneOtpAuditAsync(
            "phone_otp.challenge_started",
            maskedPhone,
            purpose,
            ipAddress,
            new
            {
                clientApplicationId,
                authorizationRequestId,
                requestedOrganizationId,
                sent = shouldSend
            },
            cancellationToken);

        return new SqlOSPhoneOtpStartResult(
            rawChallengeToken,
            normalized.E164,
            maskedPhone,
            purpose == "signup"
                ? $"Check {maskedPhone} for a sign-up code."
                : purpose == "enrollment"
                    ? $"Check {maskedPhone} for a phone verification code."
                    : PublicStartMessage,
            challenge.ExpiresAt,
            challenge.LastSentAt.Add(_options.ResendCooldown));
    }

    private async Task EnsureSendAllowedAsync(
        string phoneHash,
        string? userId,
        string? clientApplicationId,
        string? requestedOrganizationId,
        string purpose,
        string maskedPhone,
        string? ipAddress,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var admission = await _deliveryAdmission.ReservePhoneOtpAsync(
            phoneHash,
            userId,
            ipAddress,
            clientApplicationId,
            _options,
            now,
            cancellationToken);
        if (admission.Admitted)
        {
            return;
        }

        await RecordRateLimitAuditAsync(
            admission.RejectedScope ?? "phone",
            maskedPhone,
            purpose,
            ipAddress,
            clientApplicationId,
            requestedOrganizationId,
            cancellationToken);
        throw new InvalidOperationException("Too many sign-in code requests. Try again later.");
    }

    private async Task RecordRateLimitAuditAsync(
        string limit,
        string maskedPhone,
        string purpose,
        string? ipAddress,
        string? clientApplicationId,
        string? requestedOrganizationId,
        CancellationToken cancellationToken)
        => await RecordPhoneOtpAuditAsync(
            "phone_otp.rate_limit_rejected",
            maskedPhone,
            purpose,
            ipAddress,
            new { limit, clientApplicationId, requestedOrganizationId },
            cancellationToken);

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

    private NormalizedPhoneNumber NormalizePhoneNumber(string phoneNumber)
    {
        var trimmed = RequireText(phoneNumber, "Phone number is required.");
        try
        {
            var parsed = _phoneNumberUtil.Parse(trimmed, string.IsNullOrWhiteSpace(_options.DefaultRegion) ? null : _options.DefaultRegion.Trim().ToUpperInvariant());
            if (!_phoneNumberUtil.IsValidNumber(parsed))
            {
                throw new InvalidOperationException("Phone number is invalid.");
            }

            var region = _phoneNumberUtil.GetRegionCodeForNumber(parsed)?.ToUpperInvariant();
            if (!IsCountryAllowed(region))
            {
                throw new InvalidOperationException("Phone number country is not allowed.");
            }

            return new NormalizedPhoneNumber(
                _phoneNumberUtil.Format(parsed, PhoneNumberFormat.E164),
                region);
        }
        catch (NumberParseException ex)
        {
            throw new InvalidOperationException("Phone number is invalid.", ex);
        }
    }

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

    private static bool IsChallengeActive(SqlOSPhoneOtpChallenge challenge)
        => challenge.ConsumedAt == null
            && challenge.InvalidatedAt == null
            && challenge.ExpiresAt > DateTime.UtcNow;

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

    private static string MaskPhoneNumber(string e164PhoneNumber)
    {
        if (e164PhoneNumber.Length <= 5)
        {
            return e164PhoneNumber;
        }

        var prefix = e164PhoneNumber[..Math.Min(2, e164PhoneNumber.Length)];
        var suffix = e164PhoneNumber[^Math.Min(4, e164PhoneNumber.Length)..];
        return $"{prefix}{new string('*', Math.Max(3, e164PhoneNumber.Length - prefix.Length - suffix.Length))}{suffix}";
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

    private async Task RecordPhoneOtpAuditAsync(
        string eventType,
        string maskedPhone,
        string purpose,
        string? ipAddress,
        object? data,
        CancellationToken cancellationToken)
        => await _adminService.RecordAuditAsync(
            eventType,
            "system",
            null,
            ipAddress: ipAddress,
            data: new
            {
                purpose,
                maskedPhone,
                details = data
            },
            cancellationToken: cancellationToken);

    private sealed record NormalizedPhoneNumber(string E164, string? Region);

    private sealed record PhoneOtpSignupPayload(
        string ChallengeTokenHash,
        string? AuthorizationRequestId,
        string? ClientId,
        string? ClientApplicationId,
        string DisplayName,
        string PhoneNumber,
        string? OrganizationName,
        string? OrganizationId,
        JsonObject? CustomFields);
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
