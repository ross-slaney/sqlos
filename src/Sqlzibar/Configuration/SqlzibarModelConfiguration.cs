using Microsoft.EntityFrameworkCore;
using Sqlzibar.Interfaces;
using Sqlzibar.Models;

namespace Sqlzibar.Configuration;

/// <summary>
/// Configures all Sqlzibar entities on a ModelBuilder.
/// Called by consumers via modelBuilder.ApplySqlzibarModel().
/// </summary>
public static class SqlzibarModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder, SqlzibarOptions options, Type? contextType = null)
    {
        var schema = options.Schema;
        var tables = options.TableNames;

        // SubjectType
        modelBuilder.Entity<SqlzibarSubjectType>(entity =>
        {
            entity.ToTable(tables.SubjectTypes, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
        });

        // Subject
        modelBuilder.Entity<SqlzibarSubject>(entity =>
        {
            entity.ToTable(tables.Subjects, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.SubjectType)
                .WithMany(st => st.Subjects)
                .HasForeignKey(e => e.SubjectTypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // UserGroup
        modelBuilder.Entity<SqlzibarUserGroup>(entity =>
        {
            entity.ToTable(tables.UserGroups, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Subject)
                .WithOne(s => s.UserGroup)
                .HasForeignKey<SqlzibarUserGroup>(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // UserGroupMembership (SubjectId, not UserId)
        modelBuilder.Entity<SqlzibarUserGroupMembership>(entity =>
        {
            entity.ToTable(tables.UserGroupMemberships, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => new { e.SubjectId, e.UserGroupId });
            entity.HasOne(e => e.Subject)
                .WithMany()
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.UserGroup)
                .WithMany(ug => ug.Memberships)
                .HasForeignKey(e => e.UserGroupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ResourceType
        modelBuilder.Entity<SqlzibarResourceType>(entity =>
        {
            entity.ToTable(tables.ResourceTypes, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
        });

        // Resource
        modelBuilder.Entity<SqlzibarResource>(entity =>
        {
            entity.ToTable(tables.Resources, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Parent)
                .WithMany(r => r.Children)
                .HasForeignKey(e => e.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ResourceType)
                .WithMany(rt => rt.Resources)
                .HasForeignKey(e => e.ResourceTypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Grant
        modelBuilder.Entity<SqlzibarGrant>(entity =>
        {
            entity.ToTable(tables.Grants, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Subject)
                .WithMany(s => s.Grants)
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Resource)
                .WithMany(r => r.Grants)
                .HasForeignKey(e => e.ResourceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Role)
                .WithMany(r => r.Grants)
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Role
        modelBuilder.Entity<SqlzibarRole>(entity =>
        {
            entity.ToTable(tables.Roles, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
        });

        // Permission
        modelBuilder.Entity<SqlzibarPermission>(entity =>
        {
            entity.ToTable(tables.Permissions, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.ResourceType)
                .WithMany(rt => rt.Permissions)
                .HasForeignKey(e => e.ResourceTypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // RolePermission (composite key)
        modelBuilder.Entity<SqlzibarRolePermission>(entity =>
        {
            entity.ToTable(tables.RolePermissions, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => new { e.RoleId, e.PermissionId });
            entity.HasOne(e => e.Role)
                .WithMany(r => r.RolePermissions)
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Permission)
                .WithMany(p => p.RolePermissions)
                .HasForeignKey(e => e.PermissionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // User
        modelBuilder.Entity<SqlzibarUser>(entity =>
        {
            entity.ToTable(tables.Users, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Subject)
                .WithOne(s => s.User)
                .HasForeignKey<SqlzibarUser>(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Agent
        modelBuilder.Entity<SqlzibarAgent>(entity =>
        {
            entity.ToTable(tables.Agents, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Subject)
                .WithOne(s => s.Agent)
                .HasForeignKey<SqlzibarAgent>(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ServiceAccount
        modelBuilder.Entity<SqlzibarServiceAccount>(entity =>
        {
            entity.ToTable(tables.ServiceAccounts, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Subject)
                .WithOne(s => s.ServiceAccount)
                .HasForeignKey<SqlzibarServiceAccount>(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // AccessibleResource (keyless - TVF result)
        modelBuilder.Entity<SqlzibarAccessibleResource>(entity =>
        {
            entity.HasNoKey();
            entity.ToView(null); // Not mapped to any table
        });

        // Register TVF using the concrete DbContext type's MethodInfo.
        // EF Core requires the method to be on a DbContext subclass, not an interface.
        // When contextType is null (e.g., InMemory tests), TVF registration is skipped.
        if (contextType != null)
        {
            var tvfMethod = contextType.GetMethod(
                nameof(ISqlzibarDbContext.IsResourceAccessible),
                new[] { typeof(string), typeof(string), typeof(string) });

            if (tvfMethod != null)
            {
                modelBuilder.HasDbFunction(tvfMethod)
                    .HasName("fn_IsResourceAccessible")
                    .HasSchema(schema);
            }
        }
    }
}
