namespace SqlOS.AuthServer.Services;

/// <summary>
/// Raised when a SCIM connection or group mapping change would let mapped grants escape the
/// connection's grant boundary. <see cref="Error"/> is one of
/// <see cref="SqlOS.AuthServer.Contracts.SqlOSScimGrantBoundaryErrors"/>.
/// </summary>
public sealed class SqlOSScimGrantBoundaryException : InvalidOperationException
{
    public SqlOSScimGrantBoundaryException(
        string error,
        string message,
        string? grantBoundaryResourceId = null,
        string? resourceId = null)
        : base(message)
    {
        Error = error;
        GrantBoundaryResourceId = grantBoundaryResourceId;
        ResourceId = resourceId;
    }

    /// <summary>Stable machine-readable failure code.</summary>
    public string Error { get; }

    /// <summary>The boundary resource ID involved in the failure, when known.</summary>
    public string? GrantBoundaryResourceId { get; }

    /// <summary>The mapping target resource ID involved in the failure, when known.</summary>
    public string? ResourceId { get; }
}
