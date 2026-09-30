using System.Collections.Concurrent;
using System.Security.Cryptography;
using SqlOS.AuthServer.Interfaces;

namespace SqlOS.BehaviorLock.Host.Fakes;

/// <summary>
/// Plays the Twilio Verify role for phone OTP: <see cref="StartAsync"/> "sends" a random six-digit
/// code and records it as an <see cref="SmsEffect"/>; <see cref="CheckAsync"/> approves the latest
/// code issued for that verification. It reports the same provider name and status vocabulary as
/// the production channel (<c>twilio_verify</c>, <c>pending</c>, <c>approved</c>).
/// </summary>
public sealed class FakeSmsDeliveryChannel : ISqlOSOtpDeliveryChannel
{
    private const string ProviderName = "twilio_verify";
    private readonly EffectLog _effects;
    private readonly ConcurrentDictionary<string, string> _codesByVerification = new(StringComparer.Ordinal);

    public FakeSmsDeliveryChannel(EffectLog effects)
    {
        _effects = effects;
    }

    public Task<SqlOSOtpDeliveryStartResult> StartAsync(
        string e164PhoneNumber,
        SqlOSOtpDeliveryContext context,
        CancellationToken cancellationToken = default)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var verificationId = $"VE{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
        _codesByVerification[verificationId] = code;
        _effects.Record(new SmsEffect("send", e164PhoneNumber, context.Purpose, code, Approved: null));
        return Task.FromResult(new SqlOSOtpDeliveryStartResult(true, ProviderName, verificationId, "pending"));
    }

    public Task<SqlOSOtpDeliveryCheckResult> CheckAsync(
        string e164PhoneNumber,
        string code,
        SqlOSOtpDeliveryContext context,
        CancellationToken cancellationToken = default)
    {
        var approved = context.ProviderChallengeId is { } verificationId
            && _codesByVerification.TryGetValue(verificationId, out var expected)
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected),
                System.Text.Encoding.UTF8.GetBytes(code ?? string.Empty));
        if (approved)
        {
            _codesByVerification.TryRemove(context.ProviderChallengeId!, out _);
        }

        _effects.Record(new SmsEffect("check", e164PhoneNumber, context.Purpose, code, approved));
        return Task.FromResult(new SqlOSOtpDeliveryCheckResult(
            approved,
            ProviderName,
            context.ProviderChallengeId,
            approved ? "approved" : "pending"));
    }
}
