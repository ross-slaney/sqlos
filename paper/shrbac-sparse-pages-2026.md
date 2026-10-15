# Index What You Sort By: Bounded Authorized Pagination for Hierarchical Authorization

Ross Slaney

**Abstract.** Most business software is organized as a tree: a company has divisions, divisions have regions, regions have stores, stores have products. Access follows the tree. A regional manager may see everything in her region and nothing outside it. Our earlier paper introduced SHRBAC (Scoped Hierarchical Role-Based Access Control), a model that turns this shape into a single SQL predicate, and showed that a page of authorized rows costs about a page of work when the caller can see most of the table. This paper addresses the case that paper left open: a caller who can see very little. When a store manager asks for the twenty cheapest products of a million, the database must read the table in price order and test each row until twenty pass, and with one store in ten thousand it reads 184,205 rows for one page on PostgreSQL and 368,410 rows on SQL Server. We present a mechanism that makes that page cost about a page for every caller. For every index on a protected table, the database keeps a copy of that index under every node of the resource tree, in tables of its own. A page is then the first rows under each of the caller's granted nodes, merged. Nothing is added to application tables, grants never leave the database, every returned row is still verified by the original predicate, and the only rule a developer follows is the one every database already has: index the columns you sort by. We state the design constraints the solution had to meet, describe seven alternatives we built and measured before arriving at it, prove the mechanism sound and complete, bound its cost, and evaluate it on SQL Server 2022 and PostgreSQL 16 with 1,000,000 rows across dense, sparse, scattered, far-end and many-grant callers. The store manager's page falls from 8,420 ms to 22.6 ms on PostgreSQL and from 179,558 ms to 89.0 ms on SQL Server, reading 80 and 100 rows respectively, while an administrator's page stays within a few milliseconds of its previous cost. The price is storage and write amplification proportional to the number of indexes times the depth of the tree, which we measure.

---

## 1. Introduction

### 1.1 Why hierarchies, and why now

Ask a business how it is organized and you will hear a tree. A retailer has divisions, chains, regions and stores. A law firm has clients, matters and documents. A software product has organizations, workspaces, projects and items. Authority follows the same tree: the person responsible for a region is responsible for every store in it, and nothing outside it. Saltzer and Schroeder's principle of least privilege [2] asks that every principal hold only the authority its job needs, and in an organization that authority is almost always "this subtree, with this role, for this period".

The authorization industry has converged on this shape. WorkOS describes its fine-grained authorization product as "hierarchical, resource-scoped access control" in which resources are arranged in a hierarchy and permissions flow down it, and documents the organization, workspace and project example that most B2B products share [21]. Auth0's fine-grained authorization service and the OpenFGA project it is built on model parent relationships between objects so that permission on a folder cascades to its contents [22]. Google's Zanzibar, which authorizes Drive, YouTube and Cloud, stores folder containment as relationships so that permission on a folder reaches what the folder contains [1]. Dropbox defines what a user can see by the set of namespaces, which are shared folders and team folders, that she was granted [23]. Figma's permission system walks from a file up through its project, team and organization: "roles are hierarchical, so you might have access to the file through your role on a parent folder, team, or organization" [24]. Box, Google Drive and Notion all inherit permissions down their folder or page trees; Notion's engineers chose an explicit "upward pointer", the parent attribute, as the basis of their permission system [25]. Microsoft Azure's role-based access control inherits assignments from management group to subscription to resource group to resource [16]. These companies compete on everything else. On the shape of authority, they agree.

They also agree, by their documentation, on what is hard about it. Checking one object is a solved problem: Zanzibar serves point checks at millions per second [1]. Listing objects is not. OpenFGA's ListObjects call stops by default after 1,000 results or 3 seconds, and its maintainers explain that it cannot be paginated because "the graph evaluation is done concurrently (and thus is not ordered)" [26], [27]. AuthZed advises using LookupResources only "if the number of resources that a user has access to is small (e.g. less than 10,000 resources)" and sells a separate product, Materialize, that copies computed permissions "next to your application data" so that applications can "sort, filter, and paginate over many thousands of authorized objects natively in your own database" [28], [29]. Auth0's documentation offers four strategies for "search with permissions" and a decision table for choosing among them, and warns that ListObjects performance "varies drastically depending on your authorization model complexity and tuple distribution" [22]. Every vendor, in other words, tells its customers that a sorted, filtered, paginated list of what a user may see is the expensive question, and answers it with limits, deadlines or a second copy of the data.

### 1.2 What the first paper showed, and what it left open

Our earlier paper [30] formalized SHRBAC and showed that its four structural constraints, a tree of resources, flat roles, non-recursive groups and scoped time-bounded grants, let the whole access decision be one inline table-valued function (a SQL function the query planner expands into the calling query, which we abbreviate TVF). An application lists authorized rows by appending that function as a `WHERE` condition to its own query, with its own filters, sort order and cursor. On SQL Server 2022 with 1.2 million resources, a page of twenty rows cost about 3.5 ms and did not grow with the size of the table.

That paper also said, in its complexity theorem and again in its limitations, where the argument stopped. The cost per page was O(k · D) for a caller who can see most rows, where k is the page size and D the depth of the tree; for a caller who can see a fraction σ of the rows it was O(k / σ · D), and the evaluation covered only "the evaluated dense-access workloads". The sparse case was named as "an empirical failure mode to stress-test" [30, §8.4].

This paper is that stress test, and the repair. The sparse case is not an edge case. It is the common case in a multi-tenant product: of the million rows in a shared table, a given customer's user may see a thousand, a store manager a hundred, a contractor three. We measured what the first paper's method does for such callers, found it reads most of the table for every page, and then set out to make a page cost about a page for every caller without giving up anything the first paper established.

### 1.3 The problem in plain words

Suppose a million products sit under ten thousand stores, and a store manager asks for her products sorted by price, twenty at a time. The database has an index on price, so it can read products from cheapest to dearest without sorting. But the index does not know which products are hers. The only way to find out is to test each product with the access function: walk from the product up through its store, region, chain and division, and look for one of her grants. Her hundred products are scattered through the price order like a hundred grains of sand in a bucket, so to find the twenty cheapest of them the database tests, on average, a fifth of the bucket. We measured 184,205 rows read for her first page on PostgreSQL and 368,410 on SQL Server. A caller whose rows all sit at the expensive end of the price order is worse: the database tests nearly the whole table before it finds the first one.

No index on the application table can fix this, because "visible to this caller" is not a column. It is a function of the caller's grants and of the row's position in the tree, and it changes from caller to caller and from grant to grant. The query planner cannot know how sparse a caller is when it chooses a plan, so it cannot choose a plan for her. We return to this point in Section 3.3, because it explains why several reasonable designs failed.

### 1.4 Contributions

1. **Measurement of the sparse failure.** We quantify, on SQL Server 2022 and PostgreSQL 16 with 1,000,000 rows, how the first paper's per-row predicate behaves as a caller's share of the table falls from 100% to 0.01%, and for a caller whose rows sit at the far end of the sort order (Section 3).
2. **Design constraints stated as rules.** We list the nine rules the solution had to satisfy, including rules that are easy to break by accident, such as "grants never leave the database" and "no decision made from an estimate", and explain why each exists (Section 4).
3. **Alternatives built and measured.** We describe seven designs implemented and benchmarked between September 30 and October 14, 2026, closure tables, lineage columns, an adaptive tree walk, scope-index projections, a single predicate over a closure, declared page orders, and a scan-versus-list race, and which rule each one broke (Section 5).
4. **The mechanism.** For every index on a protected table, a copy of that index kept under every node of the resource tree; a page algorithm with two reads and a final check; same-transaction maintenance; and one developer rule, "index what you sort by", with no authorization-specific declaration (Section 6).
5. **Correctness and cost.** Soundness through the original predicate, completeness through an entry rule that mirrors the predicate's reachability, and a per-page bound of O(P · D · M + k · P · log k), independent of the table size N and of the caller's selectivity σ, together with storage and write bounds (Section 7).
6. **Evaluation.** Both engines, 1,000,000 rows, seven callers plus a selectivity sweep, storage per table, write amplification, and the measured behaviour of the rejected race (Section 8).

**Notation.** N is the number of rows in the paged table; P the page size; D the maximum depth of the resource tree; L the number of levels a row's resource path has, counting the row's own resource and its ancestors, so L ≤ D + 1; M the number of effective principals of the caller (the user and her groups); σ the fraction of rows the caller may see; k the number of the caller's *minimal granted nodes*, defined in Section 6.5; and I the number of indexes on the paged table that are sorts. Every time in this paper is in milliseconds (ms) or seconds (s), every size in megabytes (MB), and every "rows read" count is the number of rows the database's own execution plan reports its operators produced for the statement, as defined in Section 8.1.

### 1.5 Paper organization

Section 2 recalls the SHRBAC model and surveys how other systems list under a hierarchy. Section 3 measures the sparse failure and explains why the query planner cannot repair it. Section 4 states the design rules. Section 5 describes the designs we rejected. Section 6 presents the mechanism, Section 7 its correctness and cost, Section 8 the evaluation, Section 9 the discussion and limitations, and Section 10 concludes.

---

## 2. Background and Related Work

### 2.1 The SHRBAC model, briefly

We restate the parts of SHRBAC [30] this paper depends on; the full definitions, properties and proofs are in that paper.

Resources form a rooted tree of bounded depth D. Every row of a protected application table carries a `ResourceId` naming a node of that tree. Principals are users, groups, service accounts and agents; a user resolves to herself and her groups, and groups do not nest, so resolve(p) is a fixed small set of size M. Roles are flat lists of permissions. A grant is a tuple (principal, role, resource, from, to): it gives the role at that resource and, by inheritance, at every resource below it, during the time window. A node may be marked inactive, which cuts access to it and to everything below it.

The access decision for principal p, permission perm, resource r and time t is:

```
allowed(p, perm, r, t) =
  ∃ p' ∈ resolve(p), ∃ r' ∈ ancestors(r), ∃ role:
    (p', role, r') is an active grant at t,
    perm ∈ perms(role),
    and every resource from r up to r' is active.
```

The first paper realized this decision as an inline table-valued function, `fn_IsResourceAccessible(resourceId, principalIds, permissionId)`, whose recursive common table expression walks from r up to the root in at most D steps and joins the active grants at each step. It proved the function sound and complete (its Theorems 1 and 2) and showed that, when appended as `WHERE EXISTS (...)` to an application query with cursor pagination on an indexed sort key, a page costs O(k · D · M · G_max) for a dense caller, where G_max bounds the number of grants a principal holds on one resource. For a caller who sees a fraction σ of the rows the bound is O(k / σ · D · M · G_max), and when the caller sees nothing it is O(N · D · M · G_max): the whole table.

Everything in this paper keeps that function unchanged. It remains the point check, the final check on every page, and the definition of truth.

### 2.2 How the industry lists under a hierarchy

The systems named in Section 1.1 answer "which objects may this user see, sorted and paginated" in four ways. We describe each plainly, because our design borrows from two of them and rejects two.

**Check each candidate.** The application fetches a page of candidates from its database in the desired order and asks the authorization service about each one, repeating until it has a full page. Zanzibar describes search this way: "serving search results ... often require tens to hundreds of checks" per query [1]. Auth0's "search then check" and OpenFGA's batch-check pattern are the same idea [22], and AuthZed's "CheckBulkPermissions with pagination" notes that "because we don't know how many results are going to be accessible beforehand, we need to iterate until" the page fills [28]. This is exactly the per-row predicate of the first paper, moved out of the database, and it has the same sparse-case cost: the number of candidates tested is k / σ.

**Expand the graph backwards.** The service starts from the user and walks the relationship graph outward to enumerate every object she can reach: OpenFGA's ListObjects, SpiceDB's LookupResources, Permify's Lookup Entity, WorkOS's resource discovery [22], [26], [28], [21]. The result is an unordered set of identifiers which the application then intersects with its own query. This is why the calls are capped and unpaginated: the walk has no order, its size is the number of objects the user can see, and the application still has to sort and page the intersection. Auth0's documentation says a partial list "is not enough, because you won't be able to sort using it" [22].

**Materialize permissions next to the data.** Zanzibar's Leopard index precomputes flattened group reachability [1]; AuthZed Materialize streams computed permission changes into the customer's database so that sorting and paging can happen there [29]; Auth0 FGA documents a "Permissions Index" of precomputed direct relationships with the stated trade-off "do the expensive work when permissions change" [22]; Slack keeps "its own indexed copy of the message" for each organization in a shared channel [31]. These are the closest relatives of our design: they accept storage and write cost in exchange for a list that the database can sort and page. They differ from ours in two ways. They are eventually consistent, updated by watching a change stream, and they copy *permission* facts, which change when grants change, whereas we copy *structure* facts, which change only when rows or the tree change.

**Compile the policy into the query.** Oso's local authorization returns a SQL `WHERE` fragment [32], Cerbos's PlanResources returns a condition tree to be translated into a query [33], and Open Policy Agent's compile API returns a filter by partial evaluation [34]. These push authorization into the database, as the first paper did, and therefore inherit the per-row cost model of a predicate: the engine still tests candidates in order until a page fills.

### 2.3 Database-native enforcement and its cost model

Row-level security in PostgreSQL and SQL Server is Stonebraker and Wong's query modification [4] built into the engine. Its documentation states the cost model plainly. PostgreSQL evaluates a policy expression "for each row prior to any conditions or functions coming from the user's query" [35]. Supabase's performance guide repeats it: "Postgres evaluates a policy expression against each candidate row, so the cost scales with the rows a query scans", and its advice begins with indexing the columns the policies filter on, wrapping function calls so they run once per query, and adding explicit filters to queries rather than relying on the policy alone [36]. Its worked example, a membership check over a million rows, goes from over 180 seconds to 2–3 ms once the membership set is materialized into an array and the column is indexed [36]. SQL Server's best-practice guidance for its row-level security says to "avoid recursion in predicate functions wherever possible" [37], and practitioners advise flattening hierarchies at load time rather than walking them per query: "every join added to a predicate degrades performance at scale"; "denormalize security lookup data" [38].

The lesson is the one the first paper's own theorem states: a per-row predicate costs the number of rows tested, and the number tested is the number of candidates before the page fills. No amount of indexing of the predicate's inputs changes that count. Something has to make the authorized rows *contiguous* in the order the query wants.

### 2.4 Storing hierarchies so that subtrees are contiguous

Database designers have long known how to make a subtree contiguous in one order. SQL Server's `hierarchyid` type sorts in depth-first order, so "all nodes in the subtree of a node are colocated" in a depth-first index [39]. Google Spanner's interleaved tables physically store child rows under their parent's key, up to seven levels deep [40]. Closure tables store one row per (ancestor, descendant) pair, so that "querying it is extremely fast and simple" at the cost of moves, which must rewrite the pairs of the moved subtree [41]. Materialized paths and nested intervals are the other classic encodings [42].

Each of these gives one order: the tree's own. A product listing sorted by price needs the subtree in price order; sorted by name, in name order. The structure we describe in Section 6 can be read as a closure table extended by the sort key: one row per (ancestor, sort value, row), with a separate copy per index. It trades storage, one copy per level per index, for the property that every subtree is contiguous in every indexed order. Merging the first P rows from each of k sorted ranges is the classic multi-list top-P problem [43], and the database does it with a correlated subquery per node and an `ORDER BY ... TOP (P)` over the union.

### 2.5 Where this paper sits

Among the four industry patterns, our design is a materialization, but of structure rather than of permission, maintained in the same transaction as the write rather than by a change stream, and verified on every page by the original predicate. Among the database techniques, it is a closure table per sort order. What is new is the combination: a bounded page for every caller under a hierarchical model, with no columns, triggers or indexes added to application tables, with grants never leaving the database, with one implementation on two engines, and with no declaration beyond the indexes a developer would write anyway.

---

## 3. The Sparse Access Problem

### 3.1 The workload

Throughout this paper we use one retail workload, built with the SqlOS library [44], the open-source implementation of SHRBAC, on both database engines. The resource tree is root → 5 divisions → 10 chains per division (50) → 10 regions per chain (500) → 20 stores per region (10,000) → 100 products per store (1,000,000). Every product has its own resource node, so a product's path has six levels: itself, its store, region, chain, division and the root. Prices are spread evenly through the products, except that one chain's 20,000 products carry the highest prices of all; that chain is the "far end" caller's. The application table `Products` has an index on `(Price, Id)`, which is what any application sorting by price would have.

Seven callers represent the range of real users. Each holds grants exactly on the nodes its name says, with a role carrying the `product.view` permission:

| Caller | Grants | Products visible | Share of the table |
| --- | --- | ---: | ---: |
| Administrator | one, on the root | 1,000,000 | 100% |
| Region manager | one, on a region | 2,000 | 0.2% |
| Store manager | one, on a store | 100 | 0.01% |
| 100 stores | 100, one per store | 10,000 | 1% |
| 10,000 single products | 10,000, one per product | 10,000 | 1% |
| Far end | one, on the high-price chain | 20,000 | 2%, all at the end of the price order |
| Scoped store manager | one, on a store; the query also filters by `StoreId` | 100 | 0.01%, but the query itself selects only her store |

The query is always "products sorted by price, twenty at a time", with a cursor for the next page. The scoped caller's query adds `WHERE StoreId = @mine`, the way a screen that already knows its store would.

### 3.2 What the per-row predicate costs

Table 1 shows the first paper's method on this workload: the application query with `WHERE EXISTS (SELECT 1 FROM fn_IsResourceAccessible(...))` appended, `ORDER BY Price, Id`, and `TOP (20)`. The caller's principals are resolved once, before the clock starts, exactly as the first paper's benchmark did. "Rows read" is the count of rows the engine's execution plan reports for the operators over `Products`, taken from `SET STATISTICS XML` on SQL Server and `EXPLAIN (ANALYZE)` on PostgreSQL, counting on PostgreSQL also the rows a filter removed. The machine and measurement protocol are in Section 8.1; the point here is the shape, not the absolute times.

**Table 1. The first paper's per-row predicate at 1,000,000 products, page of 20, sorted by price.**

| Caller | Visible | SQL Server 2022: time (ms) | rows read | PostgreSQL 16: time (ms) | rows read |
| --- | ---: | ---: | ---: | ---: | ---: |
| Administrator | 1,000,000 | 23.7 | 40 | 5.6 | 20 |
| Region manager | 2,000 | 9,032 | 18,612 | 416.8 | 9,306 |
| Store manager | 100 | 179,558 | 368,410 | 8,420 | 184,205 |
| 100 stores | 10,000 | 2,227 | 4,466 | 107.6 | 2,233 |
| 10,000 single products | 10,000 | 1,420 | 2,904 | 70.8 | 1,452 |
| Far end | 20,000 | 942,593 | 1,960,040 | 44,208 | 980,020 |
| Scoped store manager | 100 | 18.4 | 40 | 5.4 | 20 |

Source: the SqlOS page-order benchmark, pull request #516, October 2026 [45]. SQL Server reads each row twice in these plans, once in the price index and once by key lookup for `ResourceId`, which is why its row counts are about double PostgreSQL's.

Three things stand out. The administrator and the scoped store manager are cheap: 20 rows read, a few milliseconds. Their rows are contiguous in the order the query wants, either because every row is theirs or because the query's own filter already narrows to their store. The unscoped store manager pays 184,205 rows and 8.4 seconds on PostgreSQL, and 3 minutes on SQL Server under emulation, for a page of twenty rows she is entitled to. The far-end caller pays almost the whole table: 980,020 rows and 44 seconds on PostgreSQL, 15.7 minutes on SQL Server. The region manager, who sees 0.2% of the table, is already 75 times slower than the administrator on PostgreSQL.

The problem was first seen in September 2026 in the library's continuous-integration benchmark, where a store manager's unscoped page took 33–43 seconds on PostgreSQL and 160 seconds on SQL Server at the same scale [46]. It is the O(k / σ · D) term of the first paper's Theorem 3 made concrete.

### 3.3 Why the planner cannot fix it

A natural hope is that a cleverer index, or better statistics, would let the query planner find the store manager's hundred rows directly. We tried this in two of the designs in Section 5 and it failed for a reason worth stating once, clearly.

A query plan is chosen before the query runs, from statistics about columns. The quantity that decides the right plan here is σ, the fraction of rows this caller can see. For the administrator the right plan is to walk the price index and test rows: she sees everything, so the first twenty rows are hers. For the store manager the right plan is to find her hundred rows first and sort them. But σ is not a property of any column. It is a property of the caller's grants, which the planner does not consult, and even when the grants were made visible to the planner as a parameter, the per-column statistics could not distinguish a root grant from a store grant: the column for the top level holds one distinct value, the root, so a predicate of the form "ancestor at level ℓ equals this node" was estimated as matching every row whatever node was supplied, and the cheap plan for a sparse caller was never chosen [47]. We observed the planner choose one plan for all callers and switch it as the table grew: at 100,000 rows PostgreSQL scanned the tree for everyone; at 1,000,000 it probed row by row for everyone, so the store manager read 400,000 rows [47].

The conclusion we drew, and which drives Section 6, is that something must know *before* the plan is chosen what this caller can see, and that this knowledge cannot be an estimate. The way to give the database that knowledge is to store, caller-independently, which rows sit under which node in which order, so that the caller's part of the question becomes a set of index seeks whose count is the number of her nodes, not a guess about her selectivity.

---

## 4. Design Rules

Before describing what we built, we state the rules it had to satisfy. They were fixed at the start of the work and several designs in Section 5 were rejected for breaking one of them even when they were fast. We state each as a plain requirement with its reason.

**R1. The resource graph is the truth.** The resource, grant, role and principal tables are the only authority on who may see what. Any other structure is a derived index, rebuildable from those tables and the application rows with one command, and every row a list returns is verified against the truth before it is returned. *Reason:* a derived index can be stale, after a bulk load or a bug, and a stale index must be able to hide a row but never to leak one.

**R2. Nothing on application tables.** The library adds one column to a protected table, `ResourceId`, and an index on it. No other columns, no triggers, no computed columns, no indexes owned by the library. *Reason:* application tables belong to the application and its schema tools; a library that decorates them surprises developers and database administrators and fights model-driven schema tools.

**R3. Grants never leave the database.** A statement that lists or checks carries exactly two caller-dependent parameters: the caller's principal identifiers and the permission identifier. It never carries a list of the caller's grants, granted nodes, roots or visible row keys, and the application never reads such a list into memory. *Reason:* the number of grants is unbounded, a parameter list is a scan in disguise, and the database can compute the set inside the statement.

**R4. One implementation on both engines.** The same tables, the same statement shapes, the same tests on SQL Server and PostgreSQL. Dialects may differ in syntax, never in structure. *Reason:* two implementations are two sets of bugs, and a design that only one planner can execute well is a design the other engine's users cannot trust.

**R5. One way, no estimates.** One list call, no modes or options, no threshold, no sampled count, no fallback chosen at runtime, and no reliance on the query planner choosing a good plan per caller. *Reason:* Section 3.3. A decision made from an estimate is right on average and wrong for the callers who matter, and a library that explains its performance with "the planner usually picks well" cannot promise anything.

**R6. The application's query stays the application's.** Filters, sort order, includes and cursor pagination are expressed in the application's own query language (LINQ over Entity Framework Core in our implementation). Any sort a screen offers must work, scoped or unscoped. *Reason:* the first paper's composability is the point of the model.

**R7. A page is never linear in the table.** The accepted budget during the design was about √(N · P · D) index operations in the worst case; the design achieves about P entries per granted node. *Reason:* a cost that grows with the table is a cost that arrives unannounced when the product succeeds.

**R8. Consistency in the write's transaction.** Whatever the library stores is updated in the same transaction as the application's write, so a committed row is visible in lists immediately and a rolled-back write leaves nothing behind. *Reason:* eventually consistent authorization indexes are the complexity the vendors in Section 2.2 ask their customers to run.

**R9. A rule a developer can keep in their head.** Whatever the developer must do to get bounded pages must be one sentence, and preferably a sentence they already know. *Reason:* an authorization mechanism that is correct only when configured correctly is a leak waiting for the first forgotten declaration; this matters doubly when code is written by autonomous agents.

---

## 5. Designs Built and Rejected

Between September 30 and October 15, 2026, we implemented, tested on both engines and benchmarked at 1,000,000 rows seven designs before the one in Section 6. We describe each briefly, in the order they were built, with the rule it broke. All of them were correct; the page always returned the right rows. Table 2 summarizes them. We include this section because the failures are instructive, and because a reader evaluating a similar system will likely reach for several of these first, as we did.

**Table 2. Designs implemented before the final one.**

| Design (pull request, dates) | What it stored | Store manager's page on PostgreSQL | Rule broken |
| --- | --- | --- | --- |
| Closure table and root enumeration (#465, Sept 30–Oct 1) | an (ancestor, descendant) closure of the tree; the caller's granted roots computed once per query | 25 s → 15 s through the composable filter; fast only through a separate fixed-order list call | R3 (roots read into memory), R5 (a second list call) |
| Lineage columns and scope columns (#467, Oct 1–8) | ancestor identifiers per level on the resource table; a scope column with per-level indexes on every protected table, kept by triggers | 1.6 ms | R2 (columns, triggers and indexes on application tables), R3 (roots passed as parameters) |
| Adaptive tree walk (#503, Oct 6–8; shipped as 8.0.0, reverted Oct 11) | per-(principal, node) grant counts, a direct-grant index, a replaced query compiler | 1.5–11 ms for one-grant callers; about 80 ms for a caller with 100 scattered stores | R2 (triggers and eleven or more indexes per protected table), R9 (an illegible footprint) |
| Owned scope-index projections (#508, Oct 9) | the scope columns moved into one projection table per protected table, kept by triggers | as above | R2 (triggers), R5 (a query rewrite) |
| One predicate over a closure, "the planner picks" (#509/#510, Oct 9–11; shipped as 9.0.0, reverted the same day) | the closure with eleven per-level indexes; one `fn_Visible` predicate | 2.9–4.8 s over 400,000 rows at 1,000,000 products | R5 (the plan depended on the planner's estimate of the caller) |
| Capped visible count choosing between two filters (same pull request) | as above, plus a function counting the caller's rows up to a cap | 4 rows at 100,000 products; seeks at 1,000,000 | R5 (a threshold, and `OPTION (RECOMPILE)` on SQL Server) |
| Declared page orders (#515/#516, Oct 12–13; shipped as 10.0.0) | a copy of each *declared* sort order under every node | 22.6 ms, 80 rows | R9 (an undeclared sort silently scanned) |
| Key-only node index with a scan-versus-list race (#521/#522, Oct 14) | which rows sit under which node, for every entity; for an undeclared sort, a race with doubling budgets between scanning in order and listing the caller's rows | 59.3 ms, 742 rows; the far-end caller 1,054,147 rows | R5 (a race is an estimate; PostgreSQL turned 20,000 key lookups into a full scan), R7 |

Three lessons recur.

First, every design that let the caller's grants or roots out of the database (#465, #467) was fast for a caller with one grant and became a parameter list for a caller with many. A caller with 10,000 grants is not exotic: a sharing feature that grants access file by file produces exactly that.

Second, every design that asked the planner to choose per caller (#509, #510, #522) worked at one scale and failed at another. The single-predicate closure design was the cleanest of all the attempts on paper: clean tables, one predicate, nothing new to maintain. Its premise was that with per-level indexes on the closure the planner would probe the index for sparse callers and scan for dense ones. It did neither per caller; it chose one plan for everyone from statistics that cannot see the caller, and switched that plan as the table grew (Section 3.3). The race in #522 is the same mistake in a different coat: two statements with growing budgets, "whichever finishes first", with the planner free to replace twenty thousand key lookups by a scan of a million rows when its cost model found them close. It did, and the far-end caller's page read 1,054,147 rows.

Third, the designs that put structure on application tables (#467, #503, #508) were the fastest, at 1–2 ms, and the most expensive to own: triggers that had to guard against re-firing, a lineage lock to keep concurrent moves from committing a cycle, eleven or more indexes per protected table, and a replaced query compiler. The developer could no longer say what the library had put in their database. That is the footprint rule R9 is about.

The declared page orders of #516 were the design of Section 6 in all but one respect: each sort had to be declared by name, and a sort that was not declared returned the right rows at the old, linear cost. The library warned about it, once. That remaining footgun is what the last change, released as SqlOS 11.0.0, removed.

---

## 6. The Mechanism

### 6.1 The idea in one sentence

For each sort order a protected table can be paged by, keep a copy of that order **under every node of the resource tree**, so that a page for any caller is "the first rows under each of my nodes, merged".

Figure 1 shows the idea for a tiny tree. A product with price 7 under store S3, region R1, chain C1, division D1 appears in the price copy five times, once under each of those nodes and once under the root. A region manager with a grant on R1 reads the range of the copy belonging to R1, which holds exactly the products in her region in price order. A manager of two regions reads two ranges and merges them. An administrator does not need the copy at all; the query's own index already serves her.

[Figure 1: A resource tree of two regions with three stores and nine products, and the price copy beside it: one row per (node, price, product), sorted within each node. The region manager's page is the first entries under R1; the two-region manager's page is the merge of the first entries under R1 and R2.]

### 6.2 The entry rule

Each copy is a table in the library's own schema, never in the application's, with three parts: the node, the sort columns, and the row's primary key. Its primary key is `(NodeId, sort columns…, RowKey)`, which is the index a page reads, and it has a second index on `(RowKey)`, which is the index maintenance deletes by.

**Definition 1 (Entry rule).** An entry (X, r) exists in the copy of an order for table T if and only if X is the resource of row r or an ancestor of it, every resource on the path from r's resource up to and including X is active, and that path is within the configured maximum depth and contains no cycle.

The rule is chosen to match the access function exactly. `fn_IsResourceAccessible` grants access to r through a grant on X precisely when X is on r's active ancestor path within the depth bound. So the nodes under which r has entries are precisely the nodes a grant could sit on and reach r. A row whose own resource is inactive, or whose `ResourceId` names no resource, has no entries, and no grant can reach it either.

In the retail workload every product has six entries per copy: 6,000,000 entries for 1,000,000 products.

### 6.3 Which orders: index what you sort by

The copies that exist are decided by the application's own indexes. For every protected table (an entity implementing the library's `IHasResourceId` interface):

1. The primary key is an order, so a list sorted by key pages.
2. Every index on the table is an order over the index's columns, with the primary key appended as the final tiebreaker. An index that already ends with the key, such as `(Price, Id)`, is the same order as `(Price)`. Whether the index is unique, filtered or descending, or has included columns, makes no difference: one copy serves ascending and descending pages.
3. Two kinds of index are lookups rather than sorts and get no copy: an index that contains `ResourceId`, and an index whose columns are exactly those of a foreign key, which the object-relational mapper adds for every relationship.
4. Nothing fails because of an automatic order. A table that cannot have copies, for example one with a composite key or mapped to a view, or an index with a column that cannot be an order column, for example a nullable column, is skipped, and the application's log says so once at startup with the reason and the fix.

Matching at query time is exact: the query's sort columns, with the key appended, must equal a copy's columns name by name, in sequence; direction is free. A sort by name then city needs an index on `(Name, City)`, as it would in any database.

The developer therefore declares nothing to the authorization library. The rule they follow is the one they already follow for performance in any application: index the columns you sort by. A sort with no index still returns the right rows, by the first paper's method, for every caller including the administrator, exactly as an unindexed sort scans in any database, and the library logs a warning naming the index to add. This is the form of rule R9 we settled on after the declared version of #516: there is no authorization-specific thing to forget.

### 6.4 Storage

A copy of order o for a table with N rows holds Σ_r L(r) entries, where L(r) is the number of levels on r's active path, at most D + 1. With I sort indexes plus the key, the total is at most N · (D + 1) · (I + 1) entries. Each entry carries the node identifier, the sort values and the row key, with the source columns' types, lengths and collations copied so that ordering in the copy agrees with ordering in the table. Section 8.6 measures 550 MB to 866 MB per copy at 1,000,000 products with six levels on PostgreSQL.

### 6.5 The caller's nodes

A page needs the set of nodes the caller holds grants on, and only the useful ones. If a manager is granted both a region and a store inside it, the store grant adds nothing: every row under the store is already under the region.

**Definition 2 (Minimal granted nodes).** For a caller p and permission perm, the minimal granted nodes K(p, perm) are the resources X such that some p' ∈ resolve(p) holds an active grant at X whose role carries perm, X is active, and no proper active ancestor of X satisfies the same condition.

The library computes K inside the database with a function, `fn_PageNodes(principalIds, permissionId)`, that takes exactly the two parameters rule R3 allows: it joins grants, roles, permissions and principals using the same text as the access function (shared, so the two cannot drift), then removes every granted node that has a qualifying node among its ancestors. The subtrees under distinct minimal nodes are disjoint, which is what lets the page merge ranges without deduplicating. We write k = |K|.

### 6.6 The page algorithm

The list call is one method on the application's query: `query.OrderBy(...).ToPageAsync(principalId, permission, pageSize, cursor)`. The query may carry any filters and includes; its sort order and the cursor are read from it. The algorithm runs a scan step, then if needed a merge step, then a load with the final check.

```
ALGORITHM AuthorizedPage(query, p, perm, P, cursor):
  principals ← resolve(p); permId ← lookup(perm)      // two small statements
  b ← P
  LOOP:
    // Scan step: the application's query, in its order, from the cursor,
    // each row tagged with the access function's verdict.
    rows ← first b rows of query after cursor, with visible(row) := EXISTS fn_IsResourceAccessible(row.ResourceId, principals, permId)
    if at least P of rows are visible: keys ← first P visible; EXIT LOOP
    if fewer than b rows came back: keys ← all visible rows; last page; EXIT LOOP
    // Merge step: only when an index covers the order.
    if a copy exists for the query's order:
      for each node X in fn_PageNodes(principals, permId):
        range_X ← first b entries of the copy under X, after the cursor, whose row passes the query's filters
      merged ← the first P of (⋃ range_X) in order
      if |merged| = P: keys ← merged; EXIT LOOP
      else: keys ← merged; last page; EXIT LOOP     // every node ran out
    b ← 4 · b                                         // only an order with no index gets here
  page ← load keys through query (includes, tracking) WHERE visible(row)   // the final check
  cursor' ← order values of the last row of page
  RETURN page, cursor'
```

**The scan step** is the first paper's method with a budget. It reads the next b rows of the query in the query's order and tags each with the access function's verdict in the same statement. For an administrator, or for a query already scoped to the caller's own store, the first twenty rows are visible and the page is done in one statement. For a caller who sees little, the scan returns twenty rows of which few or none are visible, which costs twenty access checks and nothing more.

**The merge step** runs only when the scan did not fill the page and an index covers the order. Under each minimal node it reads the next b entries after the cursor, in order, keeping only entries whose row passes the application's filters (an `EXISTS` on the row against the query without its ordering), and merges the ranges to the first P. In SQL this is one statement: the node function cross-applied to a per-node `TOP (b)` range (`CROSS APPLY` on SQL Server, `JOIN LATERAL` on PostgreSQL), then `ORDER BY` the sort columns and `TOP (P)`. Because b is never below P, the merge step settles the page in the round it runs: either P entries came back, or fewer, in which case every node is exhausted and these are the last rows.

**The budget b** starts at P and grows fourfold only for an order no index covers. That path is the first paper's method, and it is the only path where the page's cost depends on σ. An indexed order never takes it.

**The load and final check** fetch the page's rows through the application's own query, so includes and change tracking behave as the developer expects, with the access function applied once more to each row. A copy entry can therefore cause a row to be *missing* from a page, if the copy is stale, but never cause a row to be *returned* that the access function rejects. This is rule R1 in code.

**The cursor** is the last returned row's sort values. A row dropped by the final check does not stall the list, because the cursor is taken from the rows that were returned. Pages may switch between the scan step and the merge step from one page to the next with the same cursor.

### 6.7 Maintenance

The copies are updated by the library's database context in the same transaction as the application's save (rule R8). Before a save, the context checks whether the tracked changes include a resource, or a row of a protected table. If not, the save proceeds untouched. Otherwise, inside the caller's transaction or one the context opens, it saves, then collects what the save wrote, added and deleted rows, rows whose `ResourceId` or a sort column changed, and resources added, deleted, moved or whose activity changed, and issues one command that, per copy, deletes the entries of the changed rows by key, deletes and reinserts the entries of every row under a moved or deactivated resource and its descendants (a recursive walk bounded by the depth), and inserts the entries of the changed rows from the access function's own ancestor rule. Each row ends with exactly one correct set of entries, or the whole save rolls back.

Two engine-level details mattered at a million rows and are reported because a reader implementing this will meet them. On PostgreSQL the recursive walk's estimated cost crossed the just-in-time compilation threshold, so every save that added a resource compiled its statements, at a cost of about 165 ms for a single insert; the command now disables JIT for its own statements and restores the caller's setting. And walking the subtree from *every* changed resource, including added ones that can have no descendants yet, made the planner hash the whole copy for a save that added a thousand rows; walking only moved or deactivated resources brought that save from 2.4–3.1 s per statement to 66–111 ms.

Writes that bypass the object-relational mapper, bulk loads, raw SQL and bulk updates, do not touch the copies. For them the library provides one command, `RebuildPageOrdersAsync`, that deletes and rewrites every copy from the application tables and the resource graph (rule R1: one command rebuilds everything). The same rebuild runs at startup for a copy whose table exists and is empty while its table has rows, which is what happens the first time an application starts after adding an index. Until a copy's table exists at all, for example before a migration is applied, pages for that order take the scan path and the log says so.

### 6.8 What the developer does

```csharp
// The model: an entity whose rows carry a ResourceId.
modelBuilder.Entity<Product>(entity =>
{
    entity.HasKey(p => p.Id);
    entity.Property(p => p.ResourceId).HasMaxLength(100).IsRequired();
    entity.HasIndex(p => p.ResourceId);
    entity.HasIndex(p => new { p.Price, p.Id });               // the sort the screen offers
    entity.HasIndex(p => new { p.Status, p.CreatedAt, p.Id }); // another sort
});

// A list endpoint.
var page = await db.Products
    .Where(p => p.Name.Contains(term))
    .OrderBy(p => p.Price)
    .ToPageAsync(principalId, "product.view", pageSize: 20, cursor);
return Results.Ok(new { page.Data, page.NextCursor });
```

Adding the index produces, through the ordinary migration, one copy table; the next start fills it. Nothing else is declared, and no code in the endpoint mentions authorization beyond the principal and the permission.

---

## 7. Correctness and Cost

### 7.1 Soundness

**Theorem 1 (Soundness).** Every row returned by AuthorizedPage satisfies allowed(p, perm, resource(row), now).

*Proof.* The only statement that produces returned rows is the load, which applies `EXISTS fn_IsResourceAccessible(row.ResourceId, principals, permId)` to every candidate key at execution time. By Theorem 1 of the first paper, a non-empty result of that function implies allowed. Keys reach the load from the scan step or from the merge step, but neither path bypasses the load's predicate, so no entry of any copy, current or stale, can cause a row to be returned without passing the access function. □

Soundness therefore does not depend on the copies at all. They affect which rows are *found*, never which rows are *allowed*.

### 7.2 Completeness

**Lemma 1 (Entries mirror reachability).** If the copies satisfy Definition 1, then for every row r and node X, (X, r) is an entry if and only if a grant at X could reach r under the access function.

*Proof.* Both conditions are "X lies on r's active ancestor path within the depth bound". The rebuild writes exactly that set, using the same ancestor rule as the access function; the maintenance in Section 6.7 preserves it for every write it sees, which the implementation's tests verify by comparing the entries after each kind of write with a fresh rebuild. □

**Lemma 2 (Minimal nodes cover the caller's rows disjointly).** For every row r, allowed(p, perm, resource(r), now) holds if and only if some X ∈ K(p, perm) lies on r's active ancestor path, and at most one X ∈ K does.

*Proof.* If allowed holds through a grant at some node Y on r's active path, then either Y is minimal or some proper active ancestor of Y holds a qualifying grant; following ancestors upward along r's path, which is finite, reaches a minimal node on that path. Conversely a minimal node on the path holds a qualifying grant, so allowed holds. If two minimal nodes lay on the same path, one would be an ancestor of the other, contradicting minimality. □

**Theorem 2 (Completeness).** If the copies satisfy Definition 1, AuthorizedPage returns the first P rows, in the query's order after the cursor, that pass the query's filters and satisfy allowed, and it returns fewer than P only when no more such rows exist.

*Proof.* If the scan step fills the page, the P rows it returns are the first P visible rows of the query after the cursor, since it reads the query in order and tags every row. Otherwise the merge step runs. By Lemmas 1 and 2, the set of allowed rows after the cursor that pass the filters is the disjoint union over X ∈ K of the rows with an entry under X, after the cursor, passing the filters. Each per-node range reads those rows in the query's order, so the first P rows of the union are among the first P of their own ranges; reading b ≥ P from each range and taking the first P of the union yields exactly them. If the union holds fewer than P rows, every range is exhausted and all remaining rows are returned. The load's final check cannot remove any of them, because by Lemma 1 each has a qualifying grant on its path. □

Completeness, unlike soundness, depends on the copies being current. After a write that bypassed maintenance, a row may be absent from pages until the next rebuild. The implementation documents this as the one operational rule, and Section 6.7 described the command.

### 7.3 Page cost

**Theorem 3 (Per-page cost).** For an order with a copy, a page of size P for a caller with k minimal nodes and M effective principals costs O(P · D · M) for the scan step, O(k · P · log k) for the merge step, and O(P · D · M) for the load; the total is O(P · D · M + k · P · log k), independent of N and of σ.

*Proof.* The scan step reads at most P rows from the query's index after the cursor and evaluates the access function on each: P · O(D · M · G_max) with G_max bounded, as in the first paper's Theorem 3. The merge step performs one index seek per node into the copy's primary key, reads at most b = P entries per node, and merges at most k · P entries by a sort or a heap. The load reads P rows by key and evaluates the access function P times. No term contains N or σ. □

Two refinements matter in practice. First, when the caller has fewer nodes than rows per node, which is every scoped grant on a container, the merge step reads about P entries from a handful of nodes: the measured 80 to 100 rows per page in Section 8. Second, with an application filter that only a fraction f of a node's rows pass, the per-node range reads entries until P pass, so the cost per node becomes O(P / f), bounded by the rows under the node; this is the price of letting the application's filter be arbitrary, and it is bounded by what the caller can see, not by the table.

The scan step runs first for a reason the numbers justify: it costs twenty access checks, it serves the dense callers in one statement without touching any copy, and it never reads more than P rows. The alternative, running the merge step first, would charge every administrator a node lookup and a merge for pages the query's own index answers.

### 7.4 Storage and write cost

**Proposition 1.** With N rows, at most D + 1 levels per row and I sort indexes, the copies hold at most N · (D + 1) · (I + 1) entries. Inserting a row, or changing its `ResourceId` or a sort column, writes at most (D + 1) · (I + 1) entries. Moving, deactivating or reactivating a resource rewrites the entries of every row in its subtree: S · (D + 1) · (I + 1) for a subtree of S rows. A full rebuild writes every entry once.

The rebuild is the only operation linear in N. Section 8.7 gives the measured constants: a single insert went from 1.4 ms to 5.7 ms with three copies; a move of a region holding 2,000 products from 5.2 ms to 501.0 ms.

### 7.5 What is not bounded by a page

The bound of Theorem 3 contains k. A caller with 10,000 individual grants performs 10,000 seeks and reads about 2P entries from each: 20,040 rows for a page in Section 8. That is the price of the sharing pattern that grants file by file, and it is still independent of N. An application filter that almost none of the caller's rows pass reads the caller's entries until a page passes, bounded by the caller's rows. And an order with no index is served by the scan path alone, with the first paper's cost, for every caller.

---

## 8. Empirical Evaluation

### 8.1 Setup and protocol

**Machine.** All measurements in this section ran on one Apple Silicon laptop with Docker Desktop. PostgreSQL 16 runs natively with its default settings, including just-in-time compilation. SQL Server 2022 is the x64 image running under CPU emulation, which inflates every statement by tens of milliseconds; its row counts are exact, its times are not representative of native hardware, and we draw conclusions from ratios within an engine, not across engines. The first paper's 3.47 ms dense page was measured on a different machine and is not comparable to any number here.

**Data and callers.** The retail workload of Section 3.1: 1,000,000 products under 10,000 stores, 500 regions, 50 chains and 5 divisions, with every product its own resource; the seven callers of Table 1; page size 20; the query sorted by price. The `Products` table has indexes on `(Price, Id)` and `(StoreId, Price, Id)` and a unique index on `ResourceId`. In the final implementation the price index and the key give copies `SqlOSFgaPage_Products_Price` and `SqlOSFgaPage_Products_Id`; the `(StoreId, Price, Id)` index gives a third copy that no scenario pages by.

**Two methods.** Method A is the first paper's: the application query with the access function appended as a `WHERE EXISTS`, `ORDER BY Price, Id`, `TOP (20)`, with the caller's principals resolved before timing starts. Method B is the page call of Section 6.6; its time includes resolving the caller's principals and the permission, which are two further statements, so a dense page under method B runs four statements where method A ran one.

**Measurement.** Each scenario is run after a warm-up and timed for up to 15 runs within a 60-second budget; the median is reported. A run longer than a minute is measured once. "Rows read" is the sum, over every statement a page executes, of the rows the engine's actual execution plan reports its operators over `Products` and the copy tables produced, from `SET STATISTICS XML` on SQL Server and `EXPLAIN (ANALYZE, FORMAT JSON)` on PostgreSQL; on PostgreSQL rows a filter removed are included. Rows are deterministic; times on a shared laptop are not, and should be read to one significant figure of ratio.

**Provenance.** Tables 3 and 4 are from the benchmark of pull request #516 (October 13, 2026, released as SqlOS 10.0.0) [45], which measured method B with the price order declared explicitly; the final implementation (pull request #524, released as SqlOS 11.0.0 on October 15, 2026) derives the same copy from the price index and runs byte-identical statements, and its re-measurement on PostgreSQL reproduced every row count in Table 4 exactly (Section 8.3). Table 5 is from the benchmark of pull request #522 [48], run on a build whose scan and merge statements are the same as the final ones, with the same data. Tables 6 and 7 are from pull request #524 [49].

### 8.2 Results on SQL Server 2022

**Table 3. SQL Server 2022, 1,000,000 products, page of 20 sorted by price. Method A is the first paper's per-row predicate; method B is this paper's page call.**

| Caller | Visible | A: time (ms) | A: rows read | B: time (ms) | B: statements | B: rows read |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Administrator | 1,000,000 | 23.7 | 40 | 52.3 | 4 | 60 |
| Region manager | 2,000 | 9,032 | 18,612 | 88.2 | 5 | 100 |
| Store manager | 100 | 179,558 | 368,410 | 89.0 | 5 | 100 |
| 100 stores | 10,000 | 2,227 | 4,466 | 93.7 | 5 | 4,060 |
| 10,000 single products | 10,000 | 1,420 | 2,904 | 762.7 | 5 | 20,060 |
| Far end | 20,000 | 942,593 | 1,960,040 | 90.3 | 5 | 100 |
| Scoped store manager | 100 | 18.4 | 40 | 39.4 | 4 | 60 |

Building the price copy, 6,000,000 entries, took 200 s with the rebuild command.

### 8.3 Results on PostgreSQL 16

**Table 4. PostgreSQL 16, 1,000,000 products, page of 20 sorted by price.**

| Caller | Visible | A: time (ms) | A: rows read | B: time (ms) | B: statements | B: rows read |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Administrator | 1,000,000 | 5.6 | 20 | 14.0 | 4 | 40 |
| Region manager | 2,000 | 416.8 | 9,306 | 18.6 | 5 | 80 |
| Store manager | 100 | 8,420 | 184,205 | 22.6 | 5 | 80 |
| 100 stores | 10,000 | 107.6 | 2,233 | 21.8 | 5 | 4,040 |
| 10,000 single products | 10,000 | 70.8 | 1,452 | 390.2 | 5 | 20,040 |
| Far end | 20,000 | 44,208 | 980,020 | 16.4 | 5 | 80 |
| Scoped store manager | 100 | 5.4 | 20 | 12.8 | 4 | 40 |

Building the price copy took 55 s. The final implementation's re-measurement on the same machine two days later gave method B times of 14.0, 18.3, 18.6, 24.9, 457.5, 16.4 and 12.4 ms for the seven callers in table order, and the same rows read in every row of the table.

[Figure 2: Rows read per page on PostgreSQL for the seven callers, method A against method B, on a logarithmic axis. Method A spans 20 to 980,020 rows; method B spans 40 to 20,040, and is 80 for every caller with a single container grant.]

### 8.4 What the numbers say

**Sparse callers get a page for about a page.** The store manager's page falls from 184,205 rows read to 80 on PostgreSQL and from 368,410 to 100 on SQL Server; her time falls from 8,420 ms to 22.6 ms and from 179,558 ms to 89.0 ms. The region manager, with 2,000 visible rows, reads 80 rows instead of 9,306. The far-end caller, whose 20,000 rows all lie at the end of the price order and who paid almost the whole table under method A, reads 80 rows: her copy range under her chain starts with her cheapest product, and the position of that product in the global order does not matter. These three callers now cost within a few milliseconds of each other and of the administrator.

**Dense callers pay for two extra statements and a second check.** The administrator's page rose from 5.6 ms to 14.0 ms on PostgreSQL and from 23.7 ms to 52.3 ms on SQL Server. Method B runs four statements where method A ran one: resolving the caller's principals, resolving the permission, the scan step, and the load with the final check, which re-reads the twenty rows it is about to return and tests each again. The scoped store manager shows the same shape. We consider this a fair price for a page that is bounded for everyone, but it is a price, and it is the reason the page call resolves the caller on every page rather than once per request: it must see grants and memberships as they are when the statements run.

**The merge reads about two pages per node.** For callers with one container grant, method B reads 80 rows on PostgreSQL: 20 in the scan step, 40 in the merge step (20 entries and the 20 row lookups that apply the query's filters), and 20 in the load. SQL Server reads 100 because its scan step counts each row twice. The "100 stores" caller reads 4,040: 40 per node for 100 nodes, plus 40. This is the k · P term of Theorem 3 made visible.

**Many single grants are the known ceiling.** The caller with 10,000 grants on individual products reads 20,040 rows and takes 390.2 ms on PostgreSQL, against 70.8 ms under method A. Each of her nodes holds one product, so the merge step performs 10,000 seeks to find twenty rows; method A was cheap for her only because her rows happen to be spread evenly through the price order, so that a scan of 1,452 rows found twenty of them. This is the one scenario where this paper's method is slower, by a factor of 5.5 on PostgreSQL, and it is still independent of the table size.

### 8.5 Selectivity sweep

To see the two methods as functions of σ, we gave five callers grants on 1, 10, 45, 100 and 500 stores chosen at random, so that they see 0.01%, 0.1%, 0.45%, 1% and 5% of the products, spread evenly through the price order. The 0.45% point is where √(P / N) falls for P = 20 and N = 1,000,000: the point at which, under method A, the expected number of rows scanned to fill a page, about P / σ, equals the number of rows the caller can see, σ · N.

**Table 5. PostgreSQL 16, callers with grants on 1 to 500 random stores. Method A rows and times are from the scan benchmark; method B rows and times from the comparison benchmark of the same build and data.**

| Grants (stores) | Visible | Share | A: time (ms) | A: rows read | B: time (ms) | B: rows read |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 100 | 0.01% | 11,707.8 | 193,501 | 15.5 | 80 |
| 10 | 1,000 | 0.1% | 1,295.4 | 19,481 | 16.3 | 440 |
| 45 | 4,500 | 0.45% | 204.3 | 3,825 | 17.8 | 1,840 |
| 100 | 10,000 | 1% | 105.5 | 1,928 | 20.2 | 4,040 |
| 500 | 50,000 | 5% | 24.0 | 367 | 38.9 | 20,040 |

Method A's rows read fall by a factor of ten for every tenfold rise in σ: 193,501, 19,481, 1,928, consistent with P / σ. Method B's rows read are 40 · k + 40 exactly, for k granted stores: 80, 440, 1,840, 4,040, 20,040, and its time rises from 15.5 ms to 38.9 ms across the sweep. In elapsed time the two methods cross between 100 and 500 random stores, where a caller sees enough of the table that a scan finds a page quickly. Note that the sweep's callers hold their grants on *stores*; a caller who sees 5% of the table through one grant on a division, as real organizations grant, reads 80 rows under method B.

[Figure 3: Rows read per page against the caller's share of the table, both methods, logarithmic axes. Method A falls as 1/σ; method B rises with the number of granted nodes and is flat in σ for a fixed number of nodes.]

### 8.6 The rejected race, measured

Section 5 said that the scan-versus-list race of pull request #522 broke rule R5 because the planner could replace key lookups by a scan. Table 6 is that measurement, on PostgreSQL with the same data: for a sort with no declared copy, a key-only copy of which rows sit under which node, and in each round a scan of b rows in order against a listing of the caller's rows when they fit the budget b, whichever fills the page first.

**Table 6. PostgreSQL 16, the race of pull request #522 against the copy of this paper, 1,000,000 products.**

| Caller | Race: time (ms) | Race: rows read | Race: statements | This paper: time (ms) | This paper: rows read |
| --- | ---: | ---: | ---: | ---: | ---: |
| Region manager | 951.3 | 13,263 | 14 | 18.6 | 80 |
| Store manager | 59.3 | 742 | 10 | 22.6 | 80 |
| Far end | 3,044.9 | 1,054,147 | 16 | 16.4 | 80 |

The far-end caller's 20,000 rows fit the race's budget in its sixth round, and the listing statement asked for those 20,000 rows by key. PostgreSQL's cost model priced a parallel sequential scan of the million-row table within 7% of 20,000 index lookups and chose the scan: 1,054,147 rows for a page of twenty. With parallel query disabled the same statement used the index and read 74,145 rows. That a server setting decides whether a page reads 74 thousand or a million rows is precisely what rule R5 forbids, and the race was withdrawn. The copy of Section 6 reads 80 rows for the same caller under either setting, because its statement asks for the first twenty entries of one node's range, a question with one plan.

### 8.7 Storage and write cost

**Table 7. PostgreSQL 16, the copies for 1,000,000 products with six levels per product.**

| Copy | Entries | Size (MB) |
| --- | ---: | ---: |
| `SqlOSFgaPage_Products_Price` (the price index) | 6,000,000 | 706 |
| `SqlOSFgaPage_Products_StoreId_Price` (the scoped index) | 6,000,000 | 866 |
| `SqlOSFgaPage_Products_Id` (the key) | 6,000,000 | 550 |

Writing all 18,000,000 entries with the rebuild command took 175 s. The `Products` table itself is a small fraction of this: the copies are, by design, several times the table, which is the storage cost Proposition 1 predicts and the reason the rule of Section 6.3 skips lookup indexes and the documentation says to index what you sort by rather than every column.

**Table 8. PostgreSQL 16, write cost with no copies against three copies (median of 25 saves for the single insert).**

| Operation | No protected table (ms) | Three copies (ms) |
| --- | ---: | ---: |
| Insert one product with its resource | 1.4 | 5.7 |
| Insert 1,000 products with their resources, one save | 211.8 | 635.6 |
| Move a region holding 2,000 products to another chain, one save | 5.2 | 501.0 |

A single insert writes six entries into each of three copies in the save's transaction and costs about 4 ms more. A batch of a thousand costs three times as much as with no copies. A move rewrites 12,000 entries in each copy. Both the "three copies" column and the "no protected table" column include the two engine fixes of Section 6.7; before them the single insert cost 165 ms with one copy and the thousand-row save's statements 2.4–3.1 s each.

### 8.8 Threats to validity

Every number comes from one laptop, with SQL Server under emulation. Times are medians of a few runs on a shared machine; row counts are exact and are the measurements we rely on. The data is synthetic and uniform: prices evenly spread, equal fan-out at every level, one resource per row. Real tables have skew, which affects method A's P / σ constant but not method B's k · P. Every caller's grants sit on nodes that the rows name, and on PostgreSQL every statement is planned when it runs because the client does not prepare statements. The benchmark exercises the library's real schema, functions and page call, not a simplified model of them, and the integration tests that compare every page against a row-by-row point check run on both engines.

---

## 9. Discussion

### 9.1 The rule is the database's own rule

The single most useful property of the final design is that a developer cannot forget to use it. Earlier versions of this work asked for a declaration per sort, and an application that forgot one returned correct pages at the linear cost, silently, with only a log line to say so. That is the shape of most authorization bugs: correct until it is slow, and slow only for the users who see least. By deriving the copies from the application's indexes, the mechanism follows the one rule every developer, and every code-generating agent, already applies for performance. An unindexed sort is slow for every caller including the administrator, so it is noticed in the first test, and the fix, add the index, is the same fix as in any application.

### 9.2 Why the original predicate stays

The access function of the first paper does three jobs the copies cannot. It is the point check, which takes a resource identifier rather than a row, so there is nothing to look up in a copy. It is the final check on every page, which is what makes a stale or wrong copy safe (Theorem 1). And it is the path for an order with no index, which keeps every list correct whatever the developer has indexed. The copies find rows; the predicate proves them. Removing the predicate would make a derived index the authority on who sees what, which rule R1 forbids.

### 9.3 Costs and limits

The design pays in storage and write amplification, both proportional to the number of indexes times the depth of the tree. For a table with three sort indexes under a six-level tree, every row has twenty-four copy entries, and every insert writes them. That is acceptable for the business applications this model targets and would not be for a high-volume append-only feed. The composite index a developer adds for a filtered query, such as `(StoreId, Price, Id)`, is copied too, at 866 MB in Table 7, although no page sorts by store; a per-index opt-out is the obvious relief and was left out of the first release to keep the rule to one sentence.

Sort columns must be non-nullable, because the cursor's keyset comparison has no defined order for nulls; a sort by a nullable due date is served by the scan path with a warning. A caller with thousands of individual grants pays thousands of seeks per page (Section 7.5); granting on containers, as organizations do, avoids it. Moves rewrite the subtree's entries, so moving a large subtree is a heavy write. Concurrent writes, one moving a resource and one adding rows under it, can leave entries on the old path under snapshot isolation; the implementation documents this and relies on the final check and the rebuild rather than a lock that every write would contend on. Writes that bypass the mapper need the rebuild. And in a hierarchy mapped to one table with a discriminator, indexes on derived types do not get copies; only the root's do.

### 9.4 Future work

Three directions follow directly. First, maintaining the copies inside the database engine, with statement-level triggers or an extension, would make them correct for every writer and remove the rebuild rule; the entry rule and the rebuild are the specification such triggers must meet. Second, a SQL function that returns an authorized page would let clients in any language use the mechanism without the library. Third, nullable sort columns need a cursor encoding with a defined null order. Beyond these, the many-grant ceiling could be lowered by skipping the merge step's row lookups when the query has no filters, and a run on native SQL Server hardware would replace the emulated times in Table 3.

### 9.5 Autonomous principals

The first paper argued that agents, which enumerate resources in loops, need bounded list cost more than people do. This paper delivers that bound for sparse agents, which is what a least-privilege agent is: granted one subtree for one task. It also gives agents that *write* applications a rule they already follow. An agent that generates a schema with an index on every sorted column has, without being told, produced an application whose authorized lists cost a page for every user.

---

## 10. Conclusion

The first SHRBAC paper showed that organizational hierarchy, the shape of authority in nearly every business and the shape the authorization industry has converged on, can be enforced by one composable SQL predicate at a cost independent of the size of the data, for callers who see most of it. This paper measured what that predicate costs callers who see little, found it linear in the table, and repaired it without weakening anything the first paper established. The repair is a copy of every index under every node of the tree, maintained in the write's transaction and verified on every page by the original predicate. A page costs about P entries per granted node for any caller, the store manager's million-row page reads 80 rows instead of 184,205, and the only rule a developer follows is the one every database already asks of them.

We also reported seven designs that did not work and why: designs that moved grants out of the database, that put structure on application tables, or that asked the query planner to decide per caller from an estimate. We believe the last of these is the most important negative result for anyone building authorized list queries: the planner cannot see the caller, so the knowledge of what a caller can see must be stored before the plan is made, not guessed during it.

---

## AI Disclosure

In accordance with IEEE policy, the author discloses that AI tools (Claude, Anthropic) were used to assist with manuscript preparation, editing, and the implementation and benchmarking work described. All technical decisions, the design rules, the acceptance and rejection of each design, and the experimental design are the work of the author.

---

## References

[1] R. Pang, R. Caceres, M. Burrows, Z. Chen, P. Dave, N. Germer, A. Golynski, K. Graney, N. Kang, L. Kissner, J. L. Korn, A. Parmar, C. D. Richards, and M. Wang, "Zanzibar: Google's Consistent, Global Authorization System," in *Proc. USENIX ATC*, pp. 33–46, 2019. https://www.usenix.org/conference/atc19/presentation/pang

[2] J. H. Saltzer and M. D. Schroeder, "The Protection of Information in Computer Systems," *Proc. IEEE*, vol. 63, no. 9, pp. 1278–1308, 1975.

[4] M. Stonebraker and E. Wong, "Access Control in a Relational Data Base Management System by Query Modification," in *Proc. ACM National Conf.*, 1974, pp. 180–187.

[16] Microsoft, "What is Azure role-based access control (Azure RBAC)?," Microsoft Learn.

[21] WorkOS, "Fine-Grained Authorization: Overview," "Resources," and "Resource discovery," WorkOS Documentation. https://workos.com/docs/fga/index, https://workos.com/docs/fga/resources, https://workos.com/docs/fga/resource-discovery

[22] Auth0, "Search with permissions," Auth0 FGA Documentation. https://docs.fga.dev/integration/advanced/search-with-permissions

[23] Dropbox, "Architecture of Nautilus, the new Dropbox search engine," Dropbox Tech Blog, Sept. 2018. https://dropbox.tech/machine-learning/architecture-of-nautilus-the-new-dropbox-search-engine

[24] J. Silva, "How we rolled out our own permissions DSL at Figma," Figma Blog, Mar. 2024. https://www.figma.com/blog/how-we-rolled-out-our-own-permissions-dsl-at-figma/

[25] J. Teton-Landis, "The data model behind Notion's flexibility," Notion Blog, May 2021. https://www.notion.com/blog/data-model-behind-notion

[26] OpenFGA, "Configuration" (`listObjectsDeadline`, `listObjectsMaxResults`) and "Relationship queries," OpenFGA Documentation. https://openfga.dev/docs/getting-started/setup-openfga/configuration, https://openfga.dev/docs/interacting/relationship-queries

[27] J. Whit (OpenFGA maintainer), reply in OpenFGA GitHub discussion #105 on ListObjects pagination, Jan. 2023, https://github.com/orgs/openfga/discussions/105; and Auth0, "OpenFGA's improved ListObjects algorithm," Auth0 Blog, Feb. 2026. https://auth0.com/blog/openfga-improved-listobjects-algorithm/

[28] AuthZed, "Protecting a list endpoint," SpiceDB Documentation. https://authzed.com/docs/spicedb/modeling/protecting-a-list-endpoint

[29] AuthZed, "AuthZed Materialize," AuthZed Documentation. https://authzed.com/docs/authzed/concepts/authzed-materialize

[30] R. Slaney, "Database-Native Authorization for Human and Autonomous Principals: The SHRBAC Model," in *Proc. IEEE COMPSAC*, 2026.

[31] Slack, "Slack search powers visibility and compliance across your organisation," Slack Engineering Blog. https://slack.com/blog/developers/enterprise-ai-search-with-slack-search

[32] Oso, "Local Authorization" and "List filtering," Oso Documentation. https://www.osohq.com/docs/develop/facts/local-authorization, https://www.osohq.com/docs/develop/enforce/list-filtering

[33] Cerbos, "Filtering resources by permission" (PlanResources), Cerbos Documentation. https://docs.cerbos.dev/cerbos/latest/recipes/filtering-resources

[34] Open Policy Agent, "Data Filtering" and "Compile API," OPA Documentation. https://www.openpolicyagent.org/docs/filtering/, https://www.openpolicyagent.org/docs/latest/rest-api/#compile-api

[35] PostgreSQL Global Development Group, "Row Security Policies," PostgreSQL Documentation. https://www.postgresql.org/docs/current/ddl-rowsecurity.html

[36] Supabase, "Row Level Security performance," Supabase Documentation. https://supabase.com/docs/guides/database/postgres/row-level-security-performance

[37] Microsoft, "Row-Level Security," SQL Server Documentation. https://learn.microsoft.com/en-us/sql/relational-databases/security/row-level-security

[38] B. Johnston, "SQL Server RLS performance and troubleshooting," Redgate Simple Talk, Sept. 2023. https://www.red-gate.com/simple-talk/databases/sql-server/sql-server-rls-performance-and-troubleshooting/

[39] Microsoft, "Hierarchical Data (SQL Server)," SQL Server Documentation. https://learn.microsoft.com/en-us/sql/relational-databases/hierarchical-data-sql-server

[40] Google Cloud, "Schemas overview" (interleaved tables), Spanner Documentation. https://docs.cloud.google.com/spanner/docs/schema-and-data-model

[41] B. Karwin, "Moving Subtrees in Closure Table Hierarchies," Percona Blog, Feb. 2011. https://www.percona.com/blog/moving-subtrees-in-closure-table/

[42] V. Tropashko, "Nested Intervals Tree Encoding in SQL," arXiv cs/0402051, 2004. https://arxiv.org/abs/cs/0402051

[43] R. Fagin, A. Lotem, and M. Naor, "Optimal Aggregation Algorithms for Middleware," *J. Computer and System Sciences*, vol. 66, no. 4, pp. 614–656, 2003.

[44] R. Slaney, "SqlOS," open-source library (MIT). Version 10.0.0 (October 13, 2026) introduced declared page orders; version 11.0.0 (October 15, 2026) derives them from every index. https://github.com/ross-slaney/sqlos

[45] R. Slaney, "Page orders: authorized pages that cost a page for every caller, with nothing on application tables," ross-slaney/sqlos pull request #516, Oct. 2026. https://github.com/ross-slaney/sqlos/pull/516

[46] R. Slaney, "Benchmarks: the shipped SHRBAC path on SQL Server and PostgreSQL, 1M to 100M, gated in CI," ross-slaney/sqlos pull request #446 (closed), Sept. 2026. https://github.com/ross-slaney/sqlos/pull/446

[47] R. Slaney, "List filters over the resource closure, with nothing on application tables," ross-slaney/sqlos issue #509 and pull request #510, Oct. 2026. https://github.com/ross-slaney/sqlos/issues/509

[48] R. Slaney, "Page orders: bound every sort by default, and make HasPageOrder an optional optimization," ross-slaney/sqlos pull request #522 (closed), Oct. 2026. https://github.com/ross-slaney/sqlos/pull/522

[49] R. Slaney, "Page orders from indexes: every index on a protected table is a page order, and HasPageOrder goes away," ross-slaney/sqlos pull request #524, Oct. 2026. https://github.com/ross-slaney/sqlos/pull/524
