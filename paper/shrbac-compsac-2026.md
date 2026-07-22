# Database-Native Authorization for Human and Autonomous Principals: The SHRBAC Model

Ross Slaney

**Abstract.** Enterprise applications must answer two authorization questions: "can this principal act on this resource?" (point check) and "which resources can this principal access, filtered, sorted, and paginated?" (list filtering). The latter is harder: it requires composing authorization with application-defined filtering, sorting, and pagination into a single efficient query. We present SHRBAC (Scoped Hierarchical Role-Based Access Control), an authorization model whose structural constraints — tree-structured resources, flat roles, non-recursive groups, and scoped temporal grants — make enforcement a composable database predicate with bounded cost, and whose polymorphic principals extend the same grant semantics to human users, service accounts, and autonomous agents. We formalize the model, prove soundness and completeness of an inline Table-Valued Function (TVF) enforcement, and show that under cursor pagination, bounded principal sets and grant multiplicity, and indexed nested-loop plans, per-page cost is O(k · D). Empirical evaluation on SQL Server 2022 at 1.2M resource nodes (D = 5) and 1.5M (D = 10) confirms N-independence across three orders of magnitude in dense-access workloads.

**Keywords:** access control, RBAC, authorization, query composition, autonomous principals

---

## 1. Introduction

### 1.1 The Problem

Modern B2B SaaS applications require authorization systems that answer two fundamentally different questions:

1. **Point check**: "Can principal *p* perform action *a* on resource *r*?" — a boolean decision.
2. **List filtering**: "What resources of type *T* can principal *p* access, given search criteria *C*, sorted by *S*, paginated to page *k*?" — a set-returning query integrated with application data.

The first is well-understood: Google's Zanzibar [1] and its descendants handle millions of point checks per second through graph traversal and caching. The second — the **list filtering problem** — is trivial for flat access models (`WHERE tenant_id = @currentTenant` composes directly) but becomes substantially harder when access derives from grants at varying hierarchy levels, resolved through group memberships, under temporal constraints.

Increasingly, principals are not only human users but **autonomous AI agents** that enumerate and filter large resource sets in loops. SHRBAC introduces no agent-specific policy semantics; it treats agents as first-class principals, so the same scoped, temporal grant model applies to human and autonomous callers, and the **principle of least privilege** [2] is enforced structurally: narrow grants (specific subtree, limited role, bounded duration) guard against broad, long-lived authority.

When authorization logic resides outside the database, an impedance mismatch arises. Industry bridges this gap through ID lookups, batch post-filtering, partial policy evaluation, or materialized permission views — each trading scalability, consistency, or complexity. SHRBAC instead chooses structural constraints so that per-page enforcement cost is O(k · D) under bounded local parameters — linear in page size and tree depth, independent of total resource count N and policy set size.

**Notation.** Throughout the paper, N denotes total entities or resource nodes under evaluation, k the requested page size, D the maximum resource-tree depth, M = |resolve(p)| the number of effective principals for caller p, G_max the maximum active grant multiplicity for the same principal-resource pair, and σ the selectivity of authorized rows.

### 1.2 Contributions

1. **Constraint-driven model.** We introduce SHRBAC, whose four structural constraints — tree-structured resources, non-recursive principal groups, flat roles, and scoped temporal grants (principal × role × resource × time) — are deliberately chosen so that enforcement admits a fixed, schema-level relational artifact with bounded cost independent of policy set size. Polymorphic principals make users, groups, service accounts, and autonomous agents first-class grant recipients with least-privilege, time-bounded authority.
2. **Formalization with proved enforcement.** Access evaluation is two-dimensional — upward over a bounded-depth resource tree, outward over effective principals. We realize enforcement as a single inline Table-Valued Function `EXISTS` predicate defined at schema time and prove it sound and complete; grants are data rows, not predicate terms, requiring no runtime compilation, post-filtering, or external graph traversal.
3. **Complexity theorem for list filtering.** Per-row cost is bounded at O(D · M · G_max); under cursor pagination, per-page cost is O(k · D) when M and G_max are bounded — independent of resource count N, policy set size, and page depth. This formal bound for the list-filtering problem is absent in prior RBAC, ABAC, and ReBAC models.
4. **Production-scale empirical validation.** At 1.2M resources (D = 5) and 1.5M resources (D = 10), we confirm N-independence across three orders of magnitude, linear scaling in k and D, constant per-CTE-hop cost, cursor-depth independence, and grant-breadth independence in tested dense-access workloads.

---

## 2. Background and Related Work

### 2.1 RBAC Foundations

Sandhu et al. [5] defined the RBAC96 family: RBAC0 (core), RBAC1 (hierarchical roles), RBAC2 (constraints), and RBAC3 (combined). The NIST standard [6] formalized Core and Hierarchical RBAC. **Critical gap:** RBAC96 defines only a role hierarchy; the resource space is flat with no concept of resource hierarchy or scope.

### 2.2 Hierarchical Models

**ROBAC** (Zhang et al. [3]) introduced the (user, role, organization) triple with grants cascading down an organization hierarchy. SHRBAC's (principal, role, resource) grant is structurally identical, but ROBAC defines a policy semantics without constraining the model to admit a fixed enforcement artifact with bounded evaluation cost. SHRBAC's novelty is the alignment of model constraints with relational query-planning guarantees, extended to polymorphic principals. **RRBAC** (Solanki et al. [7]) formalized resource hierarchies with grant cascade but did not address polymorphic principals, query composition, or enforcement mechanisms. **OrBAC** (Kalam et al. [8]) generalized RBAC with five hierarchies; its expressiveness exceeds what TVF-based enforcement requires — SHRBAC is a practical engineering subset.

### 2.3 Query Modification and Enforcement

Stonebraker and Wong [4] introduced query modification for INGRES — the ancestor of Oracle VPD and SQL Server/PostgreSQL row-level security. Rizvi et al. [9] distinguished the *Truman model* (silent filtering, which SHRBAC implements) from the *Non-Truman model* (query rejection). Pappachan et al. [10] showed that naive predicate injection does not scale with thousands of policies; SHRBAC's predicate is a single TVF invocation regardless of policy count.

### 2.4 Zanzibar, ReBAC, and List Filtering

Google's Zanzibar [1] uses relationship tuples with a userset rewrite system; Fong [11] formalized ReBAC using modal logic. Zanzibar's `tuple_to_userset` handles resource hierarchies through general graph traversal. SHRBAC constrains resources to a tree and uses typed roles rather than arbitrary relation composition — tree traversal has bounded depth, which is what makes TVF enforcement tractable. Composing authorization into list queries has no canonical academic name: vendors call it "ACL-aware filtering," "list filtering," or a "query plan" problem [12]. At scale, SpiceDB recommends progressively more complex strategies (LookupResources → CheckBulkPermissions → Materialize). SHRBAC is designed so that the integration surface is a relational predicate — composable by construction.

### 2.5 Summary of Gaps

| Capability | RBAC96 | ROBAC | RRBAC | OrBAC | ReBAC | SHRBAC |
|---|---|---|---|---|---|---|
| Role hierarchy | RBAC1+ | Yes | Yes | Yes | N/A | No (flat) |
| Resource hierarchy | No | Org hier. | Yes | Org hier. | Graph | Yes (tree) |
| Grant cascade | No | Yes | Yes | Yes | `tuple_to_userset` | Yes |
| Polymorphic principals | No | No | No | Partial | Usersets | Yes |
| Group-as-principal | No | No | No | No | Via tuples | Yes |
| Agent-as-principal | No | No | No | No | No | Yes |
| Temporal grants | No | No | No | Contexts | No | Yes |
| Query composition | No | No | No | No | No | Yes |
| Formal enforcement | No | No | No | No | Graph trav. | Query mod. |

To our knowledge, no prior model jointly formalizes resource hierarchy with grant cascade, polymorphic principals (humans, groups, and agents as first-class grant recipients), and a concrete query-composable enforcement mechanism with a complexity bound.

---

## 3. The SHRBAC Model

### 3.1 Basic Sets

The model comprises: a finite set **P** of *principals*, each typed τ(p) ∈ {user, group, service_account, agent}, where an agent is an autonomous process (AI agent, orchestration pipeline, or background service) participating in the same grant relation as human users under the same scoping and temporal constraints; a finite set **R** of *roles*; a finite set **PERM** of atomic *permissions* (e.g., `PROJECT_VIEW`); a finite set **RES** of *resources* forming nodes in a rooted tree, each carrying a type from **RT** (e.g., `portal_root`, `agency`, `project`); and the time domain **T** (UTC timestamps).

### 3.2 Resource Hierarchy

The resource hierarchy is a rooted tree (RES, parent) with **parent**: RES → RES ∪ {⊥} and parent(*r₀*) = ⊥ for the root *r₀*. Define **ancestors(r)** = {parentⁱ(r) | i ≥ 0 ∧ parentⁱ(r) ≠ ⊥} (note r ∈ ancestors(r)), **descendants(r)** = {r' ∈ RES | r ∈ ancestors(r')}, and **depth(r)** = |ancestors(r)| − 1. The tree is bounded: depth(r) ≤ D for all r; in practice D = 3–5 for typical SaaS and up to 10–15 for deep enterprise hierarchies. An **authorized entity** is a row in an application table carrying a `ResourceId` referencing a node in the resource tree.

### 3.3 Role-Permission Assignment

**PA** ⊆ R × PERM is the role-permission assignment. Each permission has an associated resource type: **applicable**: PERM → RT. Roles are **flat** — no role hierarchy. The resource hierarchy provides the inheritance dimension, avoiding the combinatorial complexity of dual hierarchies.

### 3.4 Principal Resolution

Groups are themselves principals. **members(g)** = {u ∈ P | τ(u) = user ∧ u is a member of g}. Membership is not recursive — groups cannot contain other groups.

**Principal resolution** expands a user to their effective principal identities:

- **resolve(p)** = {p} ∪ {g ∈ P | τ(g) = group ∧ p ∈ members(g)}

For non-user principals (groups, service accounts, agents), resolve(p) = {p}.

### 3.5 Grants

**G** ⊆ P × R × RES × (T ∪ {⊥}) × (T ∪ {⊥})

A grant g = (p, role, res, t_from, t_to) assigns role `role` to principal `p` at resource `res`, effective during the closed (inclusive) interval [t_from, t_to]; a ⊥ bound means unbounded.

**Active grants** at time *t*:

- **active(t)** = {(p, role, res) | (p, role, res, t_from, t_to) ∈ G ∧ (t_from = ⊥ ∨ t_from ≤ t) ∧ (t_to = ⊥ ∨ t_to ≥ t)}

The grant triple (principal, role, resource) enforces least privilege: authority is scoped to a specific subtree (not global), a specific role (not all permissions), and optionally a specific time window (not permanent).

### 3.6 Access Evaluation Function

**Definition 1 (Access Decision).** Given principal *p*, permission *perm*, resource *r*, and time *t*:

```
allowed(p, perm, r, t) =
  ∃ p' ∈ resolve(p),
  ∃ r' ∈ ancestors(r),
  ∃ role ∈ R :
    (p', role, r') ∈ active(t) ∧ perm ∈ perms(role)
```

Access is granted if *any* effective identity has *any* active grant at *any* ancestor of the target resource with a role including the requested permission. This is **two-dimensional resolution**: simultaneously walking UP the resource tree and expanding OUT the principal set.

**Running example.** Let `portal_root → agency_7 → project_42`, with Alice in group `engineering` and a single grant `(engineering, Viewer, agency_7)`. Evaluating `allowed(alice, PROJECT_VIEW, project_42, now)` searches the 2 × 3 grid resolve(alice) × ancestors(project_42) and finds the Viewer grant at (engineering, agency_7): access is granted — the grant cascades to the descendant `project_42` (Fig. 1).

[Figure 1: Two-dimensional resolution grid for the running example — principals expand outward (columns), ancestors are walked upward (rows), and the first match short-circuits.]

### 3.7 Properties

**Property 1 (Monotonicity of Hierarchy).** If allowed(p, perm, r, t) and r' ∈ descendants(r), then the same grant provides access at r' (immediate from transitivity of the ancestor relation).

**Property 2 (Monotonicity of Groups).** Adding p to group g preserves existing access and may grant additional access (existential quantification over an expanded resolve(p)).

**Property 3 (Bounded Evaluation).** For any access decision, evaluation examines at most |resolve(p)| × |ancestors(r)| × G_max grant entries, giving O(M · D · G_max).

---

## 4. Query-Composable Enforcement via TVF

### 4.1 The Table-Valued Function

The enforcement mechanism realizes Definition 1 directly as an inline Table-Valued Function (iTVF), evaluated per row as an `EXISTS` predicate:

```sql
CREATE FUNCTION dbo.fn_IsResourceAccessible(
  @ResourceId NVARCHAR(128), @PrincipalIds NVARCHAR(MAX),
  @PermissionId NVARCHAR(128), @Now DATETIME2)
RETURNS TABLE AS RETURN (
  WITH ancestors AS (
    SELECT Id, ParentId, 0 AS Depth
      FROM Resources WHERE Id = @ResourceId
    UNION ALL
    SELECT r.Id, r.ParentId, a.Depth + 1
      FROM Resources r
      INNER JOIN ancestors a ON r.Id = a.ParentId
      WHERE a.Depth < 10)
  SELECT TOP 1 a.Id FROM ancestors a
    INNER JOIN Grants g ON a.Id = g.ResourceId
    INNER JOIN RolePermissions rp ON g.RoleId = rp.RoleId
  WHERE g.PrincipalId IN (SELECT LTRIM(RTRIM(value))
          FROM STRING_SPLIT(@PrincipalIds, ','))
    AND rp.PermissionId = @PermissionId
    AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= @Now)
    AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= @Now))
```

The recursive CTE walks UP from the target resource to the root (at most D levels), joining against active grants at each ancestor. `TOP 1` provides existential semantics — one matching grant suffices. The caller-controlled `@Now` parameter ensures deterministic evaluation across all rows in a query. As an inline TVF, the optimizer composes its body into the calling query's execution plan: the application layer resolves group memberships once per request, then appends the TVF as an `EXISTS` predicate alongside application-defined filters, cursor pagination, and sorting — authorization and application logic execute as a single database operation, one round-trip, zero external calls. We use SQL Server as an exemplar because its optimizer aggressively inlines iTVFs; the enforcement requires only recursive CTEs and predicate inlining, both supported by PostgreSQL and other modern engines.

### 4.2 Correctness

**Theorem 1 (Soundness and Completeness).** For entities with depth ≤ 10, the TVF returns a non-empty result for entity *e* iff allowed(p, perm, e.ResourceId, now) = true.

*Proof.* The recursive CTE (guard `Depth < 10`) produces exactly ancestors(e.ResourceId); the joins against `Grants` and `RolePermissions` with the temporal predicates implement the existential quantification of Definition 1 over active(t). A non-empty result therefore witnesses a satisfying (p', role, r') triple, and conversely any satisfying witness matches the corresponding CTE row, principal filter, and temporal and role-permission predicates by construction. □

---

## 5. Complexity Analysis

### 5.1 Per-Row TVF Cost

For a single entity with resource at depth d ≤ D, the CTE performs O(D) index seeks on `Resources(Id)`; joining the D ancestors against `Grants` and `RolePermissions` examines at most D · M · G_max candidates; and `TOP 1` short-circuits on the first match. **Per-row cost:** O(D · M · G_max) — with D = 5, M = 10, G_max = 3, ~150 index lookups.

### 5.2 Per-Page Cost Under Pagination

**Theorem 2 (Per-Page Complexity).** Assume (1) index coverage on Resources, Grants, RolePermissions, and entity ResourceId; (2) cursor pagination on an indexed ordering key; (3) nested-loops plans with index seeks. Then dense access (σ ≈ 1) costs O(k · D · M · G_max) per page — O(k · D) when M and G_max are bounded constants — and sparse access costs O(k/σ · D · M · G_max), degenerating to O(N · D · M · G_max) when no rows are authorized.

*Proof.* With cursor pagination the engine seeks to the cursor in O(log N) and evaluates candidates sequentially, each costing O(D · M · G_max) by the per-row bound above; collecting k authorized rows at selectivity σ examines ~k/σ candidates, giving O(k/σ · D · M · G_max). The cursor start is resolved by index seek, not by scanning prior pages, so cost is independent of page depth and N. □

The per-page cost is *parameterized and predictable* — independent of N, linear in D. Alternatives instead pay an external-service round trip per page (LookupResources, batch post-filtering), a policy-compilation step (partial evaluation), or eventual-consistency denormalization infrastructure (materialized views); SHRBAC's cost is database-local with strong consistency.

---

## 6. Empirical Evaluation

We evaluate SHRBAC on SQL Server 2022 (Docker, 4 vCPU, 8 GB RAM) across three tiers: (1) small-scale isolation tests (1K–100K entities) that individually vary each factor, (2) a production-scale workload at D = 5 with 1.2M resource nodes, and (3) a deep-hierarchy workload at D = 10 with 1.5M resource nodes. The environment is a controlled validation setup, not a production latency claim; Section 7 discusses generalizability.

### 6.1 Benchmark Methodology

**Measurement protocol.** Each query: 3 warmup runs (discarded), 20 measured runs, each on a fresh connection, recording wall-clock elapsed time. We report median and P95. Page size k = 20 unless stated otherwise.

**Query pattern.** All list-filtering benchmarks execute the canonical authorized-list query: select the top k `Products` rows satisfying `EXISTS(fn_IsResourceAccessible(...))` with a cursor predicate (`Id > @cursor`) and `ORDER BY Id`. Point checks use the same TVF against a single ResourceId. Query plans were verified to use nested loops with index seeks.

**Principal parameter passing.** All reported benchmarks, including the M = 11 and M = 21 experiments, use the `STRING_SPLIT` TVF of Section 4 (no table-valued parameters), so M-scaling conclusions are limited to the tested range. `STRING_SPLIT` can produce poor cardinality estimates; table-valued parameters are recommended for larger principal sets.

**Hierarchies.** D = 5: root → 15 chains → 150 regions → 15K stores → 1.2M products = **1,215,166 resources**. D = 10: root → 5 divisions → 25 regions → 125 districts → 500 areas → 2K zones → 12K stores → 60K departments → 240K sections → 1.2M products = **1,514,656 resources**. Resources and domain rows are bulk-inserted; `UPDATE STATISTICS` is run before benchmarks.

**Isolation tests** (Benchmarks 1–7) use the same retail domain model scaled to the target depth (e.g., D=3: root → Regions → Stores → Products(N)). Each level is a real domain table whose rows map to unique resources, and the benchmark query always targets Products, so small-scale tests exercise the same schema and query patterns as the production-scale ones.

### 6.2 Isolation Tests (1K–100K)

Controlled single-factor experiments, re-seeded per configuration, confirm the core predictions: N-independence across 1K–100K (3.32–3.57ms median at D = 3; Fig. 2), monotonic depth scaling from 2.45ms (D = 1) through 3.17ms (D = 3) to 4.21ms (D = 5) consistent with O(k · D), and a negligible principal-set effect (3.32–3.47ms across M = 1–21). P95 stayed below 6.2ms in every configuration.

[Figure 2: Per-page median latency (k = 20, cursor) is flat at ~3.3–3.6ms across three orders of magnitude — 3.57 (1K), 3.39 (10K), 3.63 (50K), 3.32 (100K), 3.47 (1.2M); the small increase at 1.2M is a depth effect (D = 5 vs D = 3), not resource count.]

### 6.3 Production-Scale at D = 5 (1.2M Resources)

Principals range from company administrators (all 1.2M products accessible) to store managers (~80 products); page-size results appear in the D = 5 vs D = 10 table below. **Scope level**: despite a 15,000× difference in accessible product count, latency varies minimally — company admin 3.47ms, chain manager 3.20ms, region manager 3.11ms, store manager 2.02ms median (P95 ≤ 6.34ms) — store-level grants fastest because the CTE short-circuits. **Cursor depth** is flat: 3.08ms at page 1, 3.25ms at page 50, 3.48ms at page 500.

### 6.4 Deep Hierarchy at D = 10 (1.5M Resources)

Identical queries across both tree depths:

| k | D=5 Median (ms) | D=10 Median (ms) | Ratio |
|---|---|---|---|
| 10 | 2.28 | 3.44 | 1.51× |
| 20 | 3.47 | 5.69 | 1.64× |
| 50 | 6.89 | 11.80 | 1.71× |
| 100 | 13.71 | 22.01 | 1.61× |

Page size scales linearly at both depths: k = 10 → 100 yields 6.01× at D = 5 and 6.40× at D = 10, closely tracking O(k). The D = 10/D = 5 ratio (1.51–1.71×) is below the theoretical 9/4 = 2.25× because fixed per-page overhead is proportionally larger at D = 5; the gap narrows at larger k. Per-row cost is ~0.14ms at D = 5 (~0.034ms per CTE hop) and ~0.22ms at D = 10 (~0.024ms per hop), giving O(k · D) a concrete, measurable constant. Scope-level and cursor-depth results at D = 10 mirror D = 5: scope varies minimally (3.06–5.54ms), and cursor depth is flat (5.11–5.38ms across pages 1–500).

### 6.5 Point Access Checks

The TVF also serves as a point check. Across target depths 0–4 at D = 5 (1.2M resources), point checks complete in 0.86–1.31ms median; across depths 0–9 at D = 10 (1.5M resources), in 0.89–1.46ms — all **under 1.5ms median** even at depth 9. Grant set size (1–20 active grants; 1.19–1.38ms) and principal set size (M = 1–21; 0.98–1.28ms) are flat in the tested `STRING_SPLIT` range. The CTE examines only the target resource's ancestor chain (≤ D nodes), not the grant table at large.

### 6.6 Analysis

**N-independence extends to 1.5M resources (Fig. 2).** With 1,215,166 unique resource nodes at D = 5, list filtering median is 3.47ms (k = 20) — compared to 3.39ms at N = 10K with D = 3 in isolation tests. The small increase is attributable to depth, not resource count. At D = 10 with 1,514,656 resources, per-page cost is 5.69ms — again a depth effect.

**Grant breadth is not local grant multiplicity.** The breadth experiments vary the number of distinct resource scopes granted to a principal: at D = 5, 1 vs. 10 chain scopes changes median latency by 0.09ms (3.31 vs. 3.40ms); at D = 10, 1 vs. 5 division scopes changes it from 5.00ms to 5.76ms. Unrelated grant rows at other scopes are not scanned when evaluating a candidate resource. G_max — multiple active grants for the same principal-resource pair — is a different factor: point checks showed no sensitivity for 1–20 active grants, though the evaluation does not refute the formal O(G_max) factor.

**Narrow grants can improve constants.** A chain-level grant (3.12ms) is 37% slower than a single store grant (2.28ms): the CTE walks UP from the product, so a store-level grant matches at the first ancestor hop while a chain-level grant requires 3–4 hops. The 100-store case (4.27ms vs. 2.50ms for 10 stores) shows many disjoint narrow grants are not monotonically faster; selectivity and plan estimates affect constants without violating the per-row bound.

**Principal set size is negligible in the tested range.** M = 1 vs M = 11 produces 3.36 vs. 3.44ms (D = 5) and 5.63 vs. 5.85ms (D = 10). Because these experiments used `STRING_SPLIT`, this is an empirical observation for small effective-principal sets (M ≤ 21), not a claim that parameter passing is irrelevant for large M.

**Cursor vs. offset pagination (Fig. 3).** Offset COUNT costs grow linearly with N while cursor pagination remains at ~3.3ms: 700× faster at N = 100K.

[Figure 3: Cursor vs. offset pagination — offset COUNT grows linearly: 231ms (10K) → 1,114ms (50K) → 2,310ms (100K); cursor holds at ~3.3ms (700× faster at 100K).]

**TVF vs. materialized.** The materialized approach is ~3.4× faster per-page (1.01ms vs. 3.42ms at N = 100K) but requires denormalization infrastructure. Both are N-independent under cursor pagination; the TVF provides strong consistency with zero infrastructure overhead.

---

## 7. Discussion

### 7.1 Constraints as Architectural Choice

SHRBAC's performance guarantees are a direct consequence of four structural constraints, each eliminating a source of unbounded computation in the enforcement path:

**Theorem 3 (Constraint-Composability Tradeoff).** SHRBAC intentionally disallows (C1) DAG-structured resources, (C2) recursive group membership, (C3) role hierarchies, and (C4) arbitrary attribute predicates. In return, (G1) CTE traversal is bounded at O(D); (G2) principal resolution is a single join producing a fixed set; (G3) the TVF body is fixed at schema design time — no runtime policy compilation; and (G4) enforcement is a standard SQL `EXISTS` subquery composable with arbitrary WHERE, ORDER BY, and pagination.

Relaxing any constraint breaks its guarantee: DAGs yield unbounded ancestor paths (G1), nested groups recursive resolution (G2), role inheritance transitive closure at evaluation time (G3), arbitrary predicates runtime policy compilation (G4). These constraints are not artificial — they codify the *de facto* structure of most multi-tenant B2B SaaS systems: containment trees rooted at a tenant boundary, flat membership groups, and roles that enumerate permissions. SHRBAC formalizes this common but undocumented pattern.

### 7.2 Relationship to ABAC and ReBAC

SHRBAC evaluates two attributes — role and resource-scope — making it a two-attribute constrained ABAC system [13]. The resource tree can be viewed as a constrained ReBAC graph in which every relationship is `parent_of` typed and the graph is a tree; that constraint (bounded depth, deterministic ancestor chains) is what makes TVF enforcement tractable. For arbitrary relationship graphs, ReBAC is more appropriate; for organizational hierarchies, SHRBAC's constraint matches the domain.

### 7.3 SHRBAC for Autonomous Principals

An agent is a principal with τ(p) = agent in the same grant relation as human users. SHRBAC therefore supports database authorization for agents that query or mutate application resources; it does not address prompt injection, tool-selection safety, model alignment, delegation-chain reasoning, or semantic policy generation. Within this scope, three properties follow directly. *Least-privilege delegation*: an agent receives only the role it needs, at only the subtree it operates on, for only the duration of its task (e.g., a 15-minute grant at one project subtree). *Predictable query cost*: under the same pagination and indexing assumptions as human callers, the per-page bound makes agent-driven enumeration loops independent of total resource count. *Auditability*: every agent action traces to a specific (agent, role, resource) grant, and when EffectiveTo < now, access ceases immediately without token-revocation infrastructure.

### 7.4 Scope and Limitations

The constraints exclude expressiveness by design: DAG resources such as matrix management are not supported (C1; ReBAC is more appropriate there), deeply nested groups require extending resolve(p) with CTE traversal (C2), roles must enumerate permissions rather than inherit (C3), and dynamic attribute conditions such as IP or device type are not expressible (C4). For very deep hierarchies, a closure table replaces the O(D) recursive CTE expansion with O(1) lookup. The evaluation focuses on dense authorized-list workloads: Theorem 2 predicts O(k/σ · D · M · G_max) for sparse access and O(N · D · M · G_max) when no rows are authorized — deny-heavy workloads remain an empirical failure mode to stress-test. Finally, all measurements use SQL Server 2022 in Docker (4 vCPU, 8 GB RAM); the measured constants are not production SLAs, and although PostgreSQL and MySQL 8.0+ support the required primitives (recursive CTEs and predicate inlining), cross-DBMS validation is future work.

---

## 8. Conclusion

We presented SHRBAC, a formal authorization model at the intersection of hierarchical RBAC, polymorphic principal resolution, and Stonebraker's query modification, whose structural constraints allow per-page enforcement cost to simplify to O(k · D) under bounded principal sets, bounded local grant multiplicity, cursor pagination, correct indexing, and stable nested-loop plans. Empirical evaluation at 1.2M (D = 5) and 1.5M (D = 10) resources confirms the predicted behavior for dense-access workloads: per-page latency is N-independent across three orders of magnitude, scales linearly with k and D, and is unaffected by cursor depth or grant breadth in tested configurations.

As autonomous agents become principals in enterprise systems, the need for scoped, auditable, time-bounded authorization with efficient list filtering will intensify. SHRBAC provides this database-native substrate by applying the same scoped grant model to autonomous and human principals.

---

## AI Disclosure

In accordance with IEEE policy, the author discloses that AI tools (Claude, Anthropic) assisted with manuscript preparation and editing; all technical content, formal definitions, proofs, implementation, and experimental design are the author's work.

---

## References

[1] R. Pang et al., "Zanzibar: Google's Consistent, Global Authorization System," in *Proc. USENIX ATC*, pp. 33–46, 2019.

[2] J. H. Saltzer and M. D. Schroeder, "The Protection of Information in Computer Systems," *Proc. IEEE*, vol. 63, no. 9, pp. 1278–1308, 1975.

[3] Z. Zhang et al., "ROBAC: Scalable Role and Organization Based Access Control Models," in *Proc. CollaborateCom*, 2006.

[4] M. Stonebraker and E. Wong, "Access Control in a Relational Data Base Management System by Query Modification," in *Proc. ACM National Conf.*, 1974, pp. 180–187.

[5] R. Sandhu et al., "Role-Based Access Control Models," *IEEE Computer*, vol. 29, no. 2, pp. 38–47, 1996.

[6] D. F. Ferraiolo et al., "Proposed NIST Standard for Role-Based Access Control," *ACM TISSEC*, vol. 4, no. 3, pp. 224–274, 2001.

[7] N. Solanki et al., "Resource and Role Hierarchy Based Access Control for Resourceful Systems," in *Proc. IEEE COMPSAC*, pp. 396–401, 2018.

[8] A. Abou El Kalam et al., "Organization Based Access Control," in *Proc. IEEE POLICY*, 2003.

[9] S. Rizvi et al., "Extending Query Rewriting Techniques for Fine-Grained Access Control," in *Proc. ACM SIGMOD*, pp. 551–562, 2004.

[10] P. Pappachan et al., "Sieve: A Middleware Approach to Scalable Access Control for Database Management Systems," *PVLDB*, vol. 13, no. 12, 2020.

[11] P. W. L. Fong, "Relationship-Based Access Control: Protection Model and Policy Language," in *Proc. CODASPY*, pp. 191–202, 2011.

[12] AuthZed, "Protecting a List Endpoint," SpiceDB Documentation; Oso, "List Filtering," Oso Documentation; Cerbos, "Filtering Data Using Authorization Logic," Cerbos Blog.

[13] V. C. Hu et al., "Guide to Attribute Based Access Control (ABAC) Definition and Considerations," *NIST SP 800-162*, 2014.
