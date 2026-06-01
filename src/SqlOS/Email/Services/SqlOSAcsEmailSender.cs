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
    private readonly string? _configurationError;
    private readonly string? _fromAddress;
    private readonly bool _hasConfigurationValues;

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
            _hasConfigurationValues = true;

            try
            {
                ValidateConnectionString(connectionString);
                _client = new EmailClient(connectionString);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                _configurationError = BuildConfigurationError(ex);
            }
        }
    }

    public bool IsConfigured => _hasConfigurationValues;

    public async Task<SqlOSEmailProviderResult> SendAsync(
        SqlOSEmailMessage message,
        CancellationToken cancellationToken = default)
    {
        if (_configurationError != null)
        {
            throw new InvalidOperationException(_configurationError);
        }

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

    private static void ValidateConnectionString(string connectionString)
    {
        var accessKey = GetConnectionStringValue(connectionString, "accesskey");
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            throw new ArgumentException("Missing accesskey in Azure Communication Services connection string.");
        }

        _ = Convert.FromBase64String(accessKey);
    }

    private static string? GetConnectionStringValue(string connectionString, string key)
    {
        foreach (var segment in connectionString.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = segment[..separatorIndex].Trim();
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                return segment[(separatorIndex + 1)..].Trim();
            }
        }

        return null;
    }

    private static string BuildConfigurationError(Exception exception)
        => exception switch
        {
            FormatException => "Azure Communication Services email connection string is invalid: accesskey must be a valid base64 value. Use the primaryConnectionString from az communication list-key, not a redacted or truncated value.",
            ArgumentException argumentException => $"Azure Communication Services email connection string is invalid: {argumentException.Message}",
            _ => "Azure Communication Services email connection string is invalid."
        };
}
