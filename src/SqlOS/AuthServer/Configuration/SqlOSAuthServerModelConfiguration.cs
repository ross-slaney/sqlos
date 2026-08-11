using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Configuration;

public static class SqlOSAuthServerModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder, SqlOSAuthServerOptions options)
    {
        var schema = options.Schema;

        modelBuilder.Entity<SqlOSOrganization>(entity =>
        {
            entity.ToTable("SqlOSOrganizations", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasIndex(x => x.PrimaryDomain).IsUnique().HasFilter("[PrimaryDomain] IS NOT NULL");
            entity.Property(x => x.Slug).HasMaxLength(120);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.PrimaryDomain).HasMaxLength(320);
        });

        modelBuilder.Entity<SqlOSUser>(entity =>
        {
            entity.ToTable("SqlOSUsers", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.DefaultEmail).HasMaxLength(320);
        });

        modelBuilder.Entity<SqlOSUserEmail>(entity =>
        {
            entity.ToTable("SqlOSUserEmails", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.NormalizedEmail).HasMaxLength(320);
            entity.HasOne(x => x.User)
                .WithMany(x => x.Emails)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSUserPhoneNumber>(entity =>
        {
            entity.ToTable("SqlOSUserPhoneNumbers", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.PhoneNumberHash)
                .IsUnique()
                .HasFilter("[RemovedAt] IS NULL");
            entity.HasIndex(x => new { x.UserId, x.RemovedAt });
            entity.Property(x => x.PhoneNumber).HasMaxLength(32);
            entity.Property(x => x.PhoneNumberHash).HasMaxLength(128);
            entity.Property(x => x.DisplayValueEncrypted).HasMaxLength(2048);
            entity.Property(x => x.RemovalReason).HasMaxLength(120);
            entity.HasOne(x => x.User)
                .WithMany(x => x.PhoneNumbers)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSCredential>(entity =>
        {
            entity.ToTable("SqlOSCredentials", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(50);
            entity.HasOne(x => x.User)
                .WithMany(x => x.Credentials)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSPasswordLoginBucket>(entity =>
        {
            entity.ToTable("SqlOSPasswordLoginBuckets", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Scope, x.BucketKey }).IsUnique();
            entity.HasIndex(x => new { x.NormalizedEmail, x.UpdatedAt });
            entity.HasIndex(x => new { x.UserId, x.UpdatedAt });
            entity.HasIndex(x => new { x.IpAddress, x.UpdatedAt });
            entity.HasIndex(x => new { x.ClientKey, x.UpdatedAt });
            entity.HasIndex(x => x.LockedUntil);
            entity.Property(x => x.Scope).HasMaxLength(40);
            entity.Property(x => x.BucketKey).HasMaxLength(512);
            entity.Property(x => x.NormalizedEmail).HasMaxLength(320);
            entity.Property(x => x.UserId).HasMaxLength(64);
            entity.Property(x => x.ClientKey).HasMaxLength(850);
            entity.Property(x => x.IpAddress).HasMaxLength(128);
            entity.Property(x => x.UserAgentHash).HasMaxLength(128);
            entity.Property(x => x.LockoutReason).HasMaxLength(120);
            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSMfaAttemptBucket>(entity =>
        {
            entity.ToTable("SqlOSMfaAttemptBuckets", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Scope, x.BucketKey }).IsUnique();
            entity.HasIndex(x => x.LastAttemptAt);
            entity.Property(x => x.Scope).HasMaxLength(40);
            entity.Property(x => x.BucketKey).HasMaxLength(128);
        });

        modelBuilder.Entity<SqlOSMembership>(entity =>
        {
            entity.ToTable("SqlOSMemberships", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => new { x.OrganizationId, x.UserId });
            entity.Property(x => x.Role).HasMaxLength(50);
            entity.HasOne(x => x.Organization)
                .WithMany(x => x.Memberships)
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.User)
                .WithMany(x => x.Memberships)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSInvitation>(entity =>
        {
            entity.ToTable("SqlOSInvitations", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.NormalizedEmail, x.CreatedAt });
            entity.HasIndex(x => new { x.NormalizedEmail, x.CreatedAt });
            entity.HasIndex(x => new { x.IpAddress, x.CreatedAt });
            entity.HasIndex(x => new { x.InvitedByUserId, x.CreatedAt });
            entity.HasIndex(x => x.ExpiresAt);
            entity.Property(x => x.InvitedEmail).HasMaxLength(320);
            entity.Property(x => x.NormalizedEmail).HasMaxLength(320);
            entity.Property(x => x.Role).HasMaxLength(50);
            entity.Property(x => x.TokenHash).HasMaxLength(128);
            entity.Property(x => x.RedirectUri).HasMaxLength(2048);
            entity.Property(x => x.Scope).HasMaxLength(1000);
            entity.Property(x => x.Resource).HasMaxLength(2048);
            entity.Property(x => x.CustomFieldsJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.LastSendError).HasMaxLength(500);
            entity.Property(x => x.RevokedReason).HasMaxLength(120);
            entity.Property(x => x.IpAddress).HasMaxLength(128);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.Property(x => x.AcceptedAt).IsConcurrencyToken();
            entity.HasOne(x => x.Organization)
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.InvitedByUser)
                .WithMany()
                .HasForeignKey(x => x.InvitedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AcceptedByUser)
                .WithMany()
                .HasForeignKey(x => x.AcceptedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ClientApplication)
                .WithMany()
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSSsoConnection>(entity =>
        {
            entity.ToTable("SqlOSSsoConnections", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.IdentityProviderEntityId).HasMaxLength(500);
            entity.Property(x => x.SingleSignOnUrl).HasMaxLength(2000);
            entity.Property(x => x.AcceptedAuthnContextClassRefsJson).HasDefaultValue("[]");
            entity.Property(x => x.ConfigurationOwner).HasMaxLength(40);
            entity.Property(x => x.ConfigurationSourceKey).HasMaxLength(160);
            entity.Property(x => x.ConfigurationFingerprint).HasMaxLength(64);
            entity.HasIndex(x => new { x.ConfigurationOwner, x.ConfigurationSourceKey })
                .IsUnique()
                .HasFilter("[ConfigurationSourceKey] IS NOT NULL");
            entity.HasOne(x => x.Organization)
                .WithMany(x => x.SsoConnections)
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSScimConnection>(entity =>
        {
            entity.ToTable("SqlOSScimConnections", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OrganizationId, x.SeedKey })
                .IsUnique()
                .HasFilter("[SeedKey] IS NOT NULL");
            entity.HasIndex(x => x.TokenHash)
                .IsUnique()
                .HasFilter("[TokenHash] IS NOT NULL");
            entity.HasIndex(x => new { x.OrganizationId, x.IsEnabled });
            entity.HasIndex(x => x.OrganizationId)
                .IsUnique()
                .HasDatabaseName("UX_SqlOSScimConnections_OneEnabledPerOrganization")
                .HasFilter("[IsEnabled] = 1");
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.SeedKey).HasMaxLength(160);
            entity.Property(x => x.TokenHash).HasMaxLength(128);
            entity.Property(x => x.TokenPrefix).HasMaxLength(24);
            entity.Property(x => x.Source).HasMaxLength(40);
            entity.Property(x => x.ConfigurationOwner).HasMaxLength(40);
            entity.Property(x => x.ConfigurationSourceKey).HasMaxLength(160);
            entity.Property(x => x.ConfigurationFingerprint).HasMaxLength(64);
            entity.HasIndex(x => new { x.OrganizationId, x.ConfigurationOwner, x.ConfigurationSourceKey })
                .IsUnique()
                .HasFilter("[ConfigurationSourceKey] IS NOT NULL");
            entity.HasOne(x => x.Organization)
                .WithMany(x => x.ScimConnections)
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSScimExternalId>(entity =>
        {
            entity.ToTable("SqlOSScimExternalIds", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConnectionId, x.ResourceType, x.ExternalId })
                .IsUnique()
                .HasFilter("[ExternalId] IS NOT NULL");
            entity.HasIndex(x => new { x.ConnectionId, x.ResourceType, x.EntityId }).IsUnique();
            entity.HasIndex(x => new { x.ConnectionId, x.ResourceType, x.UserName })
                .IsUnique()
                .HasFilter("[UserName] IS NOT NULL");
            entity.Property(x => x.ResourceType).HasMaxLength(20);
            entity.Property(x => x.ExternalId).HasMaxLength(450).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.EntityId).HasMaxLength(128);
            entity.Property(x => x.FgaSubjectId).HasMaxLength(128);
            entity.Property(x => x.UserName).HasMaxLength(450).UseCollation("Latin1_General_100_CI_AS");
            entity.Property(x => x.PrimaryEmail).HasMaxLength(320).UseCollation("Latin1_General_100_CI_AS");
            entity.Property(x => x.DisplayName).HasMaxLength(300).UseCollation("Latin1_General_100_CI_AS");
            entity.Property(x => x.FormattedName).HasMaxLength(300);
            entity.Property(x => x.GivenName).HasMaxLength(150);
            entity.Property(x => x.FamilyName).HasMaxLength(150);
            entity.HasOne(x => x.Connection)
                .WithMany(x => x.ExternalIds)
                .HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSScimGroupMapping>(entity =>
        {
            entity.ToTable("SqlOSScimGroupMappings", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConnectionId, x.SourceKey })
                .IsUnique()
                .HasFilter("[SourceKey] IS NOT NULL");
            entity.HasIndex(x => new { x.ConnectionId, x.IsEnabled });
            entity.Property(x => x.SourceKey).HasMaxLength(300);
            entity.Property(x => x.Source).HasMaxLength(40);
            entity.Property(x => x.MatchType).HasMaxLength(40);
            entity.Property(x => x.GroupDisplayName).HasMaxLength(300);
            entity.Property(x => x.GroupExternalId).HasMaxLength(450);
            entity.Property(x => x.GroupPattern).HasMaxLength(500);
            entity.Property(x => x.RoleKey).HasMaxLength(120);
            entity.Property(x => x.ResourceId).HasMaxLength(256);
            entity.Property(x => x.ResourceIdTemplate).HasMaxLength(500);
            entity.Property(x => x.Description).HasMaxLength(500);
            entity.HasOne(x => x.Connection)
                .WithMany(x => x.GroupMappings)
                .HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSScimManagedGrant>(entity =>
        {
            entity.ToTable("SqlOSScimManagedGrants", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConnectionId, x.MappingId, x.GroupExternalId, x.ResourceId, x.RoleId })
                .HasDatabaseName("IX_SqlOSScimManagedGrants_Reconcile");
            entity.HasIndex(x => x.GrantId);
            entity.Property(x => x.GroupExternalId).HasMaxLength(450);
            entity.Property(x => x.FgaGroupId).HasMaxLength(128);
            entity.Property(x => x.FgaGroupSubjectId).HasMaxLength(128);
            entity.Property(x => x.GrantId).HasMaxLength(128);
            entity.Property(x => x.RoleId).HasMaxLength(128);
            entity.Property(x => x.ResourceId).HasMaxLength(256);
            entity.HasOne(x => x.Connection)
                .WithMany()
                .HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Mapping)
                .WithMany(x => x.ManagedGrants)
                .HasForeignKey(x => x.MappingId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSScimSyncEvent>(entity =>
        {
            entity.ToTable("SqlOSScimSyncEvents", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConnectionId, x.OccurredAt });
            entity.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
            entity.Property(x => x.ResourceType).HasMaxLength(20);
            entity.Property(x => x.ResourceId).HasMaxLength(128);
            entity.Property(x => x.ExternalId).HasMaxLength(450);
            entity.Property(x => x.Action).HasMaxLength(80);
            entity.Property(x => x.Result).HasMaxLength(40);
            entity.Property(x => x.Error).HasMaxLength(1000);
            entity.Property(x => x.RequestId).HasMaxLength(128);
            entity.Property(x => x.DataJson).HasColumnType("nvarchar(max)");
            entity.HasOne(x => x.Connection)
                .WithMany(x => x.SyncEvents)
                .HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Organization)
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSScimOperationCommit>(entity =>
        {
            entity.ToTable("SqlOSScimOperationCommits", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OccurredAt);
        });

        modelBuilder.Entity<SqlOSSsoPortalSession>(entity =>
        {
            entity.ToTable("SqlOSSsoPortalSessions", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.LinkTokenHash).IsUnique();
            entity.HasIndex(x => x.SessionTokenHash)
                .IsUnique()
                .HasFilter("[SessionTokenHash] IS NOT NULL");
            entity.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            entity.HasIndex(x => new { x.OrganizationId, x.RevokedAt, x.ExpiresAt });
            entity.Property(x => x.Provider).HasMaxLength(40);
            entity.Property(x => x.ReturnUrl).HasMaxLength(1000);
            entity.Property(x => x.ActorType).HasMaxLength(80);
            entity.Property(x => x.RevokedReason).HasMaxLength(160);
            entity.Property(x => x.IpAddress).HasMaxLength(128);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.Property(x => x.LastTestStatus).HasMaxLength(40);
            entity.Property(x => x.LastTestMessage).HasMaxLength(500);
            entity.HasOne(x => x.Organization)
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Connection)
                .WithMany(x => x.PortalSessions)
                .HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSOrganizationDomain>(entity =>
        {
            entity.ToTable("SqlOSOrganizationDomains", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OrganizationId, x.Domain })
                .IsUnique()
                .HasFilter("[RevokedAt] IS NULL");
            entity.HasIndex(x => new { x.Domain, x.Status });
            entity.HasIndex(x => new { x.OrganizationId, x.Status });
            entity.Property(x => x.Domain).HasMaxLength(320);
            entity.Property(x => x.Status).HasMaxLength(50);
            entity.Property(x => x.VerificationToken).HasMaxLength(160);
            entity.Property(x => x.CreatedByUserId).HasMaxLength(64);
            entity.Property(x => x.LastError).HasMaxLength(1000);
            entity.HasOne(x => x.Organization)
                .WithMany(x => x.Domains)
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSOidcConnection>(entity =>
        {
            entity.ToTable("SqlOSAuthOidcConnections", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProviderType)
                .HasConversion<string>()
                .HasMaxLength(40);
            entity.Property(x => x.Protocol)
                .HasConversion<string>()
                .HasMaxLength(40);
            entity.Property(x => x.ClientAuthMethod)
                .HasConversion<string>()
                .HasMaxLength(40);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.ClientId).HasMaxLength(300);
            entity.Property(x => x.DiscoveryUrl).HasMaxLength(500);
            entity.Property(x => x.Issuer).HasMaxLength(500);
            entity.Property(x => x.AuthorizationEndpoint).HasMaxLength(1000);
            entity.Property(x => x.TokenEndpoint).HasMaxLength(1000);
            entity.Property(x => x.UserInfoEndpoint).HasMaxLength(1000);
            entity.Property(x => x.JwksUri).HasMaxLength(1000);
            entity.Property(x => x.MicrosoftTenant).HasMaxLength(200);
            entity.Property(x => x.AppleTeamId).HasMaxLength(100);
            entity.Property(x => x.AppleKeyId).HasMaxLength(100);
            entity.Property(x => x.AcceptedAmrValuesJson).HasDefaultValue("[]");
            entity.Property(x => x.AcceptedAcrValuesJson).HasDefaultValue("[]");
            entity.Property(x => x.ConfigurationOwner).HasMaxLength(40);
            entity.Property(x => x.ConfigurationSourceKey).HasMaxLength(160);
            entity.Property(x => x.ConfigurationFingerprint).HasMaxLength(64);
            entity.HasIndex(x => new { x.ConfigurationOwner, x.ConfigurationSourceKey })
                .IsUnique()
                .HasFilter("[ConfigurationSourceKey] IS NOT NULL");
        });

        modelBuilder.Entity<SqlOSExternalIdentity>(entity =>
        {
            entity.ToTable("SqlOSExternalIdentities", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SsoConnectionId).HasColumnName("ConnectionId");
            entity.Property(x => x.OidcConnectionId).HasColumnName("OidcConnectionId");
            entity.HasIndex(x => new { x.SsoConnectionId, x.Subject })
                .IsUnique()
                .HasFilter("[ConnectionId] IS NOT NULL");
            entity.HasIndex(x => new { x.OidcConnectionId, x.Subject })
                .IsUnique()
                .HasFilter("[OidcConnectionId] IS NOT NULL");
            entity.HasOne(x => x.User)
                .WithMany(x => x.ExternalIdentities)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.SsoConnection)
                .WithMany(x => x.ExternalIdentities)
                .HasForeignKey(x => x.SsoConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.OidcConnection)
                .WithMany(x => x.ExternalIdentities)
                .HasForeignKey(x => x.OidcConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSClientApplication>(entity =>
        {
            entity.ToTable("SqlOSClientApplications", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ClientId).IsUnique();
            entity.HasIndex(x => x.AccessMode);
            entity.HasIndex(x => x.RegistrationSource);
            entity.HasIndex(x => new { x.IsActive, x.RegistrationSource });
            entity.HasIndex(x => x.MetadataDocumentUrl);
            entity.HasIndex(x => x.LastSeenAt);
            entity.Property(x => x.ClientId).HasMaxLength(850);
            entity.Property(x => x.Audience).HasMaxLength(850);
            entity.Property(x => x.AccessMode).HasMaxLength(40);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(500);
            entity.Property(x => x.ClientType).HasMaxLength(40);
            entity.Property(x => x.RegistrationSource).HasMaxLength(20);
            entity.Property(x => x.ConfigurationOwner).HasMaxLength(40);
            entity.Property(x => x.ConfigurationSourceKey).HasMaxLength(160);
            entity.Property(x => x.ConfigurationFingerprint).HasMaxLength(64);
            entity.HasIndex(x => new { x.ConfigurationOwner, x.ConfigurationSourceKey })
                .IsUnique()
                .HasFilter("[ConfigurationSourceKey] IS NOT NULL");
            entity.Property(x => x.TokenEndpointAuthMethod).HasMaxLength(60);
            entity.Property(x => x.MetadataDocumentUrl).HasMaxLength(850);
            entity.Property(x => x.ClientUri).HasMaxLength(850);
            entity.Property(x => x.LogoUri).HasMaxLength(850);
            entity.Property(x => x.SoftwareId).HasMaxLength(200);
            entity.Property(x => x.SoftwareVersion).HasMaxLength(120);
            entity.Property(x => x.MetadataEtag).HasMaxLength(256);
            entity.Property(x => x.DisabledReason).HasMaxLength(500);
        });

        modelBuilder.Entity<SqlOSClientCredential>(entity =>
        {
            entity.ToTable("SqlOSClientCredentials", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ClientApplicationId, x.RevokedAt, x.ExpiresAt });
            entity.HasIndex(x => new { x.ClientApplicationId, x.ConfigurationOwner, x.ConfigurationSourceKey })
                .IsUnique()
                .HasFilter("[ConfigurationSourceKey] IS NOT NULL")
                .HasDatabaseName("UX_SqlOSClientCredentials_Client_Owner_SourceKey");
            entity.Property(x => x.ClientApplicationId).HasMaxLength(64);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.ConfigurationOwner).HasMaxLength(40);
            entity.Property(x => x.ConfigurationSourceKey).HasMaxLength(160);
            entity.HasOne(x => x.ClientApplication)
                .WithMany(x => x.ClientCredentials)
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSApplicationAssignment>(entity =>
        {
            entity.ToTable("SqlOSApplicationAssignments", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ClientApplicationId, x.PrincipalType, x.PrincipalId, x.OrganizationId, x.RoleKey, x.RevokedAt })
                .HasDatabaseName("IX_SqlOSApplicationAssignments_Target");
            entity.HasIndex(x => new { x.ClientApplicationId, x.RevokedAt });
            entity.HasIndex(x => new { x.OrganizationId, x.RevokedAt });
            entity.Property(x => x.ClientApplicationId).HasMaxLength(64);
            entity.Property(x => x.OrganizationId).HasMaxLength(64);
            entity.Property(x => x.PrincipalType).HasMaxLength(40);
            entity.Property(x => x.PrincipalId).HasMaxLength(128);
            entity.Property(x => x.RoleKey).HasMaxLength(80);
            entity.Property(x => x.Access).HasMaxLength(20);
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.ConfigurationOwner).HasMaxLength(40);
            entity.Property(x => x.ConfigurationSourceKey).HasMaxLength(160);
            entity.Property(x => x.ConfigurationFingerprint).HasMaxLength(64);
            entity.HasIndex(x => new { x.ClientApplicationId, x.ConfigurationOwner, x.ConfigurationSourceKey })
                .IsUnique()
                .HasFilter("[ConfigurationSourceKey] IS NOT NULL")
                .HasDatabaseName("UX_SqlOSApplicationAssignments_Client_Owner_SourceKey");
            entity.Property(x => x.CreatedByActorType).HasMaxLength(80);
            entity.Property(x => x.CreatedByActorId).HasMaxLength(128);
            entity.Property(x => x.RevokedByActorType).HasMaxLength(80);
            entity.Property(x => x.RevokedByActorId).HasMaxLength(128);
            entity.HasOne(x => x.ClientApplication)
                .WithMany(x => x.ApplicationAssignments)
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Organization)
                .WithMany(x => x.ApplicationAssignments)
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSDeviceAuthorization>(entity =>
        {
            entity.ToTable("SqlOSDeviceAuthorizations", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.DeviceCodeHash).IsUnique();
            entity.HasIndex(x => x.UserCodeHash).IsUnique();
            entity.HasIndex(x => new { x.ClientApplicationId, x.CreatedAt });
            entity.HasIndex(x => new { x.ClientApplicationId, x.Status, x.ExpiresAt });
            entity.HasIndex(x => new { x.IpAddress, x.CreatedAt });
            entity.HasIndex(x => x.ExpiresAt);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.UserCode).HasMaxLength(32);
            entity.Property(x => x.ClientApplicationId).HasMaxLength(64);
            entity.Property(x => x.Scope).HasMaxLength(1000);
            entity.Property(x => x.Resource).HasMaxLength(2048);
            entity.Property(x => x.AuthenticationMethod).HasMaxLength(50);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.UserAgent).HasMaxLength(500);
            entity.Property(x => x.ConsumedAt).IsConcurrencyToken();
            entity.HasOne(x => x.ClientApplication)
                .WithMany()
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ApprovedUser)
                .WithMany()
                .HasForeignKey(x => x.ApprovedUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ApprovedOrganization)
                .WithMany()
                .HasForeignKey(x => x.ApprovedOrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSSession>(entity =>
        {
            entity.ToTable("SqlOSSessions", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AuthenticationMethod).HasMaxLength(50);
            entity.Property(x => x.OrganizationId).HasMaxLength(64);
            entity.Property(x => x.Resource).HasMaxLength(2048);
            entity.Property(x => x.EffectiveAudience).HasMaxLength(2048);
            // Session revocation is the family-level lifecycle lock. A
            // refresh rotation that loaded an active session must fail its
            // transaction if replay detection revokes that session before
            // the rotation commits a new descendant.
            entity.Property(x => x.RevokedAt).IsConcurrencyToken();
            entity.HasOne(x => x.User)
                .WithMany(x => x.Sessions)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ClientApplication)
                .WithMany()
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Organization)
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSRefreshToken>(entity =>
        {
            entity.ToTable("SqlOSRefreshTokens", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.ReplacementTokenResponse)
                .HasColumnName("ReplacementAccessToken");
            // ConsumedAt is the rotation lock. Marking it as a concurrency
            // token forces EF Core to include it in the WHERE clause of
            // the UPDATE statement. If two concurrent refresh requests
            // (potentially on different instances behind a load balancer)
            // try to rotate the same token at the same instant, only one
            // UPDATE will affect a row — the other(s) get a
            // DbUpdateConcurrencyException, which RefreshAsync catches and
            // routes to the grace window path. This makes refresh-token
            // rotation strictly atomic across any number of app instances.
            entity.Property(x => x.ConsumedAt).IsConcurrencyToken();
            entity.HasOne(x => x.Session)
                .WithMany(x => x.RefreshTokens)
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSSigningKey>(entity =>
        {
            entity.ToTable("SqlOSSigningKeys", schema, table =>
            {
                table.ExcludeFromMigrations();
                table.HasCheckConstraint(
                    "CK_SqlOSSigningKeys_Lifecycle",
                    "([IsActive] = 1 AND [RetiredAt] IS NULL) OR ([IsActive] = 0 AND [RetiredAt] IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Kid).IsUnique();
            entity.HasIndex(x => x.IsActive)
                .IsUnique()
                .HasFilter("[IsActive] = 1");
            entity.Property(x => x.Kid).HasMaxLength(120);
            entity.Property(x => x.Algorithm).HasMaxLength(20);
            entity.Property(x => x.CustodyProvider).HasMaxLength(120);
        });

        modelBuilder.Entity<SqlOSTemporaryToken>(entity =>
        {
            entity.ToTable("SqlOSTemporaryTokens", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.Purpose).HasMaxLength(80);
            entity.Property(x => x.ConsumedAt).IsConcurrencyToken();
            entity.Property(x => x.PayloadJson).IsConcurrencyToken();
        });

        modelBuilder.Entity<SqlOSAuditEvent>(entity =>
        {
            entity.ToTable("SqlOSAuditEvents", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OccurredAt);
            entity.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
            entity.HasIndex(x => new { x.ApplicationId, x.OccurredAt });
            entity.HasIndex(x => new { x.ApplicationKey, x.OccurredAt });
            entity.HasIndex(x => new { x.Source, x.OccurredAt });
            entity.HasIndex(x => new { x.Action, x.OccurredAt });
            entity.HasIndex(x => new { x.ActorType, x.ActorId, x.OccurredAt });
            entity.HasIndex(x => x.IdempotencyKeyHash)
                .IsUnique()
                .HasFilter("[IdempotencyKeyHash] IS NOT NULL");
            entity.HasIndex(x => x.IdempotencyScopeHash)
                .IsUnique()
                .HasFilter("[IdempotencyScopeHash] IS NOT NULL");
            entity.Property(x => x.EventType).HasMaxLength(160);
            entity.Property(x => x.ApplicationId).HasMaxLength(64);
            entity.Property(x => x.ApplicationKey).HasMaxLength(200);
            entity.Property(x => x.Source).HasMaxLength(80);
            entity.Property(x => x.Action).HasMaxLength(160);
            entity.Property(x => x.ActorType).HasMaxLength(80);
            entity.Property(x => x.ActorId).HasMaxLength(128);
            entity.Property(x => x.ActorDisplayName).HasMaxLength(320);
            entity.Property(x => x.TargetsJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.ContextJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.MetadataJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.Property(x => x.RequestId).HasMaxLength(128);
            entity.Property(x => x.CorrelationId).HasMaxLength(128);
            entity.Property(x => x.IdempotencyKeyHash).HasMaxLength(128);
            entity.Property(x => x.IdempotencyScopeHash).HasMaxLength(128);
        });

        modelBuilder.Entity<SqlOSSettings>(entity =>
        {
            entity.ToTable("SqlOSSettings", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
        });

        modelBuilder.Entity<SqlOSMfaSettings>(entity =>
        {
            entity.ToTable("SqlOSMfaSettings", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.RequiredRolesJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.AvailableFactorsJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.ConfigurationOwner).HasMaxLength(40);
            entity.Property(x => x.ConfigurationSourceKey).HasMaxLength(160);
            entity.Property(x => x.ConfigurationFingerprint).HasMaxLength(64);
        });

        modelBuilder.Entity<SqlOSOrganizationMfaPolicy>(entity =>
        {
            entity.ToTable("SqlOSOrganizationMfaPolicies", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.OrganizationId);
            entity.Property(x => x.OrganizationId).HasMaxLength(64);
            entity.Property(x => x.RequiredRolesJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.AvailableFactorsJson).HasColumnType("nvarchar(max)");
            entity.HasOne(x => x.Organization)
                .WithOne(x => x.MfaPolicy)
                .HasForeignKey<SqlOSOrganizationMfaPolicy>(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSUserMfaPolicyOverride>(entity =>
        {
            entity.ToTable("SqlOSUserMfaPolicyOverrides", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.UserId).HasMaxLength(64);
            entity.HasOne(x => x.User)
                .WithOne(x => x.MfaPolicyOverride)
                .HasForeignKey<SqlOSUserMfaPolicyOverride>(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSUserAuthenticator>(entity =>
        {
            entity.ToTable("SqlOSUserAuthenticators", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.Type, x.RevokedAt });
            entity.HasIndex(x => new { x.UserId, x.IsConfirmed, x.RevokedAt });
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.UserId).HasMaxLength(64);
            entity.Property(x => x.Type).HasMaxLength(40);
            entity.Property(x => x.DisplayName).HasMaxLength(120);
            entity.Property(x => x.SecretProtected).HasMaxLength(2048);
            entity.Property(x => x.Algorithm).HasMaxLength(20);
            entity.Property(x => x.RevocationReason).HasMaxLength(120);
            entity.Property(x => x.LastAcceptedTimeStep).IsConcurrencyToken();
            entity.HasOne(x => x.User)
                .WithMany(x => x.Authenticators)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSRecoveryCode>(entity =>
        {
            entity.ToTable("SqlOSRecoveryCodes", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CodeHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.ConsumedAt, x.RevokedAt });
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.UserId).HasMaxLength(64);
            entity.Property(x => x.CodeHash).HasMaxLength(128);
            entity.Property(x => x.ConsumedAt).IsConcurrencyToken();
            entity.HasOne(x => x.User)
                .WithMany(x => x.RecoveryCodes)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSAuthPageSettings>(entity =>
        {
            entity.ToTable("SqlOSAuthPageSettings", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PrimaryColor).HasMaxLength(32);
            entity.Property(x => x.AccentColor).HasMaxLength(32);
            entity.Property(x => x.BackgroundColor).HasMaxLength(32);
            entity.Property(x => x.Layout).HasMaxLength(32);
            entity.Property(x => x.PageTitle).HasMaxLength(200);
            entity.Property(x => x.PageSubtitle).HasMaxLength(500);
            entity.Property(x => x.EmailApplicationName).HasMaxLength(200);
            entity.Property(x => x.EmailPrimaryColor).HasMaxLength(32);
            entity.Property(x => x.EmailAccentColor).HasMaxLength(32);
            entity.Property(x => x.EmailBackgroundColor).HasMaxLength(32);
        });

        modelBuilder.Entity<SqlOSEmailOtpChallenge>(entity =>
        {
            entity.ToTable("SqlOSEmailOtpChallenges", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ChallengeTokenHash).IsUnique();
            entity.HasIndex(x => new { x.NormalizedEmail, x.CreatedAt });
            entity.HasIndex(x => new { x.IpAddress, x.CreatedAt });
            entity.HasIndex(x => new { x.ClientApplicationId, x.CreatedAt });
            entity.Property(x => x.ChallengeTokenHash).HasMaxLength(128);
            entity.Property(x => x.CodeHash).HasMaxLength(128);
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.NormalizedEmail).HasMaxLength(320);
            entity.Property(x => x.InvalidatedReason).HasMaxLength(120);
            entity.Property(x => x.IpAddress).HasMaxLength(128);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.Property(x => x.ConsumedAt).IsConcurrencyToken();
            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.UserEmail)
                .WithMany()
                .HasForeignKey(x => x.UserEmailId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AuthorizationRequest)
                .WithMany()
                .HasForeignKey(x => x.AuthorizationRequestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ClientApplication)
                .WithMany()
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSPhoneOtpChallenge>(entity =>
        {
            entity.ToTable("SqlOSPhoneOtpChallenges", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ChallengeTokenHash).IsUnique();
            entity.HasIndex(x => new { x.PhoneNumberHash, x.CreatedAt });
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.HasIndex(x => new { x.IpAddress, x.CreatedAt });
            entity.HasIndex(x => new { x.ClientApplicationId, x.CreatedAt });
            entity.Property(x => x.ChallengeTokenHash).HasMaxLength(128);
            entity.Property(x => x.PhoneNumberHash).HasMaxLength(128);
            entity.Property(x => x.PhoneNumberEncrypted).HasMaxLength(2048);
            entity.Property(x => x.MaskedPhoneNumber).HasMaxLength(32);
            entity.Property(x => x.Purpose).HasMaxLength(32);
            entity.Property(x => x.Provider).HasMaxLength(40);
            entity.Property(x => x.ProviderChallengeId).HasMaxLength(128);
            entity.Property(x => x.ProviderStatus).HasMaxLength(80);
            entity.Property(x => x.InvalidatedReason).HasMaxLength(120);
            entity.Property(x => x.IpAddress).HasMaxLength(128);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.Property(x => x.ConsumedAt).IsConcurrencyToken();
            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.UserPhoneNumber)
                .WithMany()
                .HasForeignKey(x => x.UserPhoneNumberId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AuthorizationRequest)
                .WithMany()
                .HasForeignKey(x => x.AuthorizationRequestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ClientApplication)
                .WithMany()
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSAuthorizationRequest>(entity =>
        {
            entity.ToTable("SqlOSAuthorizationRequests", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.DeviceAuthorizationId)
                .IsUnique()
                .HasFilter("[DeviceAuthorizationId] IS NOT NULL");
            entity.Property(x => x.DeviceAuthorizationId).HasMaxLength(64);
            entity.Property(x => x.PresentationMode).HasMaxLength(32);
            entity.Property(x => x.LoginHintEmail).HasMaxLength(320);
            entity.Property(x => x.UiContextJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.RedirectUri).HasMaxLength(2048);
            entity.Property(x => x.State).HasMaxLength(2048);
            entity.Property(x => x.Scope).HasMaxLength(1000);
            entity.Property(x => x.Resource).HasMaxLength(2048);
            entity.Property(x => x.Nonce).HasMaxLength(256);
            entity.Property(x => x.Prompt).HasMaxLength(256);
            entity.Property(x => x.CodeChallenge).HasMaxLength(256);
            entity.Property(x => x.CodeChallengeMethod).HasMaxLength(32);
            entity.Property(x => x.ResolvedAuthMethod).HasMaxLength(50);
            entity.Property(x => x.CompletedAt).IsConcurrencyToken();
            entity.HasOne(x => x.ClientApplication)
                .WithMany()
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.DeviceAuthorization)
                .WithMany()
                .HasForeignKey(x => x.DeviceAuthorizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Organization)
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Connection)
                .WithMany()
                .HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Invitation)
                .WithMany()
                .HasForeignKey(x => x.InvitationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSSamlReplay>(entity =>
        {
            entity.ToTable("SqlOSSamlReplays", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ResponseId).HasMaxLength(450);
            entity.Property(x => x.AssertionId).HasMaxLength(450);
            entity.HasIndex(x => new { x.ConnectionId, x.ResponseId }).IsUnique();
            entity.HasIndex(x => new { x.ConnectionId, x.AssertionId }).IsUnique();
            entity.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<SqlOSAuthorizationCode>(entity =>
        {
            entity.ToTable("SqlOSAuthorizationCodes", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CodeHash).IsUnique();
            entity.HasIndex(x => x.AuthorizationRequestId).IsUnique();
            entity.Property(x => x.RedirectUri).HasMaxLength(2048);
            entity.Property(x => x.State).HasMaxLength(2048);
            entity.Property(x => x.Scope).HasMaxLength(1000);
            entity.Property(x => x.Resource).HasMaxLength(2048);
            entity.Property(x => x.CodeHash).HasMaxLength(128);
            entity.Property(x => x.CodeChallenge).HasMaxLength(256);
            entity.Property(x => x.CodeChallengeMethod).HasMaxLength(32);
            entity.Property(x => x.AuthenticationMethod).HasMaxLength(50);
            entity.Property(x => x.ConsumedAt).IsConcurrencyToken();
            entity.HasOne(x => x.AuthorizationRequest)
                .WithMany()
                .HasForeignKey(x => x.AuthorizationRequestId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ClientApplication)
                .WithMany()
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Organization)
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
