# Native scope indexes for authorized pages

An indexed page should be able to jump into a caller's granted branches, even when most rows in the application belong elsewhere. Native scope indexes add that access path while preserving SqlOS's resource tree, principals, grants, and `BuildFilterAsync` API.

```csharp
// Normal EF configuration: declare the order used by this screen.
modelBuilder.Entity<Document>().HasIndex(d => new { d.Name, d.Id });

var allowed = await authorization.BuildFilterAsync<Document>(subjectId, "document.read");
var page = await db.Documents.Where(allowed)
    .OrderBy(d => d.Name).ThenBy(d => d.Id)
    .Take(20).ToListAsync();
```

`AddSqlOS<TContext>` and `SqlOSDbContext` configure the integration automatically. A separately constructed, standalone context using the lower-level FGA interfaces can opt into the same integration with `options.UseSqlOSFga()`. Run normal SqlOS initialization after application migrations and before accepting queries. Initialization must run outside an application-owned transaction.

## What changes in the database

Application tables keep their normal columns and their `ResourceId` connection. SqlOS does not add application columns or indexes. A non-null resource link must name an existing resource; automatic `ISqlOSResourceEntity` synchronization creates the resource first. Null links, where the application mapping permits them, expose no authorized rows.

SqlOS maintains shared ancestry, a resource-to-application-key lookup, and copies of declared indexed values under each eligible ancestor. These live in the configured SqlOS schema. Application and resource triggers update them in the same transaction as the source write. SQL Server's EF save behavior is configured automatically for those triggers; application code does not need `HasTrigger` or `UseSqlOutputClause` settings.

Grants are not copied into these indexes. Permission, role, principal, group, and grant-window eligibility is evaluated when the page is queried. Changing a grant does not update every row it exposes. The data is shared between all principals.

First installation and a changed index model populate the affected structures from existing data. Unchanged startup does not rebuild or scan application data. Initialization tracks which context owns each projection, removes obsolete projections when their last owner relinquishes them, and preserves other contexts' tables. Contexts sharing a physical table must agree on the projected columns and orders; conflicting definitions fail initialization rather than replacing another context's indexes.

## How a page executes

The EF integration recognizes an ordinary authorization-filtered page and produces one SQL statement. The database finds the eligible grants, seeks the first matching rows within each granted scope, removes duplicates, selects the global page in the requested order, and retrieves the application records. Typed permissions use a scope-and-resource-type index. Overlapping grants do not duplicate records, and separate application rows attached to the same resource remain separate results.

Only the final page leaves the database. There is no grant list loaded into application memory, application-side sort, statistical selector, or saved pagination session. PostgreSQL and SQL Server use their own column types, collations, null ordering, and comparisons throughout.

## Supported queries and limits

- Ordinary single-table entities implementing `IHasResourceId`, with one supported scalar primary key. Inheritance, table/entity splitting, owned mappings, views, temporal queries, and deferrable PostgreSQL primary keys do not use the optimization.
- One positive `BuildFilterAsync` predicate, ordinary filters, a declared indexed order, and `Take`. A unique key makes ties deterministic; ordering terms after a unique key are redundant. Reversed index scans and mixed-direction declared compound orders are supported.
- Application and global filters are applied before each branch's limit. Filters on uncopied fields join the application table inside the branch. Such residual filters can examine more than a page of rows within an authorized scope.
- Keyset cursors are ordinary `Where` conditions. `Skip` must still visit the skipped prefix. Conditions after `Take` retain their normal LINQ placement.
- Counts, exports, unsupported compositions, includes, `IgnoreQueryFilters`, and orders without a maintained matching index preserve the original TVF query. They do not receive a page-work guarantee. Unsupported page shapes emit a diagnostic.
- Use stable database/schema mappings. Changing a connection's search path or default schema between initialization and querying is unsupported; use explicitly mapped schemas for applications that switch schema contexts.
- Database-native index width/type restrictions still apply. Initialization reports orders it cannot maintain rather than approximating their values. Resource IDs follow SqlOS's 450-character domain.

There is no guarantee that arbitrary filters, compound orders, counts, or exports take constant time. The improvement removes the need to scan unrelated application rows solely to find an authorized indexed page.

## Costs and tradeoffs

Let `D` be resource depth, `G` the caller's independently granted eligible scopes, `k` the requested page size, and `I` the number of maintained orders. Normal index lookup and database filter costs still apply.

| Operation | Work added by this design |
| --- | --- |
| Dense or sparse indexed page | One ordered branch lookup per eligible scope, at most `G × k` matching candidates before global deduplication and sorting; residual filter work is additional. |
| Later or empty page | Same branch lookups at the fresh cursor; an empty result checks every eligible scope. No scan of unrelated rows is required for the tested indexed shapes. |
| Direct grants on many separate resources | Still proportional to those grants. This design does not add a per-principal direct-grant sort index. |
| Application insert, update, or delete | One binding entry and up to `D` ancestor copies, maintaining their declared indexes. No work per principal. |
| New resource | Ancestor construction plus indexed lookups in registered application mappings; no scan of every application table. |
| Grant, revoke, role, or membership change | No scope-index materialization work; normal policy-table writes. |
| Move or deactivate a resource subtree | Recompute affected descendants and their attached application rows. This is an administrative operation proportional to the affected data. |
| Storage | Shared row/ancestor copies and their `I` declared indexes, plus one binding per application row. No caller factor and no sorting permutations. |

Writes share a topology gate; structural hierarchy changes take exclusive access. PostgreSQL additionally updates a resource version marker to detect stale snapshots. Concurrent writes to the same resource or parent can contend, and conflicting transactions may need the database's normal retry handling. These costs belong to write consistency, not principal count.

The original TVF remains the reference authorization predicate and serves unsupported query shapes. The new integration tests compare native pages against independent authorization results, exercise ordinary writes and concurrent moves on both engines, and inspect actual plans for sparse first, later, and empty pages. Physical-plan measurements establish the tested cases, not a universal optimizer guarantee.
