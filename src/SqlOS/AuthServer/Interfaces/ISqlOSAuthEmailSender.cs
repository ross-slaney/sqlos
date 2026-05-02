namespace SqlOS.AuthServer.Interfaces;

public interface ISqlOSAuthEmailSender
{
    bool IsConfigured { get; }

    Task SendAsync(SqlOSAuthEmailMessage message, CancellationToken cancellationToken = default);
}

public sealed record SqlOSAuthEmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string? TextBody = null);
