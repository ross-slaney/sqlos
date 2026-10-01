using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Extensions;

/// <summary>
/// Convenience extensions for the common SqlOS application path.
/// </summary>
/// <remarks>
/// The FGA helpers are the documented host API of the FGA write model: they change resources,
/// subjects and grants only through their aggregates, track the changes, and leave the save to the
/// caller.
/// </remarks>
public static partial class SqlOSErgonomicsExtensions
{
    /// <summary>
    /// Checks whether a subject has a permission on a resource and returns only the allow/deny decision.
    /// </summary>
    /// <param name="authService">The SqlOS FGA authorization service.</param>
    /// <param name="subjectId">The subject to authorize.</param>
    /// <param name="permissionKey">The permission key to require.</param>
    /// <param name="resourceId">The target resource identifier.</param>
    /// <returns><see langword="true"/> when access is allowed; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authService"/> is <see langword="null"/>.</exception>
    public static async Task<bool> Allows(
        this ISqlOSFgaAuthService authService,
        string subjectId,
        string permissionKey,
        string resourceId)
    {
        ArgumentNullException.ThrowIfNull(authService);

        var result = await authService.CheckAccessAsync(subjectId, permissionKey, resourceId);
        return result.Allowed;
    }

    private static async Task<SqlOSFgaSubject?> FindSubjectAsync(
        ISqlOSFgaDbContext context,
        string subjectId,
        CancellationToken cancellationToken)
    {
        var subjects = context.Set<SqlOSFgaSubject>();
        return subjects.Local.FirstOrDefault(x => x.Id == subjectId)
            ?? await subjects.FirstOrDefaultAsync(x => x.Id == subjectId, cancellationToken);
    }
}
