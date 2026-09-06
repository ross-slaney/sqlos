using Microsoft.EntityFrameworkCore;
using SqlOS.Calendar.Models;

namespace SqlOS.Calendar.Configuration;

public static class SqlOSCalendarModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder, string schema)
    {
        modelBuilder.Entity<SqlOSCalendarConnection>(entity =>
        {
            entity.ToTable("SqlOSCalendarConnections", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.RevokedAt });
            entity.HasIndex(x => new { x.OrganizationId, x.RevokedAt });
            entity.HasIndex(x => new { x.Mode, x.Status });
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.ProviderType)
                .HasConversion<string>()
                .HasMaxLength(40);
            entity.Property(x => x.Mode)
                .HasConversion<string>()
                .HasMaxLength(40);
            entity.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(40);
            entity.Property(x => x.OidcConnectionId).HasMaxLength(64);
            entity.Property(x => x.UserId).HasMaxLength(64);
            entity.Property(x => x.OrganizationId).HasMaxLength(64);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.ProviderAccountEmail).HasMaxLength(320);
            entity.Property(x => x.ProviderAccountSubject).HasMaxLength(256);
            entity.Property(x => x.LastError).HasMaxLength(1000);
            entity.Property(x => x.RevokedReason).HasMaxLength(160);
            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Organization)
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.OidcConnection)
                .WithMany()
                .HasForeignKey(x => x.OidcConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOSCalendarSyncState>(entity =>
        {
            entity.ToTable("SqlOSCalendarSyncStates", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CalendarConnectionId, x.ProviderCalendarId }).IsUnique();
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.CalendarConnectionId).HasMaxLength(64);
            entity.Property(x => x.ProviderCalendarId).HasMaxLength(256);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.LastSyncStatus).HasMaxLength(40);
            entity.Property(x => x.LastSyncError).HasMaxLength(1000);
            entity.HasOne(x => x.CalendarConnection)
                .WithMany(x => x.SyncStates)
                .HasForeignKey(x => x.CalendarConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SqlOSCalendarEvent>(entity =>
        {
            entity.ToTable("SqlOSCalendarEvents", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CalendarConnectionId, x.ProviderCalendarId, x.ProviderEventId })
                .IsUnique()
                .HasDatabaseName("IX_SqlOSCalendarEvents_ProviderEvent");
            entity.HasIndex(x => new { x.CalendarConnectionId, x.StartsAtUtc });
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.CalendarConnectionId).HasMaxLength(64);
            entity.Property(x => x.ProviderCalendarId).HasMaxLength(256);
            entity.Property(x => x.ProviderEventId).HasMaxLength(512);
            entity.Property(x => x.Subject).HasMaxLength(500);
            entity.Property(x => x.ShowAs).HasMaxLength(20);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.Location).HasMaxLength(500);
            entity.Property(x => x.Origin).HasMaxLength(20);
            entity.HasOne(x => x.CalendarConnection)
                .WithMany(x => x.Events)
                .HasForeignKey(x => x.CalendarConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
