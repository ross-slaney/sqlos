namespace SqlOS.Security;

/// <summary>
/// Marker applied only by SqlOS HTML renderers. <see cref="SqlOSBrowserSecurityHeaders"/>
/// substitutes the concrete nonce into this attribute and never discovers tags in assembled HTML.
/// </summary>
internal static class SqlOSCspNonce
{
    public const string Token = "__SQLOS_CSP_NONCE__";
    public const string Attribute = "nonce=\"" + Token + "\"";
}
