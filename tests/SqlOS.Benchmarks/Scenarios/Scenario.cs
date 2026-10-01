using SqlOS.Benchmarks.Data;

namespace SqlOS.Benchmarks.Scenarios;

internal enum ScenarioKind
{
    /// <summary>A cursor page of products through <c>BuildFilterAsync</c>, the list-filtering path.</summary>
    List,

    /// <summary>The same page through the previous release's function (<see cref="Infrastructure.ReferenceFunction"/>).</summary>
    ListReference,

    /// <summary>The same page through <c>ListVisibleAsync</c>: one closure range per access root.</summary>
    Visible,

    /// <summary><c>fn_IsResourceAccessible</c> for one resource, the enforcement primitive the paper measures.</summary>
    PointFunction,

    /// <summary>The previous release's function for one resource.</summary>
    PointFunctionReference,

    /// <summary><c>Allows</c> (<c>CheckAccessAsync</c>), the point check applications call.</summary>
    PointApi,
}

/// <param name="Selectivity">σ: the fraction of all products the principal may see.</param>
/// <param name="ProductDepth">Depth of the products involved below the root (4 in a D = 5 chain, 9 in the D = 10 chain).</param>
/// <param name="Cursor">For pages: the product id the page starts after (0 for the first page).</param>
/// <param name="Baseline">For the reference twins and the visible twins: the id of the current-path scenario they pair with.</param>
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
    bool ExpectAllowed = true,
    string? Baseline = null)
{
    public bool IsPage => Kind is ScenarioKind.List or ScenarioKind.ListReference or ScenarioKind.Visible;
}

internal static class ScenarioCatalog
{
    public static IReadOnlyList<Scenario> Build(RetailTree tree, Principals people, long productCount)
    {
        var standardProduct = tree.FirstProduct(l => l.ProductDepth == 4, productCount);
        var deepProduct = tree.FirstProduct(l => l.ProductDepth == 9, productCount);
        var otherChainProduct = tree.FirstProduct(l => l.Chain == 2, productCount);
        double Share(Principal p) => tree.ShareOf(p.ScopeResourceId);

        var current = new List<Scenario>
        {
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
        };

        // People holding thousands of single-product grants, the way per-item sharing accumulates. Their cost
        // must not depend on how many grants they hold, except for the closure page, which reads each one.
        foreach (var person in people.ManyGrants.Where(p => p.GrantedProducts > 0))
        {
            var share = (double)person.GrantedProducts / productCount;
            var grants = person.GrantedProducts.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            current.Add(new($"list.{person.Key}.first-page", $"{grants} single-product grants, first page", ScenarioKind.List, person, share, "4 and 9"));
            current.Add(new($"point.function.{person.Key}", $"fn_IsResourceAccessible, {grants} grants, a granted product", ScenarioKind.PointFunction, person, share, "4 or 9", ProductId: person.GrantedProductId(person.GrantedProducts - 1)));
            if (person.ProductStride > 1)
            {
                current.Add(new($"point.function.{person.Key}.denied", $"fn_IsResourceAccessible, {grants} grants, an ungranted product", ScenarioKind.PointFunction, person, 0.0, "4 or 9", ProductId: 2, ExpectAllowed: false));
            }
        }

        // Twins: the previous function for the regression gate, and the closure page for the σ-free path.
        var all = new List<Scenario>();
        foreach (var scenario in current)
        {
            all.Add(scenario);
            switch (scenario.Kind)
            {
                case ScenarioKind.List:
                    all.Add(scenario with { Id = "reference." + scenario.Id, Title = scenario.Title + " · previous function", Kind = ScenarioKind.ListReference, Baseline = scenario.Id });
                    if (scenario.StoreId is null)
                    {
                        all.Add(scenario with { Id = "visible." + scenario.Id, Title = scenario.Title + " · ListVisibleAsync", Kind = ScenarioKind.Visible, Baseline = scenario.Id });
                    }

                    break;
                case ScenarioKind.PointFunction:
                    all.Add(scenario with { Id = "reference." + scenario.Id, Title = scenario.Title + " · previous function", Kind = ScenarioKind.PointFunctionReference, Baseline = scenario.Id });
                    break;
            }
        }

        return all;
    }

    /// <summary>
    /// The grant-density pass: the region page (both paths) and the denied check, re-run while
    /// <see cref="BenchmarkModel.RootCrowdGrants"/> other people hold grants on the root.
    /// </summary>
    public static IReadOnlyList<Scenario> Density(IReadOnlyList<Scenario> scenarios)
        => scenarios
            .Where(s => s.Id is "list.region.first-page" or "visible.list.region.first-page" or "point.function.denied")
            .Select(s => s with
            {
                Id = "density." + s.Id,
                Title = $"{s.Title}, {BenchmarkModel.RootCrowdGrants} others' grants on the root",
                Baseline = null,
            })
            .ToList();
}
