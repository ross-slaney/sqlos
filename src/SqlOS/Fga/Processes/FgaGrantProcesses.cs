using Microsoft.EntityFrameworkCore;
using SqlOS.Domain;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Processes;

/// <summary>An operator grants a role to a subject on a resource (the FGA dashboard API), in one save.</summary>
internal sealed class GrantFgaRole(ISqlOSFgaDbContext context)
{
    public async Task<GrantFgaRoleOutcome> ExecuteAsync(GrantFgaRoleCommand command, GrantAuthority authority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var subject = await context.Set<SqlOSFgaSubject>().FirstOrDefaultAsync(s => s.Id == command.SubjectId, cancellationToken);
        if (subject == null)
        {
            return new GrantFgaRoleOutcome.Refused(GrantRefusal.SubjectNotFound);
        }

        var role = await context.Set<SqlOSFgaRole>().FirstOrDefaultAsync(r => r.Id == command.RoleId, cancellationToken);
        if (role == null)
        {
            return new GrantFgaRoleOutcome.Refused(GrantRefusal.RoleNotFound);
        }

        if (!await context.Set<SqlOSFgaResource>().AnyAsync(r => r.Id == command.ResourceId, cancellationToken))
        {
            return new GrantFgaRoleOutcome.Refused(GrantRefusal.ResourceNotFound);
        }

        var window = TimeWindow.Between(command.EffectiveFrom, command.EffectiveTo);
        var existing = await SqlOSFgaGrants.FindEquivalentAsync((DbContext)context, subject.Id, role.Id, command.ResourceId, window, cancellationToken);
        if (existing != null)
        {
            return new GrantFgaRoleOutcome.Refused(GrantRefusal.Duplicate, existing.Id);
        }

        var grant = SqlOSFgaGrant.Create(SqlOSIds.New("grant"), subject, role, command.ResourceId, window, null, authority, SqlOSFgaWrites.Now(context));
        context.Set<SqlOSFgaGrant>().Add(grant);
        await context.SaveChangesAsync(cancellationToken);
        return new GrantFgaRoleOutcome.Granted(grant);
    }
}

internal sealed record GrantFgaRoleCommand(string SubjectId, string RoleId, string ResourceId, DateTime? EffectiveFrom, DateTime? EffectiveTo);

internal abstract record GrantFgaRoleOutcome
{
    private GrantFgaRoleOutcome()
    {
    }

    public sealed record Granted(SqlOSFgaGrant Grant) : GrantFgaRoleOutcome;

    /// <summary>Nothing was written; <paramref name="ExistingGrantId"/> names the equivalent grant of a duplicate.</summary>
    public sealed record Refused(GrantRefusal Reason, string? ExistingGrantId = null) : GrantFgaRoleOutcome;
}

internal enum GrantRefusal
{
    SubjectNotFound,
    RoleNotFound,
    ResourceNotFound,
    Duplicate
}

/// <summary>An operator revokes a grant by its identifier (the FGA dashboard API).</summary>
internal sealed class RevokeFgaGrant(ISqlOSFgaDbContext context)
{
    /// <summary>Returns whether the grant existed.</summary>
    public async Task<bool> ExecuteAsync(string grantId, FgaActor actor, CancellationToken cancellationToken)
    {
        var grant = await context.Set<SqlOSFgaGrant>().FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken);
        if (grant == null)
        {
            return false;
        }

        SqlOSFgaGrants.Revoke((DbContext)context, [grant], actor);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
