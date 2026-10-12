# FGA page orders

How `ToPageAsync` returns an authorized page for about a page of work, whoever the caller is, with
nothing of SqlOS's on application tables. Issue #515.

## The problem

An authorized list is `.Where(filter)` over `fn_IsResourceAccessible`. The database reads the table
in the page's order and checks each row until the page is full. A caller who can see a few rows of a
large table makes it read most of the table.

## The idea

For each order an entity is paged by, SqlOS keeps, in its own schema, a copy of that order **under
every node of the resource tree**. A page for anyone is then "the first rows under each of my
nodes, merged": one index seek per node.

```csharp
modelBuilder.Entity<Product>(entity =>
{
    entity.HasPageOrder(p => p.Price);                       // (Price, Id)
    entity.HasPageOrder(p => new { p.Status, p.CreatedAt }); // (Status, CreatedAt, Id)
});

var canView = await fga.BuildFilterAsync<Product>(subjectId, "product.view");
var page = await db.Products
    .Where(p => p.Name.Contains(term))
    .OrderBy(p => p.Price)
    .ToPageAsync(canView, pageSize: 20, cursor);
```

`BuildFilterAsync`, `fn_IsResourceAccessible`, `CheckAccessAsync` and the FGA schema scripts are unchanged.

## Storage

One table per declared order, in the FGA schema, added to the application's EF model as a
shared-type entity (`Dictionary<string, object>`), so the application's migration creates it:

| Column | Meaning |
| --- | --- |
| `NodeId` | a resource on the active path from the row's resource up to the root, including the row's own resource |
| one column per order column except the key | the row's value, with the source column's type, length, precision, unicode, collation and converter |
| `RowKey` | the row's primary key |

Primary key `(NodeId, <order columns>, RowKey)` (the index a page reads); index `(RowKey)` (the index
maintenance deletes by). Name: `SqlOSFgaPage_{Table}_{Col1}[_{Col2}…]`, truncated with an 8-hex-digit
SHA-256 suffix past 128 (SQL Server) or 63 (PostgreSQL) characters. The source entity carries the
annotation `SqlOS:PageOrders` (`[{ "table", "columns" }]`, columns ending with the key).

**The entry rule.** An entry `(NodeId = X, RowKey = r)` exists if and only if X is r's own resource or
an ancestor of it, every resource from r's resource up to and including X is active, and the chain is
within `MaxResourceHierarchyDepth` with no cycle. That is exactly the set of nodes a grant can sit on
and reach r under `fn_IsResourceAccessible`. A row whose own resource is inactive, or whose
`ResourceId` names no resource, has no entries.

Size: depth-of-the-row entries per row per order (a product four levels under the root has five).

## Functions

Installed by `SqlOSFgaFunctionInitializer` next to `fn_IsResourceAccessible`, `CREATE OR ALTER` /
`CREATE OR REPLACE`, one dialect each in `SqlServerDatabaseProvider` and `PostgreSqlDatabaseProvider`:

- **`fn_ResourceAncestors(@ResourceId)` → `(NodeId, Depth)`.** The entry rule as a function. Its
  `ancestors` CTE is the access check's, shared through one C# helper (`BuildAncestorsCte`); it
  returns nothing when the chain has a cycle or is truncated.
- **`fn_PageNodes(@SubjectIds, @PermissionId)` → `(NodeId)`.** The caller's granted nodes for the
  permission, minimal: the subjects, role permission, grant window, subject activity (through the
  access check's own `BuildSubjectActivityConditions` text) and an active granted resource, minus any
  node that has a qualifying node among its own active ancestors. Minimality means no row sits under
  two returned nodes, so a page needs no `DISTINCT`.

A unit test pins the SHA-256 of `fn_IsResourceAccessible`'s text on both engines, so sharing the
helpers cannot change it.

## The page

`ToPageAsync` reads the filter (`SqlOSFgaFilterReader`: the context, the subject ids JSON and the
permission id of the `BuildFilterAsync` expression), splits the query into its ordering and the rest
(`SqlOSFgaOrderReader`), decodes the cursor (`SqlOSFgaPageCursor`), and runs the loop in
`SqlOSFgaPageQuery` with a budget `b` that starts at the page size `P`:

1. **Read 1, "your query, checked."** The next `b` rows of the application's query in its order, each
   with a visibility flag (the filter's `EXISTS` in the projection). If at least `P` are visible, they
   are the page. If fewer than `b` rows came back, the query is exhausted and the visible rows are the
   last page.
2. **Read 2, "the caller's nodes, in order."** Only for a declared order. Under each node of
   `fn_PageNodes`, the next `b` entries after the cursor that pass the application's filters (an
   `EXISTS` on the row's key against the query without its ordering), merged in order, at most `P`.
   `P` entries are the page; fewer means every node ran out, and they are the last page. Because `b`
   is never below `P`, read 2 settles the page in the round it runs.
3. Otherwise `b` grows four times and read 1 runs again. Only undeclared orders get here.

**Load.** The page's keys go back through the query with its includes and tracking and through the
filter once more, then are ordered by key position in memory. The filter is the final check: a stale
or wrong entry can only drop a row, never add one. The cursor is the last selected row's order values,
so a dropped row does not stall the list.

Each statement is an EF Core query (`ToQueryString` can print it). Read 2 projects the node's id inside
the per-node subquery; that keeps the subquery correlated, so EF Core emits `CROSS APPLY (SELECT TOP(@b)
…)` on SQL Server and `JOIN LATERAL (… LIMIT @b)` on PostgreSQL. Without it EF Core rewrites the
subquery into a `ROW_NUMBER() OVER (PARTITION BY NodeId)` join over every entry.

One line per page is logged at Debug under `SqlOS.Fga`: entity, order, whether it is declared, the
rounds, rows per round of read 1, entries per node of read 2, which read produced the page, rows the
final check dropped, elapsed time. An undeclared order that needs more than one round of read 1 is
logged once per entity and order at Warning, with the declaration to add.

## Maintenance

All in `SqlOSFgaPageOrderMaintenance`, called from `SqlOSDbContext<TContext>.SaveChanges` /
`SaveChangesAsync` after the resource synchronizer:

1. Collect, before the save: added rows; modified rows whose `ResourceId` or an order column changed;
   deleted rows; resources whose `ParentId` or `IsActive` changed, or that are deleted.
2. Nothing collected: a plain `base.SaveChanges`.
3. Otherwise, in the caller's transaction, an ambient one, or one owned here (inside the context's
   execution strategy): save, then per order
   - for changed resources, delete and re-insert the entries of the rows under the resources and their
     descendants (a recursive CTE over `ParentId`, bounded by the depth; a deleted resource is its own
     subtree);
   - for changed rows, delete their entries by key and re-insert the added and modified ones.

   Subtrees go first and keys last, so a row in both sets ends with exactly one set of entries. Key
   lists are one parameter: `OPENJSON(@keys)` on SQL Server, `= ANY(@keys)` on PostgreSQL. Any failure
   rolls everything back.

This covers every write that ends in `SaveChanges` on a `SqlOSDbContext`: application rows,
`ISqlOSResourceEntity` synchronization, `SqlOSErgonomicsExtensions`, the dashboard's resource edits and
the seed service. Writes that bypass EF Core are covered by `ISqlOSFgaAuthService.RebuildPageOrdersAsync`
(delete all, insert all, one transaction and one application lock per order). Until it runs, rows those writes
inserted are missing from pages and rows they changed page at their old position; a row the caller may not see is
never returned (the final check).

**Startup.** After the functions are installed, `SqlOSBootstrapper` fills the entry table of every
order whose table exists and is empty while its application table has rows: the first start after a
declaration needs no manual step. A missing entry table is logged at Warning and skipped; pages for
that order run read 1 only until it exists.

Contexts that implement `ISqlOSFgaDbContext` by hand get no maintenance, and `ToPageAsync` refuses them.

## Cost

`P` page size, `k` the caller's minimal granted nodes, `D` tree depth, `M` subjects (the caller and
their groups), `N` rows in the table, `f` the fraction of rows under the caller's nodes that pass the
application's filters.

| Case | Cost | Why |
| --- | --- | --- |
| Dense list, scoped or not (admin; plants in my garden) | O(P·D·M) | read 1 fills the page |
| Sees 1 in 100, no filter | O(P·D·M + P) | read 1 misses, read 2 takes P entries under one node |
| 3 single-row grants under root, millions of rows | O((P + k)·D·M) | read 1 misses; 3 seeks, 3 entries |
| 100 scattered stores | O(k·P) worst, O(k + P) typical | one seek per node, at most P entries each |
| 10,000 single-row grants | O(k) | one entry per node |
| Sees half the table, rows all at the far end of the order | O(P) | P entries under their node |
| Application filter few rows pass, no scope | O(P / f) | entries read until P pass |
| Rare filter and a caller whose rows barely overlap it | O(rows the caller sees) | read 2 reads the caller's entries until P pass the filter |
| Insert, or update of an order column or `ResourceId` | O(D × declared orders) | entries written in the same transaction |
| Move, deactivate or reactivate a resource | O(subtree × D × declared orders) | that subtree's entries rewritten |
| `RebuildPageOrdersAsync` | O(N × D × declared orders) | once |

Nothing is linear in `N` except the rebuild.

## Limits and engine notes

- Order columns are non-nullable mapped scalar properties; strings need a maximum length; one
  direction per order. The entity needs a single-column primary key, a mapped `ResourceId` and a table
  of its own (TPH is fine; owned types, views, TPT and TPC are not).
- SQL Server: the entry primary key is clustered, so `NodeId` (900 bytes at most) plus the order
  columns plus the key should fit in SQL Server's 900-byte clustered key. Table creation warns when the
  declared lengths could exceed it; resource ids and order values far below their maximum length are fine.
- SQL Server: EF Core logs `DecimalTypeKeyWarning` for a `decimal` order column, because it is part of
  the entry key. The entry column copies the source column's precision and scale, so values are never
  truncated; the warning can be ignored or suppressed with
  `ConfigureWarnings(w => w.Ignore(SqlServerEventId.DecimalTypeKeyWarning))`.
- The permission's resource-type restriction is not applied by read 2; the final check applies it. A
  type-restricted permission over rows of another type therefore produces short pages, never wrong rows.
