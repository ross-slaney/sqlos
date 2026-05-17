namespace SqlOS.AuthServer.Services;

internal static class SqlOSSignupJoinPolicy
{
    public const string UnauthorizedOrganizationJoinMessage =
        "Joining an existing organization requires an invitation or approved join policy.";

    public static void RejectUnauthorizedOrganizationJoin(string? organizationId)
    {
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            throw new InvalidOperationException(UnauthorizedOrganizationJoinMessage);
        }
    }
}
