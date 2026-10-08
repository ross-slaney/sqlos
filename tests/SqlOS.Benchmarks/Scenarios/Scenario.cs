using SqlOS.Benchmarks.Data;

namespace SqlOS.Benchmarks.Scenarios;

internal enum ScenarioKind
{
    /// <summary>A cursor page of products as one statement the optimizer plans: <c>BuildFilterAsync</c>'s filter as a predicate, the lineage read from the row's scope column.</summary>
    List,

    /// <summary>
    /// The same LINQ on a context with SqlOS's query execution (<c>UseSqlOSFga</c>): SqlOS owns the access
    /// path (the adaptive walk over the grant counts, the per-level indexes, and the direct index).
    /// </summary>
    Page,

    /// <summary><c>fn_IsResourceAccessible</c> for one resource, the enforcement primitive the paper measures.</summary>
    PointFunction,

    /// <summary><c>Allows</c> (<c>CheckAccessAsync</c>), the point check applications call.</summary>
    PointApi,
}

/// <summary>The order a page is read in: the key (a cursor page), or the price, an order the application declared an index for.</summary>
internal enum PageOrder
{
    Id,
    Price,
}

/// <param name="Selectivity">σ: the fraction of all products the principal may see.</param>
/// <param name="ProductDepth">Depth of the products involved below the root (4 in a D = 5 chain, 9 in the D = 10 chain).</param>
/// <param name="Cursor">For pages in key order: the product id the page starts after (0 for the first page).</param>
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
    PageOrder Order = PageOrder.Id)
{
    public bool IsPage => Kind is ScenarioKind.List or ScenarioKind.Page;

    /// <summary>The same query walked by SqlOS (<c>page.*</c> beside <c>list.*</c>).</summary>
    public Scenario AsPage() => this with { Id = "page." + Id["list.".Length..], Kind = ScenarioKind.Page };
}

internal static class ScenarioCatalog
{
    public static IReadOnlyList<Scenario> Build(RetailTree tree, Principals people, long productCount)
    {
        var standardProduct = tree.FirstProduct(l => l.ProductDepth == 4, productCount);
        var deepProduct = tree.FirstProduct(l => l.ProductDepth == 9, productCount);
        var otherChainProduct = tree.FirstProduct(l => l.Chain == 2, productCount);
        double Share(Principal p) => p.ShareOf(tree);

        // Every page shape, as the optimizer plans it. Each is measured once more below walked by SqlOS, as
        // page.<same id>.
        var pages = new List<Scenario>
        {
            new("list.admin.first-page", "Company admin, first page (k = 20)", ScenarioKind.List, people.Admin, 1.0, "4 and 9"),
            new("list.admin.k100", "Company admin, first page (k = 100)", ScenarioKind.List, people.Admin, 1.0, "4 and 9", PageSize: 100),
            new("list.admin.mid-cursor", "Company admin, page from the middle of the table", ScenarioKind.List, people.Admin, 1.0, "4 and 9", Cursor: (int)(productCount / 2)),
            new("list.admin.by-price", "Company admin, first page by price", ScenarioKind.List, people.Admin, 1.0, "4 and 9", Order: PageOrder.Price),
            new("list.chain.first-page", "Chain manager (D = 5)", ScenarioKind.List, people.ChainManager, Share(people.ChainManager), "4"),
            new("list.region.first-page", "Region manager (D = 5)", ScenarioKind.List, people.RegionManager, Share(people.RegionManager), "4"),
            new("list.region.by-price", "Region manager, first page by price", ScenarioKind.List, people.RegionManager, Share(people.RegionManager), "4", Order: PageOrder.Price),
            new("list.deep-chain.first-page", "Chain manager (D = 10)", ScenarioKind.List, people.DeepChainManager, Share(people.DeepChainManager), "9"),
            new("list.store.first-page", "Store manager, every visible product (sparse)", ScenarioKind.List, people.StoreManager, Share(people.StoreManager), "4"),
            new("list.store.mid-cursor", "Store manager, page from the middle of the table (sparse)", ScenarioKind.List, people.StoreManager, Share(people.StoreManager), "4", Cursor: (int)(productCount / 2)),
            new("list.store.by-price", "Store manager, first page by price (sparse)", ScenarioKind.List, people.StoreManager, Share(people.StoreManager), "4", Order: PageOrder.Price),
            new("list.store.by-store", "Store manager, filtered to the store (StoreId index)", ScenarioKind.List, people.StoreManager, Share(people.StoreManager), "4", StoreId: people.StoreManagerStoreId),
            new("list.stores100.first-page", $"{people.HundredStores.ScopeResourceIds.Count} store grants across the chains, first page", ScenarioKind.List, people.HundredStores, Share(people.HundredStores), "4 and 9"),
            new("list.stores100.by-price", $"{people.HundredStores.ScopeResourceIds.Count} store grants across the chains, first page by price", ScenarioKind.List, people.HundredStores, Share(people.HundredStores), "4 and 9", Order: PageOrder.Price),
        };

        // People holding thousands of single-product grants, the way per-item sharing accumulates: the same
        // predicate, with all of their roots in one list parameter; the walk reads them from the direct index.
        foreach (var person in people.ManyGrants.Where(p => p.GrantedProducts > 0))
        {
            var share = (double)person.GrantedProducts / productCount;
            var grants = person.GrantedProducts.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            pages.Add(new($"list.{person.Key}.first-page", $"{grants} single-product grants, first page", ScenarioKind.List, person, share, "4 and 9"));
        }

        var current = new List<Scenario>(pages);
        current.AddRange(pages.Select(p => p.AsPage()));
        current.AddRange(
        [
            new("point.function.product", "fn_IsResourceAccessible, product at depth 4", ScenarioKind.PointFunction, people.Admin, 1.0, "4", ProductId: standardProduct),
            new("point.function.deep-product", "fn_IsResourceAccessible, product at depth 9", ScenarioKind.PointFunction, people.Admin, 1.0, "9", ProductId: deepProduct),
            new("point.function.denied", "fn_IsResourceAccessible, denied (another chain's product)", ScenarioKind.PointFunction, people.StoreManager, 0.0, "4", ProductId: otherChainProduct, ExpectAllowed: false),
            new("point.api.product", "Allows (CheckAccessAsync), product at depth 4", ScenarioKind.PointApi, people.Admin, 1.0, "4", ProductId: standardProduct),
            new("point.api.deep-product", "Allows (CheckAccessAsync), product at depth 9", ScenarioKind.PointApi, people.Admin, 1.0, "9", ProductId: deepProduct),
        ]);

        foreach (var person in people.ManyGrants.Where(p => p.GrantedProducts > 0))
        {
            var share = (double)person.GrantedProducts / productCount;
            var grants = person.GrantedProducts.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            current.Add(new($"point.function.{person.Key}", $"fn_IsResourceAccessible, {grants} grants, a granted product", ScenarioKind.PointFunction, person, share, "4 or 9", ProductId: person.GrantedProductId(person.GrantedProducts - 1)));
            if (person.ProductStride > 1)
            {
                current.Add(new($"point.function.{person.Key}.denied", $"fn_IsResourceAccessible, {grants} grants, an ungranted product", ScenarioKind.PointFunction, person, 0.0, "4 or 9", ProductId: 2, ExpectAllowed: false));
            }
        }

        return current;
    }

    /// <summary>
    /// The grant-density pass: the region page (both ways) and the denied check, re-run while
    /// <see cref="BenchmarkModel.RootCrowdGrants"/> other people hold grants on the root.
    /// </summary>
    public static IReadOnlyList<Scenario> Density(IReadOnlyList<Scenario> scenarios)
        => scenarios
            .Where(s => s.Id is "list.region.first-page" or "page.region.first-page" or "point.function.denied")
            .Select(s => s with
            {
                Id = "density." + s.Id,
                Title = $"{s.Title}, {BenchmarkModel.RootCrowdGrants} others' grants on the root",
            })
            .ToList();
}
