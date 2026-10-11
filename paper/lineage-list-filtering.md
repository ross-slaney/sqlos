# The resource lineage: list filtering without walking the tree

Companion note to *SHRBAC* (COMPSAC 2026). That paper's row filter decides each row by walking up the
resource tree and probing the grants at every ancestor; Theorem 3 there bounds a page by the rows the engine
must examine, which for a caller who sees a fraction σ of the table is about k/σ rows for a page of k. This
note replaces the walk with a precomputed **lineage** on the resources table (the tree's closure, schema v11
and later), proves that both filters built on it equal Definition 1 of the paper, proves the triggers keep
the lineage exact, and gives the rule that picks a filter for each caller and the cost of a page under
each. The implementation is `SqlOSFgaLineage`, the two database providers (`fn_IsResourceAccessible`, and in
their `*.Lineage.cs` files `fn_AccessRoots`, `fn_ListVisible`, `fn_VisibleSet`, `fn_ListFirst`, `fn_CheckRow`,
the lineage routines and triggers), and `SqlOSFgaAuthService.BuildFilterAsync`.

## 1. Definitions

Let the resources form a forest: every resource has at most one parent, and no resource is its own
ancestor. Let D be the configured maximum depth. For a resource x let path(x) = (a₀, a₁, …, a_d) be the
resources from the top of x's tree down to x itself, so a_d = x and d = depth(x). Every resource is active or
inactive.

**Definition 1 (the paper's rule).** A caller with live subjects S may act on x with permission P when x is
active, depth(x) ≤ D, and some level ℓ ≤ d exists such that a_ℓ, …, a_d are all active and a subject in S
holds a current grant on a_ℓ whose role includes P (and P's resource type, if it has one, is x's).

**Definition 2 (lineage).** The lineage of x is the triple (Depth, Reach, Ancestor₀…Ancestor_D) with

- Depth(x) = d when d ≤ D and path(x) reaches a root; otherwise NULL (x is malformed: a cycle or too deep);
- Ancestor_ℓ(x) = a_ℓ for ℓ ≤ d, NULL for ℓ > d, and NULL throughout when x is malformed;
- Reach(x) = the least ℓ such that a_ℓ, …, a_d are all active, when x is active and well formed; NULL
  otherwise.

Reach is well defined: when x is active the set {ℓ : a_ℓ…a_d all active} contains d and is downward closed
from its least element.

**Definition 3 (access roots).** For a caller with live subjects S and permission P, roots(S, P) is the set
of active, well-formed resources on which a subject in S holds a current grant whose role includes P, each
with its depth. `fn_AccessRoots` returns it.

## 2. The lineage condition equals the paper's rule

**Lemma 1.** For well-formed x and a level ℓ ≤ depth(x): a_ℓ, …, a_d are all active if and only if x is
active and Reach(x) ≤ ℓ.

*Proof.* If a_ℓ…a_d are all active then x = a_d is active and ℓ belongs to the set Reach minimizes over, so
Reach(x) ≤ ℓ. Conversely, if x is active and Reach(x) = r ≤ ℓ then a_r…a_d are all active, and a_ℓ…a_d is
a suffix of that run. ∎

**Theorem 1 (equivalence).** A row whose resource is x is visible under Definition 1 if and only if there is
(a, ℓ) ∈ roots(S, P) with Ancestor_ℓ(x) = a and Reach(x) ≤ ℓ, and the type condition holds.

*Proof.* (⇒) Take the level ℓ and ancestor a_ℓ Definition 1 provides. a_ℓ is active (it is one of
a_ℓ…a_d) and well formed (it lies on the path of a well-formed x), and a subject in S holds the required
grant on it, so (a_ℓ, ℓ) ∈ roots(S, P). Ancestor_ℓ(x) = a_ℓ by Definition 2, and Reach(x) ≤ ℓ by Lemma 1.
(⇐) Ancestor_ℓ(x) = a means a = a_ℓ on path(x), so x is well formed with ℓ ≤ d; Reach(x) ≤ ℓ gives, by
Lemma 1, that x is active and a_ℓ…a_d are all active; (a, ℓ) ∈ roots(S, P) gives the grant. All of
Definition 1 holds. ∎

SqlOS evaluates the right-hand side of Theorem 1 from both ends (Section 4). `fn_ListVisible` starts from
the roots: for each root (a, ℓ) it lists the x with Ancestor_ℓ(x) = a and Reach(x) ≤ ℓ that meet the type
condition. The point check `fn_IsResourceAccessible` starts from the target: for the one x it enumerates
the levels Reach(x)…d and probes the grants on each Ancestor_ℓ(x).

## 3. The triggers keep the lineage exact

Call a resource *affected* by a statement when its parent or activity changed, or it lies beneath such a
resource. The refresh routine (`sp_…_LineageRefresh` / `fn_…_LineageRefresh`, and the inline insert path on
PostgreSQL) recomputes exactly the affected rows:

1. It rejects the statement if a changed row's chain, followed upward through the table as it now stands,
   does not reach a root within D steps (a cycle, or a row deeper than D).
2. It collects the affected set: the changed rows and everything beneath them, level by level.
3. It descends in waves. Wave 0 is the affected rows whose parent is outside the set (a root, or a row the
   statement did not touch, whose lineage is therefore final); each later wave is the affected children of
   the previous wave. Each wave is one set-based update computing a row's lineage from its parent's by the
   recurrences of Definition 2: Depth = parent's Depth + 1, Ancestor_ℓ = parent's Ancestor_ℓ for ℓ < Depth
   and the row itself at ℓ = Depth, Reach = 0 for a root, the parent's Reach when the parent is active, and
   the row's own Depth when the parent is inactive; NULL throughout when the row is inactive (Reach only) or
   the parent is malformed or sits at level D (Depth and Ancestors too). In strict mode a child of a row at
   level D rejects the statement.
4. Rows the waves never reached are set to NULL.

**Theorem 2 (invariant).** If every row satisfied Definition 2 before a statement, every row satisfies it
after the statement and its triggers.

*Proof.* Rows outside the affected set have the same path and the same activity along it as before, since a
path changes only when a resource on it changes parent or activity, and every resource beneath a changed
resource is in the set. Their lineage is unchanged and still correct. For the affected set, induct on the
waves. A wave-0 row's parent is outside the set, so its lineage is correct; the recurrences produce the
row's lineage from the parent's, and they are Definition 2 restated one level at a time (Reach: if the parent
is active, the run of active resources ending at the row extends the parent's run, so the least level is the
parent's; if the parent is inactive, the run is the row alone; if the row is inactive, Reach is NULL). A
later wave's row has its parent in the previous wave, already correct, and the same recurrence applies. A
row no wave reaches has no chain to a root within the set and the limit; since strict mode rejected cycles
and over-depth rows, such a row descends from a row that was already malformed, and NULL is its correct
lineage. Inserted rows have no descendants, so their affected set is themselves; the insert path uses the
same waves over the inserted rows. ∎

The cost of a statement is proportional to its affected set: constant for creating a row, the subtree for a
move or a change of activity, and nothing for a grant, a revoke, a retype or a delete, which are not part of
the lineage (a resource with children cannot be deleted, so no other row's lineage depends on it). The
rebuild (`…_LineageRebuild`) is the same recurrence run over every row, level by level.

## 4. Two filters over the closure

The lineage is the tree's closure written once per row: for every resource, the key (`Seq`) of its ancestor
at every level, its depth and its reach. Nothing of it is copied to the application tables. An application
row carries its `ResourceId` and an index on it, and both filters reach the closure through that column.

Each level ℓ has an index on the resources table, `IX_…_Ancestor_ℓ` on (Ancestor_ℓ, ResourceTypeId)
including Reach and Id, over the rows whose Ancestor_ℓ is not null. The resources of one type beneath a
resource at level ℓ are one range of that index.

**The functions.** S is the caller's subjects, P the permission, T its resource type (or none), t the
application table.

- `fn_AccessRoots(S, P)` returns roots(S, P) as rows (ResourceSeq, Depth): one per current grant of a live
  subject of S whose role includes P, on an active, well-formed resource. It does not deduplicate (a
  resource granted twice appears twice), so a statement that needs only its first rows stops reading
  grants once it has them.
- `fn_ListVisible(S, P, T)` lists the visible resources root by root, one branch per level:

  ```sql
  SELECT v.Id AS ResourceId
  FROM fn_AccessRoots(@SubjectIds, @PermissionId) a
  CROSS APPLY (
      SELECT r.Id FROM SqlOSFgaResources r
      WHERE a.Depth = 0 AND r.Ancestor0 = a.ResourceSeq AND r.Ancestor0 IS NOT NULL
        AND r.Reach <= 0 AND (@TypeId IS NULL OR r.ResourceTypeId = @TypeId)
      UNION ALL
      …                                   -- the same for each level ℓ = 1 … D
  ) v
  ```

  For a root at level ℓ only the ℓ-th branch passes its startup predicate `a.Depth = ℓ`, and it reads one
  range of the level-ℓ index. (`Ancestor_ℓ IS NOT NULL` follows from the equality; it is stated because SQL
  Server matches a filtered index only to a predicate that states the filter.) A resource beneath two roots
  is listed twice. The function is inlined into the statement that calls it on both engines: an inline
  table-valued function on SQL Server, a `LANGUAGE sql` function over a lateral join on PostgreSQL.
- `fn_VisibleSet(S, P, T)` returns the rows of `fn_ListVisible`, each once, materialized: on SQL Server a
  multi-statement table function whose table variable has a primary key that ignores duplicates, on
  PostgreSQL a PL/pgSQL function declared `ROWS 10`, which the planner never inlines. Either way the
  statement that reads it sees a small opaque set and starts from it.
- `fn_IsResourceAccessible(x, S, P)` is the point check. It reads x's row and, for each ℓ from Reach(x) to
  depth(x), the resource whose `Seq` is Ancestor_ℓ(x) and its grants to a live subject of S whose role
  includes P, provided P's type, if it has one, is x's. It returns the deciding grant (on the nearest such
  ancestor) or no row. Each grant carries its resource's `Seq` (schema v16, copied by a trigger; a `Seq` never
  changes), so the grants of S's subjects on ancestor ℓ are one seek each on (ResourceSeq, SubjectId): at
  most (D + 1)·|S| seeks, whatever |roots(S, P)| is and however many other subjects hold grants on the same
  ancestors.
- `fn_ListFirst(S, P, T, t)` returns one boolean: whether `fn_ListVisible(S, P, T)` has fewer than C rows,
  counting at most C of them (`TOP (C)`, `LIMIT C`). Here C = max(8·√N, 1000), with N the row count of t that
  the engine keeps in its catalog (`sys.partitions`; `pg_class.reltuples`, read as 0 before the table's first
  vacuum or analyze).

**Lemma 2.** `fn_ListVisible(S, P, T)` lists x (at least once) if and only if some (a, ℓ) ∈ roots(S, P)
has Ancestor_ℓ(x) = a and Reach(x) ≤ ℓ, and the type condition holds.

*Proof.* `fn_AccessRoots` returns exactly roots(S, P), with repetitions. For a root (a, ℓ) only the branch
of level ℓ passes `a.Depth = ℓ`, and it returns exactly the rows with Ancestor_ℓ = Seq(a), Reach ≤ ℓ and
the type condition; `Reach ≤ ℓ` is false for a NULL reach, which carries the activity and well-formedness
of x. Keys are unique, so Ancestor_ℓ = Seq(a) is Ancestor_ℓ(x) = a. ∎

`BuildFilterAsync` resolves S and P, asks `fn_ListFirst` once, and returns one of two filters on t:

- *List first*, when `fn_ListFirst(S, P, T, t)` holds:
  `EXISTS (SELECT 1 FROM fn_VisibleSet(S, P, T) v WHERE v.ResourceId = t.ResourceId)`;
- *Row check*, otherwise: `EXISTS (SELECT 1 FROM fn_CheckRow(t.ResourceId, S, P))`, where `fn_CheckRow(x, S, P)`
  is one row exactly when `fn_IsResourceAccessible(x, S, P)` returns one (a name of SqlOS's own, so an
  application's EF model may map `fn_IsResourceAccessible` as well).

Their parameters are the caller's live subject ids and the permission (and, for list first, its type). No
grant, root or row list leaves the engine; the count returns one scalar, and the statement reads the grants
again when it runs.

**Corollary 1 (both filters are exact).** Under either filter, a row of t whose resource is x passes if and
only if x is visible under Definition 1.

*Proof.* List first: `fn_VisibleSet` holds the resources `fn_ListVisible` lists, which by Lemma 2 are those
meeting the right-hand side of Theorem 1, hence the visible ones. Row check: `fn_IsResourceAccessible(x)`
returns a row iff x has a reach and, for some ℓ with Reach(x) ≤ ℓ ≤ depth(x), a live subject of S holds a
current grant on a = Ancestor_ℓ(x) whose role includes P, and the type condition holds. Such an a is active
(Lemma 1) and well formed (it lies on the path of a well-formed x), so (a, ℓ) ∈ roots(S, P): again the
right-hand side of Theorem 1. ∎

The row check is the point check itself, so it agrees with `CheckAccessAsync` by construction; the
integration tests force each filter in turn and check both against the point check, row for row. The count
decides only cost, never the answer: a stale catalog count, a count taken with multiplicity (a resource
beneath two roots counts twice), or grants that change between the count and the statement can send a
caller to the slower filter, not to a wrong one.

**The cost of each filter.** Take a page of k rows in an order the application declared an index for, a
table of N rows of which the caller may see V (V = σN), and a tree of depth D.

- *The question* reads the roots one at a time, and each root's range, until it has counted C rows: at
  most C index entries, plus, for each root it opens, the grant row, a lookup of the granted resource and
  one seek. For a caller with one root it is one range; for a caller with m grants on single resources,
  min(m, C) seeks. A root whose range holds no row of type T costs its seek and counts nothing, so a caller
  with very many such roots is the one case in which the question opens more than C ranges.
- *List first* (V < C) reads about V index entries across the roots' ranges (the question has read them
  once already), keeps each once, looks the V ids up in t's `ResourceId` index and sorts them with a top-k
  sort: about V seeks and V·log k comparisons, independent of N but for the depth of the indexes.
- *Row check* (V ≥ C) reads t in the requested order from the keyset position and runs the point check on
  each row until k pass. When the caller's rows are spread through the order, that is about k·N/V rows, each
  at most (D + 1)·|S| grant seeks. This is the paper's Theorem 3 with the per-row walk replaced by the lineage.
  For a caller who sees most of the table (V ≈ N) it is about k rows, independent of N.

**The cap.** Let c_l be the cost of a listed row (an index entry, a lookup in t, its share of the sort)
and c_r the cost of a checked row (a row of t and its point check). Listing first costs about c_l·V and the
row check about c_r·k·N/V. They are equal at V* = √(c·k·N), with c = c_r / c_l the ratio of the unit
costs. Measured on both engines, c is near 3 for pages of 20 to 25 rows, which puts V* between 7.7·√N and
8.7·√N; SqlOS takes C = 8·√N, with a floor of 1,000 for small or never-measured tables, where listing 1,000
rows first costs milliseconds. A caller below C is listed first and costs less than c_l·C; a caller at or
above C has the rows checked and costs at most c_r·k·N/C ≈ c_l·C. With the question's at most C entries,
the worst page over all callers whose rows are spread through the order is Θ(√N), near the cap: C is 8,000
at N = 10⁶, about 25,000 at N = 10⁷ and about 57,000 at N = 5·10⁷.

**The worst case the cap does not cover.** The row check's k·N/V assumes the caller's rows are spread
through the order. A caller with V ≥ C whose rows all sit at the far end of the requested order (the rows
of a customer dormant for years, sorted newest first) has the N − V rows before them read, each with its
point check: Θ(N). Listing first would cost V instead, which is the same order when V is a constant share
of N; and the count does not see where in the order the caller's rows sit, so the rule cannot tell this
caller from one whose rows are spread. Only an index on t ordered by who can see each row, then by the sort
key, lets the engine start at the caller's rows, and SqlOS by design puts nothing on application tables.
An application whose screen is scoped to the customer can declare that index itself, on its own column.

A count, a join or a list without a limit evaluates the filter on every candidate: list first reads the
caller's V rows once; the row check reads t, each row with its point check, as the same count would read t
without the filter. The point check alone reads one row of the resources table and probes the grants at
levels Reach(x)…d, as before.

## 5. The cost of a page, measured

The benchmark harness (`tests/SqlOS.Benchmarks`) measures each page and point check on both engines as the
catalog grows, holds every one to a fixed limit per engine, checks every answer against ground truth, and
records the filter `BuildFilterAsync` chose and the plan the engine ran. The figures for this design come
from the benchmark run on the `fga-closure` pull request; the table below is filled from it. Each row is the
first page in key order (k = 20); each cell gives the value at N = 1M / 10M / 50M products.
`BuildFilterAsync` is the time to resolve the caller and answer the question; the query is the page's
statement alone.

| Caller | Sees | Filter chosen (1M / 10M / 50M) | `BuildFilterAsync` ms (1M / 10M / 50M) | Query ms (1M / 10M / 50M) |
| --- | --- | --- | --- | --- |
| Administrator | everything | (to be filled from the full run) | (to be filled from the full run) | (to be filled from the full run) |
| Chain manager | about 7% | (to be filled from the full run) | (to be filled from the full run) | (to be filled from the full run) |
| Region manager | about 1% | (to be filled from the full run) | (to be filled from the full run) | (to be filled from the full run) |
| Store manager (sparse) | about 0.0065% | (to be filled from the full run) | (to be filled from the full run) | (to be filled from the full run) |
| 100 store grants | 100 stores across every chain | (to be filled from the full run) | (to be filled from the full run) | (to be filled from the full run) |
| 10,000 single-product grants | 10,000 products | (to be filled from the full run) | (to be filled from the full run) | (to be filled from the full run) |
| 100,000 single-product grants | 100,000 products | (to be filled from the full run) | (to be filled from the full run) | (to be filled from the full run) |
| Point check | one product | — | — | (to be filled from the full run) |
