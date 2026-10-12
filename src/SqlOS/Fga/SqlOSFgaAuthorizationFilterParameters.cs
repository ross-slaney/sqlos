namespace SqlOS.Fga;

/// <summary>
/// The two values an authorization filter passes to <c>fn_IsResourceAccessible</c>.
/// <c>BuildFilterAsync</c> puts them in the filter expression as fields of one instance, and
/// <c>ToPageAsync</c> reads them back from the same fields, so the two cannot drift.
/// </summary>
/// <remarks>
/// They stay fields on a constant so EF Core sends them as query parameters, not literals.
/// </remarks>
internal sealed class SqlOSFgaAuthorizationFilterParameters
{
    public SqlOSFgaAuthorizationFilterParameters(string subjectIds, string permissionId)
    {
        SubjectIds = subjectIds;
        PermissionId = permissionId;
    }

    public readonly string SubjectIds;
    public readonly string PermissionId;
}
