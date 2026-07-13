namespace SqlOS.AuthServer.Errors;

public enum SqlOSPublicAuthErrorSurface
{
    HostedPage,
    HeadlessApi,
    HeadlessView,
    OAuthAuthorize,
    OAuthToken,
    OidcCallback,
    SamlAcs,
    DynamicClientRegistration
}
