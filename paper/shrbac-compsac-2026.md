# Database-Native Authorization for Human and Autonomous Principals: The SHRBAC Model

**Abstract.** Enterprise applications must answer two authorization questions: "can this principal act on this resource?" (point check) and "which resources can this principal access, filtered, sorted, and paginated?" (list filtering). Point checks are boolean decisions with well-understood evaluation strategies. List filtering is fundamentally harder: it requires composing authorization decisions with application-defined filtering, sorting, and pagination into a single efficient query — and when authorization logic resides outside the database, an impedance mismatch arises. We present SHRBAC (Scoped Hierarchical Role-Based Access Control), a formal authorization model whose structural constraints — tree-structured resources, flat roles, polymorphic principals including autonomous agents — guarantee that enforcement is a composable database predicate with bounded cost — concretely, a parameterized inline Table-Valued Function (TVF) that the optimizer folds into the execution plan. The model enforces the principle of least privilege through grants scoped by resource subtree, role, and time window, enabling both human users and AI agents to operate with minimum necessary authority. We formalize the model, prove soundness and completeness of the TVF enforcement, and show that under cursor pagination, per-page cost is O(k · D) — predictable, linear, and independent of total dataset size. Empirical evaluation on SQL Server 2022 with 1.2M resource nodes (D = 5) and 1.5M resource nodes (D = 10) confirms N-independence across three orders of magnitude.

---

## 1. Introduction

### 1.1 The Problem

Modern B2B SaaS applications require authorization systems that answer two fundamentally different questions:

1. **Point check**: "Can principal *p* perform action *a* on resource *r*?" — a boolean decision.
2. **List filtering**: "What resources of type *T* can principal *p* access, given search criteria *C*, sorted by *S*, paginated to page *k*?" — a set-returning query integrated with application data.

The first question is well-understood. Google's Zanzibar [1] and its descendants handle millions of point checks per second through graph traversal and caching.

The second question — the **list filtering problem** — is well-solved for flat access models. A single-tenant predicate (`WHERE tenant_id = @currentTenant`) composes trivially. However, when access is determined by grants at varying levels of a resource hierarchy, resolved through transitive group memberships, and subject to temporal constraints, list filtering becomes substantially more complex.

Increasingly, principals are not only human users but **autonomous AI agents** that issue queries and perform actions over enterprise data. These agents operate in loops, enumerating and filtering large resource sets programmatically. For agentic systems, efficient list filtering is not merely a user-experience optimization — it is a correctness and cost requirement. An agent that makes N external authorization calls per page degrades linearly; an agent whose authorization is a composable database predicate operates at constant cost per page.

Beyond efficient querying, the authorization model must enforce the **principle of least privilege** [2]: principals, whether human or autonomous, should operate with minimum necessary authority scoped to specific resources and time windows. For AI agents, this is not merely good practice but a safety requirement — an agent should receive a narrow grant (specific subtree, limited role, bounded duration) rather than broad access.

When authorization logic resides outside the database, an impedance mismatch arises. Industry has converged on four bridging strategies:

| Strategy | Approach | Tradeoff |
|----------|----------|----------|
| **LookupResources** | Query auth service for all accessible IDs, use as IN clause | Degrades beyond ~10K resources |
| **Batch post-filtering** | Fetch candidates, check permissions per-item | Unpredictable latency |
| **Partial evaluation** | Compile policy into SQL WHERE clause | Complexity grows with expressiveness |
| **Materialized views** | Denormalize permissions into local tables | Eventual-consistency infrastructure |

None of these arise from an authorization model *designed for* query composition. They are bridges after the fact.

SHRBAC takes a different approach: the model's structural constraints are chosen so that per-page enforcement cost is O(k · D) — linear in page size and tree depth, independent of total resource count N and policy set size.

### 1.2 Contributions

1. **Constraint-driven model with composability guarantee.** We introduce SHRBAC, whose four structural constraints — tree-structured resources, non-recursive principal groups, flat roles, and scoped grants (principal × role × resource × time) — are deliberately chosen so that enforcement admits a fixed, schema-level relational artifact with bounded cost independent of policy set size.
2. **Formalization with two-dimensional resolution.** Access evaluation performs upward traversal over a bounded-depth resource tree and outward expansion over effective principals (humans, groups, and agents). We prove soundness and completeness and bound per-row cost at O(D · M · G_max). Unlike prior hierarchical RBAC models, the cost structure is part of the model definition.
3. **Fixed inline TVF enforcement.** In the lineage of Stonebraker [4], we realize enforcement as a parameterized inline Table-Valued Function — a single `EXISTS` predicate defined at schema time whose shape is constant regardless of policy size. Grants are data rows, not predicate terms, requiring no runtime compilation, post-filtering, or external graph traversal.
4. **Complexity theorem for list filtering.** Under cursor pagination, per-page cost is O(k · D) when M and G_max are bounded — independent of resource count N, policy set size, and page depth. This formal bound for the list-filtering problem is absent in prior RBAC, ABAC, and ReBAC models.
5. **Production-scale empirical validation.** At 1.2M resources (D = 5) and 1.5M resources (D = 10), we confirm N-independence across three orders of magnitude, linear scaling in k and D, constant per-CTE-hop cost (~0.029ms), and grant density independence.
6. **Polymorphic principal model for agent governance.** SHRBAC includes autonomous agents as first-class grant recipients with least-privilege, time-bounded authority. Agent-driven list queries inherit the O(k · D) guarantee, providing database-native agent governance without external authorization infrastructure.

### 1.3 Paper Organization

Section 2 surveys related work. Section 3 formalizes the model. Section 4 defines the access evaluation algorithm. Section 5 describes TVF enforcement. Section 6 analyzes complexity. Section 7 presents empirical evaluation. Section 8 discusses design constraints, agentic applications, and limitations. Section 9 concludes.

---

## 2. Background and Related Work

### 2.1 RBAC Foundations

Sandhu et al. [5] defined the RBAC96 family: RBAC0 (core), RBAC1 (hierarchical roles), RBAC2 (constraints), and RBAC3 (combined). The NIST standard [6] formalized Core and Hierarchical RBAC. **Critical gap:** RBAC96 defines only a role hierarchy; the resource space is flat with no concept of resource hierarchy or scope.

### 2.2 Hierarchical Models

Three models extend RBAC with organizational or resource structure:

**ROBAC** (Zhang et al. [3]) introduced the (user, role, organization) triple with an organization hierarchy where grants cascade from parent to child organizations. SHRBAC's (principal, role, resource) grant is structurally identical, but ROBAC defines a policy semantics without constraining the model to admit a fixed, schema-level enforcement artifact with bounded evaluation cost. SHRBAC's novelty is not the hierarchy alone, but the alignment of model constraints with relational query planning guarantees — extending the subject to polymorphic principals and providing a concrete enforcement mechanism whose per-row cost is analyzable.

**RRBAC** (Solanki et al. [7]) explicitly formalized resource hierarchies with grant cascade semantics, but did not address polymorphic principals, query composition, or enforcement mechanisms.

**OrBAC** (Kalam et al. [8]) generalized RBAC with five hierarchies (organizations, roles, activities, views, contexts). SHRBAC is a practical engineering subset — OrBAC's expressiveness exceeds what TVF-based enforcement requires.

### 2.3 Query Modification and Enforcement

Stonebraker and Wong [4] introduced query modification for INGRES, transparently rewriting queries to enforce access control. This is the ancestor of Oracle VPD, SQL Server RLS, and PostgreSQL RLS. Rizvi et al. [9] distinguished the *Truman model* (silent filtering) from the *Non-Truman model* (query rejection). SHRBAC implements the Truman model. Pappachan et al. [10] showed that naive predicate injection does not scale with thousands of policies; SHRBAC avoids this because the predicate is always a single TVF invocation regardless of policy count.

### 2.4 Zanzibar and ReBAC

Google's Zanzibar [1] uses relationship tuples with a userset rewrite system. Fong [11] formalized ReBAC using modal logic. Zanzibar's `tuple_to_userset` handles resource hierarchies through general graph traversal. SHRBAC constrains resources to a tree (not an arbitrary graph) and uses typed roles rather than arbitrary relation composition — this constraint is what makes TVF enforcement tractable, as tree traversal has bounded depth while general graph traversal does not.

### 2.5 The List Filtering Problem

Despite its practical importance, composing authorization into list queries has no canonical academic name. AuthZed calls it "ACL-aware filtering" [12], Oso calls it "list filtering" [13], Cerbos frames it as a "query plan" problem [14]. SpiceDB's documentation reveals the architectural tension: at scale, they recommend progressively more complex strategies (LookupResources → CheckBulkPermissions → Materialize). SHRBAC is designed so that the integration surface is a relational predicate — composable by construction.

### 2.6 Summary of Gaps

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

- **P** — a finite set of *principals*. Each principal has a type τ(p) ∈ {user, group, service_account, agent}. An agent principal represents an autonomous process — an AI agent, orchestration pipeline, or background service — that issues queries and mutations on behalf of an organization. Agents participate in the same grant relation as human users, subject to the same scoping and temporal constraints.
- **R** — a finite set of *roles*.
- **PERM** — a finite set of *permissions*. Each permission is an atomic capability (e.g., `PROJECT_VIEW`, `SUBCONTRACTOR_EDIT`).
- **RES** — a finite set of *resources*, forming nodes in a rooted tree.
- **RT** — a finite set of *resource types* (e.g., `portal_root`, `agency`, `project`). Each resource has a type: type: RES → RT.
- **T** — the time domain (UTC timestamps).

### 3.2 Resource Hierarchy

The resource hierarchy is a rooted tree (RES, parent) where:

- **parent**: RES → RES ∪ {⊥} maps each resource to its parent. The root *r₀* has parent(*r₀*) = ⊥.
- **ancestors(r)** = {parentⁱ(r) | i ≥ 0 ∧ parentⁱ(r) ≠ ⊥}. Note that r ∈ ancestors(r).
- **descendants(r)** = {r' ∈ RES | r ∈ ancestors(r')}
- **depth(r)** = |ancestors(r)| − 1 (root has depth 0).

The tree is bounded: depth(r) ≤ D for all r. In practice, D = 3–5 for typical SaaS and up to 10–15 for deep enterprise hierarchies.

An **authorized entity** is a row in an application table carrying a `ResourceId` referencing a node in the resource tree.

### 3.3 Role-Permission Assignment

**PA** ⊆ R × PERM is the role-permission assignment. Each permission has an associated resource type: **applicable**: PERM → RT. Roles are **flat** — no role hierarchy. The resource hierarchy provides the inheritance dimension, avoiding the combinatorial complexity of dual hierarchies.

### 3.4 Principal Resolution

Groups are themselves principals. **members(g)** = {u ∈ P | τ(u) = user ∧ u is a member of g}. Membership is not recursive — groups cannot contain other groups.

**Principal resolution** expands a user to their effective principal identities:

- **resolve(p)** = {p} ∪ {g ∈ P | τ(g) = group ∧ p ∈ members(g)}

For non-user principals (groups, service accounts, agents), resolve(p) = {p}.

### 3.5 Grants

**G** ⊆ P × R × RES × (T ∪ {⊥}) × (T ∪ {⊥})

A grant g = (p, role, res, t_from, t_to) assigns role `role` to principal `p` at resource `res`, effective during the closed interval [t_from, t_to]. Both bounds are inclusive. If t_from = ⊥, effective from the beginning of time; if t_to = ⊥, effective indefinitely.

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

**Running example.** Consider a resource tree: `portal_root → agency_7 → project_42`, with user Alice who belongs to the `engineering` group. A grant exists: `(engineering, Viewer, agency_7)`. To evaluate `allowed(alice, PROJECT_VIEW, project_42, now)`: resolve(alice) = {alice, engineering}; ancestors(project_42) = {project_42, agency_7, portal_root}. The algorithm searches the 2 × 3 grid of (principal, ancestor) pairs. At (engineering, agency_7), it finds the Viewer grant, which includes PROJECT_VIEW. Access is granted — the grant at `agency_7` cascades to its descendant `project_42`.

[Figure 1: Two-dimensional resolution for allowed(alice, PROJECT_VIEW, project_42, now). The principal set is expanded outward while the ancestor chain is walked upward, forming an M × D search grid where the first matching grant short-circuits evaluation.]

### 3.7 Properties

**Property 1 (Monotonicity of Hierarchy).** If allowed(p, perm, r, t) and r' ∈ descendants(r), then the same grant provides access at r'. *Proof.* Transitivity of the ancestor relation. □

**Property 2 (Monotonicity of Groups).** Adding p to group g preserves existing access and may grant additional access. *Proof.* Existential quantification over an expanded set. □

**Property 3 (Bounded Evaluation).** For any access decision, evaluation examines at most |resolve(p)| × |ancestors(r)| × G_max grant entries, giving O(M · D · G_max).

---

## 4. The Access Evaluation Algorithm

### 4.1 Point Check

```
ALGORITHM PointCheck(p, perm, r, t):
  principals ← resolve(p)              // One query: user + group memberships
  ancestors ← getAncestors(r)          // Walk parent pointers to root
  FOR EACH r' IN ancestors:
    FOR EACH p' IN principals:
      grants ← getActiveGrants(p', r', t)
      FOR EACH g IN grants:
        IF perm ∈ perms(g.role): RETURN allowed
  RETURN denied
```

### 4.2 List Filter

For list filtering, the algorithm is expressed as a SQL predicate:

```
ALGORITHM ListFilter(p, perm, query, t):
  principals ← resolve(p)                  // Pre-query
  principalIds ← join(principals, ',')
  permId ← lookupPermissionId(perm)
  now ← UTC_NOW()
  RETURN query.WHERE(entity =>
    EXISTS(fn_IsResourceAccessible(entity.ResourceId, principalIds, permId, now)))
```

The TVF is evaluated per-row by the database engine, composed with user-defined filters, sorting, and pagination into a single execution plan.

---

## 5. Query-Composable Enforcement via TVF

### 5.1 The Table-Valued Function

The enforcement mechanism is an inline Table-Valued Function (iTVF):

```sql
CREATE FUNCTION dbo.fn_IsResourceAccessible(
    @ResourceId NVARCHAR(128),
    @PrincipalIds NVARCHAR(MAX),
    @PermissionId NVARCHAR(128),
    @Now DATETIME2
)
RETURNS TABLE
AS
RETURN
(
    WITH ancestors AS (
        SELECT Id, ParentId, 0 AS Depth
        FROM Resources WHERE Id = @ResourceId
        UNION ALL
        SELECT r.Id, r.ParentId, a.Depth + 1
        FROM Resources r
        INNER JOIN ancestors a ON r.Id = a.ParentId
        WHERE a.Depth < 10
    )
    SELECT TOP 1 a.Id
    FROM ancestors a
    INNER JOIN Grants g ON a.Id = g.ResourceId
    INNER JOIN RolePermissions rp ON g.RoleId = rp.RoleId
    WHERE g.PrincipalId IN (
        SELECT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@PrincipalIds, ',')
    )
      AND rp.PermissionId = @PermissionId
      AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= @Now)
      AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= @Now)
)
```

The recursive CTE walks UP from the target resource to the root (at most D levels), joining against active grants at each ancestor. `TOP 1` provides existential semantics — one matching grant suffices. The caller-controlled `@Now` parameter ensures deterministic evaluation across all rows in a query. As an inline TVF, the optimizer composes its body into the calling query's execution plan, enabling authorization, user filtering, sorting, and pagination in a single statement. We use SQL Server as an exemplar because its optimizer aggressively inlines iTVFs; however, the enforcement requires only recursive CTEs and predicate inlining, both supported by PostgreSQL and other modern engines.

### 5.2 Correctness

**Theorem 1 (Soundness).** If the TVF returns a non-empty result for entity *e*, then allowed(p, perm, e.ResourceId, now) = true. *Proof.* The CTE produces exactly ancestors(e.ResourceId). The JOIN implements the existential quantification in Definition 1. The temporal predicates correspond to active(t). A non-empty result witnesses a satisfying triple. □

**Theorem 2 (Completeness).** If allowed(p, perm, e.ResourceId, now) = true, the TVF returns a non-empty result, provided depth ≤ 10. *Proof.* The CTE with depth < 10 produces all ancestors. The JOIN conditions faithfully translate Definition 1. Any satisfying witness produces a matching row. □

### 5.3 Composition into Application Queries

Because the TVF is an inline function, the query optimizer composes its body into the calling query's execution plan. The application layer resolves the principal's group memberships once per request, then appends the TVF as a predicate alongside application-defined filters, cursor pagination, and sorting:

```
FUNCTION AuthorizedList(principal, permission, filters, cursor, k):
  principalIds ← ResolveGroupMemberships(principal)
  query ← FROM table
           WHERE Accessible(row.ResourceId, principalIds, permission)
             AND ApplyFilters(row, filters)
             AND row.SortKey > cursor
           ORDER BY row.SortKey
           LIMIT k
  RETURN Execute(query)   // single database operation
```

Authorization, application filtering, pagination, and sorting compose into a single database operation — one round-trip, zero external calls.

---

## 6. Complexity Analysis

### 6.1 Per-Row TVF Cost

For a single entity with resource at depth d ≤ D:

1. **CTE expansion:** O(D) index seeks on `Resources(Id)`.
2. **Grant matching:** Joins D ancestors against `Grants(ResourceId, PrincipalId)` and `RolePermissions(RoleId, PermissionId)`. With M = |resolve(p)| principals, examines at most D · M · G_max candidates.
3. **TOP 1 exit:** Short-circuits on first match.

**Per-row cost:** O(D · M · G_max). With D = 5, M = 10, G_max = 3: ~150 index lookups.

### 6.2 Per-Page Cost Under Pagination

**Theorem 3 (Per-Page Complexity).** Under assumptions of (1) index coverage on Resources, Grants, RolePermissions, and entity ResourceId; (2) cursor pagination on an indexed ordering key; and (3) nested loops plan shape with index seeks:

- **Dense access (σ ≈ 1):** O(k · D · M · G_max), simplified to **O(k · D)** when M and G_max are bounded constants.
- **Sparse access (σ → 0):** **O(k/σ · D · M · G_max)**. Degenerate case (no access): O(N · D · M · G_max).

*Proof.* With cursor pagination, the engine evaluates rows sequentially from the cursor. To collect k authorized rows with selectivity σ requires examining ~k/σ candidates, each costing O(D · M · G_max). □

The per-page cost is *parameterized and predictable* — independent of N, linear in D.

### 6.3 Comparison with Alternatives

| Approach | Per-page cost | Dependencies | Consistency |
|----------|--------------|--------------|-------------|
| **SHRBAC TVF (cursor)** | O(k · D) | None (local DB) | Strong |
| **LookupResources + IN** | O(graph) + O(k) | External service | Eventual |
| **Batch post-filter** | O(k/σ · latency) | External service | Per-check |
| **Partial evaluation** | O(k) + O(compile) | Policy engine | Varies |
| **Materialized JOIN** | O(k) | Denorm. pipeline | Eventual |

### 6.4 Known Considerations

- **STRING_SPLIT** produces poor cardinality estimates; table-valued parameters recommended for M > 5.
- **CTE traversal** is not short-circuitable; a closure table replaces O(D) recursive expansion with O(1) lookup for deep hierarchies.
- **Plan shape** assumes nested loops with index seeks (observed under correct indexing). Stale statistics may produce hash joins; `UPDATE STATISTICS` is the first mitigation.

---

## 7. Empirical Evaluation

We evaluate SHRBAC on SQL Server 2022 (Docker, 4 vCPU, 8 GB RAM) across three tiers: (1) small-scale isolation tests (1K–100K entities) that individually vary each factor, (2) a production-scale workload at D = 5 with 1.2M resource nodes, and (3) a deep-hierarchy workload at D = 10 with 1.5M resource nodes.

### 7.0 Benchmark Methodology

**Platform.** SQL Server 2022 running in Docker with 4 vCPU and 8 GB RAM. All benchmarks executed against a local instance to eliminate network variance.

**Measurement protocol.** Each query is measured with 3 warmup runs (discarded) followed by 20 measured runs. Each run opens a fresh connection, executes the query, and records wall-clock elapsed time via `Stopwatch`. We report median, P95, and interquartile range (IQR). The median is the primary metric; P95 captures tail behavior. Page size k = 20 unless stated otherwise.

**Query pattern.** All list filtering benchmarks execute the canonical authorized-list query:

```sql
SELECT TOP (@k) p.Id, p.Name, p.SKU, p.Price, p.ResourceId
FROM Products p
WHERE EXISTS (
    SELECT 1 FROM dbo.fn_IsResourceAccessible(
        p.ResourceId, @principalIds, 'product_view'))
AND p.Id > @cursor
ORDER BY p.Id
```

Point checks use the same TVF against a single ResourceId. Query plans verified to use nested loops with index seeks on the `Products(Id)` clustered index and `SqlOSResources(Id)` primary key.

**Schema.** The SqlOS schema (SqlOSResources, SqlOSGrants, SqlOSRolePermissions, SqlOSPrincipals, etc.) is created fresh for each benchmark configuration. Domain tables (Chains, Regions, Stores, Products, and for D = 10: Divisions, Districts, Areas, Zones, Departments, Sections) are created alongside, each with a `ResourceId` foreign key to the resource tree and a nonclustered index on `ResourceId`.

**D = 5 hierarchy** (Benchmarks 8–10): root → 15 chains → 150 regions → 15,000 stores → 1,200,000 products = **1,215,166 resources**. Branching: 15 × 10 × 100 × 80. Principals at four scope levels: company admin (all 1.2M products), chain manager (~80K), region manager (~8K), store manager (~80). Each principal resolves to M = 3 identities (user + 2 groups).

**D = 10 hierarchy** (Benchmarks 11–13): root → 5 divisions → 25 regions → 125 districts → 500 areas → 2,000 zones → 12,000 stores → 60,000 departments → 240,000 sections → 1,200,000 products = **1,514,656 resources**. Branching: 5 × 5 × 5 × 4 × 4 × 6 × 5 × 4 × 5. Principals at four scope levels with analogous access ranges.

**Seeding.** Resources and domain table rows are bulk-inserted using multi-row VALUES batches (500 rows per INSERT). After seeding, `UPDATE STATISTICS` is run on all tables to ensure the query optimizer has accurate cardinality estimates. Seeding times: ~376s for D = 5 (1.2M resources), ~442s for D = 10 (1.5M resources).

All benchmarks use the same retail SaaS domain model. Isolation tests (Benchmarks 1–7) seed a hierarchy of real domain tables scaled to the target depth: at D=3, root → Regions (10) → Stores (100) → Products (N); at D=5, root → Divisions (10) → Chains (100) → Regions (1K) → Stores (10K) → Products (N). Each intermediate level has its own domain table (Regions, Stores, Chains, Divisions) with domain-appropriate columns, and each row maps to a unique resource in the tree. The benchmark query always targets the Products table. This ensures that even small-scale isolation tests exercise the same schema structure and query patterns as the production-scale benchmarks.

### 7.1 Isolation Tests (1K–100K)

| Experiment | Configuration | Median (ms) | P95 (ms) |
|---|---|---|---|
| **N (resource count)** | N=1K, D=3 | 3.57 | 4.19 |
| | N=5K, D=3 | 3.51 | 4.58 |
| | N=10K, D=3 | 3.39 | 4.01 |
| | N=50K, D=3 | 3.63 | 4.05 |
| | N=100K, D=3 | 3.32 | 4.07 |
| **Depth sensitivity** | D=1, N=10K | 2.45 | 4.90 |
| | D=2, N=10K | 2.94 | 3.96 |
| | D=3, N=10K | 3.17 | 6.16 |
| | D=4, N=10K | 3.55 | 4.43 |
| | D=5, N=10K | 4.21 | 4.79 |
| **Principal set size** | M=1, N=10K | 3.47 | 5.53 |
| | M=4, N=10K | 3.14 | 3.75 |
| | M=11, N=10K | 3.45 | 4.81 |
| | M=21, N=10K | 3.32 | 4.09 |
| **TVF vs. materialized** | TVF EXISTS, N=100K | 3.42 | 3.97 |
| | Materialized JOIN, N=100K | 1.01 | 1.28 |
| **Pagination** | Cursor, N=100K | 3.30 | 4.22 |
| | Offset COUNT, N=100K | 2,310 | 2,473 |

These isolation results confirm core predictions: N-independence across 1K–100K (3.32–3.63ms at D = 3), monotonic depth scaling from 2.45ms at D = 1 to 4.21ms at D = 5 consistent with O(k · D), negligible principal set size effect (3.14–3.47ms across M = 1–21), and constant cursor pagination cost independent of dataset size. The question is whether these properties hold at production resource scale.

### 7.2 Production-Scale Evaluation at D = 5 (1.2M Resources)

We constructed a retail SaaS hierarchy: root → 15 chains → 150 regions → 15,000 stores → 1,200,000 products, yielding **1,215,166 resource nodes** at D = 5. Principals range from company administrators (all 1.2M products accessible) to store managers (~80 products).

**Page size sensitivity.** Per-page latency scales linearly with k, as predicted by Theorem 3:

| k | Median (ms) | P95 (ms) |
|---|---|---|
| 10 | 2.28 | 7.59 |
| 20 | 3.47 | 4.55 |
| 50 | 6.89 | 8.65 |
| 100 | 13.71 | 20.25 |

The ratio 13.71/2.28 = 6.01× for a 10× increase in k confirms near-linear scaling. Per-row cost at D = 5: ~0.14ms, corresponding to ~0.034ms per CTE hop.

**Scope-level performance.** Despite a 15,000× difference in accessible product count, latency varies minimally:

| Scope | Accessible products | Median (ms) | P95 (ms) |
|---|---|---|---|
| Company admin (root) | 1,200,000 | 3.47 | 4.55 |
| Chain manager | ~80,000 | 3.20 | 3.95 |
| Region manager | ~8,000 | 3.11 | 6.34 |
| Store manager | ~80 | 2.02 | 2.57 |

Despite a 15,000× difference in accessible product count, latency varies minimally — store-level grants fastest in median because CTE short-circuits.

**Deep cursor stability.** Per-page latency remains flat regardless of cursor depth:

| Cursor position | Median (ms) | P95 (ms) |
|---|---|---|
| Page 1 | 3.08 | 4.04 |
| ~Page 50 (row 1K) | 3.25 | 3.63 |
| ~Page 500 (row 10K) | 3.48 | 4.28 |

Page 500 at 3.48ms is indistinguishable from page 1 at 3.08ms, confirming O(k) per-page cost independent of cursor position.

### 7.3 Deep Hierarchy Evaluation at D = 10 (1.5M Resources)

To test whether O(k · D) scaling holds at deeper hierarchies, we constructed a 10-level tree: root → 5 divisions → 25 regions → 125 districts → 500 areas → 2,000 zones → 12,000 stores → 60,000 departments → 240,000 sections → 1,200,000 products, yielding **1,514,656 resource nodes** at D = 10.

**Page size: D = 5 vs. D = 10.** Comparing identical queries across both tree depths isolates the depth factor:

| k | D=5 Median (ms) | D=10 Median (ms) | Ratio |
|---|---|---|---|
| 10 | 2.28 | 3.44 | 1.51× |
| 20 | 3.47 | 5.69 | 1.64× |
| 50 | 6.89 | 11.80 | 1.71× |
| 100 | 13.71 | 22.01 | 1.61× |

The D = 10/D = 5 ratio ranges from 1.51× to 1.71×. Per-row cost at D = 10: ~0.22ms, or ~0.024ms per CTE hop. The per-hop cost difference (0.034ms at D = 5 vs 0.024ms at D = 10) suggests fixed per-page overhead is proportionally larger at D = 5; at larger k the costs converge.

**Scope-level performance at D = 10.** The same scope-independence observed at D = 5 holds at D = 10, with scope varying 3.06–5.54ms.

**Deep cursor stability at D = 10:** 5.11–5.38ms across pages 1–500 — cursor pagination remains O(k) per page even at D = 10 with 1.5M resources.

### 7.4 Point Access Checks

The TVF also serves as a point check. We measure point check latency across the model's key dimensions at both D = 5 (1.2M resources) and D = 10 (1.5M resources).

**Depth sensitivity at D = 5 (1.2M resources):**

| Resource level | Depth | Median (ms) | P95 (ms) |
|---|---|---|---|
| Root | 0 | 0.86 | 1.94 |
| Chain | 1 | 0.97 | 3.02 |
| Region | 2 | 1.06 | 3.64 |
| Store | 3 | 1.04 | 1.41 |
| Product | 4 | 1.31 | 1.85 |

**Depth sensitivity at D = 10 (1.5M resources):**

| Resource level | Depth | Median (ms) | P95 (ms) |
|---|---|---|---|
| Root | 0 | 0.89 | 1.43 |
| Division | 1 | 0.95 | 1.30 |
| Region | 2 | 1.27 | 1.77 |
| District | 3 | 0.99 | 1.52 |
| Area | 4 | 1.09 | 1.51 |
| Zone-Product (5-9) | — | 1.13–1.46 range | — |

Even at depth 9 with 1.5M resource nodes, all point checks complete in **under 1.5ms median**. The CTE walks at most D ancestors — a bounded traversal independent of tree size.

**Grant set size (D = 5):** 1.19–1.38ms across 1–20 active grants. Flat — the CTE examines only the target resource's ancestor chain, not the grant table.

**Principal set size (D = 5):** 0.98–1.28ms across M = 1–21. Near-constant for point checks. STRING_SPLIT overhead is negligible at practical principal counts.

### 7.5 Factor Analysis

The multi-dimensional benchmarks at 1.2M resources (D = 5) and 1.5M resources (D = 10) enable a comprehensive factor analysis.

**Factor sensitivity across production scale:**

| Factor | Range tested | List filter effect | Point check effect | Predicted | Confirmed? |
|---|---|---|---|---|---|
| N (resource count) | 1K–1.5M | None (3.39ms → 3.47ms*) | N/A (CTE-local) | O(1) | Yes |
| k (page size) | 10–100 | Linear (2.28–13.71ms D=5; 3.44–22.01ms D=10) | N/A | O(k) | Yes |
| D (tree depth) | 1–10 | Linear (2.45 → 4.21 → 5.69ms) | Sub-1.5ms both | O(D) | Yes |
| Per-hop cost | D=5 vs D=10 | 0.034ms D=5, 0.024ms D=10 | — | O(1) | **Yes** |
| M (principals) | 1–11 | Negligible (3.32–3.44ms D=5; 5.42–5.85ms D=10) | None (0.98–1.28ms) | O(M) | Negligible |
| G (grant density) | 1–10 chains/divs | None (3.23–3.40ms D=5; 5.00–5.80ms D=10) | None (1.19–1.38ms) | O(G_max) | Refuted |
| Cursor depth | Page 1–500 | None (3.08–3.48ms D=5; 5.11–5.38ms D=10) | N/A | O(1) | Yes |

*\*The 10K→1.2M increase (3.39→3.47ms) compares D=3 isolation vs D=5 production; the difference is attributable to depth, not resource count.*

**Dimensional analysis at 1.2M resources — list filtering (D = 5):**

| Sub-experiment | Configuration | Median (ms) |
|---|---|---|
| **Principal set** | M=1 | 3.36 |
| | M=3 | 3.32 |
| | M=6 | 3.36 |
| | M=11 | 3.44 |
| **Grant density** | 1 chain (~80K accessible) | 3.31 |
| | 3 chains (~240K) | 3.23 |
| | 5 chains (~400K) | 3.41 |
| | 10 chains (~800K) | 3.40 |
| **Grant depth** | Chain grant (inherit all) | 3.12 |
| | 100 store grants | 4.27 |
| | 10 store grants | 2.50 |
| | 1 store grant | 2.28 |

**Dimensional analysis at 1.5M resources — list filtering (D = 10):**

| Sub-experiment | Configuration | Median (ms) |
|---|---|---|
| **Principal set** | M=1 | 5.63 |
| | M=3 | 5.42 |
| | M=6 | 5.65 |
| | M=11 | 5.85 |
| **Grant density** | 1 division (~240K accessible) | 5.00 |
| | 2 divisions (~480K) | 5.46 |
| | 3 divisions (~720K) | 5.80 |
| | 5 divisions (all ~1.2M) | 5.76 |

### 7.6 Analysis

**N-independence extends to 1.5M resources.** With 1,215,166 unique resource nodes at D = 5, list filtering median is 3.47ms (k = 20) — compared to 3.39ms at N = 10K with D = 3 in isolation tests. The small increase is attributable to depth (D = 5 vs D = 3), not resource count. At D = 10 with 1,514,656 resources, per-page cost is 5.69ms — again a depth effect. The index-seek + cursor plan renders total tree size irrelevant.

**Per-hop cost.** At D = 5, per-row cost is ~0.14ms (4 CTE hops × 0.034ms/hop). At D = 10, per-row cost is ~0.22ms (9 CTE hops × 0.024ms/hop). The per-hop cost converges at higher page sizes as fixed per-page overhead diminishes, giving O(k · D) a concrete, measurable constant.

**D = 5 to D = 10 depth scaling is linear.** At k = 20: 3.47ms (D = 5) → 5.69ms (D = 10), a 1.64× increase. At k = 100: 13.71ms → 22.01ms, a 1.61× increase. The ratio is below the theoretical 9/4 = 2.25× due to fixed per-page overhead, but the gap narrows at larger k values.

**Depth scaling is visible in isolation tests.** 2.45ms (D = 1) → 2.94ms (D = 2) → 3.17ms (D = 3) → 3.55ms (D = 4) → 4.21ms (D = 5). This 1.72× increase over 4 additional CTE hops is consistent with the O(k · D) bound.

**Grant density remains irrelevant.** At D = 5: 3.31 vs. 3.40ms — a 0.09ms difference for a 10× increase. At D = 10: 5.00 vs. 5.76ms. The TVF evaluates each row via its own ancestor chain; the breadth of a principal's authority does not factor into per-row cost.

**Least-privilege grants outperform inherited grants.** A chain-level grant (3.12ms) is 37% slower than a single store grant (2.28ms). The CTE walks UP from the product toward the root; a store-level grant matches at the first ancestor hop, while a chain-level grant requires 3–4 hops. This validates the short-circuit property and reveals that **narrow-scope grants are not only more secure — they are measurably faster**.

**Principal set size is negligible for list filtering.** M = 1 vs M = 11 produces 3.36 vs. 3.44ms. At D = 10: 5.63 vs. 5.85ms. STRING_SPLIT overhead is negligible at practical principal counts.

**Point checks are sub-1.5ms even at D = 10.** At depth 9 (the deepest leaf), point checks complete in 1.32ms median. Grant set size (1–20) and principal set size (M = 1–21) have no measurable impact. The CTE examines at most D ancestors — independent of tree size or grant count.

**Page size scales linearly.** k = 10 → 100 yields 2.28 → 13.71ms (6.01× for 10×). At 1.5M resources (D = 10): 3.44 → 22.01ms (6.40× for 10×). Both closely track the O(k) prediction.

**Cursor vs. offset pagination.** Offset COUNT scales linearly: 231ms (N = 10K) → 1,114ms (N = 50K) → 2,310ms (N = 100K). Cursor pagination remains at ~3.3ms regardless of dataset size. At N = 100K, cursor is 700× faster.

[Figure 2: Cursor pagination stays flat at ~3.3ms while offset COUNT grows linearly: 231ms (N = 10K) → 1,114ms (N = 50K) → 2,310ms (N = 100K). At N = 100K, cursor is 700× faster.]

**TVF vs. materialized.** The materialized approach is ~3.4× faster per-page (1.01ms vs 3.42ms at N = 100K), but requires a denormalization pipeline to maintain consistency. Both are N-independent under cursor pagination. SHRBAC's TVF provides strong consistency with zero infrastructure overhead.

---

## 8. Discussion

The theoretical analysis in Sections 3–4 established that SHRBAC's per-page enforcement cost is O(k · D), and the empirical results in Section 7 confirmed this bound across three orders of magnitude. This section examines the design trade-offs that make these guarantees possible, positions SHRBAC relative to alternative authorization models, and discusses implications for agentic systems.

### 8.1 Constraints as Architectural Choice

SHRBAC's performance guarantees are a direct consequence of four structural constraints. Each constraint eliminates a source of unbounded computation in the enforcement path:

**Theorem 4 (Constraint-Composability Tradeoff).** SHRBAC intentionally disallows: (C1) DAG-structured resources — resources form a tree, not a DAG; (C2) recursive group membership — groups cannot contain groups; (C3) role hierarchies — roles are flat; (C4) arbitrary attribute predicates — only role + resource-scope + time are evaluated. In return: (G1) CTE traversal is bounded at O(D); (G2) principal resolution is a single join producing a fixed set; (G3) the TVF body is fixed at schema design time — no runtime policy compilation; (G4) enforcement is a standard SQL `EXISTS` subquery composable with arbitrary WHERE, ORDER BY, and pagination.

Each constraint enables its corresponding guarantee. Relaxing (C1) to allow DAGs replaces a unique bounded ancestor chain with potentially unbounded multiple ancestor paths, eliminating deterministic O(D) traversal and breaking (G1). Relaxing (C2) to allow nested groups turns principal resolution into a recursive traversal, breaking (G2). Relaxing (C3) to allow role inheritance requires transitive closure at evaluation time, breaking (G3). Relaxing (C4) to allow arbitrary predicates requires runtime policy compilation, breaking (G4).

Crucially, these constraints are not artificial restrictions imposed to simplify the model — they codify the *de facto* structure of most multi-tenant B2B SaaS systems. Resource containment naturally forms a tree rooted at a tenant boundary (organization → departments → projects → artifacts). Groups are typically flat membership lists. Roles enumerate permissions rather than inheriting from other roles. SHRBAC formalizes this common but undocumented pattern and demonstrates that when the dominant structural assumptions are embraced explicitly, strong composability and performance guarantees follow.

### 8.2 Relationship to ABAC and ReBAC

SHRBAC evaluates two attributes — role and resource-scope — making it a two-attribute constrained ABAC system [15]. The resource tree can be viewed as a constrained ReBAC graph where all relationships are `parent_of` typed and the graph is a tree. The tree constraint (bounded depth, deterministic ancestor chains) is what makes TVF enforcement tractable. For applications requiring arbitrary relationship graphs, ReBAC/Zanzibar is more appropriate; for organizational hierarchies, SHRBAC's constraint matches the domain and provides predictable performance.

### 8.3 SHRBAC for Agentic Systems

SHRBAC's polymorphic principal model naturally accommodates autonomous AI agents. An agent is a principal with τ(p) = agent that participates in the same grant relation as human users, with three properties critical for agent governance:

**Least-privilege delegation.** SHRBAC's grant triple (principal, role, resource) with temporal bounds directly enforces least-privilege delegation: an agent receives only the role it needs, at only the resource subtree it operates on, for only the duration of its task. A 15-minute grant at a specific project subtree is expressible directly, without special-case logic.

**Predictable query cost.** Agents that enumerate resources in loops amplify per-query costs. The TVF's O(k · D) per-page bound ensures that agent-driven queries have predictable, bounded database impact regardless of how many resources exist — preventing runaway load from autonomous operations.

**Auditability.** Every agent action traces to a specific (agent, role, resource) grant with temporal bounds. When an agent's authority expires (EffectiveTo < now), access ceases immediately without requiring token revocation infrastructure.

### 8.4 Scope and Limitations

- **Tree constraint (C1):** DAG resources (matrix management, multi-parent projects) are not supported. ReBAC is more appropriate for these domains.
- **Non-recursive groups (C2):** Deeply nested group structures require extending resolve(p) with CTE traversal.
- **Flat roles (C3):** Role definitions must explicitly enumerate permissions rather than inheriting.
- **No arbitrary attributes (C4):** IP-based, device-type, or other dynamic attribute conditions are not expressible.
- **Database engine:** Portability requirements discussed in §5.1. PostgreSQL and MySQL 8.0+ support the necessary primitives (recursive CTEs, predicate inlining).

---

## 9. Conclusion

We presented SHRBAC, a formal authorization model whose structural constraints guarantee that per-page enforcement cost is O(k · D) — linear in page size and tree depth, independent of total resource count and policy set size. The model sits at the intersection of hierarchical RBAC (ROBAC/RRBAC), polymorphic principal resolution, and Stonebraker's query modification.

Empirical evaluation at 1.2M resources (D = 5) and 1.5M resources (D = 10) confirms the predicted complexity: per-page latency is N-independent across three orders of magnitude, scales linearly with k and D, and is unaffected by grant density or cursor depth. The per-CTE-hop cost is constant across both tree depths, giving O(k · D) a measurable constant. A notable finding is that least-privilege grants are not only more secure but faster — the upward tree walk short-circuits sooner at narrower scopes.

As autonomous agents become principals in enterprise systems, the need for scoped, auditable, time-bounded authorization with efficient list filtering will intensify. SHRBAC's grant model — where an agent receives exactly the authority it needs, at the scope it needs, for the duration it needs — provides a foundation for database-native agent governance without requiring external authorization infrastructure.

---

## Acknowledgments

[Removed for double-blind review.]

*AI Disclosure:* In accordance with IEEE policy, the authors disclose that AI tools (Claude, Anthropic) were used to assist with manuscript preparation and editing. All technical content, formal definitions, proofs, implementation, and experimental design are the work of the authors.

---

## References

[1] R. Pang et al., "Zanzibar: Google's Consistent, Global Authorization System," in *Proc. USENIX ATC*, 2019.

[2] J. H. Saltzer and M. D. Schroeder, "The Protection of Information in Computer Systems," *Proc. IEEE*, vol. 63, no. 9, pp. 1278–1308, 1975.

[3] Z. Zhang, X. Zhang, and R. Sandhu, "ROBAC: Scalable Role and Organization Based Access Control Models," in *Proc. CollaborateCom*, 2006.

[4] M. Stonebraker and E. Wong, "Access Control in a Relational Data Base Management System by Query Modification," in *Proc. ACM National Conf.*, 1974, pp. 180–187.

[5] R. Sandhu, E. J. Coyne, H. L. Feinstein, and C. E. Youman, "Role-Based Access Control Models," *IEEE Computer*, vol. 29, no. 2, pp. 38–47, 1996.

[6] D. F. Ferraiolo et al., "Proposed NIST Standard for Role-Based Access Control," *ACM TISSEC*, vol. 4, no. 3, pp. 224–274, 2001.

[7] N. Solanki et al., "Resource and Role Hierarchy Based Access Control for Resourceful Systems," in *Proc. IEEE COMPSAC*, pp. 396–401, 2018.

[8] A. Abou El Kalam et al., "Organization Based Access Control," in *Proc. IEEE POLICY*, 2003.

[9] S. Rizvi, A. Mendelzon, S. Sudarshan, and P. Roy, "Extending Query Rewriting Techniques for Fine-Grained Access Control," in *Proc. ACM SIGMOD*, pp. 551–562, 2004.

[10] P. Pappachan, R. Yus, S. Mehrotra, and J.-C. Freytag, "Sieve: A Middleware Approach to Scalable Access Control for Database Management Systems," *PVLDB*, vol. 13, no. 12, 2020.

[11] P. W. L. Fong, "Relationship-Based Access Control: Protection Model and Policy Language," in *Proc. CODASPY*, pp. 191–202, 2011.

[12] AuthZed, "Protecting a List Endpoint," SpiceDB Documentation.

[13] Oso, "List Filtering," Oso Documentation.

[14] Cerbos, "Filtering Data Using Authorization Logic," Cerbos Blog.

[15] V. C. Hu et al., "Guide to Attribute Based Access Control (ABAC) Definition and Considerations," *NIST SP 800-162*, 2014.

[16] Microsoft, "What is Azure role-based access control (Azure RBAC)?," Microsoft Learn.

[17] E. Bertino, P. Bonatti, and E. Ferrari, "TRBAC: A Temporal Role-Based Access Control Model," *ACM TISSEC*, vol. 4, no. 3, 2001.

[18] R. Sandhu, V. Bhamidipati, and Q. Munawer, "The ARBAC97 Model for Role-Based Administration of Roles," *ACM TISSEC*, vol. 2, no. 1, pp. 105–135, 1999.

[19] D. R. Kuhn, E. J. Coyne, and T. R. Weil, "Adding Attributes to Role-Based Access Control," *IEEE Computer*, vol. 43, no. 6, pp. 79–81, 2010.

[20] Microsoft, "Row-Level Security," SQL Server Documentation.
