using Microsoft.EntityFrameworkCore;
using SqlOS.Benchmarks.Infrastructure;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Benchmarks.Data;

/// <summary>
/// The authorization model the benchmark seeds: resource types, two permissions, four flat roles, and five
/// people granted at different heights of the tree.
/// </summary>
internal static class BenchmarkModel
{
    /// <summary>
    /// The company root. The <c>ou::</c> prefix keeps every organizational node sorted before the
    /// <c>product::</c> ids (see <see cref="RetailTree.ProductResourceId"/>).
    /// </summary>
    public const string RootResourceId = "ou::root";

    public const string ProductView = "PRODUCT_VIEW";
    public const string StoreView = "STORE_VIEW";

    /// <summary>
    /// For the grant-density pass: this many other people (org-wide auditors, say) hold grants on the root.
    /// Every row a scan rejects walks up to the root, so this shows whether a caller pays for grants that are
    /// not theirs. The model's cost bound depends only on the caller's own grants.
    /// </summary>
    public const int RootCrowdGrants = 100;

    private const string CrowdPrefix = "crowd::";

    public static SqlOSFgaSeedData Seed { get; } = new()
    {
        ResourceTypes =
        [
            Type("chain", "Chain"), Type("region", "Region"), Type("district", "District"),
            Type("area", "Area"), Type("zone", "Zone"), Type("store", "Store"),
            Type("department", "Department"), Type("section", "Section"), Type("product", "Product"),
        ],
        Permissions =
        [
            new SqlOSFgaPermission { Id = "perm_product_view", Key = ProductView, Name = "View products", ResourceTypeId = "product" },
            new SqlOSFgaPermission { Id = "perm_store_view", Key = StoreView, Name = "View stores", ResourceTypeId = "store" },
        ],
        Roles =
        [
            new SqlOSFgaRole { Id = "role_company_admin", Key = "company_admin", Name = "Company admin" },
            new SqlOSFgaRole { Id = "role_chain_manager", Key = "chain_manager", Name = "Chain manager" },
            new SqlOSFgaRole { Id = "role_region_manager", Key = "region_manager", Name = "Region manager" },
            new SqlOSFgaRole { Id = "role_store_manager", Key = "store_manager", Name = "Store manager" },
        ],
        RolePermissions =
        [
            ("company_admin", [ProductView, StoreView]),
            ("chain_manager", [ProductView, StoreView]),
            ("region_manager", [ProductView, StoreView]),
            ("store_manager", [ProductView, StoreView]),
        ],
    };

    /// <summary>
    /// Creates the people the scenarios query as. Each resolves to three subjects (the user and two groups),
    /// matching the paper's M = 3. The admin's grant is held by a group, so group resolution is exercised too.
    /// </summary>
    public static async Task<Principals> CreatePrincipalsAsync(BenchDbContext db, RetailTree tree, CancellationToken cancellationToken)
    {
        var store = tree.MedianStoreIn(1);
        var principals = new Principals(
            Admin: new Principal("admin", tree.RootId, "role_company_admin", GrantViaGroup: true),
            ChainManager: new Principal("chain", RetailTree.ChainId(1), "role_chain_manager", GrantViaGroup: false),
            RegionManager: new Principal("region", RetailTree.RegionId(1, 1), "role_region_manager", GrantViaGroup: false),
            DeepChainManager: new Principal("deep", RetailTree.ChainId(RetailTree.DeepChain), "role_chain_manager", GrantViaGroup: false),
            StoreManager: new Principal("store", store.ResourceId, "role_store_manager", GrantViaGroup: false),
            StoreManagerStoreId: store.StoreId);

        foreach (var principal in principals.All)
        {
            db.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject { Id = principal.SubjectId, SubjectTypeId = "user", DisplayName = $"{principal.Key} user" });
            db.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser { Id = $"usr_{principal.Key}", SubjectId = principal.SubjectId, IsActive = true });
            for (var g = 1; g <= 2; g++)
            {
                var groupSubject = principal.GroupSubjectId(g);
                db.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject { Id = groupSubject, SubjectTypeId = "group", DisplayName = $"{principal.Key} group {g}" });
                db.Set<SqlOSFgaUserGroup>().Add(new SqlOSFgaUserGroup { Id = $"ug_{principal.Key}_{g}", SubjectId = groupSubject, Name = $"{principal.Key} group {g}", IsActive = true });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var principal in principals.All)
        {
            for (var g = 1; g <= 2; g++)
            {
                db.Set<SqlOSFgaUserGroupMembership>().Add(new SqlOSFgaUserGroupMembership { SubjectId = principal.SubjectId, UserGroupId = $"ug_{principal.Key}_{g}" });
            }

            db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
            {
                Id = $"grant_{principal.Key}",
                SubjectId = principal.GrantViaGroup ? principal.GroupSubjectId(1) : principal.SubjectId,
                ResourceId = principal.ScopeResourceId,
                RoleId = principal.RoleId,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        var resolvedSubjects = await db.Set<SqlOSFgaUserGroupMembership>().CountAsync(cancellationToken);
        if (resolvedSubjects != principals.All.Count * 2)
        {
            throw new InvalidOperationException("Group memberships were not created.");
        }

        return principals;
    }

    /// <summary>
    /// The rest of the organization, so the grant, subject, and group tables have production-like sizes and
    /// the engines plan their lookups as they would for a real tenant: a manager with a grant on every store,
    /// region, and chain, and a team group per region whose members are that region's store managers.
    /// </summary>
    /// <returns>The number of grants created.</returns>
    public static async Task<int> PopulateOrganizationAsync(BenchDbContext db, RetailTree tree, CancellationToken cancellationToken)
    {
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        var scopes = tree.Nodes.Where(n => n.TypeId is "store" or "region" or "chain").ToList();
        var regionOfStore = tree.Nodes.Where(n => n.TypeId == "store").ToDictionary(n => n.Id, n => n.ParentId);
        var regions = scopes.Where(n => n.TypeId == "region").Select(n => n.Id).ToHashSet();
        var grants = 0;

        foreach (var region in regions)
        {
            var group = $"team::{region}";
            db.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject { Id = group, SubjectTypeId = "group", DisplayName = $"{region} team" });
            db.Set<SqlOSFgaUserGroup>().Add(new SqlOSFgaUserGroup { Id = $"ug::{region}", SubjectId = group, Name = $"{region} team", IsActive = true });
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        foreach (var batch in scopes.Chunk(2_000))
        {
            foreach (var scope in batch)
            {
                var subject = $"staff::{scope.Id}";
                db.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject { Id = subject, SubjectTypeId = "user", DisplayName = $"{scope.TypeId} manager" });
                db.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser { Id = $"usr::{scope.Id}", SubjectId = subject, IsActive = true });
                db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
                {
                    Id = $"grant::{scope.Id}",
                    SubjectId = subject,
                    ResourceId = scope.Id,
                    RoleId = scope.TypeId switch { "store" => "role_store_manager", "region" => "role_region_manager", _ => "role_chain_manager" },
                });
                grants++;
                if (scope.TypeId == "store" && regions.Contains(regionOfStore[scope.Id]))
                {
                    db.Set<SqlOSFgaUserGroupMembership>().Add(new SqlOSFgaUserGroupMembership { SubjectId = subject, UserGroupId = $"ug::{regionOfStore[scope.Id]}" });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        // The people the grant-density pass grants on the root; their grants exist only during that pass.
        for (var i = 1; i <= RootCrowdGrants; i++)
        {
            var subject = $"{CrowdPrefix}{i:D3}";
            db.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject { Id = subject, SubjectTypeId = "user", DisplayName = $"Auditor {i}" });
            db.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser { Id = $"usr::{subject}", SubjectId = subject, IsActive = true });
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return grants;
    }

    /// <summary>Grants each auditor a company-wide role on the root, for the grant-density pass.</summary>
    public static async Task AddRootCrowdAsync(BenchDbContext db, RetailTree tree, CancellationToken cancellationToken)
    {
        for (var i = 1; i <= RootCrowdGrants; i++)
        {
            db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
            {
                Id = $"grant::{CrowdPrefix}{i:D3}",
                SubjectId = $"{CrowdPrefix}{i:D3}",
                ResourceId = tree.RootId,
                RoleId = "role_company_admin",
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static Task RemoveRootCrowdAsync(BenchDbContext db, CancellationToken cancellationToken)
        => db.Set<SqlOSFgaGrant>().Where(g => g.Id.StartsWith("grant::" + CrowdPrefix)).ExecuteDeleteAsync(cancellationToken);

    private static SqlOSFgaResourceType Type(string id, string name) => new() { Id = id, Name = name };
}

internal sealed record Principal(string Key, string ScopeResourceId, string RoleId, bool GrantViaGroup)
{
    public string SubjectId => $"u_{Key}";
    public string GroupSubjectId(int group) => $"g_{Key}_{group}";

    /// <summary>What <c>BuildFilterAsync</c> resolves this subject to: the user first, then its active groups.</summary>
    public IReadOnlyList<string> ResolvedSubjectIds => [SubjectId, GroupSubjectId(1), GroupSubjectId(2)];
}

internal sealed record Principals(
    Principal Admin,
    Principal ChainManager,
    Principal RegionManager,
    Principal DeepChainManager,
    Principal StoreManager,
    int StoreManagerStoreId)
{
    public IReadOnlyList<Principal> All => [Admin, ChainManager, RegionManager, DeepChainManager, StoreManager];
}
