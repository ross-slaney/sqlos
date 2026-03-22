using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Extensions;
using SqlOS.Todo.Api.Models;

namespace SqlOS.Todo.Api.Data;

public sealed class TodoSampleDbContext(DbContextOptions<TodoSampleDbContext> options)
    : DbContext(options), ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public DbSet<TodoItem> TodoItems => Set<TodoItem>();

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId,
        string subjectIds,
        string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseSqlOS(Database.IsRelational() ? GetType() : null);

        modelBuilder.Entity<TodoItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SqlOSUserId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => x.SqlOSUserId);
            entity.HasIndex(x => new { x.SqlOSUserId, x.IsCompleted });
        });
    }
}
