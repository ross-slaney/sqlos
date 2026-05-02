using SqlOS.AuthServer.Interfaces;

namespace SqlOS.IntegrationTests.Infrastructure;

public sealed class TestAuthEmailSender : ISqlOSAuthEmailSender
{
    public bool IsConfigured { get; set; }

    public List<SqlOSAuthEmailMessage> Messages { get; } = [];

    public Task SendAsync(SqlOSAuthEmailMessage message, CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}
