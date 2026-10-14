using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga.Infrastructure;

/// <summary>
/// A <c>SqlOSDbContext</c> over the shared test database with protected application tables: some
/// orders declared with <c>HasPageOrder</c>, the rest plain (served through each entity's node index).
/// </summary>
public sealed class PageOrderTestDbContext : SqlOSDbContext<PageOrderTestDbContext>
{
    public const string RankEntries = "SqlOSFgaPage_PagedDocuments_Rank";
    public const string TitleEntries = "SqlOSFgaPage_PagedDocuments_Title";
    public const string CounterEntries = "SqlOSFgaPage_PagedCounters_Rank";
    public const string WorkItemEntries = "SqlOSFgaPage_PagedWorkItems_Rank";
    public const string ScoreEntries = "SqlOSFgaPage_PagedScores_Score";

    // The node index SqlOS adds for every protected entity.
    public const string DocumentNodeIndex = "SqlOSFgaPage_PagedDocuments_Id";
    public const string CounterNodeIndex = "SqlOSFgaPage_PagedCounters_Id";
    public const string WorkItemNodeIndex = "SqlOSFgaPage_PagedWorkItems_Id";
    public const string ScoreNodeIndex = "SqlOSFgaPage_PagedScores_Id";
    public const string EventNodeIndex = "SqlOSFgaPage_PagedEvents_Id";

    public PageOrderTestDbContext(DbContextOptions<PageOrderTestDbContext> options)
        : base(options)
    {
    }

    public DbSet<PagedDocument> Documents => Set<PagedDocument>();
    public DbSet<PagedCategory> Categories => Set<PagedCategory>();
    public DbSet<PagedCounter> Counters => Set<PagedCounter>();
    public DbSet<PagedWorkItem> WorkItems => Set<PagedWorkItem>();
    public DbSet<PagedScore> Scores => Set<PagedScore>();
    public DbSet<PagedEvent> Events => Set<PagedEvent>();

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

        modelBuilder.Entity<PagedScore>(entity =>
        {
            entity.ToTable("PagedScores");
            entity.HasKey(score => score.Id);
            entity.Property(score => score.ResourceId).HasMaxLength(450);
            entity.HasIndex(score => score.ResourceId);
            entity.Property(score => score.Score)
                .HasComputedColumnSql(TestDatabase.IsPostgreSql ? "\"Points\" * 10" : "[Points] * 10", stored: true);
            entity.HasPageOrder(score => score.Score);
        });

        modelBuilder.Entity<PagedEvent>(entity =>
        {
            entity.ToTable("PagedEvents");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ResourceId).HasMaxLength(450);
            entity.HasIndex(item => item.ResourceId);
            // The application's index for its order. It carries ResourceId so that read 1 reads one
            // index row per row on both engines, which keeps the plan counts of the work tests exact.
            entity.HasIndex(item => new { item.Sequence, item.Id, item.ResourceId });
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

/// <summary>A row ordered by a column the database computes from another.</summary>
public sealed class PagedScore : IHasResourceId
{
    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public int Points { get; set; }
    public int Score { get; set; }
}

/// <summary>Rows paged by an order no page order declares, for the plain work tests.</summary>
public sealed class PagedEvent : IHasResourceId
{
    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public int Sequence { get; set; }
}

/// <summary>Rows that share their folder's resource, for the work test.</summary>
public sealed class PagedWorkItem : IHasResourceId
{
    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string FolderResourceId { get; set; } = string.Empty;
    public int Rank { get; set; }
}
