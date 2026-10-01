namespace SqlOS.Benchmarks.Data;

/// <summary>Rows for the bulk loaders, in the column order of <see cref="Columns"/>.</summary>
internal static class DatasetRows
{
    public static readonly DateTime Timestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public static class Columns
    {
        public static readonly (string Name, Type Type)[] Resources =
        [
            ("Id", typeof(string)), ("ParentId", typeof(string)), ("Name", typeof(string)), ("Description", typeof(string)),
            ("ResourceTypeId", typeof(string)), ("IsActive", typeof(bool)), ("CreatedAt", typeof(DateTime)), ("UpdatedAt", typeof(DateTime)),
            ("Seq", typeof(long)),
        ];

        public static readonly (string Name, Type Type)[] Closure =
        [
            ("AncestorSeq", typeof(long)), ("TypeSeq", typeof(int)), ("DescendantSeq", typeof(long)),
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
            yield return [node.Id, node.ParentId, node.Name, null, node.TypeId, true, Timestamp, Timestamp, tree.SeqOf(node.Id)];
        }
    }

    /// <summary>The closure rows of the organizational nodes: one per proper ancestor. Every node is active.</summary>
    public static IEnumerable<object?[]> HierarchyClosure(RetailTree tree, IReadOnlyDictionary<string, int> typeSeq)
    {
        var rows = new List<(long Ancestor, int Type, long Descendant)>();
        foreach (var node in tree.Nodes)
        {
            var descendant = tree.SeqOf(node.Id);
            var type = typeSeq[node.TypeId];
            foreach (var ancestor in tree.AncestorsOf(node.Id))
            {
                rows.Add((tree.SeqOf(ancestor), type, descendant));
            }
        }

        rows.Sort();
        foreach (var (ancestor, type, descendant) in rows)
        {
            yield return [ancestor, type, descendant];
        }
    }

    public static IEnumerable<object?[]> Stores(RetailTree tree)
    {
        foreach (var store in tree.Stores)
        {
            yield return [store.Id, store.Chain, store.ResourceId, store.Name];
        }
    }

    /// <summary>The FGA resources for products <c>(from, to]</c>, in ascending id order.</summary>
    public static IEnumerable<object?[]> ProductResources(RetailTree tree, long from, long to)
    {
        for (var id = from + 1; id <= to; id++)
        {
            yield return [RetailTree.ProductResourceId(id), tree.LeafOf(id).ResourceId, ProductName(id), null, "product", true, Timestamp, Timestamp, tree.ProductSeq(id)];
        }
    }

    /// <summary>The number of ancestor positions a product can have: the deepest leaf's ancestors.</summary>
    public static int AncestorPositions(RetailTree tree) => tree.Leaves.Max(l => l.Ancestors.Length);

    /// <summary>
    /// The closure rows of products <c>(from, to]</c> for one ancestor position (0 = the product's parent, then
    /// its parent, and so on up to the root), sorted by (AncestorSeq, DescendantSeq). One pass per position
    /// keeps the bulk stream in clustered-key order while holding only one integer per product in memory.
    /// </summary>
    public static IEnumerable<object?[]> ProductClosure(RetailTree tree, int position, int productTypeSeq, long from, long to)
    {
        var byAncestor = new Dictionary<long, List<int>>();
        for (var id = from + 1; id <= to; id++)
        {
            var ancestors = tree.LeafOf(id).AncestorSeqs;
            if (position >= ancestors.Length)
            {
                continue;
            }

            if (!byAncestor.TryGetValue(ancestors[position], out var list))
            {
                byAncestor[ancestors[position]] = list = [];
            }

            list.Add((int)id);
        }

        var ancestorSeqs = byAncestor.Keys.ToArray();
        Array.Sort(ancestorSeqs);
        foreach (var ancestor in ancestorSeqs)
        {
            foreach (var id in byAncestor[ancestor])
            {
                yield return [ancestor, productTypeSeq, tree.ProductSeq(id)];
            }
        }
    }

    /// <summary>The catalog rows for products <c>(from, to]</c>, in ascending id order.</summary>
    public static IEnumerable<object?[]> Products(RetailTree tree, long from, long to)
    {
        for (var id = from + 1; id <= to; id++)
        {
            yield return [(int)id, tree.LeafOf(id).StoreId, RetailTree.ProductResourceId(id), ProductName(id), Price(id)];
        }
    }

    public static string ProductName(long id) => $"Product {id}";

    public static decimal Price(long id) => (id * 7919 % 99_900 + 99) / 100m;
}
