using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSTotpMfaService
{
    public const string EnrollmentPurpose = "mfa_totp_enrollment";

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
        var stale = await _context.Set<SqlOSUserAuthenticator>()
            .Where(x =>
                x.UserId == userId
                && x.Type == SqlOSMfaFactorTypes.Totp
                && !x.IsConfirmed
                && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var authenticator in stale)
        {
            authenticator.RevokedAt = now;
            authenticator.RevocationReason = "replaced_unconfirmed";
        }

        var secret = EncodeBase32(RandomNumberGenerator.GetBytes(_options.Mfa.Totp.SecretBytes));
        var authenticatorId = _cryptoService.GenerateId("mfa");
        var authenticatorName = string.IsNullOrWhiteSpace(displayName)
            ? "Authenticator app"
            : displayName.Trim();
        var authenticatorRow = new SqlOSUserAuthenticator
        {
            Id = authenticatorId,
            UserId = userId,
            Type = SqlOSMfaFactorTypes.Totp,
            DisplayName = authenticatorName,
            SecretProtected = _cryptoService.ProtectSecret(secret),
            SecretVersion = 1,
            Algorithm = _options.Mfa.Totp.Algorithm,
            Digits = _options.Mfa.Totp.Digits,
            PeriodSeconds = _options.Mfa.Totp.PeriodSeconds,
            IsConfirmed = false,
            CreatedAt = now
        };
        _context.Set<SqlOSUserAuthenticator>().Add(authenticatorRow);

        var user = await _context.Set<SqlOSUser>()
            .AsNoTracking()
            .FirstAsync(x => x.Id == userId, cancellationToken);
        var token = await _cryptoService.CreateTemporaryTokenAsync(
            EnrollmentPurpose,
            userId,
            clientApplicationId,
            organizationId,
            new TotpEnrollmentPayload(authenticatorId, challengeBinding),
            _options.Mfa.Totp.EnrollmentTokenLifetime,
            cancellationToken);

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
        var temporaryToken = await _cryptoService.FindTemporaryTokenAsync(EnrollmentPurpose, request.EnrollmentToken, cancellationToken)
            ?? throw new InvalidOperationException("Authenticator enrollment is invalid or expired.");
        if (temporaryToken.UserId == null)
        {
            throw new InvalidOperationException("Authenticator enrollment is invalid.");
        }

        var payload = _cryptoService.DeserializePayload<TotpEnrollmentPayload>(temporaryToken)
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
                SqlOSAuthService.MfaChallengePurpose,
                request.MfaToken,
                cancellationToken)
            ?? throw ChallengeEnrollmentRejected();
        var enrollmentToken = await _cryptoService.FindTemporaryTokenAsync(
                EnrollmentPurpose,
                request.EnrollmentToken,
                cancellationToken)
            ?? throw ChallengeEnrollmentRejected();
        var challengePayload = _cryptoService.DeserializePayload<SqlOSMfaChallengePayload>(challengeToken)
            ?? throw ChallengeEnrollmentRejected();
        var enrollmentPayload = _cryptoService.DeserializePayload<TotpEnrollmentPayload>(enrollmentToken)
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

        var authenticator = await _context.Set<SqlOSUserAuthenticator>()
            .FirstOrDefaultAsync(x =>
                x.Id == payload.AuthenticatorId
                && x.UserId == temporaryToken.UserId
                && x.Type == SqlOSMfaFactorTypes.Totp
                && x.RevokedAt == null,
                cancellationToken)
            ?? throw new InvalidOperationException("Authenticator enrollment is invalid.");

        if (authenticator.IsConfirmed)
        {
            throw new InvalidOperationException("Authenticator enrollment has already been confirmed.");
        }

        var secret = _cryptoService.UnprotectSecret(authenticator.SecretProtected);
        if (!TryValidateTotp(secret, code, authenticator.PeriodSeconds, authenticator.Digits, out var matchedStep))
        {
            throw new InvalidOperationException("Authenticator code is invalid.");
        }

        authenticator.IsConfirmed = true;
        authenticator.ConfirmedAt = DateTime.UtcNow;
        authenticator.LastUsedAt = DateTime.UtcNow;
        authenticator.LastAcceptedTimeStep = matchedStep;
        temporaryToken.ConsumedAt = DateTime.UtcNow;
        if (challengeToken != null)
        {
            challengeToken.ConsumedAt = DateTime.UtcNow;
        }

        var recoveryCodes = await ReplaceRecoveryCodesAsync(
            temporaryToken.UserId,
            temporaryToken.OrganizationId,
            cancellationToken);
        await EnsureUserOptInPolicyAsync(temporaryToken.UserId, cancellationToken);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
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
        var authenticator = await _context.Set<SqlOSUserAuthenticator>()
            .FirstOrDefaultAsync(x => x.Id == authenticatorId && x.UserId == userId && x.RevokedAt == null, cancellationToken)
            ?? throw new InvalidOperationException("Authenticator was not found.");
        authenticator.RevokedAt = DateTime.UtcNow;
        authenticator.RevocationReason = reason;
        await _context.SaveChangesAsync(cancellationToken);
    }

    internal async Task<SqlOSTemporaryToken> GetPendingMfaTokenAsync(string mfaToken, CancellationToken cancellationToken)
        => await _cryptoService.FindTemporaryTokenAsync(SqlOSAuthService.MfaChallengePurpose, mfaToken, cancellationToken)
            ?? throw new InvalidOperationException("MFA challenge is invalid or expired.");

    private async Task<bool> TryVerifyTotpAsync(
        string userId,
        string code,
        CancellationToken cancellationToken)
    {
        var authenticators = await _context.Set<SqlOSUserAuthenticator>()
            .Where(x =>
                x.UserId == userId
                && x.Type == SqlOSMfaFactorTypes.Totp
                && x.IsConfirmed
                && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var authenticator in authenticators)
        {
            var secret = _cryptoService.UnprotectSecret(authenticator.SecretProtected);
            if (!TryValidateTotp(secret, code, authenticator.PeriodSeconds, authenticator.Digits, out var matchedStep))
            {
                continue;
            }

            if (authenticator.LastAcceptedTimeStep.HasValue && matchedStep <= authenticator.LastAcceptedTimeStep.Value)
            {
                continue;
            }

            authenticator.LastAcceptedTimeStep = matchedStep;
            authenticator.LastUsedAt = DateTime.UtcNow;
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

        var hash = _cryptoService.HashToken(normalized);
        var recoveryCode = await _context.Set<SqlOSRecoveryCode>()
            .FirstOrDefaultAsync(x =>
                x.UserId == userId
                && x.CodeHash == hash
                && x.ConsumedAt == null
                && x.RevokedAt == null,
                cancellationToken);
        if (recoveryCode == null)
        {
            return false;
        }

        recoveryCode.ConsumedAt = DateTime.UtcNow;
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
        string userId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var evaluation = await _policyService.EvaluateAsync(userId, organizationId, authenticationMethod: null, cancellationToken);
        if (!evaluation.RecoveryCodesEnabled || !evaluation.AvailableFactors.Contains(SqlOSMfaFactorTypes.RecoveryCode, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var existing = await _context.Set<SqlOSRecoveryCode>()
            .Where(x => x.UserId == userId && x.ConsumedAt == null && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var recoveryCode in existing)
        {
            recoveryCode.RevokedAt = DateTime.UtcNow;
        }

        var rawCodes = Enumerable.Range(0, _options.Mfa.Totp.RecoveryCodeCount)
            .Select(_ => FormatRecoveryCode(EncodeBase32(RandomNumberGenerator.GetBytes(8))[..10]))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var rawCode in rawCodes)
        {
            _context.Set<SqlOSRecoveryCode>().Add(new SqlOSRecoveryCode
            {
                Id = _cryptoService.GenerateId("mrc"),
                UserId = userId,
                CodeHash = _cryptoService.HashToken(NormalizeRecoveryCode(rawCode)),
                CreatedAt = DateTime.UtcNow
            });
        }

        return rawCodes;
    }

    private async Task EnsureUserOptInPolicyAsync(string userId, CancellationToken cancellationToken)
    {
        var userOverride = await _context.Set<SqlOSUserMfaPolicyOverride>()
            .FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (userOverride == null)
        {
            _context.Set<SqlOSUserMfaPolicyOverride>().Add(new SqlOSUserMfaPolicyOverride
            {
                UserId = userId,
                RequireMfa = true,
                UpdatedAt = DateTime.UtcNow
            });
            return;
        }

        if (userOverride.RequireMfa == null)
        {
            userOverride.RequireMfa = true;
            userOverride.UpdatedAt = DateTime.UtcNow;
        }
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

    private sealed record TotpEnrollmentPayload(
        string AuthenticatorId,
        TotpEnrollmentChallengeBinding? ChallengeBinding = null);

    private sealed record TotpEnrollmentChallengeBinding(
        string ChallengeTokenId,
        string UserId,
        string ClientApplicationId,
        string? OrganizationId,
        string Flow,
        string ClientId,
        string? AuthorizationRequestId,
        string? Resource);
}

internal sealed record SqlOSTotpChallengeEnrollmentVerification(
    SqlOSTemporaryToken ChallengeToken,
    SqlOSMfaChallengePayload ChallengePayload,
    SqlOSTotpEnrollmentVerifyResult Enrollment);
