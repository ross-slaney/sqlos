# FGA page orders

How `ToPageAsync` returns an authorized page by any order for a cost bounded by the rows the caller
can see, and for about a page of work when the order is declared, whoever the caller is, with nothing
of SqlOS's on application tables. Issues #515 and #521.

## The problem

An authorized list used to be `.Where(filter)` over `fn_IsResourceAccessible` (`BuildFilterAsync`).
The database reads the table in the page's order and checks each row until the page is full. A
caller who can see a few rows of a large table makes it read most of the table, and nothing in the
code says so: the query compiles, returns the right rows and passes every test. `BuildFilterAsync`
is removed; `ToPageAsync` is the one way to read an authorized list, and `CheckAccessAsync` the one
way to check one resource. There is no authorized count, `Any`, join or unpaged list.

## The idea

For every protected entity, SqlOS keeps, in its own schema, a **node index**: which rows sit under which
node of the resource tree. A page by any order races two reads with growing budgets: the application's
query in its order, each row checked, and the caller's own rows from the node index, sorted through the
query. Whichever settles first is the page, so a page costs a small multiple of the smaller of "rows the
query needs" and "rows the caller can see".

For an order declared with `HasPageOrder`, SqlOS also keeps a copy of that order **under every node of
the resource tree**. A page for anyone is then "the first rows under each of my nodes, merged": one
index seek per node, about a page.

```csharp
modelBuilder.Entity<Product>(entity =>
{
    entity.HasPageOrder(p => p.Price);                       // optional: (Price, Id)
});

var page = await db.Products
    .Where(p => p.Name.Contains(term))
    .OrderBy(p => p.Price)                                   // any order
    .ToPageAsync(subjectId, "product.view", pageSize: 20, cursor);
```

`fn_IsResourceAccessible`, `CheckAccessAsync` and the FGA schema scripts are unchanged.

## Storage

One table per page order, in the FGA schema, added to the application's EF model as a shared-type
entity (`Dictionary<string, object>`), so the application's migration creates it. Every root entity type
of a `SqlOSDbContext<TContext>` model that implements `IHasResourceId` gets one for its key alone, the
node index (`SqlOSFgaPage_{Table}_{Key}`, columns `NodeId` and `RowKey`); each `HasPageOrder` adds one
more. A TPH hierarchy has one node index, on its root. An entity that cannot have page orders (owned,
view-mapped, TPT/TPC, no or a composite primary key, no mapped `ResourceId`) gets no node index and does
not fail the model: startup logs one warning per entity naming the reason, and its pages run read 1 only.
A declaration on such an entity still fails the model build. Declaring the key alone is the node index,
a no-op duplicate. SqlOS's own entities implement none of this.

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

Size: depth-of-the-row entries per row per table (a product four levels under the root has five in the
node index and five in each declared order).

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

On PostgreSQL, `fn_PageNodes` is shaped for the planner; each point was measured with the benchmark:

- Minimality is a set difference (`EXCEPT`): the caller's active granted nodes (`fn_ResourceAncestors`
  returns the node at depth 0) minus those with a granted node among their ancestors. A correlated
  `NOT EXISTS` against the granted CTE scans the materialized CTE once per granted node: 10,000 single-row
  grants took 6.5 s, and take 0.3 s this way.
- Grants are not joined to the resource table to check that the node is active (the depth-0 row does
  that), because a misestimated grant count makes the planner scan the whole resource table.
- The function is declared `ROWS 10` and `SET jit = off`. A function with a `SET` clause is not inlined,
  so the page query plans with its row estimate, and its body runs without JIT. Inlined, the query's
  estimated cost crossed `jit_above_cost` and JIT compilation added about half a second to every page
  that read 2 answers, against about 20 ms of work.

SQL Server plans the straightforward form (`NOT EXISTS`, a join to the resource table) well, so its
function keeps it. Both return the same nodes; the integration tests run on both engines.

## The page

`ToPageAsync` finds the query's context (`SqlOSFgaQueryContexts`: each `SqlOSDbContext` registers
itself under its query provider when it is constructed, which a pooled context keeps across
leases), splits the query into its ordering and the rest (`SqlOSFgaOrderReader`), decodes the cursor
(`SqlOSFgaPageCursor`), and resolves the caller (`SqlOSFgaPrincipals`: the subject and its active
groups in one statement, then the permission; the same code the point checks use). An unknown
subject or permission is an empty page with no page statements. Then it runs the loop in
`SqlOSFgaPageQuery` with a budget `b` that starts at the page size `P`:

1. **Read 1, "your query, checked."** The next `b` rows of the application's query in its order, each
   with a visibility flag (`SqlOSFgaVisibility`: the `fn_IsResourceAccessible` `EXISTS` in the projection). If at least `P` are visible, they
   are the page. If fewer than `b` rows came back, the query is exhausted and the visible rows are the
   last page.
2. **Read 2, "the caller's nodes, in order."** Only for a declared order whose table exists. Under each
   node of `fn_PageNodes`, the next `b` entries after the cursor that pass the application's filters (an
   `EXISTS` on the row's key against the query without its ordering), merged in order, at most `P`.
   `P` entries are the page; fewer means every node ran out, and they are the last page. Because `b`
   is never below `P`, read 2 settles the page in the round it runs.
3. **Read 3, "the caller's rows, sorted."** For every other order, when the entity's node index exists.
   Two statements, so that neither plan depends on the other's parameters:
   - **3a.** Under each node of `fn_PageNodes`, the node index's `RowKey`s, at most `b + 1` in all (per
     node `ORDER BY RowKey` with `TOP(b + 1)`, so each node is a range of the primary key that stops at
     the cap; the merge `ORDER BY (SELECT 1)` with `TOP(b + 1)`, which neither engine sorts by and
     which keeps EF Core from warning about a limit without an order). More than `b` keys: not this
     round. The node index has no order columns, so 3a reads the caller's rows whatever the cursor.
     The nodes are first sorted by the length of their id (`ORDER BY LEN(NodeId)` in a `TOP` subquery),
     an order no index provides, meant to make the engine produce all of them before the cap applies:
     otherwise SQL Server gives the cap's row goal to `fn_PageNodes`, whose minimality check then
     rescans the caller's grants for every ancestor of every node it reads (1.55 million spool rows at a
     cap of 21 for a caller with 10,000 single-row grants). The sort held in the 100,000-row tests, but not
     in the page-order benchmark at a million rows: SQL Server moved it below the check, and one round at
     a cap of 1,281 read 69 million spool rows in 25 s for that caller. The caller's grants and nodes stay
     in the database, so 3a stays one statement, `fn_PageNodes` joined to the node index, run in every
     round.
   - **3b.** With at most `b` keys, they are all of the caller's rows: the query without its ordering,
     `Where(e => EF.Parameter(keys).Contains(e.Key))`, then the keyset after the cursor, the order and
     `Take(P + 1)`, projected to the order values. The keys go as one parameter (`OPENJSON` on SQL
     Server, an array on PostgreSQL), past SQL Server's 2,100 parameters. The key filter sits in a
     subquery of its own (`OrderBy(constant).Skip(0)`), so it is looked up by key, then sorted: otherwise
     PostgreSQL can walk the order's index and test every row against the keys, which reads every row
     before a far-end caller's (and EXPLAIN does not show it). More than `P` rows: the page and a
     cursor; otherwise the last page.
4. Otherwise `b` grows four times and the round runs again. Read 1 runs first in every round, so a
   caller who sees most rows settles the page in round 0 with no read 3 statement. A declared order
   never runs read 3; an entity with no node index, or whose node index table startup found missing,
   runs read 1 only.

**Load.** The page's keys go back through the query with its includes and tracking and through the
visibility predicate once more, then are ordered by key position in memory. That is the final check: a
stale or wrong entry can only drop a row, never add one.

The predicate passes the context to `IsResourceAccessible` the way a C# closure does, so EF Core makes
it a query parameter. As a constant (what `BuildFilterAsync` built), every context instance was a
new compiled query; a test pins that a page on a second context compiles nothing. The cursor is the last selected row's order values,
so a dropped row does not stall the list.

Each statement is an EF Core query (`ToQueryString` can print it). Reads 2 and 3a project the node's id
inside the per-node subquery; that keeps the subquery correlated, so EF Core emits `CROSS APPLY (SELECT
TOP(@b) …)` on SQL Server and `JOIN LATERAL (… LIMIT @b)` on PostgreSQL. Without it EF Core rewrites the
subquery into a `ROW_NUMBER() OVER (PARTITION BY NodeId)` join over every entry.

**The bound.** `R1` is the rows read 1 needs (the query's rows from the cursor up to the page's last
visible row, or all of them), `V` the rows under the caller's nodes. The rounds stop at the first budget
`b_k ≥ min(R1, V)`, and `b_k < 4·min(R1, V)` unless `k = 0`. Read 1 reads `Σ b_j < 4/3·b_k`, 3a reads
`Σ_{j<k} (b_j + 1) < b_k/3 + k` plus `V` in the round it settles, 3b `V` keys and the load at most `P`, so a
page touches fewer than `26/3·min(R1, V) + k + P` rows and entries: about nine times the smaller, plus a
page. For rows spread through the order with density `V/N`, `R1 ≈ P·N/V`, and the smaller is largest at
`V = √(P·N)`: 4,472 for a million rows and `P = 20`. Bunched at the far end, `R1 ≈ N` and the page costs
about `V`. When the index read 1 walks does not include `ResourceId`, SQL Server reads each row it checks
twice, once in that index and once by the key lookup for `ResourceId`. Counted that way, read 1's term
doubles and the bound is `14·min(R1, V) + k + P`.

One line per page is logged at Debug under `SqlOS.Fga`: entity, order, whether it is declared (and
whether its node index is there), the rounds, rows per round of read 1, entries per node of read 2, keys
per round of read 3, which read produced the page, rows the final check dropped, elapsed time. Nothing is
logged above Debug: a plain order is the normal path.

## Maintenance

All in `SqlOSFgaPageOrderMaintenance`, called from `SqlOSDbContext<TContext>.SaveChanges` /
`SaveChangesAsync` after the resource synchronizer:

1. Before the save, a check: does the change tracker hold an added, modified or deleted resource, or row
   of an entity with a page order? No: a plain `base.SaveChanges`.
2. Otherwise, in a transaction: the caller's, inside a savepoint (as EF Core does for the save alone, so a
   failure leaves it as it was before the save); an ambient or enlisted `System.Transactions` one; or one
   owned here, inside the context's execution strategy. Save with `acceptAllChangesOnSuccess: false`, then
   collect what the save wrote: added and deleted rows; modified rows whose `ResourceId` or an order column
   changed, or with an order column the database computes on update; resources added, deleted, or whose
   `ParentId` or `IsActive` changed. Collecting after the save sees values set by `SaveChanges`
   interceptors and `SavingChanges` handlers, cascade deletes and generated keys.
3. One command for everything collected. Per order:
   - delete the entries of the rows under the changed resources: under the moved, deactivated or
     reactivated ones and their descendants (a recursive CTE over `ParentId`, bounded by the depth), and
     under the added or deleted ones themselves, without a walk. `ParentId` is a foreign key, so the only
     resources under an added or deleted one are added or deleted in the same save, and are anchors too.
     The ids anchor the lookup, so a deleted resource still names the rows that point at it and an added
     one finds the rows saved before it. Walking from every changed resource made a save that adds a
     thousand resources take seconds on PostgreSQL: it estimated the walk from a thousand anchors at
     millions of rows and hashed the whole entry table instead of looking the rows' entries up;
   - delete the entries of the changed rows, by key (added rows too, so a stale entry left by a write that
     bypassed EF cannot make the insert of a reused key fail);
   - insert the entries of the rows under the changed resources, except the changed rows;
   - insert the entries of the changed rows.

   Each row ends with exactly one set of entries. Key lists are one parameter per entity: `OPENJSON(@keysN)`
   on SQL Server, `= ANY(@keysN)` on PostgreSQL. Any failure rolls everything back. On PostgreSQL the
   command turns JIT off for itself and puts the transaction's setting back after it
   (`set_config('jit', 'off', true)`): the subtree statements' estimated cost (a recursive walk times the
   inlined `fn_ResourceAncestors`) crosses `jit_optimize_above_cost`, and in the benchmark compiling them
   took over half a second, against well under a millisecond of work, on every save that adds a resource.
4. Accept the changes.

This covers every write that ends in `SaveChanges` on a `SqlOSDbContext`: application rows,
`ISqlOSResourceEntity` synchronization, `SqlOSErgonomicsExtensions`, the dashboard's resource edits and
the seed service. `ISqlOSFgaAuthService.RebuildPageOrdersAsync` (delete all, insert all, one transaction
and one application lock per order, no command timeout) covers the rest:

- writes that bypass EF Core: bulk loads, raw SQL, `ExecuteUpdate`/`ExecuteDelete`, triggers;
- an interceptor that changes paged rows in a save that changes no paged row or resource (step 1 sees
  nothing to do);
- two concurrent saves, one moving or deactivating a resource and one adding or changing rows under it.
  With snapshot reads (PostgreSQL, SQL Server with `READ_COMMITTED_SNAPSHOT`) each reads the other's rows
  as they were, so those rows can keep entries on the old path. With locking reads they wait for each other,
  and one can fail with a deadlock. Either can fail with a duplicate key. Closing this takes a lock that
  every save writing entries would contend on; it is left open because moves are rare, and a stale entry
  never shows a row the caller may not see (the final check).

Until a rebuild, rows those writes inserted are missing from pages and rows they changed page at their old
position; a row the caller may not see is never returned. Saves during a rebuild wait for it, or make it
fail with a deadlock or duplicate key; it is meant for quiet periods, and running it again fixes what they
left.

**Startup.** After the functions are installed, `SqlOSBootstrapper` logs, at Warning, each protected entity
that has no node index and why. It then fills the entry table of every page order (node index or declared)
whose table exists and is empty while its application table has rows: the first start after an upgrade
or a declaration needs no manual step. The fill is a rebuild of that table (application lock, no command
timeout); other instances starting at the same time wait for it and then find the table filled. A missing
table is logged at Warning and skipped; a missing declared table makes its order plain, and a missing
node index makes that entity's plain orders run read 1 only, until it exists. In a rolling deployment,
instances still on the previous version write rows without entries, so the rollout ends with a rebuild.

Because every protected entity has a node index, `MayChange` is true for any save that touches a resource
or a protected row, even in an application that declares nothing: those saves run in a transaction with
their entry statements.

Contexts that implement `ISqlOSFgaDbContext` by hand get no maintenance, and `ToPageAsync` refuses them.

## Cost

`P` page size, `k` the caller's minimal granted nodes, `D` tree depth, `M` subjects (the caller and
their groups), `N` rows in the table, `f` the fraction of rows under the caller's nodes that pass the
application's filters.

`R1` and `V` as above. A plain order (no declaration) costs:

| Case | Cost | Why |
| --- | --- | --- |
| Dense list, scoped or not (admin; plants in my garden) | O(P·D·M) | read 1 fills the page in round 0; no read 3 |
| Sees few rows (`V ≤ P`) | O(P·D·M + V) | round 0: read 1 misses, 3a finds `V` keys, 3b sorts them |
| Rows spread through the order | O(min(R1, V)), at most O(√(P·N)) | the rounds stop when the budget holds either |
| Rows all at the far end of the order | O(V) | read 1 finds nothing; read 3 settles once the budget holds `V` |
| `k` granted nodes | `k` node resolutions per round of read 3 | `fn_PageNodes` runs in every 3a statement |

A declared order costs:

| Case | Cost | Why |
| --- | --- | --- |
| Dense list, scoped or not | O(P·D·M) | read 1 fills the page |
| Sees 1 in 100, no filter | O(P·D·M + P) | read 1 misses, read 2 takes P entries under one node |
| 3 single-row grants under root, millions of rows | O((P + k)·D·M) | read 1 misses; 3 seeks, 3 entries |
| 100 scattered stores | O(k·P) worst, O(k + P) typical | one seek per node, at most P entries each |
| 10,000 single-row grants | O(k) | one entry per node |
| Sees half the table, rows all at the far end of the order | O(P) | P entries under their node |
| Application filter few rows pass, no scope | O(P / f) | entries read until P pass |
| Rare filter and a caller whose rows barely overlap it | O(rows the caller sees) | read 2 reads the caller's entries until P pass the filter |

Writes, with `T` the page-order tables of the entity (its node index plus its declared orders):

| Case | Cost | Why |
| --- | --- | --- |
| Insert, or update of `ResourceId` | O(D × T) | entries written in the same transaction |
| Update of an order column | O(D × declared orders using it) | the node index has no order columns |
| Add a resource | O(T) lookups by `ResourceId` | rows saved before it get entries |
| Move, deactivate or reactivate a resource | O(subtree × D × T) | that subtree's entries rewritten |
| `RebuildPageOrdersAsync` | O(N × D × T) | once |

Nothing is linear in `N` except the rebuild.

## Limits and engine notes

- The cursor carries the last row's order values, with non-ASCII text unescaped, up to 65,536 characters.
  Values that need more throw when the cursor is issued; order columns that a page order indexes are far
  below that, bounded by the engine's index key size.
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
- The permission's resource-type restriction is not applied by reads 2 and 3; the final check applies it.
  A type-restricted permission over rows of another type therefore produces short pages, never wrong rows.
- Ordering by the key alone matches the node index, so it is served by read 2 over the node index: about a
  page, with no declaration.
- PostgreSQL: when the caller's rows are a percent or more of the table, the planner can read 3b's keys
  with one parallel sequential scan of the table instead of key lookups, which it estimates cheaper; the
  page then reads the table once, without the per-row check. In the benchmark (20,000 keys of a million
  rows) that scan took about 40 ms; with parallel query off the same statement looks the keys up.
- PostgreSQL: read 1 inlines `fn_IsResourceAccessible` for each row, so its estimated cost grows with the
  budget and crosses the JIT thresholds. In the benchmark, read 1 was compiled from a budget of 1,280 rows
  (about 90 ms of compilation) and optimized and inlined from 5,120 (about 1.3 s, against about 0.8 s of
  work). `fn_PageNodes` and the save statements run without JIT; read 1 cannot, short of changing the
  access check. Deployments that serve OLTP traffic often turn `jit` off.
