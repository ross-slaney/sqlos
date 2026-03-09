using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Example.Api.Models;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Example.Api.Data;

public sealed class ExampleAppDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public ExampleAppDbContext(DbContextOptions<ExampleAppDbContext> options) : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId,
        string subjectIds,
        string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseAuthServer();
        if (Database.IsRelational())
        {
            modelBuilder.UseFGA(GetType());
        }
        else
        {
            modelBuilder.UseFGA();
        }

        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.OrganizationId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.OrganizationId);
            entity.HasIndex(x => x.ResourceId);
        });
    }
}
