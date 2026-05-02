using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSAcsAuthEmailSender : ISqlOSAuthEmailSender
{
    private readonly EmailClient? _client;
    private readonly SqlOSEmailOtpOptions _options;

    public SqlOSAcsAuthEmailSender(IOptions<SqlOSAuthServerOptions> options)
    {
        _options = options.Value.EmailOtp;

        if (_options.IsConfigured)
        {
            _client = new EmailClient(_options.AzureCommunicationServicesConnectionString!);
        }
    }

    public bool IsConfigured => _options.IsConfigured && _client != null;

    public async Task SendAsync(SqlOSAuthEmailMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Email OTP delivery is not configured.");
        }

        var content = new EmailContent(message.Subject)
        {
            Html = message.HtmlBody,
            PlainText = message.TextBody
        };

        var email = new Azure.Communication.Email.EmailMessage(
            senderAddress: _options.FromAddress!,
            recipients: new EmailRecipients([new EmailAddress(message.To)]),
            content: content);

        await _client!.SendAsync(WaitUntil.Started, email, cancellationToken);
    }
}
