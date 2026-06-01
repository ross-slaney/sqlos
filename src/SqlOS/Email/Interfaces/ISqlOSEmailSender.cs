using SqlOS.Email.Contracts;

namespace SqlOS.Email.Interfaces;

public interface ISqlOSEmailSender
{
    bool IsConfigured { get; }

    Task<SqlOSEmailProviderResult> SendAsync(
        SqlOSEmailMessage message,
        CancellationToken cancellationToken = default);
}
