using SqlOS.AuthServer.Interfaces;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;

namespace SqlOS.Email.Services;

public sealed class SqlOSDefaultEmailSender : ISqlOSEmailSender
{
    private readonly SqlOSAcsEmailSender _acsEmailSender;
    private readonly ISqlOSAuthEmailSender _authEmailSender;

    public SqlOSDefaultEmailSender(
        SqlOSAcsEmailSender acsEmailSender,
        ISqlOSAuthEmailSender authEmailSender)
    {
        _acsEmailSender = acsEmailSender;
        _authEmailSender = authEmailSender;
    }

    public bool IsConfigured => _acsEmailSender.IsConfigured || _authEmailSender.IsConfigured;

    public async Task<SqlOSEmailProviderResult> SendAsync(
        SqlOSEmailMessage message,
        CancellationToken cancellationToken = default)
    {
        if (_acsEmailSender.IsConfigured)
        {
            return await _acsEmailSender.SendAsync(message, cancellationToken);
        }

        if (_authEmailSender.IsConfigured)
        {
            await _authEmailSender.SendAsync(
                new SqlOSAuthEmailMessage(message.To, message.Subject, message.HtmlBody, message.TextBody),
                cancellationToken);
            return new SqlOSEmailProviderResult(null);
        }

        throw new InvalidOperationException("Transactional email delivery is not configured.");
    }
}
