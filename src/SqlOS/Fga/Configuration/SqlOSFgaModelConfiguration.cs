using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Configuration;

/// <summary>
/// Configures all SqlOSFga entities on a ModelBuilder.
/// Called by consumers via modelBuilder.ApplySqlOSFgaModel().
/// </summary>
public static class SqlOSFgaModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder, SqlOSFgaOptions options, Type? contextType = null)
    {
        var schema = options.Schema;
        var tables = options.TableNames;
        modelBuilder.Model.SetAnnotation(
            SqlOSFgaHierarchyDepth.ModelAnnotationName,
            SqlOSFgaHierarchyDepth.Normalize(options.MaxResourceHierarchyDepth));

        // SubjectType
        modelBuilder.Entity<SqlOSFgaSubjectType>(entity =>
        {
            entity.ToTable(tables.SubjectTypes, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
        });

        // Subject
        modelBuilder.Entity<SqlOSFgaSubject>(entity =>
        {
            entity.ToTable(tables.Subjects, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.SubjectType)
                .WithMany(st => st.Subjects)
                .HasForeignKey(e => e.SubjectTypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // UserGroup
        modelBuilder.Entity<SqlOSFgaUserGroup>(entity =>
        {
            entity.ToTable(tables.UserGroups, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Subject)
                .WithOne(s => s.UserGroup)
                .HasForeignKey<SqlOSFgaUserGroup>(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // UserGroupMembership (SubjectId, not UserId)
        modelBuilder.Entity<SqlOSFgaUserGroupMembership>(entity =>
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
        modelBuilder.Entity<SqlOSFgaResourceType>(entity =>
        {
            entity.ToTable(tables.ResourceTypes, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);

            // The compact key of the type (schema v11), assigned by the database.
            DatabaseOwned(entity.Property<int?>(SqlOSFgaLineage.SeqColumn));
        });

        // Resource
        modelBuilder.Entity<SqlOSFgaResource>(entity =>
        {
            entity.ToTable(tables.Resources, schema, t =>
            {
                t.ExcludeFromMigrations();

                // The lineage triggers (SqlOSFgaFunctionInitializer). Declared so EF Core's SQL Server update
                // pipeline does not emit OUTPUT without INTO, which SQL Server rejects on a table with triggers.
                foreach (var trigger in SqlOSFgaLineage.TriggerNames(tables.Resources))
                {
                    t.HasTrigger(trigger);
                }
            });
            entity.HasKey(e => e.Id);

            // The lineage (schema v11): the database assigns Seq and the triggers keep Depth, Reach, and the
            // ancestor at every level exact, so EF Core never writes them. Queries read them: the filter
            // BuildFilterAsync returns compares a row's ancestor at the caller's level with the caller's grants.
            DatabaseOwned(entity.Property<long?>(SqlOSFgaLineage.SeqColumn));
            DatabaseOwned(entity.Property<short?>(SqlOSFgaLineage.DepthColumn));
            DatabaseOwned(entity.Property<short?>(SqlOSFgaLineage.ReachColumn));
            for (var level = 0; level < SqlOSFgaLineage.Levels(options); level++)
            {
                DatabaseOwned(entity.Property<long?>(SqlOSFgaLineage.AncestorColumn(level)));
            }
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
        modelBuilder.Entity<SqlOSFgaGrant>(entity =>
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
        modelBuilder.Entity<SqlOSFgaRole>(entity =>
        {
            entity.ToTable(tables.Roles, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
        });

        // Permission
        modelBuilder.Entity<SqlOSFgaPermission>(entity =>
        {
            entity.ToTable(tables.Permissions, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).HasMaxLength(SqlOSFgaPermission.MaxKeyLength);
            entity.HasIndex(e => e.Key).IsUnique();
            entity.HasOne(e => e.ResourceType)
                .WithMany(rt => rt.Permissions)
                .HasForeignKey(e => e.ResourceTypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // RolePermission (composite key)
        modelBuilder.Entity<SqlOSFgaRolePermission>(entity =>
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
        modelBuilder.Entity<SqlOSFgaUser>(entity =>
        {
            entity.ToTable(tables.Users, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Subject)
                .WithOne(s => s.User)
                .HasForeignKey<SqlOSFgaUser>(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Agent
        modelBuilder.Entity<SqlOSFgaAgent>(entity =>
        {
            entity.ToTable(tables.Agents, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Subject)
                .WithOne(s => s.Agent)
                .HasForeignKey<SqlOSFgaAgent>(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ServiceAccount
        modelBuilder.Entity<SqlOSFgaServiceAccount>(entity =>
        {
            entity.ToTable(tables.ServiceAccounts, schema, t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ClientId).HasMaxLength(450);
            entity.Property(e => e.ConfigurationOwner).HasMaxLength(32);
            entity.Property(e => e.ConfigurationSourceKey).HasMaxLength(200);
            entity.Property(e => e.ConfigurationFingerprint).HasMaxLength(128);
            entity.HasIndex(e => e.ClientId).IsUnique();
            entity.HasIndex(e => new { e.ConfigurationOwner, e.ConfigurationSourceKey }).IsUnique();
            entity.HasOne(e => e.Subject)
                .WithOne(s => s.ServiceAccount)
                .HasForeignKey<SqlOSFgaServiceAccount>(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // AccessibleResource (keyless - TVF result)
        modelBuilder.Entity<SqlOSFgaAccessibleResource>(entity =>
        {
            entity.HasNoKey();
            entity.ToView(null); // Not mapped to any table
        });

        // AccessRoot (keyless - fn_AccessRoots result): the resources a caller holds a usable grant on.
        modelBuilder.Entity<SqlOSFgaAccessRoot>(entity =>
        {
            entity.HasNoKey();
            entity.ToView(null);
        });

        // ActiveSubject (keyless - fn_ActiveSubjects result): the caller's live subjects.
        modelBuilder.Entity<SqlOSFgaActiveSubject>(entity =>
        {
            entity.HasNoKey();
            entity.ToView(null);
        });

        // Register TVF using the concrete DbContext type's MethodInfo.
        // EF Core requires the method to be on a DbContext subclass, not an interface.
        // When contextType is null (e.g., InMemory tests), TVF registration is skipped.
        if (contextType != null)
        {
            var tvfMethod = contextType.GetMethod(
                nameof(ISqlOSFgaDbContext.IsResourceAccessible),
                new[] { typeof(string), typeof(string), typeof(string) });

            if (tvfMethod != null)
            {
                modelBuilder.HasDbFunction(tvfMethod)
                    .HasName("fn_IsResourceAccessible")
                    .HasSchema(schema);
            }
        }
    }

    /// <summary>A column the database fills and maintains: EF Core reads it and never includes it in a write.</summary>
    internal static void DatabaseOwned(PropertyBuilder property)
    {
        property.ValueGeneratedNever();
        property.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
