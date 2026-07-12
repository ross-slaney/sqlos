namespace SqlOS.AuthServer.Configuration;

/// <summary>
/// Configures the built-in ASP.NET Core Data Protection signing-key custody provider.
/// Custom <c>ISqlOSSigningKeyCustody</c> implementations ignore these settings.
/// </summary>
public sealed class SqlOSSigningKeyCustodyOptions
{
    /// <summary>
    /// Confirms that the host has deliberately configured a durable Data Protection key ring outside
    /// the SqlOS application database and, when the application has multiple instances, that every
    /// instance uses the same ring. The built-in custody provider fails closed unless this is true.
    /// </summary>
    public bool DataProtectionKeyRingIsPersistedAndShared { get; set; }
}
