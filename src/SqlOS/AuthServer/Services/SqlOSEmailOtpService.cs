using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSEmailOtpService
{
    private const string InvalidCodeMessage = "The sign-in code is invalid or expired.";

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ISqlOSAuthEmailSender _emailSender;
    private readonly ISqlOSTransactionalEmailService? _transactionalEmailService;
    private readonly IAuditRecorder _auditRecorder;
    private readonly SqlOSEmailOtpAttemptLedger _attempts;
    private readonly IAdmissionGate _admission;
    private readonly SqlOSEmailOtpOptions _options;

    public SqlOSEmailOtpService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService,
        ISqlOSAuthEmailSender emailSender,
        IOptions<SqlOSAuthServerOptions> options,
        ISqlOSTransactionalEmailService? transactionalEmailService = null)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
        _emailSender = emailSender;
        _transactionalEmailService = transactionalEmailService;
        _auditRecorder = new SqlOSAuditRecorder(context);
        _attempts = new SqlOSEmailOtpAttemptLedger(context);
        _admission = SqlOSAdmissionGate.Create(context, adminService, cryptoService, options);
        _options = options.Value.EmailOtp;
    }

    public bool IsRuntimeConfigured => _options.BuildMessage == null || _emailSender.IsConfigured;

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
            new EmailOtpChallengeContext(authorizationRequest?.Id, authorizationRequest?.ClientApplicationId, RequestedOrganizationId: null),
            EmailOtpPurposes.Login,
            sendWhenNoUser: false,
            httpContext,
            cancellationToken);
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

        return await StartSignupForAuthorizationRequestAsync(
            new EmailOtpSignupStart(
                RequireDisplayName(displayName),
                RequireValidEmail(email),
                authorizationRequest,
                authorizationRequest?.ClientApplicationId,
                authorizationRequest?.ClientApplication?.ClientId,
                ExistingEmailAuditOrganizationId: authorizationRequest?.OrganizationId,
                // An invitation authorizes the join it names; without one, the request may not join.
                JoinsOrganizationWithoutInvitation: string.IsNullOrWhiteSpace(authorizationRequest?.InvitationId),
                JoinOrganizationId: authorizationRequest?.OrganizationId,
                organizationName,
                customFields),
            httpContext,
            cancellationToken);
    }

    public async Task<SqlOSEmailOtpSignupStartResult> StartSignupForClientAsync(
        SqlOSEmailOtpSignupStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEmailOtpEnabledAsync(cancellationToken);

        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, userId: null, cancellationToken);
        return await StartSignupForAuthorizationRequestAsync(
            new EmailOtpSignupStart(
                RequireDisplayName(request.DisplayName),
                RequireValidEmail(request.Email),
                AuthorizationRequest: null,
                client.Id,
                client.ClientId,
                ExistingEmailAuditOrganizationId: request.OrganizationId,
                JoinsOrganizationWithoutInvitation: true,
                JoinOrganizationId: request.OrganizationId,
                request.OrganizationName,
                request.CustomFields),
            httpContext,
            cancellationToken);
    }

    public async Task<SqlOSEmailOtpStartResult> StartForClientAsync(
        SqlOSEmailOtpStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEmailOtpEnabledAsync(cancellationToken);

        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, userId: null, cancellationToken);
        return await CreateChallengeAsync(
            request.Email,
            new EmailOtpChallengeContext(AuthorizationRequestId: null, client.Id, request.OrganizationId),
            EmailOtpPurposes.Login,
            sendWhenNoUser: false,
            httpContext,
            cancellationToken);
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
            EmailOtpVerificationMode.SignIn,
            cancellationToken);

        if (challenge.User == null || !challenge.User.IsActive)
        {
            throw new InvalidOperationException(InvalidCodeMessage);
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
            ?? throw new InvalidOperationException(InvalidCodeMessage);
        var token = await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.EmailOtpSignup, signupToken, cancellationToken)
            ?? throw new InvalidOperationException(InvalidCodeMessage);
        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.EmailOtpSignup)
            ?? throw new InvalidOperationException(InvalidCodeMessage);

        if (requireAuthorizationRequestMatch
            && !AnswersAuthorizationRequest(payload.AuthorizationRequestId, expectedAuthorizationRequestId))
        {
            throw new InvalidOperationException(InvalidCodeMessage);
        }

        var rawChallengeToken = request.ChallengeToken?.Trim()
            ?? throw new InvalidOperationException(InvalidCodeMessage);

        if (!string.Equals(payload.ChallengeTokenHash, _cryptoService.HashToken(rawChallengeToken), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(InvalidCodeMessage);
        }

        await VerifyChallengeAsync(
            new SqlOSEmailOtpVerifyRequest(rawChallengeToken, request.Code),
            expectedAuthorizationRequestId,
            requireAuthorizationRequestMatch,
            EmailOtpVerificationMode.Signup,
            cancellationToken);

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
            ?? throw new InvalidOperationException(InvalidCodeMessage);
        _ = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.EmailOtpSignup, rawSignupToken, cancellationToken)
            ?? throw new InvalidOperationException(InvalidCodeMessage);
    }

    /// <summary>
    /// Verifies a code against its challenge. The attempt is spent in the database before the code
    /// is compared (#424); a wrong code is recorded, and invalidates the challenge when it spent
    /// the last attempt. A right code completes the challenge, which proves its recipient's
    /// mailbox: a sign-in claims an unverified address with that proof, and a sign-up refuses an
    /// address an account already owns.
    /// </summary>
    private async Task<SqlOSEmailOtpChallenge> VerifyChallengeAsync(
        SqlOSEmailOtpVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        EmailOtpVerificationMode mode,
        CancellationToken cancellationToken)
    {
        var rawChallengeToken = request.ChallengeToken?.Trim()
            ?? throw new InvalidOperationException(InvalidCodeMessage);
        var normalizedCode = NormalizeCode(request.Code);

        var challengeHash = _cryptoService.HashToken(rawChallengeToken);
        var challenge = await _context.Set<SqlOSEmailOtpChallenge>()
            .Include(x => x.User)
            .Include(x => x.UserEmail)
            .Include(x => x.AuthorizationRequest)
            .ThenInclude(x => x!.ClientApplication)
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(x => x.ChallengeTokenHash == challengeHash, cancellationToken)
            ?? throw new InvalidOperationException(InvalidCodeMessage);

        var now = DateTime.UtcNow;
        if (!challenge.IsOpen(now)
            // The code was delivered to an address that is no longer this account's address.
            || !challenge.IsStillAddressedToItsAccount()
            || (requireAuthorizationRequestMatch && !challenge.AnswersAuthorizationRequest(expectedAuthorizationRequestId)))
        {
            throw new InvalidOperationException(InvalidCodeMessage);
        }

        var reservation = await _attempts.TryReserveAsync(
                challenge,
                now,
                mode == EmailOtpVerificationMode.Signup ? EmailOtpAttemptScope.SignupTransaction : EmailOtpAttemptScope.Independent,
                cancellationToken)
            ?? throw new InvalidOperationException(InvalidCodeMessage);

        if (challenge.RegisterAttempt(reservation, rawChallengeToken, normalizedCode) == EmailOtpAttemptOutcome.Rejected)
        {
            var exhausted = await _attempts.TryExhaustAsync(challenge, reservation, now, cancellationToken);
            challenge.RejectCode(attemptsExhausted: exhausted);
            await _context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(InvalidCodeMessage);
        }

        var completion = SqlOSTrackedChangeSnapshot.Capture(_context);
        var ownership = challenge.Complete(now);
        if (challenge.UserEmail != null && challenge.User is { IsActive: true })
        {
            // The code proved the mailbox. An unverified address is claimed: whatever was
            // attached before the owner proved it is evicted in this same save.
            await ClaimEmailOwnership.StageAsync(
                _context,
                challenge.User,
                ownership,
                PresentedCredentials.None,
                now,
                cancellationToken);
        }

        if (challenge.User != null && challenge.UserEmail != null)
        {
            // The address the code went to becomes the account's default email.
            await _context.LoadUserPartsAsync(challenge.User, SqlOSUserParts.Emails, cancellationToken);
            challenge.User.MakeDefaultEmail(ownership, DateTime.UtcNow);
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent verification spent the challenge first. Nothing this one staged may
            // reach a later save of the same unit of work.
            completion?.Revert(_context);
            ((DbContext)_context).Entry(challenge).State = EntityState.Detached;
            throw new InvalidOperationException(InvalidCodeMessage);
        }

        if (mode == EmailOtpVerificationMode.Signup
            && await FindExistingAccountRefusalAsync(challenge, cancellationToken) is { } refusal)
        {
            _auditRecorder.Record(refusal);
            await _context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(InvalidCodeMessage);
        }

        return challenge;
    }

    /// <summary>
    /// A sign-up code proves a mailbox, but never creates a second account for it: the challenge
    /// must not be bound to an account, and no account may have claimed the address meanwhile.
    /// </summary>
    /// <returns>The refusal to record, or <see langword="null"/> when the sign-up may go on.</returns>
    private async Task<EmailOtpSignupRejectedForExistingEmail?> FindExistingAccountRefusalAsync(
        SqlOSEmailOtpChallenge challenge,
        CancellationToken cancellationToken)
    {
        var reason = challenge.User != null
            ? "challenge_bound_to_existing_user"
            : await _context.Set<SqlOSUserEmail>()
                .AsNoTracking()
                .FindByNormalizedEmailAsync(challenge.NormalizedEmail, challenge.Email, cancellationToken) != null
                ? "email_claimed_after_challenge_started"
                : null;
        return reason == null
            ? null
            : new EmailOtpSignupRejectedForExistingEmail(
                Masked.Email(challenge.Email),
                challenge.IpAddress,
                challenge.ClientApplicationId,
                challenge.AuthorizationRequestId,
                reason);
    }

    /// <summary>
    /// Starts an email-code sign-up for the authorization request <paramref name="start"/> names,
    /// or, without one, for a first-party client's own sign-up form (as the public overload, which
    /// takes a missing request too): refuses an unauthorized organization join, sends a sign-up
    /// code to the typed address, and issues the sign-up token that carries the account to create.
    /// </summary>
    private async Task<SqlOSEmailOtpSignupStartResult> StartSignupForAuthorizationRequestAsync(
        EmailOtpSignupStart start,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FindByEmailAsync(start.Email, cancellationToken);
        if (existingEmail != null)
        {
            _auditRecorder.Record(new EmailOtpSignupStartedForExistingEmail(
                Masked.Email(start.Email),
                httpContext?.Connection.RemoteIpAddress?.ToString(),
                start.ClientApplicationId,
                start.AuthorizationRequest?.Id,
                start.ExistingEmailAuditOrganizationId));
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (start.JoinsOrganizationWithoutInvitation)
        {
            SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(start.JoinOrganizationId);
        }

        if (start.AuthorizationRequest != null)
        {
            start.AuthorizationRequest.LoginHintEmail = start.Email;
            await _context.SaveChangesAsync(cancellationToken);
        }

        var challenge = await CreateChallengeAsync(
            start.Email,
            new EmailOtpChallengeContext(start.AuthorizationRequest?.Id, start.ClientApplicationId, RequestedOrganizationId: null),
            EmailOtpPurposes.Signup,
            sendWhenNoUser: true,
            httpContext,
            cancellationToken);

        var signupToken = (await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.EmailOtpSignup,
            new EmailOtpSignupPayload(
                _cryptoService.HashToken(challenge.ChallengeToken),
                start.AuthorizationRequest?.Id,
                start.ClientId,
                start.ClientApplicationId,
                start.DisplayName,
                start.Email,
                string.IsNullOrWhiteSpace(start.OrganizationName) ? null : start.OrganizationName.Trim(),
                OrganizationId: null,
                start.CustomFields),
            new TemporaryTokenBinding(ClientApplicationId: start.ClientApplicationId),
            _options.ChallengeLifetime,
            cancellationToken)).RawToken;

        return new SqlOSEmailOtpSignupStartResult(
            challenge.ChallengeToken,
            signupToken,
            challenge.Email,
            challenge.MaskedEmail,
            challenge.Message,
            challenge.ExpiresAt,
            challenge.NextAllowedSendAt);
    }

    private async Task<SqlOSEmailOtpStartResult> CreateChallengeAsync(
        string email,
        EmailOtpChallengeContext context,
        string purpose,
        bool sendWhenNoUser,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var requestedAddress = EmailAddress.Parse(RequireValidEmail(email));
        var normalizedEmail = requestedAddress.Canonical;
        var request = new EmailOtpChallengeRequest(
            requestedAddress,
            purpose,
            context,
            httpContext?.Connection.RemoteIpAddress?.ToString(),
            httpContext?.Request.Headers.UserAgent.ToString());
        var now = DateTime.UtcNow;

        // The send is admitted atomically before anything is written, so requests sent together
        // can never exceed a limit between them (#424).
        var admission = await _admission.AdmitEmailCodeAsync(
            requestedAddress,
            new AdmissionOrigin(request.IpAddress, request.UserAgent),
            context.ClientApplicationId,
            now,
            cancellationToken);
        if (!admission.Admitted)
        {
            _auditRecorder.Record(new EmailOtpSendRateLimited(
                purpose,
                Masked.Email(requestedAddress.Address),
                request.IpAddress,
                admission.RefusedLimit!,
                context.ClientApplicationId,
                context.RequestedOrganizationId));
            await _context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Too many sign-in code requests. Try again later.");
        }

        var recentChallenges = (await _context.Set<SqlOSEmailOtpChallenge>()
                .Where(x => x.NormalizedEmail == normalizedEmail && x.CreatedAt >= now.AddHours(-1))
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(cancellationToken))
            .Where(x => string.Equals(x.NormalizedEmail, normalizedEmail, StringComparison.Ordinal))
            .ToList();
        var latestContextChallenge = recentChallenges
            .FirstOrDefault(x => x.WasStartedIn(context) && !x.IsInvalidated);
        if (latestContextChallenge != null && latestContextChallenge.WasSentWithin(_options.ResendCooldown, now))
        {
            // A resend the cooldown refuses sends nothing, so it does not count against a limit.
            await _admission.WithdrawAsync(admission, now, cancellationToken);
            throw new InvalidOperationException($"Wait {(int)Math.Ceiling(_options.ResendCooldown.TotalSeconds)} seconds before requesting another code.");
        }

        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FindByNormalizedEmailAsync(normalizedEmail, email, cancellationToken);

        var activeChallenges = await _context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.NormalizedEmail == normalizedEmail
                && x.ConsumedAt == null
                && x.InvalidatedAt == null
                && x.ExpiresAt > now
                && x.AuthorizationRequestId == context.AuthorizationRequestId
                && x.ClientApplicationId == context.ClientApplicationId
                && x.RequestedOrganizationId == context.RequestedOrganizationId)
            .ToListAsync(cancellationToken);

        foreach (var activeChallenge in activeChallenges.Where(x => string.Equals(x.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)))
        {
            activeChallenge.Supersede(now);
        }

        // A code for an existing account is only ever delivered to the address stored on that
        // account, never to the typed spelling. Without an account, the typed address is the
        // address being signed up.
        var issued = SqlOSEmailOtpChallenge.Issue(
            request,
            emailRecord,
            new EmailOtpChallengeSettings(_options.CodeLength, _options.MaxAttempts, _options.ChallengeLifetime),
            now);
        var challenge = issued.Challenge;
        _context.Set<SqlOSEmailOtpChallenge>().Add(challenge);
        await _context.SaveChangesAsync(cancellationToken);

        var codeSent = (emailRecord?.User != null && emailRecord.User.IsActive) || sendWhenNoUser;
        if (codeSent)
        {
            try
            {
                await SendCodeAsync(issued, purpose, cancellationToken);
            }
            catch
            {
                challenge.FailDelivery(request, DateTime.UtcNow);
                await _context.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException("We couldn't send a sign-in code right now.");
            }
        }

        challenge.RecordStart(request, codeSent);
        await _context.SaveChangesAsync(cancellationToken);

        var maskedEmail = Masked.Email(requestedAddress.Address);
        return new SqlOSEmailOtpStartResult(
            issued.ChallengeToken,
            requestedAddress.Address,
            maskedEmail,
            purpose == EmailOtpPurposes.Signup
                ? $"Check {maskedEmail} for a sign-up code."
                : $"If an account exists for {maskedEmail}, check your email for a sign-in code.",
            challenge.ExpiresAt,
            challenge.LastSentAt.Add(_options.ResendCooldown));
    }

    private static bool AnswersAuthorizationRequest(string? authorizationRequestId, string? expectedAuthorizationRequestId)
        => string.IsNullOrWhiteSpace(expectedAuthorizationRequestId)
            ? string.IsNullOrWhiteSpace(authorizationRequestId)
            : string.Equals(authorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal);

    private static string RequireDisplayName(string? displayName)
    {
        var trimmedDisplayName = displayName?.Trim()
            ?? throw new InvalidOperationException("Display name is required.");
        if (string.IsNullOrWhiteSpace(trimmedDisplayName))
        {
            throw new InvalidOperationException("Display name is required.");
        }

        return trimmedDisplayName;
    }

    private static string RequireValidEmail(string? email)
    {
        var trimmedEmail = email?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedEmail))
        {
            throw new InvalidOperationException("Email address is required.");
        }

        if (!SqlOSEmailAddress.TryCanonicalize(trimmedEmail, out var address, out _))
        {
            throw new InvalidOperationException(SqlOSEmailAddress.InvalidEmailMessage);
        }

        return address;
    }

    private async Task EnsureEmailOtpEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!settings.EmailOtpEnabled)
        {
            throw new InvalidOperationException("Email sign-in is unavailable.");
        }
    }

    private static string NormalizeCode(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(char.IsDigit)
            .ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(InvalidCodeMessage);
        }

        return normalized;
    }

    /// <summary>Sends the code to the challenge's stored recipient, the only address it ever goes to.</summary>
    private async Task SendCodeAsync(
        IssuedEmailOtpChallenge issued,
        string purpose,
        CancellationToken cancellationToken)
    {
        var challenge = issued.Challenge;
        var context = await BuildMessageContextAsync(
            challenge.Email,
            Masked.Email(challenge.Email),
            issued.Code,
            challenge.ExpiresAt,
            purpose,
            cancellationToken);
        if (_options.BuildMessage != null)
        {
            await _emailSender.SendAsync(BuildLegacyMessage(context), cancellationToken);
            return;
        }

        var transactionalEmailService = _transactionalEmailService
            ?? throw new InvalidOperationException("Transactional email service is not registered.");
        var result = await transactionalEmailService.SendAsync(
            new SqlOSSendEmailRequest(
                SqlOSBuiltInEmailTemplates.AuthEmailOtpKey,
                challenge.Email,
                BuildTemplateVariables(context),
                IdempotencyKey: $"auth-email-otp:{challenge.Id}"),
            cancellationToken);

        if (string.Equals(result.Status, SqlOSEmailDeliveryStatuses.Failed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.SanitizedError ?? "Email OTP delivery failed.");
        }
    }

    private async Task<SqlOSEmailOtpMessageContext> BuildMessageContextAsync(
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

        return context;
    }

    private SqlOSAuthEmailMessage BuildLegacyMessage(SqlOSEmailOtpMessageContext context)
    {
        var defaultSubject = context.Purpose == "signup"
            ? $"Your {context.ApplicationName} sign-up code"
            : $"Your {context.ApplicationName} sign-in code";
        var subject = string.Equals(_options.Subject, "Your SqlOS sign-in code", StringComparison.Ordinal)
            ? defaultSubject
            : _options.Subject;

        return _options.BuildMessage?.Invoke(context)
            ?? new SqlOSAuthEmailMessage(
                context.Email,
                subject,
                SqlOSAuthEmailTemplateRenderer.BuildOtpHtmlBody(context),
                SqlOSAuthEmailTemplateRenderer.BuildOtpTextBody(context));
    }

    private static IReadOnlyDictionary<string, object?> BuildTemplateVariables(SqlOSEmailOtpMessageContext context)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(context.ChallengeLifetime.TotalMinutes));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["applicationName"] = context.ApplicationName,
            ["logoBase64"] = context.Branding.LogoBase64 ?? string.Empty,
            ["logoImageDisplay"] = string.IsNullOrWhiteSpace(context.Branding.LogoBase64) ? "none" : "block",
            ["logoTextDisplay"] = string.IsNullOrWhiteSpace(context.Branding.LogoBase64) ? "block" : "none",
            ["purposeLabel"] = context.Purpose == "signup" ? "sign-up" : "sign-in",
            ["heading"] = context.Purpose == "signup" ? "Your sign-up code" : "Your sign-in code",
            ["action"] = context.Purpose == "signup" ? "creating your account" : "signing in",
            ["maskedEmail"] = context.MaskedEmail,
            ["code"] = context.Code,
            ["expiresInMinutes"] = minutes,
            ["primaryColor"] = context.Branding.PrimaryColor,
            ["accentColor"] = context.Branding.AccentColor,
            ["backgroundColor"] = context.Branding.BackgroundColor
        };
    }

    /// <summary>Whether a verification signs in or completes a sign-up.</summary>
    private enum EmailOtpVerificationMode
    {
        SignIn,

        /// <summary>
        /// A sign-up code: verified inside the sign-up transaction
        /// (<see cref="EmailOtpAttemptScope.SignupTransaction"/>), and refused for an address an
        /// account already owns.
        /// </summary>
        Signup
    }

    /// <summary>
    /// One email-code sign-up start, from either surface. <c>ExistingEmailAuditOrganizationId</c>
    /// is the organization the existing-address audit records, as each 7.2.1 surface did, and
    /// <c>JoinsOrganizationWithoutInvitation</c> says whether the join policy must authorize the
    /// sign-up's organization join.
    /// </summary>
    private sealed record EmailOtpSignupStart(
        string DisplayName,
        string Email,
        SqlOSAuthorizationRequest? AuthorizationRequest,
        string? ClientApplicationId,
        string? ClientId,
        string? ExistingEmailAuditOrganizationId,
        bool JoinsOrganizationWithoutInvitation,
        string? JoinOrganizationId,
        string? OrganizationName,
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
