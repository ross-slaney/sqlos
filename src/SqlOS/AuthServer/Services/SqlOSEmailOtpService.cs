using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
            clientApplicationId: null,
            requestedOrganizationId: null,
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
        return await CreateChallengeAsync(
            request.Email,
            authorizationRequestId: null,
            clientApplicationId: client.Id,
            requestedOrganizationId: request.OrganizationId,
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

        if (challenge.User == null || !challenge.User.IsActive)
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        var organizations = await _adminService.GetUserOrganizationsAsync(challenge.User.Id, cancellationToken);
        return new SqlOSEmailOtpVerificationResult(challenge, challenge.User, organizations, "email_otp");
    }

    private async Task<SqlOSEmailOtpStartResult> CreateChallengeAsync(
        string email,
        string? authorizationRequestId,
        string? clientApplicationId,
        string? requestedOrganizationId,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var trimmedEmail = email?.Trim()
            ?? throw new InvalidOperationException("Email address is required.");
        if (string.IsNullOrWhiteSpace(trimmedEmail))
        {
            throw new InvalidOperationException("Email address is required.");
        }

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(trimmedEmail);
        var now = DateTime.UtcNow;

        var recentChallenges = await _context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.NormalizedEmail == normalizedEmail && x.CreatedAt >= now.AddHours(-1))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        if (recentChallenges.Count >= _options.MaxChallengesPerHour)
        {
            throw new InvalidOperationException("Too many sign-in code requests. Try again later.");
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
            IpAddress = httpContext?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = httpContext?.Request.Headers.UserAgent.ToString()
        };

        _context.Set<SqlOSEmailOtpChallenge>().Add(challenge);
        await _context.SaveChangesAsync(cancellationToken);

        if (emailRecord?.User != null && emailRecord.User.IsActive)
        {
            try
            {
                await _emailSender.SendAsync(
                    new SqlOSAuthEmailMessage(
                        trimmedEmail,
                        _options.Subject,
                        BuildHtmlBody(code, maskedEmail, _options.ChallengeLifetime),
                        BuildTextBody(code, _options.ChallengeLifetime)),
                    cancellationToken);
            }
            catch
            {
                challenge.InvalidatedAt = DateTime.UtcNow;
                challenge.InvalidatedReason = "delivery_failed";
                await _context.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException("We couldn't send a sign-in code right now.");
            }
        }

        return new SqlOSEmailOtpStartResult(
            rawChallengeToken,
            trimmedEmail,
            maskedEmail,
            $"If an account exists for {maskedEmail}, check your email for a sign-in code.",
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

    private static string BuildHtmlBody(string code, string maskedEmail, TimeSpan lifetime)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(lifetime.TotalMinutes));
        return $"""
        <!DOCTYPE html>
        <html lang="en">
        <body style="margin:0;padding:24px;background:#f8fafc;font-family:Segoe UI,Arial,sans-serif;color:#0f172a;">
          <div style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e2e8f0;border-radius:20px;padding:32px;">
            <p style="margin:0 0 12px;font-size:14px;color:#475569;">SqlOS sign-in</p>
            <h1 style="margin:0 0 12px;font-size:28px;line-height:1.1;">Your sign-in code</h1>
            <p style="margin:0 0 20px;font-size:15px;line-height:1.6;color:#475569;">Use this one-time code to finish signing in to {maskedEmail}. It expires in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.</p>
            <div style="margin:0 0 20px;padding:18px 20px;border-radius:16px;background:#eff6ff;border:1px solid #bfdbfe;font-size:34px;letter-spacing:0.24em;font-weight:700;text-align:center;color:#1d4ed8;">{code}</div>
            <p style="margin:0;font-size:13px;line-height:1.6;color:#64748b;">If you didn't request this code, you can ignore this email.</p>
          </div>
        </body>
        </html>
        """;
    }

    private static string BuildTextBody(string code, TimeSpan lifetime)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(lifetime.TotalMinutes));
        return $"Your SqlOS sign-in code is {code}. It expires in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
    }
}

public sealed record SqlOSEmailOtpVerificationResult(
    SqlOSEmailOtpChallenge Challenge,
    SqlOSUser User,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    string AuthenticationMethod);
