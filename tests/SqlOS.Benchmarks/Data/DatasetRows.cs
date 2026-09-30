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
            yield return [node.Id, node.ParentId, node.Name, null, node.TypeId, true, Timestamp, Timestamp];
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
            yield return [RetailTree.ProductResourceId(id), tree.LeafOf(id).ResourceId, ProductName(id), null, "product", true, Timestamp, Timestamp];
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
