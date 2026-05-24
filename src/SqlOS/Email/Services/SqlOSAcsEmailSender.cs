using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.Email.Configuration;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;

namespace SqlOS.Email.Services;

public sealed class SqlOSAcsEmailSender : ISqlOSEmailSender
{
    private readonly EmailClient? _client;
    private readonly string? _fromAddress;

    public SqlOSAcsEmailSender(
        IOptions<SqlOSEmailOptions> options,
        IOptions<SqlOSAuthServerOptions>? authOptions = null)
    {
        var emailOptions = options.Value;
        var authEmailOptions = authOptions?.Value.EmailOtp;
        var connectionString = emailOptions.AzureCommunicationServicesConnectionString;
        _fromAddress = emailOptions.FromAddress;

        if (string.IsNullOrWhiteSpace(connectionString) && authEmailOptions?.IsConfigured == true)
        {
            connectionString = authEmailOptions.AzureCommunicationServicesConnectionString;
            _fromAddress = authEmailOptions.FromAddress;
        }

        if (!string.IsNullOrWhiteSpace(connectionString) && !string.IsNullOrWhiteSpace(_fromAddress))
        {
            _client = new EmailClient(connectionString);
        }
    }

    public bool IsConfigured => _client != null && !string.IsNullOrWhiteSpace(_fromAddress);

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
            senderAddress: _fromAddress!,
            recipients: new EmailRecipients([new EmailAddress(message.To)]),
            content: content);

        var operation = await _client!.SendAsync(WaitUntil.Started, email, cancellationToken);
        return new SqlOSEmailProviderResult(operation.Id);
    }
}
