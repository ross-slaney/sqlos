using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSEmailOtpService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ISqlOSAuthEmailSender _emailSender;
    private readonly SqlOSEmailOtpOptions _options;

    public SqlOSEmailOtpService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService,
        ISqlOSAuthEmailSender emailSender,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
        _emailSender = emailSender;
        _options = options.Value.EmailOtp;
    }

    public bool IsRuntimeConfigured => _emailSender.IsConfigured;

    public async Task<SqlOSEmailOtpStartResult> StartForAuthorizationRequestAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string email,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEmailOtpEnabledAsync(cancellationToken);

        if (authorizationRequest != null)
        {
            authorizationRequest.LoginHintEmail = email.Trim();
            await _context.SaveChangesAsync(cancellationToken);
        }

        return await CreateChallengeAsync(
            email,
            authorizationRequestId: authorizationRequest?.Id,
            clientApplicationId: authorizationRequest?.ClientApplicationId,
            requestedOrganizationId: null,
            httpContext,
            cancellationToken,
            purpose: "login");
    }

    public async Task<SqlOSEmailOtpSignupStartResult> StartSignupForAuthorizationRequestAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string displayName,
        string email,
        string? organizationName,
        JsonObject? customFields = null,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEmailOtpEnabledAsync(cancellationToken);

        var trimmedDisplayName = displayName?.Trim()
            ?? throw new InvalidOperationException("Display name is required.");
        if (string.IsNullOrWhiteSpace(trimmedDisplayName))
        {
            throw new InvalidOperationException("Display name is required.");
        }

        var trimmedEmail = email?.Trim()
            ?? throw new InvalidOperationException("Email address is required.");
        if (string.IsNullOrWhiteSpace(trimmedEmail))
        {
            throw new InvalidOperationException("Email address is required.");
        }

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(trimmedEmail);
        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (existingEmail != null)
        {
            throw new InvalidOperationException("An account already exists for this email. Sign in with an email code instead.");
        }

        if (string.IsNullOrWhiteSpace(authorizationRequest?.InvitationId))
        {
            SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(authorizationRequest?.OrganizationId);
        }

        if (authorizationRequest != null)
        {
            authorizationRequest.LoginHintEmail = trimmedEmail;
            await _context.SaveChangesAsync(cancellationToken);
        }

        var challenge = await CreateChallengeAsync(
            trimmedEmail,
            authorizationRequestId: authorizationRequest?.Id,
            clientApplicationId: authorizationRequest?.ClientApplicationId,
            requestedOrganizationId: null,
            httpContext,
            cancellationToken,
            sendWhenNoUser: true,
            purpose: "signup");

        var signupToken = await _cryptoService.CreateTemporaryTokenAsync(
            "email_otp_signup",
            userId: null,
            clientApplicationId: authorizationRequest?.ClientApplicationId,
            organizationId: null,
            payload: new EmailOtpSignupPayload(
                _cryptoService.HashToken(challenge.ChallengeToken),
                authorizationRequest?.Id,
                authorizationRequest?.ClientApplication?.ClientId,
                authorizationRequest?.ClientApplicationId,
                trimmedDisplayName,
                trimmedEmail,
                string.IsNullOrWhiteSpace(organizationName) ? null : organizationName.Trim(),
                OrganizationId: null,
                CustomFields: customFields),
            lifetime: _options.ChallengeLifetime,
            cancellationToken);

        return new SqlOSEmailOtpSignupStartResult(
            challenge.ChallengeToken,
            signupToken,
            challenge.Email,
            challenge.MaskedEmail,
            challenge.Message,
            challenge.ExpiresAt,
            challenge.NextAllowedSendAt);
    }

    public async Task<SqlOSEmailOtpSignupStartResult> StartSignupForClientAsync(
        SqlOSEmailOtpSignupStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEmailOtpEnabledAsync(cancellationToken);

        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        var trimmedDisplayName = request.DisplayName?.Trim()
            ?? throw new InvalidOperationException("Display name is required.");
        if (string.IsNullOrWhiteSpace(trimmedDisplayName))
        {
            throw new InvalidOperationException("Display name is required.");
        }

        var trimmedEmail = request.Email?.Trim()
            ?? throw new InvalidOperationException("Email address is required.");
        if (string.IsNullOrWhiteSpace(trimmedEmail))
        {
            throw new InvalidOperationException("Email address is required.");
        }

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(trimmedEmail);
        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (existingEmail != null)
        {
            throw new InvalidOperationException("An account already exists for this email. Sign in with an email code instead.");
        }

        SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(request.OrganizationId);

        var challenge = await CreateChallengeAsync(
            trimmedEmail,
            authorizationRequestId: null,
            clientApplicationId: client.Id,
            requestedOrganizationId: null,
            httpContext,
            cancellationToken,
            sendWhenNoUser: true,
            purpose: "signup");

        var signupToken = await _cryptoService.CreateTemporaryTokenAsync(
            "email_otp_signup",
            userId: null,
            clientApplicationId: client.Id,
            organizationId: null,
            payload: new EmailOtpSignupPayload(
                _cryptoService.HashToken(challenge.ChallengeToken),
                AuthorizationRequestId: null,
                ClientId: client.ClientId,
                ClientApplicationId: client.Id,
                DisplayName: trimmedDisplayName,
                Email: trimmedEmail,
                OrganizationName: string.IsNullOrWhiteSpace(request.OrganizationName) ? null : request.OrganizationName.Trim(),
                OrganizationId: null,
                CustomFields: request.CustomFields),
            lifetime: _options.ChallengeLifetime,
            cancellationToken);

        return new SqlOSEmailOtpSignupStartResult(
            challenge.ChallengeToken,
            signupToken,
            challenge.Email,
            challenge.MaskedEmail,
            challenge.Message,
            challenge.ExpiresAt,
            challenge.NextAllowedSendAt);
    }

    public async Task<SqlOSEmailOtpStartResult> StartForClientAsync(
        SqlOSEmailOtpStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEmailOtpEnabledAsync(cancellationToken);

        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        return await CreateChallengeAsync(
            request.Email,
            authorizationRequestId: null,
            clientApplicationId: client.Id,
            requestedOrganizationId: request.OrganizationId,
            httpContext,
            cancellationToken,
            purpose: "login");
    }

    public async Task<SqlOSEmailOtpVerificationResult> VerifyAsync(
        SqlOSEmailOtpVerifyRequest request,
        CancellationToken cancellationToken = default)
        => await VerifyAsync(
            request,
            expectedAuthorizationRequestId: null,
            requireAuthorizationRequestMatch: false,
            cancellationToken);

    public async Task<SqlOSEmailOtpVerificationResult> VerifyAsync(
        SqlOSEmailOtpVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        await EnsureEmailOtpEnabledAsync(cancellationToken);

        var challenge = await VerifyChallengeAsync(
            request,
            expectedAuthorizationRequestId,
            requireAuthorizationRequestMatch,
            cancellationToken);

        if (challenge.User == null || !challenge.User.IsActive)
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        var organizations = await _adminService.GetUserOrganizationsAsync(challenge.User.Id, cancellationToken);
        return new SqlOSEmailOtpVerificationResult(challenge, challenge.User, organizations, "email_otp");
    }

    public async Task<SqlOSEmailOtpSignupVerificationResult> VerifySignupAsync(
        SqlOSEmailOtpSignupVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        await EnsureEmailOtpEnabledAsync(cancellationToken);

        var signupToken = request.SignupToken?.Trim()
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
        var token = await _cryptoService.FindTemporaryTokenAsync("email_otp_signup", signupToken, cancellationToken)
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
        var payload = _cryptoService.DeserializePayload<EmailOtpSignupPayload>(token)
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");

        if (requireAuthorizationRequestMatch)
        {
            if (string.IsNullOrWhiteSpace(expectedAuthorizationRequestId))
            {
                if (!string.IsNullOrWhiteSpace(payload.AuthorizationRequestId))
                {
                    throw new InvalidOperationException("The sign-in code is invalid or expired.");
                }
            }
            else if (!string.Equals(payload.AuthorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The sign-in code is invalid or expired.");
            }
        }

        var rawChallengeToken = request.ChallengeToken?.Trim()
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");

        if (!string.Equals(payload.ChallengeTokenHash, _cryptoService.HashToken(rawChallengeToken), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        var challenge = await VerifyChallengeAsync(
            new SqlOSEmailOtpVerifyRequest(rawChallengeToken, request.Code),
            expectedAuthorizationRequestId,
            requireAuthorizationRequestMatch,
            cancellationToken);

        if (challenge.User != null)
        {
            throw new InvalidOperationException("An account already exists for this email. Sign in with an email code instead.");
        }

        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == challenge.NormalizedEmail, cancellationToken);
        if (existingEmail != null)
        {
            throw new InvalidOperationException("An account already exists for this email. Sign in with an email code instead.");
        }

        return new SqlOSEmailOtpSignupVerificationResult(
            signupToken,
            token.ClientApplicationId ?? payload.ClientApplicationId,
            payload.ClientId,
            payload.DisplayName,
            payload.Email,
            payload.OrganizationName,
            token.OrganizationId ?? payload.OrganizationId,
            payload.CustomFields);
    }

    public async Task ConsumeSignupTokenAsync(
        string signupToken,
        CancellationToken cancellationToken = default)
    {
        var rawSignupToken = signupToken?.Trim()
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
        _ = await _cryptoService.ConsumeTemporaryTokenAsync("email_otp_signup", rawSignupToken, cancellationToken)
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
    }

    private async Task<SqlOSEmailOtpChallenge> VerifyChallengeAsync(
        SqlOSEmailOtpVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken)
    {
        var rawChallengeToken = request.ChallengeToken?.Trim()
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
        var normalizedCode = NormalizeCode(request.Code);

        var challengeHash = _cryptoService.HashToken(rawChallengeToken);
        var challenge = await _context.Set<SqlOSEmailOtpChallenge>()
            .Include(x => x.User)
            .Include(x => x.UserEmail)
            .Include(x => x.AuthorizationRequest)
            .ThenInclude(x => x!.ClientApplication)
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(x => x.ChallengeTokenHash == challengeHash, cancellationToken)
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");

        if (!IsChallengeActive(challenge))
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        if (requireAuthorizationRequestMatch)
        {
            if (string.IsNullOrWhiteSpace(expectedAuthorizationRequestId))
            {
                if (!string.IsNullOrWhiteSpace(challenge.AuthorizationRequestId))
                {
                    throw new InvalidOperationException("The sign-in code is invalid or expired.");
                }
            }
            else if (!string.Equals(challenge.AuthorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The sign-in code is invalid or expired.");
            }
        }

        if (!string.Equals(challenge.CodeHash, ComputeCodeHash(rawChallengeToken, normalizedCode), StringComparison.Ordinal))
        {
            challenge.AttemptCount++;
            if (challenge.AttemptCount >= challenge.MaxAttempts)
            {
                challenge.InvalidatedAt = DateTime.UtcNow;
                challenge.InvalidatedReason = "max_attempts";
            }

            await _context.SaveChangesAsync(cancellationToken);
            await RecordOtpAuditAsync(
                "email_otp.verify_failed",
                MaskEmail(challenge.Email),
                challenge.User == null ? "signup" : "login",
                challenge.IpAddress,
                new
                {
                    challenge.ClientApplicationId,
                    challenge.AuthorizationRequestId,
                    reason = challenge.InvalidatedReason ?? "wrong_code"
                },
                cancellationToken);
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        challenge.ConsumedAt = DateTime.UtcNow;

        if (challenge.UserEmail != null && !challenge.UserEmail.IsVerified)
        {
            challenge.UserEmail.IsVerified = true;
            challenge.UserEmail.VerifiedAt = DateTime.UtcNow;
        }

        if (challenge.User != null)
        {
            challenge.User.UpdatedAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(challenge.UserEmail?.Email))
            {
                challenge.User.DefaultEmail = challenge.UserEmail.Email;
            }
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        await RecordOtpAuditAsync(
            "email_otp.verify_succeeded",
            MaskEmail(challenge.Email),
            challenge.User == null ? "signup" : "login",
            challenge.IpAddress,
            new
            {
                challenge.UserId,
                challenge.ClientApplicationId,
                challenge.AuthorizationRequestId
            },
            cancellationToken);

        return challenge;
    }

    private async Task<SqlOSEmailOtpStartResult> CreateChallengeAsync(
        string email,
        string? authorizationRequestId,
        string? clientApplicationId,
        string? requestedOrganizationId,
        HttpContext? httpContext,
        CancellationToken cancellationToken,
        bool sendWhenNoUser = false,
        string purpose = "login")
    {
        var trimmedEmail = email?.Trim()
            ?? throw new InvalidOperationException("Email address is required.");
        if (string.IsNullOrWhiteSpace(trimmedEmail))
        {
            throw new InvalidOperationException("Email address is required.");
        }

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(trimmedEmail);
        var now = DateTime.UtcNow;
        var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();

        var recentChallenges = await _context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.NormalizedEmail == normalizedEmail && x.CreatedAt >= now.AddHours(-1))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        if (recentChallenges.Count >= _options.MaxChallengesPerHour)
        {
            await RecordOtpAuditAsync(
                "email_otp.rate_limit_rejected",
                maskedEmail: MaskEmail(trimmedEmail),
                purpose,
                ipAddress,
                new { limit = "email", clientApplicationId, requestedOrganizationId },
                cancellationToken);
            throw new InvalidOperationException("Too many sign-in code requests. Try again later.");
        }

        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            var recentIpChallengeCount = await _context.Set<SqlOSEmailOtpChallenge>()
                .CountAsync(x => x.IpAddress == ipAddress && x.CreatedAt >= now.AddHours(-1), cancellationToken);
            if (recentIpChallengeCount >= _options.MaxChallengesPerIpPerHour)
            {
                await RecordOtpAuditAsync(
                    "email_otp.rate_limit_rejected",
                    maskedEmail: MaskEmail(trimmedEmail),
                    purpose,
                    ipAddress,
                    new { limit = "ip", clientApplicationId, requestedOrganizationId },
                    cancellationToken);
                throw new InvalidOperationException("Too many sign-in code requests. Try again later.");
            }
        }

        if (!string.IsNullOrWhiteSpace(clientApplicationId))
        {
            var recentClientChallengeCount = await _context.Set<SqlOSEmailOtpChallenge>()
                .CountAsync(x => x.ClientApplicationId == clientApplicationId && x.CreatedAt >= now.AddHours(-1), cancellationToken);
            if (recentClientChallengeCount >= _options.MaxChallengesPerClientPerHour)
            {
                await RecordOtpAuditAsync(
                    "email_otp.rate_limit_rejected",
                    maskedEmail: MaskEmail(trimmedEmail),
                    purpose,
                    ipAddress,
                    new { limit = "client", clientApplicationId, requestedOrganizationId },
                    cancellationToken);
                throw new InvalidOperationException("Too many sign-in code requests. Try again later.");
            }
        }

        var latestContextChallenge = recentChallenges
            .FirstOrDefault(x => string.Equals(x.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal)
                && string.Equals(x.ClientApplicationId, clientApplicationId, StringComparison.Ordinal)
                && string.Equals(x.RequestedOrganizationId, requestedOrganizationId, StringComparison.Ordinal)
                && x.InvalidatedAt == null);

        if (latestContextChallenge != null && latestContextChallenge.LastSentAt > now.Subtract(_options.ResendCooldown))
        {
            throw new InvalidOperationException($"Wait {(int)Math.Ceiling(_options.ResendCooldown.TotalSeconds)} seconds before requesting another code.");
        }

        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);

        var activeChallenges = await _context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.NormalizedEmail == normalizedEmail
                && x.ConsumedAt == null
                && x.InvalidatedAt == null
                && x.ExpiresAt > now
                && x.AuthorizationRequestId == authorizationRequestId
                && x.ClientApplicationId == clientApplicationId
                && x.RequestedOrganizationId == requestedOrganizationId)
            .ToListAsync(cancellationToken);

        foreach (var activeChallenge in activeChallenges)
        {
            activeChallenge.InvalidatedAt = now;
            activeChallenge.InvalidatedReason = "superseded";
        }

        var rawChallengeToken = _cryptoService.GenerateOpaqueToken();
        var code = GenerateCode(_options.CodeLength);
        var maskedEmail = MaskEmail(trimmedEmail);
        var challenge = new SqlOSEmailOtpChallenge
        {
            Id = _cryptoService.GenerateId("otp"),
            ChallengeTokenHash = _cryptoService.HashToken(rawChallengeToken),
            CodeHash = ComputeCodeHash(rawChallengeToken, code),
            Email = trimmedEmail,
            NormalizedEmail = normalizedEmail,
            UserId = emailRecord?.UserId,
            UserEmailId = emailRecord?.Id,
            AuthorizationRequestId = authorizationRequestId,
            ClientApplicationId = clientApplicationId,
            RequestedOrganizationId = requestedOrganizationId,
            AttemptCount = 0,
            MaxAttempts = _options.MaxAttempts,
            CreatedAt = now,
            ExpiresAt = now.Add(_options.ChallengeLifetime),
            LastSentAt = now,
            IpAddress = ipAddress,
            UserAgent = httpContext?.Request.Headers.UserAgent.ToString()
        };

        _context.Set<SqlOSEmailOtpChallenge>().Add(challenge);
        await _context.SaveChangesAsync(cancellationToken);

        if ((emailRecord?.User != null && emailRecord.User.IsActive) || sendWhenNoUser)
        {
            try
            {
                await _emailSender.SendAsync(await BuildMessageAsync(trimmedEmail, maskedEmail, code, challenge.ExpiresAt, purpose, cancellationToken), cancellationToken);
            }
            catch
            {
                challenge.InvalidatedAt = DateTime.UtcNow;
                challenge.InvalidatedReason = "delivery_failed";
                await _context.SaveChangesAsync(cancellationToken);
                await RecordOtpAuditAsync(
                    "email_otp.send_failed",
                    maskedEmail,
                    purpose,
                    ipAddress,
                    new { clientApplicationId, requestedOrganizationId },
                    cancellationToken);
                throw new InvalidOperationException("We couldn't send a sign-in code right now.");
            }
        }

        await RecordOtpAuditAsync(
            "email_otp.challenge_started",
            maskedEmail,
            purpose,
            ipAddress,
            new
            {
                clientApplicationId,
                authorizationRequestId,
                requestedOrganizationId,
                sent = (emailRecord?.User != null && emailRecord.User.IsActive) || sendWhenNoUser
            },
            cancellationToken);

        return new SqlOSEmailOtpStartResult(
            rawChallengeToken,
            trimmedEmail,
            maskedEmail,
            purpose == "signup"
                ? $"Check {maskedEmail} for a sign-up code."
                : $"If an account exists for {maskedEmail}, check your email for a sign-in code.",
            challenge.ExpiresAt,
            challenge.LastSentAt.Add(_options.ResendCooldown));
    }

    private async Task EnsureEmailOtpEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!settings.EmailOtpEnabled)
        {
            throw new InvalidOperationException("Email sign-in is unavailable.");
        }
    }

    private static bool IsChallengeActive(SqlOSEmailOtpChallenge challenge)
        => challenge.ConsumedAt == null
            && challenge.InvalidatedAt == null
            && challenge.ExpiresAt > DateTime.UtcNow
            && challenge.AttemptCount < challenge.MaxAttempts;

    private static string NormalizeCode(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(char.IsDigit)
            .ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        return normalized;
    }

    private static string GenerateCode(int length)
    {
        var maxValue = (int)Math.Pow(10, Math.Max(1, length));
        return RandomNumberGenerator.GetInt32(0, maxValue)
            .ToString($"D{length}", CultureInfo.InvariantCulture);
    }

    private static string ComputeCodeHash(string rawChallengeToken, string normalizedCode)
    {
        var payload = Encoding.UTF8.GetBytes($"{rawChallengeToken}:{normalizedCode}");
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static string MaskEmail(string email)
    {
        var atIndex = email.IndexOf('@');
        if (atIndex <= 1 || atIndex == email.Length - 1)
        {
            return email;
        }

        var local = email[..atIndex];
        var domain = email[(atIndex + 1)..];
        var visibleCount = Math.Min(2, local.Length);
        return $"{local[..visibleCount]}***@{domain}";
    }

    private async Task<SqlOSAuthEmailMessage> BuildMessageAsync(
        string email,
        string maskedEmail,
        string code,
        DateTime expiresAt,
        string purpose,
        CancellationToken cancellationToken)
    {
        var branding = await _settingsService.GetResolvedAuthEmailBrandingAsync(cancellationToken);
        var applicationName = string.IsNullOrWhiteSpace(branding.ApplicationName)
            ? string.IsNullOrWhiteSpace(_options.ApplicationName)
                ? "SqlOS"
                : _options.ApplicationName.Trim()
            : branding.ApplicationName;
        var context = new SqlOSEmailOtpMessageContext(
            purpose,
            email,
            maskedEmail,
            code,
            expiresAt,
            _options.ChallengeLifetime,
            applicationName)
        {
            Branding = branding with { ApplicationName = applicationName }
        };

        var defaultSubject = context.Purpose == "signup"
            ? $"Your {context.ApplicationName} sign-up code"
            : $"Your {context.ApplicationName} sign-in code";
        var subject = string.Equals(_options.Subject, "Your SqlOS sign-in code", StringComparison.Ordinal)
            ? defaultSubject
            : _options.Subject;

        return _options.BuildMessage?.Invoke(context)
            ?? new SqlOSAuthEmailMessage(
                email,
                subject,
                SqlOSAuthEmailTemplateRenderer.BuildOtpHtmlBody(context),
                SqlOSAuthEmailTemplateRenderer.BuildOtpTextBody(context));
    }

    private async Task RecordOtpAuditAsync(
        string eventType,
        string maskedEmail,
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
                maskedEmail,
                details = data
            },
            cancellationToken: cancellationToken);

    private sealed record EmailOtpSignupPayload(
        string ChallengeTokenHash,
        string? AuthorizationRequestId,
        string? ClientId,
        string? ClientApplicationId,
        string DisplayName,
        string Email,
        string? OrganizationName,
        string? OrganizationId,
        JsonObject? CustomFields);
}

public sealed record SqlOSEmailOtpVerificationResult(
    SqlOSEmailOtpChallenge Challenge,
    SqlOSUser User,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    string AuthenticationMethod);

public sealed record SqlOSEmailOtpSignupVerificationResult(
    string SignupToken,
    string? ClientApplicationId,
    string? ClientId,
    string DisplayName,
    string Email,
    string? OrganizationName,
    string? OrganizationId,
    JsonObject? CustomFields);
