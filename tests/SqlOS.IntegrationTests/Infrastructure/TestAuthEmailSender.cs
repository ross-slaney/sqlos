using SqlOS.AuthServer.Interfaces;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;

namespace SqlOS.IntegrationTests.Infrastructure;

public sealed class TestAuthEmailSender : ISqlOSAuthEmailSender, ISqlOSEmailSender
{
    public bool IsConfigured { get; set; }

    public List<SqlOSAuthEmailMessage> Messages { get; } = [];

    public Task SendAsync(SqlOSAuthEmailMessage message, CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<SqlOSEmailProviderResult> SendAsync(
        SqlOSEmailMessage message,
        CancellationToken cancellationToken = default)
    {
        Messages.Add(new SqlOSAuthEmailMessage(message.To, message.Subject, message.HtmlBody, message.TextBody));
        return Task.FromResult(new SqlOSEmailProviderResult($"provider-{Messages.Count}"));
    }
}
