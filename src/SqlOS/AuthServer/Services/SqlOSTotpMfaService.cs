using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Authenticator apps (TOTP) and recovery codes: the host's account-settings API over the identity
/// processes (<c>SqlOS.AuthServer.Processes.Identity</c>), and the authenticator channel those
/// processes use: the TOTP options, secrets and their protection, code matching (RFC 6238),
/// provisioning URIs and QR codes, recovery codes, and the MFA policy.
/// </summary>
/// <remarks>
/// Enrolling, confirming and checking a factor are the processes' (<see cref="StartTotpEnrollment"/>,
/// <see cref="VerifyTotpEnrollment"/>, <see cref="SecondFactors"/>); the public methods below keep
/// their 7.x signatures and delegate to them. Code matching takes the time it matches at: a process
/// reads its clock once.
/// </remarks>
public sealed class SqlOSTotpMfaService
{
    public const string EnrollmentPurpose = SqlOSTemporaryTokenKinds.Purposes.TotpEnrollment;

    private static readonly char[] Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSMfaPolicyService _policyService;
    private readonly IOptions<SqlOSAuthServerOptions> _authOptions;
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
        _authOptions = options;
        _options = options.Value;
    }

    /// <summary>The TOTP options: algorithm, digits, period, skew, lifetimes and limits.</summary>
    internal SqlOSTotpMfaOptions Options => _options.Mfa.Totp;

    /// <summary>The MFA policy an enrollment and its recovery codes follow.</summary>
    internal SqlOSMfaPolicyService Policy => _policyService;

    /// <summary>The parameters a new authenticator is enrolled with.</summary>
    internal TotpParameters Parameters => new(Options.Algorithm, Options.Digits, Options.PeriodSeconds);

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

    /// <summary>Starts the account's own authenticator enrollment, under the policy of <paramref name="organizationId"/>.</summary>
    public async Task<SqlOSTotpEnrollmentStartResult> StartEnrollmentAsync(
        string userId,
        string? organizationId = null,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = await new StartTotpEnrollment(_context, Admin(), this, new SqlOSAuditRecorder(_context), _cryptoService.Clock).ExecuteAsync(
            new StartTotpEnrollmentCommand(
                new TotpEnrollmentTarget.Account(userId, organizationId),
                displayName,
                SqlOSRequestContext.System),
            cancellationToken);
        return outcome switch
        {
            TotpEnrollmentStartOutcome.Started started => started.Result,
            TotpEnrollmentStartOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown enrollment outcome '{outcome.GetType().Name}'.")
        };
    }

    /// <summary>Confirms the account's own enrollment with the authenticator's first code.</summary>
    public async Task<SqlOSTotpEnrollmentVerifyResult> VerifyEnrollmentAsync(
        SqlOSTotpEnrollmentVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        // The account's own enrollment completes no login, so it needs no hub.
        var admin = Admin();
        var outcome = await new VerifyTotpEnrollment(_context, admin, this, new SqlOSHttpLoginCompletion(null, admin, _options), _cryptoService.Clock).ExecuteAsync(
            new VerifyTotpEnrollmentCommand(request.EnrollmentToken, request.Code, Challenge: null, SqlOSRequestContext.System),
            cancellationToken);
        return outcome switch
        {
            TotpEnrollmentVerifyOutcome.Confirmed confirmed => confirmed.Result,
            TotpEnrollmentVerifyOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown enrollment outcome '{outcome.GetType().Name}'.")
        };
    }

    /// <summary>
    /// Checks a second factor of the account, an authenticator code or a recovery code, and spends
    /// it; returns the factor (<c>totp</c> or <c>recovery_code</c>).
    /// </summary>
    public async Task<string> VerifySecondFactorCodeAsync(
        string userId,
        string code,
        CancellationToken cancellationToken = default)
    {
        var check = await new VerifySecondFactor(new SecondFactors(_context, this), _cryptoService.Clock).ExecuteAsync(
            new VerifySecondFactorCommand(userId, code),
            cancellationToken);
        return check switch
        {
            SecondFactorCheck.Verified verified => verified.Factor,
            SecondFactorCheck.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown second-factor check '{check.GetType().Name}'.")
        };
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

    /// <summary>A new random secret, base32 as authenticator apps take it.</summary>
    internal string NewSecret() => EncodeBase32(RandomNumberGenerator.GetBytes(Options.SecretBytes));

    /// <summary>Protects a secret for storage.</summary>
    internal string Protect(string secret) => _cryptoService.ProtectSecret(secret);

    /// <summary>Reads a stored secret back.</summary>
    internal string Unprotect(string protectedSecret) => _cryptoService.UnprotectSecret(protectedSecret);

    /// <summary>New raw recovery codes (<c>XXXXX-XXXXX</c>), distinct, as many as the options ask; shown once.</summary>
    internal string[] NewRecoveryCodes()
        => Enumerable.Range(0, Options.RecoveryCodeCount)
            .Select(_ => FormatRecoveryCode(EncodeBase32(RandomNumberGenerator.GetBytes(8))[..10]))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// True when <paramref name="code"/> is the code of <paramref name="secret"/> for a time step
    /// within the allowed skew of <paramref name="now"/>; <paramref name="matchedStep"/> is that step.
    /// </summary>
    internal bool TryMatchCode(
        string secret,
        string code,
        int periodSeconds,
        int digits,
        DateTime now,
        out long matchedStep)
    {
        matchedStep = 0;
        var normalizedCode = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalizedCode.Length != digits)
        {
            return false;
        }

        var secretBytes = DecodeBase32(secret);
        var nowStep = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUnixTimeSeconds() / periodSeconds;
        for (var offset = -Options.AllowedClockSkewSteps; offset <= Options.AllowedClockSkewSteps; offset++)
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

    /// <summary>The operator service the processes' first-party checks audit through; the account's own enrollment never uses it.</summary>
    private SqlOSAdminService Admin() => new(_context, _authOptions, _cryptoService);

    /// <summary>The <c>otpauth://</c> URI that adds <paramref name="secret"/> to an authenticator app for <paramref name="user"/>.</summary>
    internal string BuildProvisioningUri(SqlOSUser user, string secret)
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

    /// <summary>The QR code of <paramref name="value"/> as an SVG data URL.</summary>
    internal static string BuildQrCodeDataUrl(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        var svg = new SvgQRCode(data).GetGraphic(5);
        return $"data:image/svg+xml;charset=utf-8,{Uri.EscapeDataString(svg)}";
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

    /// <summary>A recovery code as stored and compared: its letters and digits, upper case.</summary>
    internal static string NormalizeRecoveryCode(string code)
        => new((code ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string FormatRecoveryCode(string code)
        => $"{code[..5]}-{code[5..10]}";
}
