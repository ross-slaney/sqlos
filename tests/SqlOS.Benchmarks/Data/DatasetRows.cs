using SqlOS.Fga;

namespace SqlOS.Benchmarks.Data;

/// <summary>
/// Rows for the bulk loaders, in the column order of <see cref="Columns"/>. The loaders write the lineage of
/// every resource (depth, reach, and the ancestor at each level) themselves, so the dataset loads without
/// firing the triggers; the first scale checks the result against SqlOS's own rebuild.
/// </summary>
internal static class DatasetRows
{
    public static readonly DateTime Timestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>Levels 0..10: the benchmark's tree is configured with the default depth of 10.</summary>
    public const int Levels = 11;

    public static class Columns
    {
        public static readonly (string Name, Type Type)[] Resources =
        [
            ("Id", typeof(string)), ("ParentId", typeof(string)), ("Name", typeof(string)), ("Description", typeof(string)),
            ("ResourceTypeId", typeof(string)), ("IsActive", typeof(bool)), ("CreatedAt", typeof(DateTime)), ("UpdatedAt", typeof(DateTime)),
            ("Seq", typeof(long)), ("Depth", typeof(short)), ("Reach", typeof(short)),
            .. Enumerable.Range(0, Levels).Select(level => (SqlOSFgaLineage.AncestorColumn(level), typeof(long))),
        ];

        public static readonly (string Name, Type Type)[] Products =
        [
            ("Id", typeof(int)), ("StoreId", typeof(int)), ("ResourceId", typeof(string)), ("Name", typeof(string)), ("Price", typeof(decimal)),
        ];

        public static readonly (string Name, Type Type)[] Stores =
        [
            ("Id", typeof(int)), ("Chain", typeof(int)), ("ResourceId", typeof(string)), ("Name", typeof(string)),
        ];
    }

    public static IEnumerable<object?[]> Hierarchy(RetailTree tree)
    {
        foreach (var node in tree.Nodes)
        {
            // The node's ancestors, nearest first, then the node itself at its own level.
            var chain = tree.AncestorsOf(node.Id).Select(tree.SeqOf).ToList();
            var depth = chain.Count;
            var row = new object?[Columns.Resources.Length];
            row[0] = node.Id;
            row[1] = node.ParentId;
            row[2] = node.Name;
            row[3] = null;
            row[4] = node.TypeId;
            row[5] = true;
            row[6] = Timestamp;
            row[7] = Timestamp;
            row[8] = tree.SeqOf(node.Id);
            row[9] = (short)depth;
            row[10] = (short)0;
            for (var level = 0; level < depth; level++)
            {
                row[11 + level] = chain[depth - 1 - level];
            }

            row[11 + depth] = tree.SeqOf(node.Id);
            yield return row;
        }
    }

    public static IEnumerable<object?[]> Stores(RetailTree tree)
    {
        foreach (var store in tree.Stores)
        {
            yield return [store.Id, store.Chain, store.ResourceId, store.Name];
        }
    }

    /// <summary>The FGA resources for products <c>(from, to]</c>, in ascending id order, with their lineage.</summary>
    public static IEnumerable<object?[]> ProductResources(RetailTree tree, long from, long to)
    {
        for (var id = from + 1; id <= to; id++)
        {
            var leaf = tree.LeafOf(id);
            var row = new object?[Columns.Resources.Length];
            row[0] = RetailTree.ProductResourceId(id);
            row[1] = leaf.ResourceId;
            row[2] = ProductName(id);
            row[3] = null;
            row[4] = "product";
            row[5] = true;
            row[6] = Timestamp;
            row[7] = Timestamp;
            row[8] = tree.ProductSeq(id);
            row[9] = (short)leaf.ProductDepth;
            row[10] = (short)0;
            WriteAncestors(row, 11, leaf, tree.ProductSeq(id));
            yield return row;
        }
    }

    /// <summary>The catalog rows for products <c>(from, to]</c>, in ascending id order.</summary>
    public static IEnumerable<object?[]> Products(RetailTree tree, long from, long to)
    {
        for (var id = from + 1; id <= to; id++)
        {
            var leaf = tree.LeafOf(id);
            yield return [(int)id, leaf.StoreId, RetailTree.ProductResourceId(id), ProductName(id), Price(id)];
        }
    }

    /// <summary>Ancestor columns of a product: the leaf's chain (root first) and the product itself at its level.</summary>
    private static void WriteAncestors(object?[] row, int first, Leaf leaf, long productSeq)
    {
        var chain = leaf.AncestorSeqs; // leaf itself first, root last
        var depth = chain.Length;      // the product sits one level below the leaf
        for (var level = 0; level < depth; level++)
        {
            row[first + level] = chain[depth - 1 - level];
        }

        row[first + depth] = productSeq;
    }

    public static string ProductName(long id) => $"Product {id}";

    public static decimal Price(long id) => (id * PriceMultiplier % PriceModulus + 99) / 100m;

    /// <summary>Price(id) cycles through every cent from 0.99 to 999.99: see <see cref="IdsByPrice"/>.</summary>
    public const long PriceMultiplier = 7919;

    public const long PriceModulus = 99_900;

    /// <summary>
    /// Product ids in ascending (Price, Id) order: for each cent value in turn, the ids whose price it is, which
    /// are an arithmetic progression (<see cref="PriceMultiplier"/> is coprime with <see cref="PriceModulus"/>).
    /// Ground truth for the pages ordered by price without sorting the catalog.
    /// </summary>
    public static IEnumerable<long> IdsByPrice(long productCount)
    {
        var inverse = ModularInverse(PriceMultiplier, PriceModulus);
        for (long cents = 0; cents < PriceModulus; cents++)
        {
            var first = cents * inverse % PriceModulus;
            if (first == 0)
            {
                first = PriceModulus;
            }

            for (var id = first; id <= productCount; id += PriceModulus)
            {
                yield return id;
            }
        }
    }

    private static long ModularInverse(long value, long modulus)
    {
        long t = 0, newT = 1, r = modulus, newR = value % modulus;
        while (newR != 0)
        {
            var quotient = r / newR;
            (t, newT) = (newT, t - quotient * newT);
            (r, newR) = (newR, r - quotient * newR);
        }

        if (r != 1)
        {
            throw new InvalidOperationException("The price multiplier must be coprime with the modulus.");
        }

        return t < 0 ? t + modulus : t;
    }
}
