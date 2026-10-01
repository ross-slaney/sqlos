# Closure-Indexed List Filtering for SHRBAC

Companion note to *Database-Native Authorization for Human and Autonomous Principals: The SHRBAC Model*
(`shrbac-compsac-2026.md`). It closes the sparse-access case that Theorem 3 of the paper leaves open, without
changing the model, the row filter's answers, or the public API.

**Notation** follows the paper: N resources, D the maximum hierarchy depth (`MaxResourceHierarchyDepth`), k the
page size, M = |resolve(p)| the caller's principal set, σ the selectivity of authorized rows. New here: A, the
caller's *access roots* (Section 2), and C, the *ancestor closure* (Section 3). Database artifacts are in
monospace.

---

## 1. The gap

Theorem 3 of the paper bounds a cursor page of the row filter at O(k/σ · D · M · G_max): the engine examines
about k/σ candidate rows to find k authorized ones. For dense access (σ ≈ 1) that is O(k · D). For sparse
access it is not: a manager of one store among 13,800, listing "every product I can see" over a catalog in
which products arrive in time order, examines k/σ ≈ 400,000 rows per page. The benchmark in
`tests/SqlOS.Benchmarks` measures 33 s on PostgreSQL and 161 s on SQL Server for that page, at every N.

The cost is not in the model. Definition 1 says who may see what; the row filter *tests* it per candidate
row, and testing is the wrong direction for enumeration. This note *inverts* the test: it stores, for every
ancestor, the resources beneath it, and reads a page as one index range per grant the caller holds.

Section 6.4 of the paper already names the device: "a closure table replaces O(D) recursive expansion with
O(1) lookup". Here the closure is keyed by ancestor, which is what turns it from a faster point check into a
page that costs O(|A| · (k + log N)) regardless of σ.

---

## 2. Definitions

**Resources.** R is the set of resource rows. Each r ∈ R has parent(r) ∈ R ∪ {⊥}, IsActive(r) ∈ {0, 1},
type τ(r), and a sequence number Seq(r) ∈ ℕ assigned at creation (`Resources.Seq`), unique and increasing
in creation order. Seq is the page order and the cursor.

**The active chain.** For r with IsActive(r) = 1, define c_0 = r and, while i < D, parent(c_i) ≠ ⊥ and
IsActive(parent(c_i)) = 1, define c_{i+1} = parent(c_i). Write chain(r) = (c_0, …, c_L), L ≤ D. This is
exactly the set of rows the row filter's recursive CTE visits: the walk starts at an active target and
passes only through active ancestors, at most D steps.

**Malformed.** r is *malformed* when IsActive(r) = 1 and either some c_j equals an earlier c_i (i < j ≤ L: a
cycle among the walked nodes) or L = D and parent(c_D) ≠ ⊥ (the walk reached the depth limit with more above
it). These are the two conditions under which `fn_IsResourceAccessible` fails closed (`CycleDetected`,
`truncated`), stated on the same walk.

**Access roots.** For caller p, permission perm and time t:

```
A(p, perm, t) = { r ∈ R : IsActive(r) ∧ alive(p, t) ∧
                  ∃ p' ∈ resolve(p), ∃ (p', role, r) ∈ Grants :
                      alive(p', t) ∧ within(g, t) ∧ perm ∈ perms(role) }
```

where alive() is the subject lifecycle rule (active user, active group, unexpired service account, existing
agent) and within() the grant's time window. This is `fn_AccessRoots(@SubjectIds, @PermissionId)`. A is a
property of the caller and the query, not of any candidate row.

**The decision.** The shipped row filter computes

```
allowed(p, perm, r, t) = IsActive(r) ∧ ¬malformed(r) ∧ typeOK(perm, r) ∧ ∃ i ≤ L : c_i ∈ A(p, perm, t)
```

which is Definition 1 of the paper with the lifecycle refinements the library added since: the existential
over ancestors is over the active chain, and the grant conditions are those of A.

**The closure.** For every r, define its *pairs*

```
pairs(r) = { (c_i, τ(r), r) : 1 ≤ i ≤ L }      if IsActive(r) = 1 and ¬malformed(r)
pairs(r) = ∅                                    otherwise
```

and the closure C = ⋃_r pairs(r), stored as `{Resources}Closure(AncestorSeq, TypeSeq, DescendantSeq)` with
that clustered primary key. A row (a, T, d) of C says: d is active and well formed, a is a proper ancestor of
d, and every node from d up to and including a is active.

**Invariant I.** The stored table equals C for the current resource table.

---

## 3. Maintenance

The closure is maintained by three statement-level triggers on the resources table and one procedure,
`Apply`, which they share. No application code participates; a resource row written by EF Core, by raw
SQL, or by a DBA is covered alike.

**Apply(T)** walks up from every target d ∈ T with one recursive query:

```
chain:  (d, a_1, 1)          for d ∈ T with parent(d) ≠ ⊥ and IsActive(d)
        (d, a_{i+1}, i+1)    from (d, a_i, i) when parent(a_i) ≠ ⊥, IsActive(a_i), i ≤ D
pairs:  (d, a_i, i, Active = IsActive(a_i))
```

It raises (SQL Server error 51011 / 51012, PostgreSQL SQLSTATE `SQ011` / `SQ012`) if some generated row has
a_i = d or depth > D, and otherwise inserts every generated pair with Active = 1. The rebuild calls the
same walk in *tolerant* mode, which skips the malformed descendants instead of raising. On PostgreSQL the
walk, the insert and the malformed count are one statement (a data-modifying CTE), so an insert of one
resource costs one recursive query and no temporary table.

**Lemma 1 (Apply computes pairs).** For an active target d, the rows Apply generates with Active = 1 are
exactly {(a_i, τ(d), d) : 1 ≤ i ≤ L}, and Apply raises (or, tolerantly, skips d) exactly when d is
malformed.

*Proof.* The walk generates a_1 when d is active with a parent, and generates a_{i+1} from a_i exactly when
a_i is active, has a parent, and i ≤ D. Hence the generated ancestors are a_1, …, a_L (the active chain) and
possibly one more row a_{L+1}: the first inactive ancestor (Active = 0, not inserted), or, when L = D and
parent(c_D) ≠ ⊥, a row at depth D + 1. A row at depth D + 1 exists iff L = D ∧ parent(c_D) ≠ ⊥, the
truncation condition. A row with a_i = d (i ≤ D + 1) is a cycle through d: if i ≤ D the row filter sees d
repeated; if i = D + 1 the row filter is truncated. A cycle not through d keeps the walk inside the cycle's
active nodes, which all have parents, until depth D + 1. Conversely, a repeated node among c_0..c_D in the
row filter's walk means the closure walk never reaches a node without a parent, so it reaches depth D + 1.
So the walk raises iff r is malformed, and the Active = 1 rows are the chain pairs. □

**Lemma 2 (locality).** pairs(d) is a function of the rows reachable from d by at most D + 1 parent steps.
A statement that changes a set X of rows (inserted, updated or deleted) can change pairs(d) only for d ∈ X
or d a descendant of some x ∈ X, by the parent pointers before or after the statement.

*Proof.* If d ∉ X and no row on d's old or new parent path is in X, both paths consist of unchanged rows,
so they are the same path with the same values, and pairs(d) is unchanged. □

**The insert trigger** calls Apply on the inserted rows. New rows have no closure rows yet (Seq is new), and
no existing row can have a new row on its path (the foreign key on `ParentId` forbids a parent that does not
exist yet; a child inserted in the same statement is itself a target). With Lemma 1 and Lemma 2, I holds
after the statement.

**The delete trigger** computes, for the deleted rows, their pairs in the table *as it was* (the current rows
plus the deleted images, which is exactly the old table) and deletes those closure rows by primary key. No
other row's pairs change: a deleted row cannot be on a remaining row's path (the foreign key).

**The update trigger** returns at once unless some row's `ParentId`, `IsActive` or `ResourceTypeId`
changed. Otherwise its targets are the changed rows and every current descendant of them (the only rows
whose pairs can change, Lemma 2). It deletes the targets' old pairs by primary key, then calls Apply on the
targets. The old pairs are computed by the same walk over an *old-state source*. Both triggers first copy
the statement's old images into a temp table with a primary key on `Id`, so every step of the walk is an
index probe whether it lands on a current row or an old image (a bulk statement of n rows would otherwise
scan its n old images once per probe, O(n²)).

- On PostgreSQL the source is exact: the current rows with each updated row replaced by its old image (an
  outer join to the old images, which is allowed inside a recursive term there).
- On SQL Server the recursive member of a CTE may not contain an outer join, so the source is
  U = (current rows) ∪ (old images): a changed row appears twice, once with its old parent and state and
  once with its new ones.

**Lemma 3 (old pairs are deleted, nothing else).** The walk over either source from a target d generates
every pair in pairs_old(d) with Active = 1, and generates only rows whose descendant is a target.

*Proof.* With the exact source the walk is the walk of Lemma 1 over the old table, which generates
pairs_old(d) exactly. With U, d's old active chain is a path whose every node is in U with its old values
(unchanged rows are their own old image; changed rows contribute their old image), so the walk, which
follows every row in U with a matching Id, generates that path with its old activity flags and old type.
Every generated row carries the target it started from. □

Over U the walk may generate more rows than the old pairs (a changed row's new parent is also followed).
Those extra rows name only targets as descendants, and Apply reinserts every target's current pairs
afterwards, so over-deletion of a target's own rows is harmless.

**Theorem 1 (the triggers keep I).** If I holds before a statement on the resources table and the
statement's trigger completes, I holds after it. If the trigger raises, the statement is rolled back and
nothing changed.

*Proof.* Insert and delete: above. Update: for d not a target, pairs(d) is unchanged (Lemma 2); for a target
d, every old pair was removed (Lemma 3), the only removed rows belong to targets, and Apply inserted exactly
pairs_new(d) (Lemma 1). □

**Corollary (well-formedness is preserved).** A resource table with no malformed row stays that way under
any sequence of completed statements: a statement that would make a target malformed is rejected (Lemma 1),
and a non-target cannot become malformed (Lemma 2). The rebuild (tolerant Apply over every row with a parent,
in one transaction) establishes I on any table, excluding whatever malformed rows it already had.

---

## 4. The authorized page

**Definition (visible set and page).** For caller p, permission perm, type T and cursor κ,

```
V(p, perm, T, κ) = { r : τ(r) = T ∧ Seq(r) > κ ∧
                     ( ∃ a ∈ A : (a, T, r) ∈ C
                       ∨ ( r ∈ A ∧ wf(r) ) ) }

wf(r) = parent(r) = ⊥ ∨ IsActive(parent(r)) = 0 ∨ (parent(r), T, r) ∈ C
```

page(p, perm, T, κ, k) is the k smallest elements of V by Seq; the next cursor is the Seq of the last one.
This is `BuildVisibleResourcesPageSql`: for each root a ∈ A (one `fn_AccessRoots` evaluation), the first k
closure rows in the range (AncestorSeq = Seq(a), TypeSeq = T, DescendantSeq > κ), plus a itself when it is
of type T and wf(a) holds (one point lookup), merged, deduplicated, cut to k, and joined to `Resources` by
Seq. `ListVisibleAsync` returns an empty page when perm is typed to a type other than T; otherwise
typeOK(perm, r) holds for every r of type T.

**Theorem 2 (equivalence).** Under I, for every resource table (malformed rows included), every caller,
permission, type and cursor:

```
r ∈ V(p, perm, T, κ)  ⟺  allowed(p, perm, r, t) ∧ τ(r) = T ∧ Seq(r) > κ
```

*Proof.* (⇐) allowed gives IsActive(r), ¬malformed(r), and c_i ∈ A for some i ≤ L. If i ≥ 1, (c_i, τ(r), r)
∈ pairs(r) ⊆ C, so r ∈ V by the first disjunct. If i = 0, r ∈ A; and wf(r): if parent(r) = ⊥ or inactive,
immediately; else parent(r) = c_1 is active, L ≥ 1, and (c_1, τ(r), r) ∈ pairs(r) ⊆ C since r is not
malformed.

(⇒) First disjunct: (a, T, r) ∈ C with a ∈ A. By I this row is in pairs(r), so r is active and not malformed,
a = c_i for some i ≥ 1, and c_i ∈ A: allowed. Second disjunct: r ∈ A, so IsActive(r), and c_0 = r ∈ A; it
remains to show ¬malformed(r). If parent(r) = ⊥ or inactive, chain(r) = (r): no repeated node and, as D ≥ 1,
not truncated. Otherwise (parent(r), T, r) ∈ C, which by I lies in pairs(r), so r is not malformed. □

So a page is the row filter's answer restricted to one type, in Seq order, on any data. The two paths cannot
disagree, which is what lets an application use either for the same screen.

**Theorem 3 (page cost).** Let |C| ≤ N · D be the closure's size. One page performs:

- one evaluation of `fn_AccessRoots`: the caller's live subjects (M rows), then their current grants
  read by subject, O(M · log G + |A|) index entries, independent of N and of other callers' grants;
- for each root a ∈ A: one ordered index range read of at most k closure entries (one seek, O(log |C|)) and
  one point lookup for wf(a);
- a merge of at most |A| · (k + 1) candidates to the k smallest, O(|A| · k · log(|A| · k)) comparisons;
- k point lookups into `Resources` by Seq, O(k · log N).

Index entries read: O(|A| · (k + log N)). This bound does not involve σ, the cursor position, N (beyond the
logarithm), or the number of grants other callers hold. □

**Proposition (tightness).** Any algorithm that answers the page from an index keyed per ancestor must read
Ω(k + |A|) entries: k to produce the answer, and one from each root's range, since any root's first entry may
be the page's smallest. The page reads at most (k + 1) per root, so it is within a factor of k + 1 of that
bound, and it meets it when the roots are disjoint and the page is filled from one of them.

**Corollary (the sparse case).** The store manager's page (|A| = 1 grant, M = 3 subjects, k = 20) reads at
most 21 closure entries and 20 resource rows, at any N and any σ. The row filter's page for the same caller
examines k/σ ≈ 400,000 rows (Theorem 3 of the paper, confirmed by the benchmark's captured plans). The
benchmark's `improvement` gate requires the closure page to take at most 5% of the row-filter page's time;
the measured ratio is far smaller.

---

## 5. The row filter after the change

`fn_IsResourceAccessible` keeps the paper's evaluation: the same walk up (recursive CTE, cycle and depth
guards) and, for each ancestor c_i, a lookup of the grants on c_i. What moved is the part of each grant
check that depends only on who is asking:

```
live(p, t) = { p' ∈ resolve(p) : alive(p', t) }   if alive(p, t)
live(p, t) = ∅                                    otherwise
```

This is `fn_ActiveSubjects(@SubjectIds)`, evaluated once per query: M rows, whatever the caller was
granted. Per ancestor the filter probes the grants' `ResourceId` index (which carries `SubjectId`,
`RoleId` and the time window) and keeps a grant when its subject is in live(p, t), its role carries the
permission, and it is within its window.

**Proposition (same answers).** The previous predicate was ∃ i ≤ L, ∃ grant g on c_i with g.subject ∈
resolve(p), alive(g.subject, t), alive(p, t), perm ∈ perms(g.role) and within(g, t). Since alive(p, t) does
not depend on g, and g.subject ∈ resolve(p) ∧ alive(g.subject, t) ∧ alive(p, t) ⟺ g.subject ∈ live(p, t),
the two predicates are equal row by row.

**Proposition (no grant-count term).** Per candidate row the filter performs at most D + 1 index probes,
each reading the grants on one resource. Once per query it reads M subjects and the roles that carry the
permission. Nothing depends on how many grants the caller holds elsewhere, so the per-row bound is the
paper's O(D · M · G_max), and the per-page bound its Theorem 3. (The index is keyed by `ResourceId` alone,
since migration v10 kept keys under SQL Server's size limit, so a probe also passes over other people's
grants on that resource, filtering them on the index's included columns. The density pass measures
that: ×1.0 with 100 other people's grants on the root.)

**Why the grants are not hoisted too.** An earlier revision of this change evaluated the whole grant
condition once per query, as the root set A, and tested each ancestor for membership in A. That adds an
O(|A|) term to every query, a point check included, and the membership test is constant-time only if the
engine hashes A. PostgreSQL's `= ANY(array)` over a computed array scans it: 100,000 grants and 10,000
probes took 4.2 s, against 14 ms for a hashed `IN (SELECT …)`. Every benchmark principal then held exactly
one grant, so no gate could see it. The benchmark now includes people with 10,000 and 100,000
single-product grants, and their checks and pages run beside the previous release's function under the
`regression` gate.

`fn_AccessRoots` remains, for the authorized page only (Section 4): enumerating from grants must read each
grant once, which is the |A| in its bound.

---

## 6. What maintenance costs

With d at depth ℓ ≤ D and S(x) the subtree beneath x:

| Statement | Reads | Closure rows written |
|---|---|---|
| insert one row at depth ℓ | ℓ + 1 point lookups | ≤ ℓ inserts |
| insert n rows | O(n · D) | ≤ n · D |
| reparent, deactivate, reactivate or retype x | O(\|S(x)\| · D) | ≤ 2 · \|S(x)\| · D (delete old, insert new) |
| delete n rows | O(n · D) | ≤ n · D deletes |
| rebuild | O(N · D) | ≤ N · D, one transaction |

A subtree operation changes every pair that passes through x, so Ω(|S(x)|) writes are inherent; the factor
D is the pairs per node. Storage is |C| = Σ_r L(r) ≤ N · D rows of 20 bytes (8 + 4 + 8), one index: at 100M
products with 4–9 ancestors each, about 450M rows. The benchmark's maintenance pass reports the measured
cost of each operation beside the single-row insert cost without the triggers.

---

## 7. Relation to the paper

- **Definition 1 is unchanged.** The decision is the paper's, with the lifecycle refinements the library
  already applied. A and C are derived from it, not added to it.
- **One mechanism for both questions.** `fn_AccessRoots` is the caller's side of Definition 1 (the
  principal expansion and the grant match); the resource side is either walked up (point check, row filter)
  or read down from the closure (page). The point check's bound O(D · M · G_max) and the row filter's
  O(k/σ · D · M · G_max) stand; the page adds O(|A| · (k + log N)).
- **Section 6.4's closure remark**, realized: keyed by ancestor, maintained by the database, and used for
  enumeration rather than for a faster point check.
- **The page order is fixed** (creation order) and its only predicate is the type. Any other ordering or
  predicate composes with `BuildFilterAsync`, whose bound depends on the selectivity of the composed query;
  an application chooses by which bound it needs.
