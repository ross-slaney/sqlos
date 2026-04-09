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

        modelBuilder.Entity<SqlOSSsoConnection>(entity =>
        {
            entity.ToTable("SqlOSSsoConnections", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.HasOne(x => x.Organization)
                .WithMany(x => x.SsoConnections)
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
            entity.HasIndex(x => x.RegistrationSource);
            entity.HasIndex(x => new { x.IsActive, x.RegistrationSource });
            entity.HasIndex(x => x.MetadataDocumentUrl);
            entity.HasIndex(x => x.LastSeenAt);
            entity.Property(x => x.ClientId).HasMaxLength(850);
            entity.Property(x => x.Audience).HasMaxLength(850);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(500);
            entity.Property(x => x.ClientType).HasMaxLength(40);
            entity.Property(x => x.RegistrationSource).HasMaxLength(20);
            entity.Property(x => x.TokenEndpointAuthMethod).HasMaxLength(60);
            entity.Property(x => x.MetadataDocumentUrl).HasMaxLength(850);
            entity.Property(x => x.ClientUri).HasMaxLength(850);
            entity.Property(x => x.LogoUri).HasMaxLength(850);
            entity.Property(x => x.SoftwareId).HasMaxLength(200);
            entity.Property(x => x.SoftwareVersion).HasMaxLength(120);
            entity.Property(x => x.MetadataEtag).HasMaxLength(256);
            entity.Property(x => x.DisabledReason).HasMaxLength(500);
        });

        modelBuilder.Entity<SqlOSSession>(entity =>
        {
            entity.ToTable("SqlOSSessions", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AuthenticationMethod).HasMaxLength(50);
            entity.Property(x => x.Resource).HasMaxLength(2048);
            entity.Property(x => x.EffectiveAudience).HasMaxLength(2048);
            entity.HasOne(x => x.User)
                .WithMany(x => x.Sessions)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ClientApplication)
                .WithMany()
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSRefreshToken>(entity =>
        {
            entity.ToTable("SqlOSRefreshTokens", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
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
            entity.ToTable("SqlOSSigningKeys", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Kid).IsUnique();
            entity.Property(x => x.Kid).HasMaxLength(120);
            entity.Property(x => x.Algorithm).HasMaxLength(20);
        });

        modelBuilder.Entity<SqlOSTemporaryToken>(entity =>
        {
            entity.ToTable("SqlOSTemporaryTokens", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.Purpose).HasMaxLength(80);
        });

        modelBuilder.Entity<SqlOSAuditEvent>(entity =>
        {
            entity.ToTable("SqlOSAuditEvents", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(120);
            entity.Property(x => x.ActorType).HasMaxLength(80);
        });

        modelBuilder.Entity<SqlOSSettings>(entity =>
        {
            entity.ToTable("SqlOSSettings", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
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
        });

        modelBuilder.Entity<SqlOSAuthorizationRequest>(entity =>
        {
            entity.ToTable("SqlOSAuthorizationRequests", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PresentationMode).HasMaxLength(32);
            entity.Property(x => x.LoginHintEmail).HasMaxLength(320);
            entity.Property(x => x.UiContextJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.RedirectUri).HasMaxLength(2048);
            entity.Property(x => x.State).HasMaxLength(256);
            entity.Property(x => x.Scope).HasMaxLength(1000);
            entity.Property(x => x.Resource).HasMaxLength(2048);
            entity.Property(x => x.Nonce).HasMaxLength(256);
            entity.Property(x => x.Prompt).HasMaxLength(256);
            entity.Property(x => x.CodeChallenge).HasMaxLength(256);
            entity.Property(x => x.CodeChallengeMethod).HasMaxLength(32);
            entity.Property(x => x.ResolvedAuthMethod).HasMaxLength(50);
            entity.HasOne(x => x.ClientApplication)
                .WithMany()
                .HasForeignKey(x => x.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Organization)
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Connection)
                .WithMany()
                .HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSAuthorizationCode>(entity =>
        {
            entity.ToTable("SqlOSAuthorizationCodes", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CodeHash).IsUnique();
            entity.Property(x => x.RedirectUri).HasMaxLength(2048);
            entity.Property(x => x.State).HasMaxLength(256);
            entity.Property(x => x.Scope).HasMaxLength(1000);
            entity.Property(x => x.Resource).HasMaxLength(2048);
            entity.Property(x => x.CodeHash).HasMaxLength(128);
            entity.Property(x => x.CodeChallenge).HasMaxLength(256);
            entity.Property(x => x.CodeChallengeMethod).HasMaxLength(32);
            entity.Property(x => x.AuthenticationMethod).HasMaxLength(50);
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
