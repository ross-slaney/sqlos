using Microsoft.EntityFrameworkCore;
using SqlOS.Database;
using SqlOS.Email.Models;

namespace SqlOS.Email.Configuration;

public static class SqlOSEmailModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder, string schema, string? providerName = null)
    {
        modelBuilder.Entity<SqlOSEmailTemplate>(entity =>
        {
            entity.ToTable("SqlOSEmailTemplates", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Key).IsUnique();
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Key).HasMaxLength(120);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.SubjectTemplate).HasMaxLength(500);
        });

        modelBuilder.Entity<SqlOSEmailDelivery>(entity =>
        {
            entity.ToTable("SqlOSEmailDeliveries", schema, t => t.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TemplateKey, x.CreatedAt });
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
            entity.HasIndex(x => new { x.To, x.CreatedAt });
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => x.IdempotencyKey)
                .IsUnique()
                .HasFilter(SqlOSModelSql.IsNotNull(providerName, "IdempotencyKey"));
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.TemplateId).HasMaxLength(64);
            entity.Property(x => x.TemplateKey).HasMaxLength(120);
            entity.Property(x => x.To).HasMaxLength(320);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.ProviderMessageId).HasMaxLength(200);
            entity.Property(x => x.SanitizedError).HasMaxLength(500);
            entity.Property(x => x.RenderedSubject).HasMaxLength(500);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200);
            entity.HasOne(x => x.Template)
                .WithMany()
                .HasForeignKey(x => x.TemplateId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
