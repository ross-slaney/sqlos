using SqlOS.AuthServer.Interfaces;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;

namespace SqlOS.BehaviorLock.Host.Fakes;

/// <summary>
/// Stands in for both SqlOS email seams: auth emails (OTP codes, magic links, invitations,
/// password resets) and transactional template emails. Nothing leaves the process; every
/// message is recorded in the <see cref="EffectLog"/>.
/// </summary>
public sealed class CapturingEmailSender : ISqlOSAuthEmailSender, ISqlOSEmailSender
{
    private readonly EffectLog _effects;
    private int _deliveries;

    public CapturingEmailSender(EffectLog effects, bool isConfigured = true)
    {
        _effects = effects;
        IsConfigured = isConfigured;
    }

    public bool IsConfigured { get; }

    public Task SendAsync(SqlOSAuthEmailMessage message, CancellationToken cancellationToken = default)
    {
        _effects.Record(new EmailEffect("auth", message.To, message.Subject, message.TextBody, message.HtmlBody));
        return Task.CompletedTask;
    }

    public Task<SqlOSEmailProviderResult> SendAsync(SqlOSEmailMessage message, CancellationToken cancellationToken = default)
    {
        _effects.Record(new EmailEffect("transactional", message.To, message.Subject, message.TextBody, message.HtmlBody));
        var delivery = Interlocked.Increment(ref _deliveries);
        return Task.FromResult(new SqlOSEmailProviderResult($"behavior-lock-email-{delivery}"));
    }
}
