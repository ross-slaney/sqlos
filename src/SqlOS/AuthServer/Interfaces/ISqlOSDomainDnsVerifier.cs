namespace SqlOS.AuthServer.Interfaces;

public interface ISqlOSDomainDnsVerifier
{
    Task<bool> HasTxtRecordValueAsync(
        string recordName,
        string expectedValue,
        CancellationToken cancellationToken = default);
}
