using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SqlOS.Fga;
using SqlOS.Fga.Models;
using SqlOS.Fga.Paging;

namespace SqlOS.Fga.Configuration;

/// <summary>
/// Configures all SqlOSFga entities on a ModelBuilder.
/// Called by consumers via modelBuilder.ApplySqlOSFgaModel().
/// </summary>
public static class SqlOSFgaModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder, SqlOSFgaOptions options)
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

            // The lineage columns (Seq, Depth, Reach, and the ancestor at every level) are the database's: the
            // triggers keep them exact and only SqlOS's SQL routines read them, so the EF model leaves them out
            // and does not depend on the configured depth.
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
            entity.ToTable(tables.Grants, schema, t =>
            {
                t.ExcludeFromMigrations();

                // The page-index triggers (grant counts and direct indexes), declared for the same reason as
                // the lineage triggers above.
                foreach (var trigger in SqlOSFgaPageIndex.GrantTriggerNames(tables.Grants))
                {
                    t.HasTrigger(trigger);
                }
            });
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

        // AccessMatch (keyless - fn_IsResourceAccessible result): the grant that decides a point check.
        modelBuilder.Entity<SqlOSFgaAccessMatch>(entity =>
        {
            entity.HasNoKey();
            entity.ToView(null);
        });

        // PathNode (keyless): a resource on a target's path, read from its lineage to explain a decision.
        modelBuilder.Entity<SqlOSFgaPathNode>(entity =>
        {
            entity.HasNoKey();
            entity.ToView(null);
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

        // The functions the list filter composes into application queries, and how a query reads a row's
        // scope value. Mapped to static methods, so the filter carries no DbContext instance.
        SqlOSFgaFunctions.Register(modelBuilder, schema);
        SqlOSFgaScope.Register(modelBuilder);
    }

    /// <summary>A column the database fills and maintains: EF Core reads it and never includes it in a write.</summary>
    internal static void DatabaseOwned(PropertyBuilder property)
    {
        property.ValueGeneratedNever();
        property.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
