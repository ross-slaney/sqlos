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
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSEmailOtpService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ISqlOSAuthEmailSender _emailSender;
    private readonly ISqlOSTransactionalEmailService? _transactionalEmailService;
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
            authorizationRequestId: authorizationRequest?.Id,
            clientApplicationId: authorizationRequest?.ClientApplicationId,
            requestedOrganizationId: null,
            httpContext,
            cancellationToken,
            purpose: "login",
            signupWhenNoAccount: true);
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

        var trimmedEmail = RequireValidEmail(email);
        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FindByEmailAsync(trimmedEmail, cancellationToken);
        if (existingEmail != null)
        {
            await RecordExistingEmailSignupAuditAsync(
                trimmedEmail,
                authorizationRequest?.Id,
                authorizationRequest?.ClientApplicationId,
                authorizationRequest?.OrganizationId,
                httpContext,
                cancellationToken);
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
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, userId: null, cancellationToken);
        var trimmedDisplayName = request.DisplayName?.Trim()
            ?? throw new InvalidOperationException("Display name is required.");
        if (string.IsNullOrWhiteSpace(trimmedDisplayName))
        {
            throw new InvalidOperationException("Display name is required.");
        }

        var trimmedEmail = RequireValidEmail(request.Email);
        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FindByEmailAsync(trimmedEmail, cancellationToken);
        if (existingEmail != null)
        {
            await RecordExistingEmailSignupAuditAsync(
                trimmedEmail,
                authorizationRequestId: null,
                client.Id,
                request.OrganizationId,
                httpContext,
                cancellationToken);
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
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, userId: null, cancellationToken);
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
        var (signupToken, token, payload, challenge) = await CheckSignupCodeAsync(
            request,
            expectedAuthorizationRequestId,
            requireAuthorizationRequestMatch,
            cancellationToken);
        await RecordVerifySucceededAsync(challenge, cancellationToken);
        return await SignupVerificationAsync(signupToken, token, payload, challenge, cancellationToken);
    }

    /// <summary>
    /// Checks a sign-up code like <see cref="VerifySignupAsync"/>, except that a correct code for an
    /// address that already has an active account signs that account in: the code proved the
    /// mailbox, so the person gets the sign-in they meant instead of an invalid-code error. The
    /// sign-up token and the challenge are spent in that case. The start response never said which
    /// case it was, and only the mailbox owner can reach this answer.
    /// </summary>
    public async Task<SqlOSEmailOtpSignupOutcome> VerifySignupOrSignInAsync(
        SqlOSEmailOtpSignupVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        var (signupToken, token, payload, challenge) = await CheckSignupCodeAsync(
            request,
            expectedAuthorizationRequestId,
            requireAuthorizationRequestMatch,
            cancellationToken);

        if (challenge.User is { IsActive: true } existingUser)
        {
            _ = await _cryptoService.ConsumeTemporaryTokenAsync("email_otp_signup", signupToken, cancellationToken)
                ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
            await SpendVerifiedChallengeAsync(challenge, cancellationToken);
            await RecordVerifySucceededAsync(challenge, cancellationToken);
            await RecordOtpAuditAsync(
                "email_otp.signup_existing_email_signed_in",
                MaskEmail(challenge.Email),
                "signup",
                challenge.IpAddress,
                new { challenge.ClientApplicationId, challenge.AuthorizationRequestId },
                cancellationToken);
            var organizations = await _adminService.GetUserOrganizationsAsync(existingUser.Id, cancellationToken);
            return new SqlOSEmailOtpSignupOutcome(
                Signup: null,
                SignIn: new SqlOSEmailOtpVerificationResult(challenge, existingUser, organizations, "email_otp"));
        }

        await RecordVerifySucceededAsync(challenge, cancellationToken);
        return new SqlOSEmailOtpSignupOutcome(
            Signup: await SignupVerificationAsync(signupToken, token, payload, challenge, cancellationToken),
            SignIn: null);
    }

    private async Task<(string SignupToken, SqlOSTemporaryToken Token, EmailOtpSignupPayload Payload, SqlOSEmailOtpChallenge Challenge)> CheckSignupCodeAsync(
        SqlOSEmailOtpSignupVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken)
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

        // Callers verify before they open the sign-up transaction, so a wrong code stays counted
        // when that transaction rolls back. The challenge is spent later, inside the transaction,
        // together with the sign-up token (ConsumeSignupTokenAsync).
        var challenge = await CheckChallengeCodeAsync(
            new SqlOSEmailOtpVerifyRequest(rawChallengeToken, request.Code),
            expectedAuthorizationRequestId,
            requireAuthorizationRequestMatch,
            cancellationToken);
        return (signupToken, token, payload, challenge);
    }

    private async Task<SqlOSEmailOtpSignupVerificationResult> SignupVerificationAsync(
        string signupToken,
        SqlOSTemporaryToken token,
        EmailOtpSignupPayload payload,
        SqlOSEmailOtpChallenge challenge,
        CancellationToken cancellationToken)
    {
        if (challenge.User != null)
        {
            await RecordOtpAuditAsync(
                "email_otp.signup_existing_email_rejected",
                MaskEmail(challenge.Email),
                "signup",
                challenge.IpAddress,
                new
                {
                    challenge.ClientApplicationId,
                    challenge.AuthorizationRequestId,
                    reason = "challenge_bound_to_existing_user"
                },
                cancellationToken);
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FindByNormalizedEmailAsync(challenge.NormalizedEmail, challenge.Email, cancellationToken);
        if (existingEmail != null)
        {
            await RecordOtpAuditAsync(
                "email_otp.signup_existing_email_rejected",
                MaskEmail(challenge.Email),
                "signup",
                challenge.IpAddress,
                new
                {
                    challenge.ClientApplicationId,
                    challenge.AuthorizationRequestId,
                    reason = "email_claimed_after_challenge_started"
                },
                cancellationToken);
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
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

    /// <summary>
    /// Spends the sign-up token and the code challenge it is bound to. Call it inside the sign-up
    /// transaction, after <see cref="VerifySignupAsync"/> succeeded outside it: a sign-up that rolls
    /// back then leaves both unspent, while every attempt the verification counted stays counted.
    /// </summary>
    public async Task ConsumeSignupTokenAsync(
        string signupToken,
        CancellationToken cancellationToken = default)
    {
        var rawSignupToken = signupToken?.Trim()
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
        var token = await _cryptoService.ConsumeTemporaryTokenAsync("email_otp_signup", rawSignupToken, cancellationToken)
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
        var payload = _cryptoService.DeserializePayload<EmailOtpSignupPayload>(token)
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");

        var challenge = await _context.Set<SqlOSEmailOtpChallenge>()
            .FirstOrDefaultAsync(x => x.ChallengeTokenHash == payload.ChallengeTokenHash && x.ConsumedAt == null, cancellationToken)
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
        challenge.ConsumedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // A concurrent sign-up spent the challenge first.
            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }

            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }
    }

    /// <summary>
    /// Checks a sign-in code. A correct code for an active account signs it in, as
    /// <see cref="VerifyAsync(SqlOSEmailOtpVerifyRequest, string?, bool, CancellationToken)"/> does.
    /// A correct code for an address with no account (sign-in start sent it a sign-up code, see
    /// <see cref="StartForAuthorizationRequestAsync"/>) offers to create the account instead: the code
    /// is spent and a one-use verified sign-up token comes back, which
    /// <see cref="VerifySignupCompletionAsync"/> turns into a sign-up once the person gives a name.
    /// Codes from the direct first-party start are never sent to unknown addresses, so they never
    /// offer a sign-up.
    /// </summary>
    public async Task<SqlOSEmailOtpSignInOutcome> VerifyOrOfferSignupAsync(
        SqlOSEmailOtpVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        await EnsureEmailOtpEnabledAsync(cancellationToken);

        var challenge = await CheckChallengeCodeAsync(
            request,
            expectedAuthorizationRequestId,
            requireAuthorizationRequestMatch,
            cancellationToken);
        await SpendVerifiedChallengeAsync(challenge, cancellationToken);
        await RecordVerifySucceededAsync(challenge, cancellationToken);

        if (challenge.User is { IsActive: true } user)
        {
            var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
            return new SqlOSEmailOtpSignInOutcome(
                SignIn: new SqlOSEmailOtpVerificationResult(challenge, user, organizations, "email_otp"),
                Signup: null);
        }

        var sentByDirectStart = challenge.AuthorizationRequestId == null && challenge.ClientApplicationId != null;
        if (challenge.UserId != null || challenge.UserEmailId != null || sentByDirectStart)
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        var claimed = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FindByNormalizedEmailAsync(challenge.NormalizedEmail, challenge.Email, cancellationToken);
        if (claimed != null)
        {
            await RecordOtpAuditAsync(
                "email_otp.signup_existing_email_rejected",
                MaskEmail(challenge.Email),
                "signup",
                challenge.IpAddress,
                new
                {
                    challenge.ClientApplicationId,
                    challenge.AuthorizationRequestId,
                    reason = "email_claimed_after_challenge_started"
                },
                cancellationToken);
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        var signupToken = await _cryptoService.CreateTemporaryTokenAsync(
            VerifiedSignupTokenPurpose,
            userId: null,
            clientApplicationId: challenge.ClientApplicationId,
            organizationId: null,
            payload: new EmailOtpVerifiedSignupPayload(
                challenge.AuthorizationRequestId,
                challenge.AuthorizationRequest?.ClientApplication?.ClientId ?? challenge.ClientApplication?.ClientId,
                challenge.ClientApplicationId,
                challenge.Email),
            lifetime: _options.ChallengeLifetime,
            cancellationToken);
        await RecordOtpAuditAsync(
            "email_otp.signup_offered",
            MaskEmail(challenge.Email),
            "signup",
            challenge.IpAddress,
            new { challenge.ClientApplicationId, challenge.AuthorizationRequestId },
            cancellationToken);

        return new SqlOSEmailOtpSignInOutcome(
            SignIn: null,
            Signup: new SqlOSEmailOtpSignupOffer(signupToken, challenge.Email));
    }

    /// <summary>
    /// Checks the name step that follows <see cref="VerifyOrOfferSignupAsync"/>: the verified sign-up
    /// token, the display name, and that the address is still free. The result is the same shape a
    /// sign-up code produces, so callers create the account on the path they already have; spend the
    /// token with <see cref="ConsumeSignupTokenAsync(SqlOSEmailOtpSignupVerificationResult, CancellationToken)"/>
    /// inside that transaction.
    /// </summary>
    public async Task<SqlOSEmailOtpSignupVerificationResult> VerifySignupCompletionAsync(
        SqlOSEmailOtpSignupCompleteRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        await EnsureEmailOtpEnabledAsync(cancellationToken);

        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Display name is required.");
        }

        var signupToken = request.SignupToken?.Trim()
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
        var token = await _cryptoService.FindTemporaryTokenAsync(VerifiedSignupTokenPurpose, signupToken, cancellationToken)
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
        var payload = _cryptoService.DeserializePayload<EmailOtpVerifiedSignupPayload>(token)
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");

        if (requireAuthorizationRequestMatch
            && !string.Equals(payload.AuthorizationRequestId ?? string.Empty, expectedAuthorizationRequestId ?? string.Empty, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        var claimed = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FindByEmailAsync(payload.Email, cancellationToken);
        if (claimed != null)
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        return new SqlOSEmailOtpSignupVerificationResult(
            signupToken,
            token.ClientApplicationId ?? payload.ClientApplicationId,
            payload.ClientId,
            displayName,
            payload.Email,
            string.IsNullOrWhiteSpace(request.OrganizationName) ? null : request.OrganizationName.Trim(),
            OrganizationId: null,
            request.CustomFields)
        {
            CodeAlreadySpent = true
        };
    }

    /// <summary>
    /// Spends whichever token a sign-up verification came from: a sign-up code's token and its
    /// challenge (<see cref="ConsumeSignupTokenAsync(string, CancellationToken)"/>), or the verified
    /// token of the name step, whose challenge was spent when the code was checked.
    /// </summary>
    public async Task ConsumeSignupTokenAsync(
        SqlOSEmailOtpSignupVerificationResult verification,
        CancellationToken cancellationToken = default)
    {
        if (!verification.CodeAlreadySpent)
        {
            await ConsumeSignupTokenAsync(verification.SignupToken, cancellationToken);
            return;
        }

        _ = await _cryptoService.ConsumeTemporaryTokenAsync(VerifiedSignupTokenPurpose, verification.SignupToken, cancellationToken)
            ?? throw new InvalidOperationException("The sign-in code is invalid or expired.");
    }

    private async Task<SqlOSEmailOtpChallenge> VerifyChallengeAsync(
        SqlOSEmailOtpVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken)
    {
        var challenge = await CheckChallengeCodeAsync(
            request,
            expectedAuthorizationRequestId,
            requireAuthorizationRequestMatch,
            cancellationToken);

        await SpendVerifiedChallengeAsync(challenge, cancellationToken);
        await RecordVerifySucceededAsync(challenge, cancellationToken);
        return challenge;
    }

    /// <summary>
    /// Spends a challenge whose code was checked and claims the address it proved. Shared by sign-in
    /// and by a sign-up code that turned out to belong to an existing account.
    /// </summary>
    private async Task SpendVerifiedChallengeAsync(
        SqlOSEmailOtpChallenge challenge,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        challenge.ConsumedAt = now;

        if (challenge.UserEmail != null && challenge.User is { IsActive: true })
        {
            // The code proved the mailbox. An unverified address is claimed: whatever was
            // attached before the owner proved it is evicted in this same save.
            await SqlOSEmailOwnershipClaim.ClaimAsync(
                _context,
                challenge.UserEmail,
                "email_otp",
                SqlOSEmailClaimPresentation.None,
                now,
                cancellationToken);
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
    }

    /// <summary>
    /// Loads the challenge, counts this attempt, and compares the code. A wrong code is recorded
    /// (attempt, audit event, and the max-attempts invalidation) before it throws, and none of it
    /// depends on a transaction the caller may roll back. The challenge is not consumed.
    /// </summary>
    private async Task<SqlOSEmailOtpChallenge> CheckChallengeCodeAsync(
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

        if (challenge.UserId != null
            && (challenge.UserEmail == null
                || !string.Equals(challenge.UserEmail.UserId, challenge.UserId, StringComparison.Ordinal)
                || !SqlOSEmailAddress.MatchesStoredEmail(challenge.UserEmail, challenge.NormalizedEmail)))
        {
            // The code was delivered to an address that is no longer this account's address.
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

        // Count the attempt before comparing the code. The reservation is the only admission, so
        // concurrent guesses that all loaded the same count cannot overrun MaxAttempts.
        if (!await TryReserveAttemptAsync(challenge, cancellationToken))
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        if (!string.Equals(challenge.CodeHash, ComputeCodeHash(rawChallengeToken, normalizedCode), StringComparison.Ordinal))
        {
            var exhausted = await InvalidateIfAttemptsExhaustedAsync(challenge, cancellationToken);
            await RecordOtpAuditAsync(
                "email_otp.verify_failed",
                MaskEmail(challenge.Email),
                challenge.User == null ? "signup" : "login",
                challenge.IpAddress,
                new
                {
                    challenge.ClientApplicationId,
                    challenge.AuthorizationRequestId,
                    reason = exhausted ? "max_attempts" : "wrong_code"
                },
                cancellationToken);
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        return challenge;
    }

    /// <summary>
    /// Counts one attempt against the challenge if it is still active and has attempts left. On a
    /// relational database this is a single conditional update that commits on its own, so it must
    /// not run inside a transaction: a rollback would erase the attempt.
    /// </summary>
    private async Task<bool> TryReserveAttemptAsync(SqlOSEmailOtpChallenge challenge, CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational())
        {
            // The in-memory provider (single-process tests) has no set-based updates.
            challenge.AttemptCount++;
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (_context.Database.CurrentTransaction != null)
        {
            throw new InvalidOperationException(
                "Email OTP verification cannot run inside a database transaction: a rollback would erase the attempt it counts.");
        }

        var now = DateTime.UtcNow;
        // Bypasses the change tracker on purpose: the tracked AttemptCount stays unmodified, so a
        // later save of this challenge never writes a stale count over concurrent reservations.
        var reserved = await _context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.Id == challenge.Id
                && x.ConsumedAt == null
                && x.InvalidatedAt == null
                && x.ExpiresAt > now
                && x.AttemptCount < x.MaxAttempts)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), cancellationToken);
        return reserved == 1;
    }

    /// <summary>Invalidates the challenge once its counted attempts reach MaxAttempts.</summary>
    private async Task<bool> InvalidateIfAttemptsExhaustedAsync(SqlOSEmailOtpChallenge challenge, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (!_context.Database.IsRelational())
        {
            if (challenge.AttemptCount < challenge.MaxAttempts)
            {
                return false;
            }

            challenge.InvalidatedAt = now;
            challenge.InvalidatedReason = "max_attempts";
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }

        var invalidated = await _context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.Id == challenge.Id
                && x.ConsumedAt == null
                && x.InvalidatedAt == null
                && x.AttemptCount >= x.MaxAttempts)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.InvalidatedAt, now)
                .SetProperty(x => x.InvalidatedReason, "max_attempts"), cancellationToken);
        return invalidated == 1;
    }

    private async Task RecordVerifySucceededAsync(SqlOSEmailOtpChallenge challenge, CancellationToken cancellationToken)
        => await RecordOtpAuditAsync(
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

    private async Task<SqlOSEmailOtpStartResult> CreateChallengeAsync(
        string email,
        string? authorizationRequestId,
        string? clientApplicationId,
        string? requestedOrganizationId,
        HttpContext? httpContext,
        CancellationToken cancellationToken,
        bool sendWhenNoUser = false,
        string purpose = "login",
        bool signupWhenNoAccount = false)
    {
        var trimmedEmail = RequireValidEmail(email);
        SqlOSEmailAddress.TryCanonicalize(trimmedEmail, out var typedAddress, out var normalizedEmail);
        var now = DateTime.UtcNow;
        var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();

        var recentChallenges = (await _context.Set<SqlOSEmailOtpChallenge>()
                .Where(x => x.NormalizedEmail == normalizedEmail && x.CreatedAt >= now.AddHours(-1))
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(cancellationToken))
            .Where(x => string.Equals(x.NormalizedEmail, normalizedEmail, StringComparison.Ordinal))
            .ToList();

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
            .FindByNormalizedEmailAsync(normalizedEmail, email, cancellationToken);

        var activeChallenges = await _context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.NormalizedEmail == normalizedEmail
                && x.ConsumedAt == null
                && x.InvalidatedAt == null
                && x.ExpiresAt > now
                && x.AuthorizationRequestId == authorizationRequestId
                && x.ClientApplicationId == clientApplicationId
                && x.RequestedOrganizationId == requestedOrganizationId)
            .ToListAsync(cancellationToken);

        foreach (var activeChallenge in activeChallenges.Where(x => string.Equals(x.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)))
        {
            activeChallenge.InvalidatedAt = now;
            activeChallenge.InvalidatedReason = "superseded";
        }

        // A code for an existing account is only ever delivered to the address stored on that
        // account, never to the typed spelling. Without an account, the typed address is the
        // address being signed up.
        var deliveryAddress = emailRecord?.Email.Trim() ?? typedAddress;
        var rawChallengeToken = _cryptoService.GenerateOpaqueToken();
        var code = GenerateCode(_options.CodeLength);
        var maskedEmail = MaskEmail(trimmedEmail);
        var challenge = new SqlOSEmailOtpChallenge
        {
            Id = _cryptoService.GenerateId("otp"),
            ChallengeTokenHash = _cryptoService.HashToken(rawChallengeToken),
            CodeHash = ComputeCodeHash(rawChallengeToken, code),
            Email = deliveryAddress,
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

        // Signing in with an address that has no account: the code goes to the typed address as a
        // sign-up code, and a correct one offers to create the account (VerifyOrOfferSignupAsync).
        // The response is the same either way; only the mailbox learns which door it was.
        var offersSignup = signupWhenNoAccount && emailRecord == null;
        var sendPurpose = offersSignup ? "signup" : purpose;
        var sent = (emailRecord?.User != null && emailRecord.User.IsActive) || sendWhenNoUser || offersSignup;
        if (sent)
        {
            try
            {
                await SendEmailAsync(
                    deliveryAddress,
                    MaskEmail(deliveryAddress),
                    code,
                    challenge.ExpiresAt,
                    sendPurpose,
                    challenge.Id,
                    cancellationToken);
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
                sent
            },
            cancellationToken);

        return new SqlOSEmailOtpStartResult(
            rawChallengeToken,
            trimmedEmail,
            maskedEmail,
            purpose == "signup"
                ? $"Check {maskedEmail} for a sign-up code."
                : signupWhenNoAccount
                    ? $"Check {maskedEmail} for a code."
                    : $"If an account exists for {maskedEmail}, check your email for a sign-in code.",
            challenge.ExpiresAt,
            challenge.LastSentAt.Add(_options.ResendCooldown));
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

    private async Task SendEmailAsync(
        string email,
        string maskedEmail,
        string code,
        DateTime expiresAt,
        string purpose,
        string challengeId,
        CancellationToken cancellationToken)
    {
        var context = await BuildMessageContextAsync(email, maskedEmail, code, expiresAt, purpose, cancellationToken);
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
                email,
                BuildTemplateVariables(context),
                IdempotencyKey: $"auth-email-otp:{challengeId}"),
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

    private async Task RecordExistingEmailSignupAuditAsync(
        string email,
        string? authorizationRequestId,
        string? clientApplicationId,
        string? requestedOrganizationId,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
        => await RecordOtpAuditAsync(
            "email_otp.signup_existing_email",
            MaskEmail(email),
            "signup",
            httpContext?.Connection.RemoteIpAddress?.ToString(),
            new
            {
                clientApplicationId,
                authorizationRequestId,
                requestedOrganizationId,
                reason = "existing_email"
            },
            cancellationToken);

    private const string VerifiedSignupTokenPurpose = "email_otp_signup_verified";

    private sealed record EmailOtpVerifiedSignupPayload(
        string? AuthorizationRequestId,
        string? ClientId,
        string? ClientApplicationId,
        string Email);

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
    JsonObject? CustomFields)
{
    /// <summary>True for the name step after a sign-in code: the code's challenge is already spent.</summary>
    public bool CodeAlreadySpent { get; init; }
}

/// <summary>A sign-up code's outcome: a new account to create, or the existing account it signs in.</summary>
public sealed record SqlOSEmailOtpSignupOutcome(
    SqlOSEmailOtpSignupVerificationResult? Signup,
    SqlOSEmailOtpVerificationResult? SignIn);

/// <summary>A sign-in code's outcome: the account it signs in, or an offer to create one for the address.</summary>
public sealed record SqlOSEmailOtpSignInOutcome(
    SqlOSEmailOtpVerificationResult? SignIn,
    SqlOSEmailOtpSignupOffer? Signup);

/// <summary>The verified sign-up token the name step completes, and the address it proved.</summary>
public sealed record SqlOSEmailOtpSignupOffer(
    string SignupToken,
    string Email);
