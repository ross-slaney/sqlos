using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Options;
using SqlOS.Email.Configuration;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;

namespace SqlOS.Email.Services;

public sealed class SqlOSAcsEmailSender : ISqlOSEmailSender
{
    private readonly EmailClient? _client;
    private readonly SqlOSEmailOptions _options;

    public SqlOSAcsEmailSender(IOptions<SqlOSEmailOptions> options)
    {
        _options = options.Value;

        if (_options.IsConfigured)
        {
            _client = new EmailClient(_options.AzureCommunicationServicesConnectionString!);
        }
    }

    public bool IsConfigured => _options.IsConfigured && _client != null;

    public async Task<SqlOSEmailProviderResult> SendAsync(
        SqlOSEmailMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Transactional email delivery is not configured.");
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

        var operation = await _client!.SendAsync(WaitUntil.Started, email, cancellationToken);
        return new SqlOSEmailProviderResult(operation.Id);
    }
}
