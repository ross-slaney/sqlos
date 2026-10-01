using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Processes;

/// <summary>
/// Seeds what every FGA store holds: the four built-in subject types, the root resource type and
/// the configured root resource. Existing records are left as they are.
/// </summary>
internal sealed class SeedFgaCore(ISqlOSFgaDbContext context, SqlOSFgaOptions options)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await SubjectTypeAsync("user", "User", "A human user", cancellationToken);
        await SubjectTypeAsync("group", "Group", "A user group", cancellationToken);
        await SubjectTypeAsync("service_account", "Service Account", "An automated service account", cancellationToken);
        await SubjectTypeAsync("agent", "Agent", "An automated agent (job, worker, AI)", cancellationToken);

        if (await context.Set<SqlOSFgaResourceType>().FindAsync(["root"], cancellationToken) == null)
        {
            context.Set<SqlOSFgaResourceType>().Add(SqlOSFgaResourceType.Define("root", "Root", "The root resource type"));
        }

        if (await context.Set<SqlOSFgaResource>().FindAsync([options.RootResourceId], cancellationToken) == null)
        {
            context.Set<SqlOSFgaResource>().Add(SqlOSFgaResource.Create(
                options.RootResourceId,
                options.RootResourceName,
                "root",
                description: null,
                SqlOSFgaAncestry.None,
                SqlOSFgaWrites.Now(context)));
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task SubjectTypeAsync(string id, string name, string description, CancellationToken cancellationToken)
    {
        if (await context.Set<SqlOSFgaSubjectType>().FindAsync([id], cancellationToken) == null)
        {
            context.Set<SqlOSFgaSubjectType>().Add(SqlOSFgaSubjectType.Define(id, name, description));
        }
    }
}
