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
            entity.Property(x => x.OwnerSubjectId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => x.ResourceId).IsUnique();
            entity.HasIndex(x => x.OwnerSubjectId);
            entity.HasIndex(x => new { x.OwnerSubjectId, x.IsCompleted });

            // The order the list pages in (newest first, the key as the tiebreaker). SqlOS mirrors it per level
            // of the resource tree, so an authorized page in this order is one index seek per place the caller
            // is granted.
            entity.HasIndex(x => new { x.CreatedAt, x.Id }).HasDatabaseName("IX_TodoItems_CreatedAt");
        });
    }
}
