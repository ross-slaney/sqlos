using SqlOS.Benchmarks.Data;

namespace SqlOS.Benchmarks.Scenarios;

internal enum ScenarioKind
{
    /// <summary>A cursor page of products through <c>BuildFilterAsync</c>, the list-filtering path.</summary>
    List,

    /// <summary><c>fn_IsResourceAccessible</c> for one resource, the enforcement primitive the paper measures.</summary>
    PointFunction,

    /// <summary><c>Allows</c> (<c>CheckAccessAsync</c>), the point check applications call.</summary>
    PointApi,
}

/// <param name="Selectivity">σ: the fraction of all products the principal may see.</param>
/// <param name="ProductDepth">Depth of the products involved below the root (4 in a D = 5 chain, 9 in the D = 10 chain).</param>
internal sealed record Scenario(
    string Id,
    string Title,
    ScenarioKind Kind,
    Principal Principal,
    double Selectivity,
    string ProductDepth,
    int PageSize = 20,
    int Cursor = 0,
    int? StoreId = null,
    long ProductId = 0,
    bool ExpectAllowed = true);

internal static class ScenarioCatalog
{
    public static IReadOnlyList<Scenario> Build(RetailTree tree, Principals people, long productCount)
    {
        var standardProduct = tree.FirstProduct(l => l.ProductDepth == 4, productCount);
        var deepProduct = tree.FirstProduct(l => l.ProductDepth == 9, productCount);
        var otherChainProduct = tree.FirstProduct(l => l.Chain == 2, productCount);
        double Share(Principal p) => tree.ShareOf(p.ScopeResourceId);

        return
        [
            new("list.admin.first-page", "Company admin, first page (k = 20)", ScenarioKind.List, people.Admin, 1.0, "4 and 9"),
            new("list.admin.k100", "Company admin, first page (k = 100)", ScenarioKind.List, people.Admin, 1.0, "4 and 9", PageSize: 100),
            new("list.admin.mid-cursor", "Company admin, page from the middle of the table", ScenarioKind.List, people.Admin, 1.0, "4 and 9", Cursor: (int)(productCount / 2)),
            new("list.chain.first-page", "Chain manager (D = 5)", ScenarioKind.List, people.ChainManager, Share(people.ChainManager), "4"),
            new("list.region.first-page", "Region manager (D = 5)", ScenarioKind.List, people.RegionManager, Share(people.RegionManager), "4"),
            new("list.deep-chain.first-page", "Chain manager (D = 10)", ScenarioKind.List, people.DeepChainManager, Share(people.DeepChainManager), "9"),
            new("list.store.first-page", "Store manager, every visible product (sparse)", ScenarioKind.List, people.StoreManager, Share(people.StoreManager), "4"),
            new("list.store.by-store", "Store manager, filtered to the store (StoreId index)", ScenarioKind.List, people.StoreManager, Share(people.StoreManager), "4", StoreId: people.StoreManagerStoreId),
            new("point.function.product", "fn_IsResourceAccessible, product at depth 4", ScenarioKind.PointFunction, people.Admin, 1.0, "4", ProductId: standardProduct),
            new("point.function.deep-product", "fn_IsResourceAccessible, product at depth 9", ScenarioKind.PointFunction, people.Admin, 1.0, "9", ProductId: deepProduct),
            new("point.function.denied", "fn_IsResourceAccessible, denied (walks to the root)", ScenarioKind.PointFunction, people.StoreManager, 0.0, "4", ProductId: otherChainProduct, ExpectAllowed: false),
            new("point.api.product", "Allows (CheckAccessAsync), product at depth 4", ScenarioKind.PointApi, people.Admin, 1.0, "4", ProductId: standardProduct),
            new("point.api.deep-product", "Allows (CheckAccessAsync), product at depth 9", ScenarioKind.PointApi, people.Admin, 1.0, "9", ProductId: deepProduct),
        ];
    }

    /// <summary>
    /// The grant-density pass: the same region page and denied check, re-run while
    /// <see cref="BenchmarkModel.RootCrowdGrants"/> other people hold grants on the root.
    /// </summary>
    public static IReadOnlyList<Scenario> Density(IReadOnlyList<Scenario> scenarios)
        => scenarios
            .Where(s => s.Id is "list.region.first-page" or "point.function.denied")
            .Select(s => s with
            {
                Id = "density." + s.Id,
                Title = $"{s.Title}, {BenchmarkModel.RootCrowdGrants} others' grants on the root",
            })
            .ToList();
}
