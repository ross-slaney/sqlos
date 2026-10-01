using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.Database;
using QRCoder;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSTotpMfaService
{
    public const string EnrollmentPurpose = SqlOSTemporaryTokenKinds.Purposes.TotpEnrollment;

    private static readonly char[] Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSMfaPolicyService _policyService;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSTotpMfaService(
        ISqlOSAuthServerDbContext context,
        SqlOSCryptoService cryptoService,
        SqlOSMfaPolicyService policyService,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _cryptoService = cryptoService;
        _policyService = policyService;
        _options = options.Value;
    }

    public async Task<SqlOSMfaStatusResult> GetStatusAsync(
        string userId,
        string? organizationId = null,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await _policyService.EvaluateAsync(userId, organizationId, authenticationMethod: null, cancellationToken);
        return new SqlOSMfaStatusResult(
            evaluation.Enabled,
            evaluation.RequiresMfa,
            evaluation.EnrollmentRequired,
            evaluation.CanSelfEnroll,
            evaluation.HasTotp,
            evaluation.RecoveryCodeCount,
            evaluation.AvailableFactors,
            evaluation.Reason);
    }

    public async Task<IReadOnlyList<SqlOSMfaAuthenticatorDto>> ListAuthenticatorsAsync(
        string userId,
        CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSUserAuthenticator>()
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new SqlOSMfaAuthenticatorDto(
                x.Id,
                x.Type,
                x.DisplayName,
                x.IsConfirmed,
                x.CreatedAt,
                x.ConfirmedAt,
                x.LastUsedAt))
            .ToListAsync(cancellationToken);

    public async Task<SqlOSTotpEnrollmentStartResult> StartEnrollmentAsync(
        string userId,
        string? organizationId = null,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await _policyService.EvaluateAsync(userId, organizationId, authenticationMethod: null, cancellationToken);
        if (!evaluation.Enabled || !evaluation.AvailableFactors.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Authenticator app enrollment is not enabled.");
        }

        if (!evaluation.CanSelfEnroll && !evaluation.EnrollmentRequired)
        {
            throw new InvalidOperationException("Authenticator app enrollment is not available for this account.");
        }

        return await CreateEnrollmentAsync(
            userId,
            organizationId,
            clientApplicationId: null,
            displayName,
            challengeBinding: null,
            cancellationToken);
    }

    internal async Task<SqlOSTotpEnrollmentStartResult> StartChallengeEnrollmentAsync(
        SqlOSTemporaryToken challengeToken,
        SqlOSMfaChallengePayload challengePayload,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        if (challengeToken.UserId == null || challengeToken.ClientApplicationId == null)
        {
            throw ChallengeEnrollmentRejected();
        }

        var evaluation = await _policyService.EvaluateAsync(
            challengeToken.UserId,
            challengeToken.OrganizationId,
            challengePayload.AuthenticationMethod,
            cancellationToken);
        if (!challengePayload.EnrollmentRequired
            || challengePayload.PermittedEnrollmentFactors?.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase) != true
            || !evaluation.EnrollmentRequired
            || evaluation.HasTotp
            || !evaluation.AvailableFactors.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase))
        {
            throw ChallengeEnrollmentRejected();
        }

        return await CreateEnrollmentAsync(
            challengeToken.UserId,
            challengeToken.OrganizationId,
            challengeToken.ClientApplicationId,
            displayName,
            new TotpEnrollmentChallengeBinding(
                challengeToken.Id,
                challengeToken.UserId,
                challengeToken.ClientApplicationId,
                challengeToken.OrganizationId,
                challengePayload.Flow,
                challengePayload.ClientId,
                challengePayload.AuthorizationRequestId,
                challengePayload.Resource),
            cancellationToken);
    }

    private async Task<SqlOSTotpEnrollmentStartResult> CreateEnrollmentAsync(
        string userId,
        string? organizationId,
        string? clientApplicationId,
        string? displayName,
        TotpEnrollmentChallengeBinding? challengeBinding,
        CancellationToken cancellationToken)
    {

        var now = DateTime.UtcNow;
        var user = await _context.GetUserAsync(userId, SqlOSUserParts.Authenticators, cancellationToken);
        var secret = EncodeBase32(RandomNumberGenerator.GetBytes(_options.Mfa.Totp.SecretBytes));
        var authenticator = user.EnrollTotp(
            _cryptoService.ProtectSecret(secret),
            displayName,
            new TotpParameters(_options.Mfa.Totp.Algorithm, _options.Mfa.Totp.Digits, _options.Mfa.Totp.PeriodSeconds),
            now);
        var authenticatorId = authenticator.Id;
        var token = (await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.TotpEnrollment,
            new TotpEnrollmentPayload(authenticatorId, challengeBinding),
            new TemporaryTokenBinding(UserId: userId, ClientApplicationId: clientApplicationId, OrganizationId: organizationId),
            _options.Mfa.Totp.EnrollmentTokenLifetime,
            cancellationToken)).RawToken;

        var provisioningUri = BuildProvisioningUri(user, secret);

        return new SqlOSTotpEnrollmentStartResult(
            token,
            authenticatorId,
            secret,
            provisioningUri,
            BuildQrCodeDataUrl(provisioningUri),
            now.Add(_options.Mfa.Totp.EnrollmentTokenLifetime));
    }

    public async Task<SqlOSTotpEnrollmentVerifyResult> VerifyEnrollmentAsync(
        SqlOSTotpEnrollmentVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var temporaryToken = await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.TotpEnrollment, request.EnrollmentToken, cancellationToken)
            ?? throw new InvalidOperationException("Authenticator enrollment is invalid or expired.");
        if (temporaryToken.UserId == null)
        {
            throw new InvalidOperationException("Authenticator enrollment is invalid.");
        }

        var payload = temporaryToken.ReadPayload(SqlOSTemporaryTokenKinds.TotpEnrollment)
            ?? throw new InvalidOperationException("Authenticator enrollment payload is invalid.");
        if (payload.ChallengeBinding != null)
        {
            throw new InvalidOperationException("Challenge-bound enrollment must be verified with its original MFA challenge.");
        }

        return await ConfirmEnrollmentAsync(temporaryToken, payload, request.Code, challengeToken: null, cancellationToken);
    }

    internal async Task<SqlOSTotpChallengeEnrollmentVerification> VerifyChallengeEnrollmentAsync(
        SqlOSTotpEnrollmentVerifyRequest request,
        string expectedFlow,
        string? expectedAuthorizationRequestId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.MfaToken))
        {
            throw ChallengeEnrollmentRejected();
        }

        var challengeToken = await _cryptoService.FindTemporaryTokenAsync(
                SqlOSTemporaryTokenKinds.MfaChallenge,
                request.MfaToken,
                cancellationToken)
            ?? throw ChallengeEnrollmentRejected();
        var enrollmentToken = await _cryptoService.FindTemporaryTokenAsync(
                SqlOSTemporaryTokenKinds.TotpEnrollment,
                request.EnrollmentToken,
                cancellationToken)
            ?? throw ChallengeEnrollmentRejected();
        var challengePayload = challengeToken.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)
            ?? throw ChallengeEnrollmentRejected();
        var enrollmentPayload = enrollmentToken.ReadPayload(SqlOSTemporaryTokenKinds.TotpEnrollment)
            ?? throw ChallengeEnrollmentRejected();
        var binding = enrollmentPayload.ChallengeBinding
            ?? throw ChallengeEnrollmentRejected();

        if (challengeToken.UserId == null
            || challengeToken.ClientApplicationId == null
            || !challengePayload.EnrollmentRequired
            || challengePayload.PermittedEnrollmentFactors?.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase) != true
            || !string.Equals(enrollmentToken.UserId, challengeToken.UserId, StringComparison.Ordinal)
            || !string.Equals(enrollmentToken.ClientApplicationId, challengeToken.ClientApplicationId, StringComparison.Ordinal)
            || !string.Equals(enrollmentToken.OrganizationId, challengeToken.OrganizationId, StringComparison.Ordinal)
            || !string.Equals(binding.ChallengeTokenId, challengeToken.Id, StringComparison.Ordinal)
            || !string.Equals(binding.UserId, challengeToken.UserId, StringComparison.Ordinal)
            || !string.Equals(binding.ClientApplicationId, challengeToken.ClientApplicationId, StringComparison.Ordinal)
            || !string.Equals(binding.OrganizationId, challengeToken.OrganizationId, StringComparison.Ordinal)
            || !string.Equals(binding.Flow, challengePayload.Flow, StringComparison.Ordinal)
            || !string.Equals(challengePayload.Flow, expectedFlow, StringComparison.Ordinal)
            || !string.Equals(binding.ClientId, challengePayload.ClientId, StringComparison.Ordinal)
            || !string.Equals(binding.AuthorizationRequestId, challengePayload.AuthorizationRequestId, StringComparison.Ordinal)
            || (expectedAuthorizationRequestId != null
                && !string.Equals(challengePayload.AuthorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal))
            || !string.Equals(binding.Resource, challengePayload.Resource, StringComparison.Ordinal))
        {
            throw ChallengeEnrollmentRejected();
        }

        var evaluation = await _policyService.EvaluateAsync(
            challengeToken.UserId,
            challengeToken.OrganizationId,
            challengePayload.AuthenticationMethod,
            cancellationToken);
        if (!evaluation.EnrollmentRequired
            || evaluation.HasTotp
            || !evaluation.AvailableFactors.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase))
        {
            throw ChallengeEnrollmentRejected();
        }

        var result = await ConfirmEnrollmentAsync(
            enrollmentToken,
            enrollmentPayload,
            request.Code,
            challengeToken,
            cancellationToken);
        return new SqlOSTotpChallengeEnrollmentVerification(challengeToken, challengePayload, result);
    }

    private async Task<SqlOSTotpEnrollmentVerifyResult> ConfirmEnrollmentAsync(
        SqlOSTemporaryToken temporaryToken,
        TotpEnrollmentPayload payload,
        string code,
        SqlOSTemporaryToken? challengeToken,
        CancellationToken cancellationToken)
    {
        if (temporaryToken.UserId == null)
        {
            throw new InvalidOperationException("Authenticator enrollment is invalid.");
        }

        var user = await _context.FindUserAsync(
                temporaryToken.UserId,
                SqlOSUserParts.Authenticators | SqlOSUserParts.RecoveryCodes | SqlOSUserParts.MfaPolicyOverride,
                cancellationToken)
            ?? throw new InvalidOperationException("Authenticator enrollment is invalid.");
        var authenticator = user.FindAuthenticator(payload.AuthenticatorId) is { IsTotp: true } found
            ? found
            : throw new InvalidOperationException("Authenticator enrollment is invalid.");

        if (authenticator.IsConfirmed)
        {
            throw new InvalidOperationException("Authenticator enrollment has already been confirmed.");
        }

        var secret = _cryptoService.UnprotectSecret(authenticator.SecretProtected);
        if (!TryValidateTotp(secret, code, authenticator.PeriodSeconds, authenticator.Digits, out var matchedStep))
        {
            throw new InvalidOperationException("Authenticator code is invalid.");
        }

        // Confirming opts the account into MFA unless it already chose.
        var now = DateTime.UtcNow;
        user.ConfirmTotp(authenticator.Id, matchedStep, now);
        temporaryToken.Consume(SqlOSTemporaryTokenKinds.TotpEnrollment, now);
        challengeToken?.Consume(SqlOSTemporaryTokenKinds.MfaChallenge, now);

        var recoveryCodes = await ReplaceRecoveryCodesAsync(user, temporaryToken.OrganizationId, now, cancellationToken);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("MFA enrollment challenge has already been used.");
        }
        catch (DbUpdateException ex) when (SqlOSDatabaseErrors.IsUniqueConstraintViolation(ex))
        {
            throw new InvalidOperationException("MFA enrollment challenge has already been used.");
        }

        return new SqlOSTotpEnrollmentVerifyResult(authenticator.Id, recoveryCodes);
    }

    public async Task<string> VerifySecondFactorCodeAsync(
        string userId,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("MFA code is required.");
        }

        if (await TryVerifyTotpAsync(userId, code, cancellationToken))
        {
            return SqlOSMfaFactorTypes.Totp;
        }

        if (await TryConsumeRecoveryCodeAsync(userId, code, cancellationToken))
        {
            return SqlOSMfaFactorTypes.RecoveryCode;
        }

        throw new InvalidOperationException("MFA code is invalid.");
    }

    public async Task RevokeAuthenticatorAsync(
        string userId,
        string authenticatorId,
        string reason = "user_removed",
        CancellationToken cancellationToken = default)
    {
        var user = await _context.FindUserAsync(userId, SqlOSUserParts.Authenticators, cancellationToken);
        if (user?.FindAuthenticator(authenticatorId) == null)
        {
            throw new InvalidOperationException("Authenticator was not found.");
        }

        user.RevokeAuthenticator(authenticatorId, reason, DateTime.UtcNow);
        await _context.SaveChangesAsync(cancellationToken);
    }

    internal async Task<SqlOSTemporaryToken> GetPendingMfaTokenAsync(string mfaToken, CancellationToken cancellationToken)
        => await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.MfaChallenge, mfaToken, cancellationToken)
            ?? throw new InvalidOperationException("MFA challenge is invalid or expired.");

    private async Task<bool> TryVerifyTotpAsync(
        string userId,
        string code,
        CancellationToken cancellationToken)
    {
        var user = await _context.FindUserAsync(userId, SqlOSUserParts.Authenticators, cancellationToken);
        if (user == null)
        {
            return false;
        }

        foreach (var authenticator in user.ConfirmedTotpAuthenticators)
        {
            var secret = _cryptoService.UnprotectSecret(authenticator.SecretProtected);
            if (!TryValidateTotp(secret, code, authenticator.PeriodSeconds, authenticator.Digits, out var matchedStep))
            {
                continue;
            }

            // A code for a time step already accepted is a replay.
            if (!user.AcceptTotpCode(authenticator.Id, matchedStep, DateTime.UtcNow))
            {
                continue;
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new InvalidOperationException("MFA code has already been used.");
            }
        }

        return false;
    }

    private async Task<bool> TryConsumeRecoveryCodeAsync(
        string userId,
        string code,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRecoveryCode(code);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var user = await _context.FindUserAsync(userId, SqlOSUserParts.RecoveryCodes, cancellationToken);
        if (user == null || !user.UseRecoveryCode(normalized, DateTime.UtcNow))
        {
            return false;
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("Recovery code has already been used.");
        }
    }

    private async Task<string[]> ReplaceRecoveryCodesAsync(
        SqlOSUser user,
        string? organizationId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var evaluation = await _policyService.EvaluateAsync(user.Id, organizationId, authenticationMethod: null, cancellationToken);
        if (!evaluation.RecoveryCodesEnabled || !evaluation.AvailableFactors.Contains(SqlOSMfaFactorTypes.RecoveryCode, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var rawCodes = Enumerable.Range(0, _options.Mfa.Totp.RecoveryCodeCount)
            .Select(_ => FormatRecoveryCode(EncodeBase32(RandomNumberGenerator.GetBytes(8))[..10]))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        user.IssueRecoveryCodes(rawCodes.Select(NormalizeRecoveryCode).ToArray(), now);
        return rawCodes;
    }

    private string BuildProvisioningUri(SqlOSUser user, string secret)
    {
        var issuer = string.IsNullOrWhiteSpace(_options.Mfa.Totp.Issuer)
            ? "SqlOS"
            : _options.Mfa.Totp.Issuer.Trim();
        var account = string.IsNullOrWhiteSpace(user.DefaultEmail)
            ? user.Id
            : user.DefaultEmail;
        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}";
        var query = string.Join("&", new[]
        {
            $"secret={Uri.EscapeDataString(secret)}",
            $"issuer={Uri.EscapeDataString(issuer)}",
            $"algorithm={Uri.EscapeDataString(_options.Mfa.Totp.Algorithm)}",
            $"digits={_options.Mfa.Totp.Digits.ToString(CultureInfo.InvariantCulture)}",
            $"period={_options.Mfa.Totp.PeriodSeconds.ToString(CultureInfo.InvariantCulture)}"
        });

        return $"otpauth://totp/{label}?{query}";
    }

    private static string BuildQrCodeDataUrl(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        var svg = new SvgQRCode(data).GetGraphic(5);
        return $"data:image/svg+xml;charset=utf-8,{Uri.EscapeDataString(svg)}";
    }

    private bool TryValidateTotp(
        string secret,
        string code,
        int periodSeconds,
        int digits,
        out long matchedStep)
    {
        matchedStep = 0;
        var normalizedCode = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalizedCode.Length != digits)
        {
            return false;
        }

        var secretBytes = DecodeBase32(secret);
        var nowStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / periodSeconds;
        for (var offset = -_options.Mfa.Totp.AllowedClockSkewSteps; offset <= _options.Mfa.Totp.AllowedClockSkewSteps; offset++)
        {
            var step = nowStep + offset;
            var expected = ComputeTotp(secretBytes, step, digits);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(normalizedCode)))
            {
                matchedStep = step;
                return true;
            }
        }

        return false;
    }

    public string GenerateCodeForTesting(string secret, DateTimeOffset? timestamp = null)
    {
        var step = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / _options.Mfa.Totp.PeriodSeconds;
        return ComputeTotp(DecodeBase32(secret), step, _options.Mfa.Totp.Digits);
    }

    private static string ComputeTotp(byte[] secret, long timeStep, int digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BitConverter.TryWriteBytes(counter, timeStep);
        if (BitConverter.IsLittleEndian)
        {
            counter.Reverse();
        }

        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0f;
        var binary =
            ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        var modulo = (int)Math.Pow(10, digits);
        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    private static string EncodeBase32(byte[] data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = data[0] & 0xff;
        var next = 1;
        var bitsLeft = 8;
        while (bitsLeft > 0 || next < data.Length)
        {
            if (bitsLeft < 5)
            {
                if (next < data.Length)
                {
                    buffer <<= 8;
                    buffer |= data[next++] & 0xff;
                    bitsLeft += 8;
                }
                else
                {
                    var pad = 5 - bitsLeft;
                    buffer <<= pad;
                    bitsLeft += pad;
                }
            }

            var index = 0x1f & (buffer >> (bitsLeft - 5));
            bitsLeft -= 5;
            output.Append(Base32Alphabet[index]);
        }

        return output.ToString();
    }

    private static byte[] DecodeBase32(string input)
    {
        var cleaned = new string((input ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (cleaned.Length == 0)
        {
            return [];
        }

        var bytes = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var character in cleaned)
        {
            var value = Array.IndexOf(Base32Alphabet, character);
            if (value < 0)
            {
                throw new InvalidOperationException("Authenticator secret is invalid.");
            }

            buffer <<= 5;
            buffer |= value & 0x1f;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)(buffer >> (bitsLeft - 8)));
                bitsLeft -= 8;
            }
        }

        return bytes.ToArray();
    }

    private static string NormalizeRecoveryCode(string code)
        => new((code ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string FormatRecoveryCode(string code)
        => $"{code[..5]}-{code[5..10]}";

    private static InvalidOperationException ChallengeEnrollmentRejected()
        => new("MFA enrollment is not authorized for this challenge.");
}

internal sealed record SqlOSTotpChallengeEnrollmentVerification(
    SqlOSTemporaryToken ChallengeToken,
    SqlOSMfaChallengePayload ChallengePayload,
    SqlOSTotpEnrollmentVerifyResult Enrollment);
