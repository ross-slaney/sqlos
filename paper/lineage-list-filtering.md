# The resource lineage: list filtering without walking the tree

Companion note to *SHRBAC* (COMPSAC 2026). That paper's row filter decides each row by walking up the
resource tree and probing the grants at every ancestor; Theorem 3 there bounds a page by the rows the engine
must examine, which for a caller who sees a fraction σ of the table is about k/σ rows for a page of k. This
note replaces the walk with a precomputed **lineage** on the resources table, proves the filter it supports
equals Definition 1 of the paper, proves the triggers keep the lineage exact, and gives the cost of a page
under the two forms the filter takes. The implementation is `SqlOSFgaLineage`, the two database providers'
`*.Lineage.cs` files, and `SqlOSFgaFilterBuilder`.

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

## 2. The filter equals the paper's rule

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

The predicate `SqlOSFgaFilterBuilder` emits is the disjunction over the caller's roots of
`Ancestor_ℓ = a AND Reach ≤ ℓ`, grouped by level, conjoined with the type condition; with the scope columns
it reads the same values from the application row, which carries a copy of its resource's lineage
(Section 4). The point check `fn_IsResourceAccessible` evaluates the same condition from the other side:
for the one target x it enumerates the levels Reach(x)…d and probes the grants on each Ancestor_ℓ(x).

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
move or a change of activity, and nothing for a grant or a revoke, which are not part of the lineage. The
rebuild (`…_LineageRebuild`) is the same recurrence run over every row, level by level.

## 4. Scope columns

With `ScopeColumns` on, every application table whose entity carries a `ResourceId` holds a copy of its
resource's Ancestor₀…Ancestor_D, Reach, and type. The copy is refreshed by the resources triggers for every
affected resource (step 5 of the refresh, keyed by `ResourceId`), by the table's own insert trigger, and by
its update trigger when a row's `ResourceId` changes; a deleted resource clears the copy. Each copy is
therefore equal to the lineage of the resource the row names at all times, or NULL when that resource does
not exist, which denies the row. Theorem 1 then applies to the row's own columns.

## 5. The cost of a page

Take a page of k rows in an order the application chose, for a caller whose roots cover a fraction σ of the
table, N rows in all, and let S be the number of rows beneath the caller's roots (S = σN).

**Without scope columns**, the predicate joins each candidate row to its resource. The optimizer has two
plans, and chooses from its statistics on the ancestor columns:

- *Scan*: read the table in the requested order and test each row with one primary-key lookup of its
  resource. About k/σ rows are examined; each costs one lookup, not a walk of D steps with a grant probe at
  each. This is the plan for a caller who sees most of the table.
- *Drive*: read the caller's scope from the ancestor index at the root's level (S entries), join the rows by
  `ResourceId`, sort, and take k. This is the plan for a caller who sees little of it.

The cost is therefore min(k/σ, S) lookups plus a sort of at most S rows, and a page never examines more rows
than the paper's filter did; it only examines them more cheaply. Over all callers the worst case is a scope
of about √(kN) rows, where both plans cost the same.

**With scope columns**, for a caller with one root at level ℓ and an order the application declared an
index for, the predicate is `Ancestor_ℓ = a AND Reach ≤ ℓ` on the row itself, and the mirrored index
(Ancestor_ℓ, order columns, key) answers the page with one seek followed by k entries: the cost does not
depend on N or σ. A caller with several roots reads one seek per root; the engine merges or sorts the
streams, which costs at most S. A caller with more roots than the list limit (1,000) is checked row by row by
`fn_IsResourceAccessible`, which enumerates the levels Reach(x)…d of each candidate row and probes the grants on
its ancestor at each: D + 1 lookups per row at most, whatever the number of grants, and the scan plan only.

The benchmark harness (`tests/SqlOS.Benchmarks`) measures the three on the same data from 1M to 50M rows.
