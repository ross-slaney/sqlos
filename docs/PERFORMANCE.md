# Performance

Sqlzibar's TVF-based authorization is designed for **predictable, bounded latency** regardless of dataset size. The benchmark suite validates this across 10 test dimensions.

## Key Performance Characteristics

| Property               | Behavior                                            | Evidence                                                         |
| ---------------------- | --------------------------------------------------- | ---------------------------------------------------------------- |
| **Page fetch time**    | O(k) where k = page size, NOT O(N)                  | 3ms at 1K entities, 3ms at 1.2M entities (k=20)                  |
| **Cursor depth**       | Constant — page 500 same speed as page 1            | 3.02ms → 2.74ms from page 1 to page 500 (120K accessible rows)   |
| **Hierarchy depth**    | Negligible impact                                   | 2.00–2.83ms across D=1 to D=5                                    |
| **Access scope**       | Narrower scope = same or faster                     | 3.25ms (full access) vs 1.83ms (single store)                    |
| **Point access check** | ~1ms, independent of total resource count           | 0.98ms at root, 1.69ms at depth=4 (45K resources, 1.2M entities) |
| **Grant set size**     | Negligible impact on point checks                   | 1.07–1.48ms across 1–20 grants                                   |
| **COUNT(\*)**          | O(N) — catastrophic at scale, intentionally avoided | 184ms at 10K → 1,998ms at 100K                                   |

## What Drives Query Time

**Factors that matter:**

- **Page size (k)**: Dominant factor. k=10 → ~2ms, k=20 → ~3ms, k=50 → ~6ms, k=100 → ~12ms
- **Principal set size (M)**: Mild impact at M > 10. STRING_SPLIT + join overhead grows with M
- **Grant density**: More grants on a user = slightly more join work per TVF call
- **Adversarial data layout**: Interleaved unauthorized rows force more scanning (~16ms worst case at 10K)

**Factors that don't matter:**

- **Total entity count (N)**: 1K to 1.2M — no meaningful change in page fetch time
- **Total resource count**: 50 resources vs 45K resources — point check time unchanged
- **Cursor depth**: Page 1 vs page 500 — identical performance (no offset scanning)
- **Hierarchy depth**: D=1 to D=5 — bounded CTE with indexed ParentId lookups

## Benchmark Results (Retail SaaS at 1.2M)

Realistic retail company: 10 chains, 50 regions, 5,000 stores, 40,000 departments, 1,200,000 inventory items. 5-level resource hierarchy.

### Page Size Scaling (company admin, full access)

| Page Size (k) | Median  | P95     | IQR    |
| ------------- | ------- | ------- | ------ |
| k=10          | 2.03ms  | 2.45ms  | 0.13ms |
| k=20          | 3.22ms  | 5.39ms  | 0.30ms |
| k=50          | 6.00ms  | 7.59ms  | 0.52ms |
| k=100         | 11.09ms | 12.87ms | 0.68ms |

Linear with k. Doubling page size roughly doubles query time.

### Access Scope (k=20)

| User           | Scope      | Accessible Rows | Median |
| -------------- | ---------- | --------------- | ------ |
| Company Admin  | Everything | 1,200,000       | 3.25ms |
| Chain Manager  | 1 chain    | ~120,000        | 2.76ms |
| Region Manager | 1 region   | ~24,000         | 2.41ms |
| Store Manager  | 1 store    | ~240            | 1.83ms |

Narrower scope is the same speed or faster.

### TVF vs Materialized Permissions

| Method              | N=10K  | N=50K  | N=100K |
| ------------------- | ------ | ------ | ------ |
| TVF EXISTS (cursor) | 3.43ms | 2.73ms | 2.70ms |
| Materialized JOIN   | 1.30ms | 1.08ms | 0.91ms |

TVF is ~2-3x slower than pre-materialized permissions, but requires zero maintenance on grant changes.

### Point Access Check (Benchmark 9)

Single-resource TVF call — the SQL equivalent of `CheckAccessAsync()`. Tests on the 1.2M entity / 45K resource tree.

| Dimension         | Config          | Median | P95    |
| ----------------- | --------------- | ------ | ------ |
| Hierarchy depth   | root (0 hops)   | 0.98ms | 1.23ms |
| Hierarchy depth   | chain (1 hop)   | 0.98ms | 1.35ms |
| Hierarchy depth   | region (2 hops) | 1.02ms | 1.51ms |
| Hierarchy depth   | store (3 hops)  | 1.43ms | 2.76ms |
| Hierarchy depth   | dept (4 hops)   | 1.69ms | 3.16ms |
| Grant set size    | 1 grant         | 1.48ms | 3.13ms |
| Grant set size    | 5 grants        | 1.07ms | 1.52ms |
| Grant set size    | 10 grants       | 1.11ms | 2.48ms |
| Grant set size    | 20 grants       | 1.29ms | 1.66ms |
| Principal set (M) | M=1             | 1.11ms | 1.46ms |
| Principal set (M) | M=3             | 1.00ms | 1.47ms |
| Principal set (M) | M=6             | 1.06ms | 1.46ms |
| Principal set (M) | M=11            | 1.44ms | 1.66ms |
| Principal set (M) | M=21            | 1.79ms | 2.92ms |

Point checks are sub-2ms in all realistic scenarios. Grant set size has no meaningful impact (the CTE only walks 5 ancestors, so only grants at those specific nodes are checked). Principal set size causes mild degradation above M=10 from STRING_SPLIT overhead.

### Dimensional Analysis at 1.2M (Benchmark 10)

| Dimension                  | Config                    | Median | P95    |
| -------------------------- | ------------------------- | ------ | ------ |
| Principal set (list query) | M=1                       | 3.12ms | 4.43ms |
| Principal set (list query) | M=3                       | 3.34ms | 4.50ms |
| Principal set (list query) | M=6                       | 3.15ms | 3.78ms |
| Principal set (list query) | M=11                      | 3.25ms | 3.94ms |
| Grant density              | 1 chain (~120K rows)      | 2.61ms | 3.38ms |
| Grant density              | 3 chains (~360K rows)     | 3.00ms | 3.85ms |
| Grant density              | 5 chains (~600K rows)     | 2.85ms | 4.03ms |
| Grant density              | 10 chains (all 1.2M)      | 3.19ms | 7.73ms |
| Sparse access              | chain grant (inherit all) | 2.75ms | 4.14ms |
| Sparse access              | 8/8 depts (explicit)      | 1.60ms | 2.21ms |
| Sparse access              | 2/8 depts (25%)           | 1.35ms | 2.13ms |
| Sparse access              | 1/8 depts (12.5%)         | 1.53ms | 1.86ms |

Key findings: Principal set size has negligible impact on list queries at million-scale (all ~3ms). Grant density shows mild increase from 1→10 chains but stays under 4ms. Department-level grants are actually faster than chain-level grants because the TVF finds matching grants sooner (closer in the ancestor walk).

## Running Benchmarks

Requires a SQL Server instance (the Aspire integration test container works):

```bash
cd libraries/Sqlzibar/tests/Sqlzibar.Benchmarks
dotnet run

# Or with a custom connection string:
dotnet run -- "Server=localhost,1434;Database=Sqlzibar_Benchmark;User Id=sa;Password=YourPassword;TrustServerCertificate=True;"
```

The full suite takes ~25 minutes (mostly seeding 1.2M entities). Benchmarks 1-7 run on small datasets (seconds). Benchmarks 8-10 share a single 1.2M-entity seed to avoid re-seeding.

### Benchmark Suite Overview

| #   | Benchmark            | Dimension                    | Scale     |
| --- | -------------------- | ---------------------------- | --------- |
| 1   | Entity Count Scaling | Total entities (N)           | 1K–100K   |
| 2   | Hierarchy Depth      | Resource tree depth (D)      | D=1–5     |
| 3   | Principal Set Size   | IDs in authorization (M)     | M=1–21    |
| 4   | Selectivity          | Dense vs sparse access (σ)   | σ=0.1–1.0 |
| 5   | Adversarial Layout   | Worst-case data interleaving | 10K       |
| 6   | TVF vs Materialized  | Authorization strategy       | 10K–100K  |
| 7   | Pagination Strategy  | Cursor vs offset vs COUNT    | 10K–100K  |
| 8   | Retail SaaS          | Realistic million-scale      | 1.2M      |
| 9   | Point Access Check   | Single-resource TVF call     | 1.2M      |
| 10  | Dimensional Analysis | Factor isolation at scale    | 1.2M      |
