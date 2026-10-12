using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;

namespace SqlOS.IntegrationTests.Fga.Infrastructure;

/// <summary>
/// A <c>SqlOSDbContext</c> over the shared test database with page-ordered application tables.
/// </summary>
public sealed class PageOrderTestDbContext : SqlOSDbContext<PageOrderTestDbContext>
{
    public const string RankEntries = "SqlOSFgaPage_PagedDocuments_Rank";
    public const string TitleEntries = "SqlOSFgaPage_PagedDocuments_Title";
    public const string CounterEntries = "SqlOSFgaPage_PagedCounters_Rank";
    public const string WorkItemEntries = "SqlOSFgaPage_PagedWorkItems_Rank";

    public PageOrderTestDbContext(DbContextOptions<PageOrderTestDbContext> options)
        : base(options)
    {
    }

    public DbSet<PagedDocument> Documents => Set<PagedDocument>();
    public DbSet<PagedCategory> Categories => Set<PagedCategory>();
    public DbSet<PagedCounter> Counters => Set<PagedCounter>();
    public DbSet<PagedWorkItem> WorkItems => Set<PagedWorkItem>();

    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PagedCategory>(entity =>
        {
            entity.ToTable("PagedCategories");
            entity.HasKey(category => category.Id);
            entity.Property(category => category.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<PagedDocument>(entity =>
        {
            entity.ToTable("PagedDocuments");
            entity.HasKey(document => document.Id);
            entity.Property(document => document.ResourceId).HasMaxLength(450);
            entity.Property(document => document.FolderResourceId).HasMaxLength(450);
            entity.Property(document => document.Title).HasMaxLength(200);
            entity.Property(document => document.Body).HasMaxLength(500);
            entity.HasIndex(document => document.ResourceId).IsUnique();
            entity.HasOne(document => document.Category).WithMany().HasForeignKey(document => document.CategoryId);
            entity.Ignore(document => document.ResourceTypeId);
            entity.Ignore(document => document.ResourceName);
            entity.Ignore(document => document.ParentResourceId);
            entity.Ignore(document => document.ResourceDescription);
            entity.Ignore(document => document.ResourceIsActive);
            entity.HasPageOrder(document => document.Rank);
            entity.HasPageOrder(document => document.Title);
        });

        modelBuilder.Entity<PagedCounter>(entity =>
        {
            entity.ToTable("PagedCounters");
            entity.HasKey(counter => counter.Id);
            entity.Property(counter => counter.ResourceId).HasMaxLength(450);
            entity.HasIndex(counter => counter.ResourceId);
            entity.HasPageOrder(counter => counter.Rank);
        });

        modelBuilder.Entity<PagedWorkItem>(entity =>
        {
            entity.ToTable("PagedWorkItems");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ResourceId).HasMaxLength(450);
            entity.Property(item => item.FolderResourceId).HasMaxLength(450);
            entity.HasIndex(item => item.ResourceId);
            entity.HasIndex(item => new { item.Rank, item.Id });
            entity.HasPageOrder(item => item.Rank);
        });
    }
}

/// <summary>A document with its own FGA resource under a folder resource.</summary>
public sealed class PagedDocument : ISqlOSResourceEntity
{
    public const string ResourceType = "page_document";

    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string FolderResourceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Rank { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public int? CategoryId { get; set; }
    public PagedCategory? Category { get; set; }

    public string ResourceTypeId => ResourceType;
    public string ResourceName => Title;
    public string? ParentResourceId => FolderResourceId;
    public string? ResourceDescription => null;
    public bool ResourceIsActive => Active;
}

public sealed class PagedCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>A row with an identity key that points at an existing resource it does not own.</summary>
public sealed class PagedCounter : IHasResourceId
{
    public int Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public int Rank { get; set; }
}

/// <summary>Rows that share their folder's resource, for the work test.</summary>
public sealed class PagedWorkItem : IHasResourceId
{
    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string FolderResourceId { get; set; } = string.Empty;
    public int Rank { get; set; }
}
