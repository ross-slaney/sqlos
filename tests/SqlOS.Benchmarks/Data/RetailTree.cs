using System.Globalization;

namespace SqlOS.Benchmarks.Data;

/// <summary>
/// The benchmark's resource hierarchy: a retail company with twelve chains under the SqlOS root.
/// </summary>
/// <remarks>
/// <para>
/// Chains 1-11 are shallow (D = 5): root → chain → region → store → product. Region and store counts vary
/// per chain, so fan-out is uneven. Chain 12 is deep (D = 10): root → chain → region → district → area →
/// zone → store → department → section → product. That is the deepest tree the shipped function walks
/// (<c>MaxResourceHierarchyDepth</c> = 10), and it holds about 10% of the products.
/// </para>
/// <para>
/// Store sizes are log-normal, so a few stores are several times the median and many are small. Each product
/// is placed with a hash of its id, so a store's products are spread through the whole id range, the way rows
/// arrive over time in a real catalog. The paper's harness inserted products store by store, which put the
/// store manager's rows at the front of the table and hid the cost of sparse access.
/// </para>
/// <para>
/// Everything is a pure function of the seed. Product <c>i</c> can be regenerated in any order, so the
/// dataset grows in place (1M → 10M → 100M), and every page a query returns can be checked against ground
/// truth.
/// </para>
/// </remarks>
internal sealed class RetailTree
{
    public const int DeepChain = 12;
    private const int StandardChains = 11;
    private const double DeepChainShare = 0.10;
    private const double StoreSizeSigma = 0.6;

    private readonly double[] _cumulative;
    private readonly Dictionary<string, double> _shares;
    private readonly ulong _seedMix;

    private RetailTree(
        string rootId,
        IReadOnlyList<ResourceRow> nodes,
        IReadOnlyList<StoreRow> stores,
        IReadOnlyList<Leaf> leaves,
        double[] cumulative,
        Dictionary<string, double> shares,
        ulong seedMix)
    {
        RootId = rootId;
        Nodes = nodes;
        Stores = stores;
        Leaves = leaves;
        _cumulative = cumulative;
        _shares = shares;
        _seedMix = seedMix;
    }

    public string RootId { get; }

    /// <summary>Every resource except the root and the products, parents before children.</summary>
    public IReadOnlyList<ResourceRow> Nodes { get; }

    private Dictionary<string, long>? _seqById;

    /// <summary>The root's sequence number, read from the database after SqlOS seeds it.</summary>
    public long RootSeq { get; private set; }

    /// <summary>Products are numbered after the hierarchy: product <c>i</c> has <c>Seq = ProductSeqOffset + i</c>.</summary>
    public long ProductSeqOffset => RootSeq + Nodes.Count;

    /// <summary>
    /// Fixes every resource's sequence number: the root's from the database, then the organizational nodes in
    /// their creation order, then the products by id. The loaders write these values explicitly so the
    /// closure rows can be generated without reading the database back.
    /// </summary>
    public void AssignSeqs(long rootSeq)
    {
        RootSeq = rootSeq;
        _seqById = new Dictionary<string, long>(Nodes.Count + 1, StringComparer.Ordinal) { [RootId] = rootSeq };
        for (var i = 0; i < Nodes.Count; i++)
        {
            _seqById[Nodes[i].Id] = rootSeq + 1 + i;
        }

        foreach (var leaf in Leaves)
        {
            leaf.AncestorSeqs = leaf.Ancestors.Select(id => _seqById[id]).ToArray();
        }
    }

    public long SeqOf(string nodeId) => (_seqById ?? throw new InvalidOperationException("Call AssignSeqs first."))[nodeId];

    public long ProductSeq(long productId) => ProductSeqOffset + productId;

    /// <summary>The proper ancestors of a node, nearest first, ending at the root.</summary>
    public IEnumerable<string> AncestorsOf(string nodeId)
    {
        var parents = _parentById ??= Nodes.ToDictionary(n => n.Id, n => n.ParentId, StringComparer.Ordinal);
        var current = parents.GetValueOrDefault(nodeId);
        while (current is not null)
        {
            yield return current;
            current = parents.GetValueOrDefault(current);
        }
    }

    private Dictionary<string, string>? _parentById;

    public IReadOnlyList<StoreRow> Stores { get; }

    /// <summary>Where products attach: standard stores (depth 3) and deep-chain sections (depth 8).</summary>
    public IReadOnlyList<Leaf> Leaves { get; }

    public static string ChainId(int chain) => $"ou::chain::{chain:D2}";
    public static string RegionId(int chain, int region) => $"ou::region::{chain:D2}-{region:D2}";

    /// <summary>
    /// Product resource ids sort after every <c>ou::</c> id, so growing the catalog appends to the end of the
    /// resource key space and the clustered index is filled in order.
    /// </summary>
    public static string ProductResourceId(long productId) => $"product::{productId:D9}";

    public static RetailTree Build(string rootId, int seed)
    {
        var random = new Random(seed);
        var nodes = new List<ResourceRow>(80_000);
        var stores = new List<StoreRow>(14_000);
        var leaves = new List<(string ResourceId, int StoreId, int Chain, int ProductDepth, double Weight, string[] Ancestors, bool Deep)>(60_000);
        var storeNumber = 0;

        for (var chain = 1; chain <= StandardChains; chain++)
        {
            var chainId = ChainId(chain);
            nodes.Add(new ResourceRow(chainId, rootId, "chain", $"Chain {chain}"));
            var regions = 8 + random.Next(5);
            for (var region = 1; region <= regions; region++)
            {
                var regionId = RegionId(chain, region);
                nodes.Add(new ResourceRow(regionId, chainId, "region", $"Region {chain}-{region}"));
                var storeCount = 60 + random.Next(81);
                for (var s = 0; s < storeCount; s++)
                {
                    storeNumber++;
                    var storeId = StoreResourceId(storeNumber);
                    nodes.Add(new ResourceRow(storeId, regionId, "store", $"Store {storeNumber}"));
                    stores.Add(new StoreRow(storeNumber, chain, storeId, $"Store {storeNumber}"));
                    leaves.Add((storeId, storeNumber, chain, 4, LogNormal(random), [storeId, regionId, chainId, rootId], false));
                }
            }
        }

        // The deep chain: 5 regions x 5 districts x 4 areas x 4 zones x 6 stores x 5 departments x 4 sections.
        var deepChainId = ChainId(DeepChain);
        nodes.Add(new ResourceRow(deepChainId, rootId, "chain", $"Chain {DeepChain}"));
        int district = 0, area = 0, zone = 0, department = 0, section = 0;
        for (var region = 1; region <= 5; region++)
        {
            var regionId = RegionId(DeepChain, region);
            nodes.Add(new ResourceRow(regionId, deepChainId, "region", $"Region {DeepChain}-{region}"));
            for (var d = 0; d < 5; d++)
            {
                var districtId = $"ou::district::{++district:D4}";
                nodes.Add(new ResourceRow(districtId, regionId, "district", $"District {district}"));
                for (var a = 0; a < 4; a++)
                {
                    var areaId = $"ou::area::{++area:D4}";
                    nodes.Add(new ResourceRow(areaId, districtId, "area", $"Area {area}"));
                    for (var z = 0; z < 4; z++)
                    {
                        var zoneId = $"ou::zone::{++zone:D4}";
                        nodes.Add(new ResourceRow(zoneId, areaId, "zone", $"Zone {zone}"));
                        for (var s = 0; s < 6; s++)
                        {
                            storeNumber++;
                            var storeId = StoreResourceId(storeNumber);
                            nodes.Add(new ResourceRow(storeId, zoneId, "store", $"Store {storeNumber}"));
                            stores.Add(new StoreRow(storeNumber, DeepChain, storeId, $"Store {storeNumber}"));
                            for (var dp = 0; dp < 5; dp++)
                            {
                                var departmentId = $"ou::department::{++department:D5}";
                                nodes.Add(new ResourceRow(departmentId, storeId, "department", $"Department {department}"));
                                for (var sc = 0; sc < 4; sc++)
                                {
                                    var sectionId = $"ou::section::{++section:D6}";
                                    nodes.Add(new ResourceRow(sectionId, departmentId, "section", $"Section {section}"));
                                    leaves.Add((
                                        sectionId, storeNumber, DeepChain, 9, LogNormal(random),
                                        [sectionId, departmentId, storeId, zoneId, areaId, districtId, regionId, deepChainId, rootId],
                                        true));
                                }
                            }
                        }
                    }
                }
            }
        }

        // Normalize: standard stores carry 90% of the catalog, deep sections 10%.
        var standardTotal = leaves.Where(l => !l.Deep).Sum(l => l.Weight);
        var deepTotal = leaves.Where(l => l.Deep).Sum(l => l.Weight);
        var finalLeaves = new Leaf[leaves.Count];
        var cumulative = new double[leaves.Count];
        var shares = new Dictionary<string, double>(StringComparer.Ordinal);
        var running = 0.0;
        for (var i = 0; i < leaves.Count; i++)
        {
            var l = leaves[i];
            var weight = l.Deep
                ? l.Weight / deepTotal * DeepChainShare
                : l.Weight / standardTotal * (1 - DeepChainShare);
            running += weight;
            cumulative[i] = running;
            finalLeaves[i] = new Leaf(i, l.ResourceId, l.StoreId, l.Chain, l.ProductDepth, weight, l.Ancestors);
            foreach (var ancestor in l.Ancestors)
            {
                shares[ancestor] = shares.GetValueOrDefault(ancestor) + weight;
            }
        }

        cumulative[^1] = 1.0;
        var seedMix = SplitMix64((ulong)seed * 0x9E3779B97F4A7C15UL);
        return new RetailTree(rootId, nodes, stores, finalLeaves, cumulative, shares, seedMix);
    }

    /// <summary>The leaf (store or section) product <paramref name="productId"/> belongs to.</summary>
    public Leaf LeafOf(long productId)
    {
        var hash = SplitMix64((ulong)productId ^ _seedMix);
        var unit = (hash >> 11) * (1.0 / (1UL << 53));
        var index = Array.BinarySearch(_cumulative, unit);
        if (index < 0)
        {
            index = ~index;
        }

        return Leaves[Math.Min(index, Leaves.Count - 1)];
    }

    /// <summary>Expected fraction of all products beneath <paramref name="scopeId"/> (σ for a grant on it).</summary>
    public double ShareOf(string scopeId) => _shares.GetValueOrDefault(scopeId);

    public int TotalResources(long products) => 1 + Nodes.Count + checked((int)Math.Min(products, int.MaxValue));

    /// <summary>
    /// The closure's size with <paramref name="products"/> products: one row per (proper ancestor, resource)
    /// pair. Every node is active, so every pair is on an active path.
    /// </summary>
    public long ClosureRows(long products)
    {
        long rows = Nodes.Sum(n => (long)AncestorsOf(n.Id).Count());
        for (long id = 1; id <= products; id++)
        {
            rows += LeafOf(id).Ancestors.Length;
        }

        return rows;
    }

    /// <summary>The standard store in <paramref name="chain"/> whose size is the median of that chain's stores.</summary>
    public Leaf MedianStoreIn(int chain)
    {
        var inChain = Leaves.Where(l => l.Chain == chain && l.ProductDepth == 4).OrderBy(l => l.Weight).ToArray();
        return inChain[inChain.Length / 2];
    }

    /// <summary>The first product id (from 1) whose leaf satisfies <paramref name="predicate"/>.</summary>
    public long FirstProduct(Func<Leaf, bool> predicate, long maxProduct)
    {
        for (long id = 1; id <= maxProduct; id++)
        {
            if (predicate(LeafOf(id)))
            {
                return id;
            }
        }

        throw new InvalidOperationException("No product matched.");
    }

    private static string StoreResourceId(int store) => $"ou::store::{store:D6}";

    private static double LogNormal(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return Math.Exp(StoreSizeSigma * z);
    }

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    public static string Count(long value)
        => value switch
        {
            >= 1_000_000 when value % 1_000_000 == 0 => $"{value / 1_000_000}M",
            >= 1_000 when value % 1_000 == 0 => $"{value / 1_000}K",
            _ => value.ToString("N0", CultureInfo.InvariantCulture),
        };
}

internal readonly record struct ResourceRow(string Id, string ParentId, string TypeId, string Name);

internal sealed record StoreRow(int Id, int Chain, string ResourceId, string Name);

/// <summary>A node products attach to. <see cref="Ancestors"/> runs from the leaf itself up to the root.</summary>
internal sealed record Leaf(int Index, string ResourceId, int StoreId, int Chain, int ProductDepth, double Weight, string[] Ancestors)
{
    /// <summary>The sequence numbers of <see cref="Ancestors"/>, in the same order, once assigned.</summary>
    public long[] AncestorSeqs { get; set; } = [];

    public bool IsUnder(string scopeId)
    {
        foreach (var ancestor in Ancestors)
        {
            if (string.Equals(ancestor, scopeId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
