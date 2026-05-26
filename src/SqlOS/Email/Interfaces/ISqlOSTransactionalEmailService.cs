using SqlOS.Email.Contracts;

namespace SqlOS.Email.Interfaces;

public interface ISqlOSTransactionalEmailService
{
    Task<SqlOSRenderedEmailPreview> PreviewAsync(
        string templateKey,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken = default);

    Task<SqlOSSendEmailResult> SendAsync(
        SqlOSSendEmailRequest request,
        CancellationToken cancellationToken = default);
}
