namespace SqlOS.AuthServer.Services;

internal sealed class SqlOSSsoPortalSessionUnavailableException
    : InvalidOperationException
{
    internal const string PublicMessage = "Portal session is invalid or expired.";

    internal SqlOSSsoPortalSessionUnavailableException()
        : base(PublicMessage)
    {
    }
}
