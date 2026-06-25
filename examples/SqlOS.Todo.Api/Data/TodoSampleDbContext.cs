using Microsoft.EntityFrameworkCore;
using SqlOS;
using SqlOS.Todo.Api.Models;

namespace SqlOS.Todo.Api.Data;

public sealed class TodoSampleDbContext(DbContextOptions<TodoSampleDbContext> options)
    : SqlOSDbContext<TodoSampleDbContext>(options)
{
    public DbSet<TodoItem> TodoItems => Set<TodoItem>();

    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TodoItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ResourceId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.SqlOSUserId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => x.ResourceId).IsUnique();
            entity.HasIndex(x => x.SqlOSUserId);
            entity.HasIndex(x => new { x.SqlOSUserId, x.IsCompleted });
            entity.Ignore(x => x.ResourceTypeId);
            entity.Ignore(x => x.ResourceName);
            entity.Ignore(x => x.ParentResourceId);
            entity.Ignore(x => x.ResourceDescription);
            entity.Ignore(x => x.ResourceIsActive);
        });
    }
}
