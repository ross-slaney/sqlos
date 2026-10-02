# SqlOS SHRBAC benchmarks

Measures authorized list pages and point checks as SqlOS ships them, on SQL Server and PostgreSQL, while the
catalog grows from 1M to 50M products in CI, and fails CI when that behavior regresses.

It is the maintained successor to the harness behind the paper's Section 7 (kept in `paper/benchmark`),
which ran a hand-copied version of the schema and function at 1.2M–1.5M resources on SQL Server only.

## Three ways to filter the same table

Every list page is measured three ways on the same `Products` table, the same data, and the same LINQ:

| Way | Scenario ids | What the filter does |
|---|---|---|
| **Previous function** | `reference.list.*` | The row filter as the last release shipped it (`Reference/*.sql`, created as `fn_IsResourceAccessible_Reference`): for every candidate row, walk up the tree and look for a grant at each ancestor |
| **Lineage** | `list.*` | `BuildFilterAsync` on a context without scope columns: for every candidate row, one lookup of its resource's lineage (its ancestor at the caller's level and its reach), or, when the optimizer prefers it, the caller's scope read from the ancestor index and joined to the rows |
| **Scope columns** | `scoped.list.*` | `BuildFilterAsync` on a context with `ScopeColumns = true`: the lineage sits on the product row, so a single-grant caller's page is one seek of an index that starts with the ancestor column and continues with the page's order |

The two `BuildFilterAsync` ways are the same predicate; only where the lineage is read differs. Point
checks are measured twice (`fn_IsResourceAccessible` now and as the previous release shipped it), plus
`Allows` (`CheckAccessAsync`), which applications call.

## What is measured

- **The shipped artifacts.** The FGA schema scripts, their indexes, the lineage columns and their triggers,
  the scope columns and theirs, `fn_ActiveSubjects`, `fn_AccessRoots`, `fn_IsResourceAccessible`, and the
  core seed are created by SqlOS's own initializers (`SqlOSFgaSchemaInitializer`,
  `SqlOSFgaFunctionInitializer`, `SqlOSFgaSeedService`) from a context that declares the scope columns. A
  change to any of them is what gets measured.
- **The application's queries.** Pages go through `BuildFilterAsync<Product>` and EF Core, so the timing
  includes the SQL EF generates for callers. The filter is built before the clock starts, as the paper
  measured; the authorized query alone is timed. Warm cache; median and p95 over up to 25 runs.
- **Every answer is checked against ground truth.** The exact page (k + 1 rows after the cursor, or the first
  k + 1 rows by price) and every allow or deny are recomputed from the dataset generator, so a fast wrong
  answer fails the run.
- **The actual plan is captured** for each page (`EXPLAIN (ANALYZE, BUFFERS)` or `SET STATISTICS XML`). From
  it the report takes product rows read, server execution time, and server planning time. The plans are
  uploaded with the results.
- **The lineage is verified.** At the first scale, the lineage and scope columns the loader generated are
  compared (counts and order-independent hashes over every column) with the same after the maintenance pass
  and with what SqlOS rebuilds from the resource tree alone with its own procedure.
- **Every query has a budget** (`--scenario-budget`, 600 s by default). A scenario whose first execution
  exceeds it is reported as `> 600 s‡` and counted at the budget, a lower bound, in every ratio: the
  previous function's sparse pages take hours at 50M, and they bound the run instead of ending it.

## The dataset

A retail company under the SqlOS root: twelve chains, about 13,800 stores and 74,000 organizational nodes.

- **Mixed depth.** Chains 1–11 are D = 5 (root → chain → region → store → product). Chain 12 is D = 10
  (root → chain → region → district → area → zone → store → department → section → product), the deepest
  tree the configured depth allows. It holds about 10% of the catalog.
- **Uneven fan-out.** Regions per chain and stores per region vary. Store sizes are log-normal, so some
  stores are several times the median.
- **Realistic row order.** Each product is placed by a hash of its id, so a store's products are spread
  through the whole id range, the way rows arrive over time. (The paper's harness inserted products store by
  store, which put the store manager's rows at the front of the table and hid the cost of sparse access.)
- **Every product is a resource**, as with `ISqlOSResourceEntity`. `Products` carries the indexes such a
  table has: the unique `ResourceId`, the `StoreId` foreign key, and one declared order besides the key,
  `Price`. With the scope columns on, SqlOS mirrors the key and the price index once per level.
- **Five people, M = 3 each** (the user and two groups): a company admin granted through a group on the
  root, a chain manager, a region manager, a manager of the deep chain, and the manager of a median-sized
  store. Two more people hold 10,000 and 100,000 grants on single products.

The data is a pure function of the seed, so it grows in place (1M → 10M → 50M) and any page can be verified.
The loaders write sequence numbers, lineage, and scope columns themselves; the first scale checks them
against SqlOS's rebuild.

## Scenarios

| Id | What it shows |
|---|---|
| `list.admin.first-page`, `list.admin.k100` | Dense access (σ = 1): cost follows k, not N |
| `list.admin.mid-cursor` | A page from the middle of the table costs the same as the first |
| `list.admin.by-price`, `list.region.by-price`, `list.store.by-price` | Pages in an order the application declared an index for, dense to sparse |
| `list.chain.first-page`, `list.region.first-page` | Narrower grants (σ = 7%, 1%) |
| `list.deep-chain.first-page` | The same at D = 10 |
| `list.store.first-page` | Sparse access (σ ≈ 0.0065%): the previous function examined about 400K rows per page |
| `list.store.by-store` | The same person listing their store with `WHERE StoreId = …`: the application narrowed the page itself |
| `list.grants10k.first-page`, `list.grants100k.first-page` | More roots than the filter lists: each row is checked by `fn_IsResourceAccessible`, which probes the grants of the row's ancestors |
| `reference.*`, `scoped.*` | The twins of every page and point check, as above |
| `point.function.*`, `point.api.*` | `fn_IsResourceAccessible` for one product at depth 4 and 9, a denial, the many-grants people, and `Allows` |
| `density.*` | At the first scale only: the region pages and the denied check re-run while 100 other people hold grants on the root. Only the caller's own grants should matter. Reported, not gated |
| maintenance | At the first scale only: 2,000 single-row inserts with the lineage triggers on and off, one 2,000-row insert, one 2,000-row delete, and reparent, deactivate and reactivate of a region subtree; then a rebuild from scratch, compared with the maintained lineage |

The previous function's sparse pages cost minutes, so they run only at the first and last scales, which are
the two the scale gate compares. The lineage and scope-columns pages run at every scale.

## Results of the latest full run

Run 36933080921 (2026-10-01, hosted `ubuntu-latest`, 4 vCPU, 17 GB, 8 GB to the engine), 1M → 10M → 50M on
both engines, every gate green. **Before** is the previous release's function through the same
`BuildFilterAsync`, **R1** the lineage read from the resources table, **R2** the lineage read from the row's
scope columns. A dash means that form was not run at that scale. The run's summary page has the 10M tables,
every scenario with rows read and server time, and the plans.

**PostgreSQL 16, 1M products** (median ms; × = times faster than before)

| Page | σ | Before | R1 lineage | R2 scope columns | R1 × | R2 × |
|---|---:|---:|---:|---:|---:|---:|
| Company admin, first page (k = 20) | 1 | 10.98 | 2.05 | 1.82 | ×5.4 | ×6.0 |
| Company admin, first page (k = 100) | 1 | 20.19 | 2.58 | 1.99 | ×7.8 | ×10 |
| Company admin, page from the middle of the table | 1 | 11.21 | 2.11 | 1.81 | ×5.3 | ×6.2 |
| Company admin, first page by price | 1 | 11.11 | 2.07 | 1.87 | ×5.4 | ×5.9 |
| Chain manager (D = 5) | 7.1% | 33.54 | 3.25 | 1.83 | ×10 | ×18 |
| Region manager (D = 5) | 0.9729% | 184 | 11.02 | 1.85 | ×17 | ×100 |
| Region manager, first page by price | 0.9729% | 256 | 20.63 | 1.94 | ×12 | ×132 |
| Chain manager (D = 10) | 10% | 32.65 | 3.16 | 2.06 | ×10 | ×16 |
| Store manager, every visible product (sparse) | 0.0065% | 44.9 s | 2.54 | 1.88 | ×17,658 | ×23,919 |
| Store manager, first page by price (sparse) | 0.0065% | 49.0 s | 2.45 | 1.83 | ×19,995 | ×26,815 |
| Store manager, filtered to the store (StoreId index) | 0.0065% | 12.03 | 1.99 | 1.97 | ×6.0 | ×6.1 |
| 10,000 single-product grants, first page | 0.02% | – | 82.34 | 82.39 | – | – |
| 100,000 single-product grants, first page | 0.2% | – | 14.43 | 14.57 | – | – |
| fn_IsResourceAccessible, product at depth 4 | – | 8.38 | 1.88 | – | ×4.5 | – |
| fn_IsResourceAccessible, 100,000 grants, a granted product | – | 8.22 | 1.92 | – | ×4.3 | – |

**PostgreSQL 16, 50M products** (median ms; × = times faster than before)

| Page | σ | Before | R1 lineage | R2 scope columns | R1 × | R2 × |
|---|---:|---:|---:|---:|---:|---:|
| Company admin, first page (k = 20) | 1 | 11.00 | 2.07 | 1.81 | ×5.3 | ×6.1 |
| Company admin, first page (k = 100) | 1 | 21.01 | 2.78 | 1.97 | ×7.6 | ×11 |
| Company admin, page from the middle of the table | 1 | 11.74 | 2.08 | 1.88 | ×5.7 | ×6.2 |
| Company admin, first page by price | 1 | 10.75 | 2.20 | 1.78 | ×4.9 | ×6.1 |
| Chain manager (D = 5) | 7.1% | 35.91 | 3.62 | 1.88 | ×9.9 | ×19 |
| Region manager (D = 5) | 0.9729% | 197 | 13.95 | 1.91 | ×14 | ×103 |
| Region manager, first page by price | 0.9729% | 322 | 31.36 | 1.77 | ×10 | ×181 |
| Chain manager (D = 10) | 10% | 33.84 | 3.48 | 1.85 | ×9.7 | ×18 |
| Store manager, every visible product (sparse) | 0.0065% | 47.0 s | 38.27 | 1.81 | ×1,229 | ×25,989 |
| Store manager, first page by price (sparse) | 0.0065% | 120.6 s | 37.97 | 1.79 | ×3,175 | ×67,304 |
| Store manager, filtered to the store (StoreId index) | 0.0065% | 241 | 9.11 | 6.78 | ×26 | ×36 |
| 10,000 single-product grants, first page | 0.02% | – | 88.49 | 88.21 | – | – |
| 100,000 single-product grants, first page | 0.2% | – | 14.99 | 14.91 | – | – |
| fn_IsResourceAccessible, product at depth 4 | – | 8.44 | 1.94 | – | ×4.4 | – |
| fn_IsResourceAccessible, 100,000 grants, a granted product | – | 8.76 | 1.99 | – | ×4.4 | – |

**SQL Server 2022, 1M products** (median ms; × = times faster than before)

| Page | σ | Before | R1 lineage | R2 scope columns | R1 × | R2 × |
|---|---:|---:|---:|---:|---:|---:|
| Company admin, first page (k = 20) | 1 | 15.31 | 0.85 | 0.84 | ×18 | ×18 |
| Company admin, first page (k = 100) | 1 | 71.90 | 1.30 | 1.17 | ×55 | ×61 |
| Company admin, page from the middle of the table | 1 | 15.49 | 0.84 | 0.84 | ×18 | ×19 |
| Company admin, first page by price | 1 | 16.02 | 0.88 | 0.85 | ×18 | ×19 |
| Chain manager (D = 5) | 7.1% | 71.14 | 1.44 | 1.02 | ×49 | ×70 |
| Region manager (D = 5) | 0.9729% | 426 | 3.67 | 1.13 | ×116 | ×378 |
| Region manager, first page by price | 0.9729% | 578 | 8.49 | 0.87 | ×68 | ×667 |
| Chain manager (D = 10) | 10% | 73.11 | 1.40 | 1.03 | ×52 | ×71 |
| Store manager, every visible product (sparse) | 0.0065% | 103.4 s | 1.56 | 0.85 | ×66,248 | ×121,082 |
| Store manager, first page by price (sparse) | 0.0065% | 111.2 s | 1.57 | 0.90 | ×70,628 | ×123,538 |
| Store manager, filtered to the store (StoreId index) | 0.0065% | 16.00 | 0.94 | 1.47 | ×17 | ×11 |
| 10,000 single-product grants, first page | 0.02% | – | 66.06 | 65.84 | – | – |
| 100,000 single-product grants, first page | 0.2% | – | 11.30 | 11.11 | – | – |
| fn_IsResourceAccessible, product at depth 4 | – | 1.81 | 0.59 | – | ×3.0 | – |
| fn_IsResourceAccessible, 100,000 grants, a granted product | – | 319 | 0.65 | – | ×489 | – |

**SQL Server 2022, 50M products** (median ms; × = times faster than before)

| Page | σ | Before | R1 lineage | R2 scope columns | R1 × | R2 × |
|---|---:|---:|---:|---:|---:|---:|
| Company admin, first page (k = 20) | 1 | 92.23 | 0.84 | 0.83 | ×109 | ×111 |
| Company admin, first page (k = 100) | 1 | 170 | 1.35 | 1.16 | ×126 | ×146 |
| Company admin, page from the middle of the table | 1 | 81.29 | 1.03 | 1.02 | ×79 | ×79 |
| Company admin, first page by price | 1 | 84.09 | 0.88 | 0.86 | ×96 | ×98 |
| Chain manager (D = 5) | 7.1% | 487 | 1.57 | 1.02 | ×311 | ×479 |
| Region manager (D = 5) | 0.9729% | 3301 | 4.13 | 1.11 | ×799 | ×2,973 |
| Region manager, first page by price | 0.9729% | 3084 | 15.83 | 0.86 | ×195 | ×3,590 |
| Chain manager (D = 10) | 10% | 425 | 1.53 | 1.02 | ×278 | ×417 |
| Store manager, every visible product (sparse) | 0.0065% | 369.2 s | 49.44 | 0.87 | ×7,467 | ×426,327 |
| Store manager, first page by price (sparse) | 0.0065% | > 600 s‡ | 52.62 | 0.85 | > ×11,403 | > ×703,070 |
| Store manager, filtered to the store (StoreId index) | 0.0065% | 16.30 | 0.95 | 13.44 | ×17 | ×1.2 |
| 10,000 single-product grants, first page | 0.02% | – | 66.78 | 67.02 | – | – |
| 100,000 single-product grants, first page | 0.2% | – | 11.07 | 11.16 | – | – |
| fn_IsResourceAccessible, product at depth 4 | – | 1.82 | 0.59 | – | ×3.1 | – |
| fn_IsResourceAccessible, 100,000 grants, a granted product | – | 327 | 0.64 | – | ×507 | – |

The store manager's R1 page grows with the catalog (2.5 → 38 ms on PostgreSQL, 1.6 → 49 ms on SQL Server)
because the store itself does: 65 products at 1M, 3,315 at 50M, and the page reads the whole scope
(min(k / σ, σN)). The R2 page reads 21 rows at either size. The page filtered to the store reads the store's
rows through the `StoreId` index on either engine; at 50M, SQL Server's optimizer took that path for the R2
form (13 ms) and the ancestor index for the R1 form (1 ms).

Costs measured in the same run, at 1M: a single-row resource insert 3.9 ms with the lineage triggers and
0.8 ms without on PostgreSQL (2.9 ms and 1.2 ms on SQL Server); reparenting a region of 9,959 resources with
scope columns on about 1.7 s on SQL Server; the first-start rebuild of 1,074,481 resources and 1,000,000
scope-column rows 120 s on PostgreSQL and 56 s on SQL Server. Storage at 50M products with scope columns and
their per-level indexes: 62.7 GB on PostgreSQL, 71.1 GB on SQL Server, of which the lineage and scope
columns with their indexes are about half.

## Gates (`gates.json`)

- **correctness**: every scenario returned exactly the authorized answer.
- **lineage**: the loaded lineage and scope columns, the same after the maintenance pass, and SqlOS's
  rebuild are identical (counts and hashes).
- **scale**: per-page cost must follow the work the paper predicts, not N. The median at the largest scale
  may be at most `maxRatio` (3.0) times the median at the smallest, times the growth of the rows the page has
  to touch, plus `slackMilliseconds`. A lineage page touches min(k / σ, σN) rows (the cheaper of the rows in
  the requested order and the caller's scope): flat for a dense caller, growing with the catalog for a sparse
  one whose scope grows with it. The previous function touches min(k / σ, N). A page filtered to a store
  touches that store's σN rows through its own index. The scope-columns pages use `scopedMaxRatio` (1.5) and
  no growth term: one index seek and k rows at any N. The previous function's pages are measured at every
  scale but not gated: how its cost grows is a finding about it, not a regression of the current filter.
- **regression**: the lineage and the scope-columns pages against the previous function, at every scale: at
  most `maxRatio` (1.15) times the previous median, plus slack.
- **improvement**: for the sparse pages, the lineage page must take at most `lineageMaxRatio` (0.2) of the
  previous function's time and the scope-columns page at most `scopedMaxRatio` (0.05).
- **ceiling**: an absolute median budget per scenario and engine, for regressions that slow every scale
  alike. Set them to several times the values CI reports, and raise them only with an explanation.

## Run it

Docker is the only requirement; the harness starts the database with Testcontainers.

```bash
# PostgreSQL, 100K and 1M products (a few minutes)
dotnet run --project tests/SqlOS.Benchmarks -c Release -- --provider postgresql

# SQL Server, as CI runs it
dotnet run --project tests/SqlOS.Benchmarks -c Release -- --provider sqlserver --scales 1m,10m,50m --data-dir /mnt/sqlos-bench

# An existing server (drops and recreates the SqlOSBenchmarks database on it; PostgreSQL needs a superuser)
dotnet run --project tests/SqlOS.Benchmarks -c Release -- --provider sqlserver --connection "Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True"
```

Results go to `artifacts/benchmarks/<provider>/`: `results.json`, `summary.md`, and `plans/`. Run with
`--help` for every option.

SQL Server is published for x64 only. On Apple Silicon it runs under emulation, which is fine for checking
correctness but not for timings.

## CI

`.github/workflows/benchmarks.yml` runs one job per engine beside Pull Request CI, when `src/SqlOS`, this
project, or the workflow changes. Standard GitHub-hosted runners (4 vCPU, 16 GB) are free for public
repositories.

| Tier | When | Scales | What is skipped |
|---|---|---|---|
| Pull request | Every pull request that touches `src/SqlOS` | 1M → 10M | the previous function's sparse pages (minutes each) |
| Full | Every merge to `main`, weekly, on demand, and on a pull request labelled `benchmark-full` | 1M → 10M → 50M | the previous function's many-grants pages |

Most of the full run is loading: 90M new rows in each of two tables, then rebuilding indexes. PostgreSQL
loads in parallel `COPY` streams. SQL Server takes the table lock that minimal logging needs, so it has one
bulk stream per table. SQL Server's data, log, and tempdb files are capped below the disk's free space, so
overflowing fails the benchmark with a SQL error instead of taking down the runner. Before growing to the
next scale the harness projects the database's size from the current one; a scale that would not fit the
disk is reported as a failed `disk` gate and not attempted.

The Markdown summary is on the run page, and the JSON results and plans are uploaded as artifacts.

## Results

Filled in from the CI runs of this branch; see the run summaries until then.

## Loading

Each engine uses its bulk path, into the tables SqlOS created:

- **SQL Server**: ordered `SqlBulkCopy` with a table lock into the clustered primary keys (minimally logged
  under simple recovery). Bulk copy fires no triggers, so the lineage and scope columns travel with the rows.
- **PostgreSQL**: parallel `COPY … (FORMAT BINARY)` with foreign-key and lineage triggers skipped for the
  loading sessions (`session_replication_role = replica`).

Secondary indexes, the per-level ancestor and scope indexes among them, are set aside during a load and
rebuilt from their own definitions, so no DDL is copied into the harness. Foreign keys are revalidated (SQL
Server), statistics are refreshed, PostgreSQL tables are vacuumed so the visibility map matches a table
autovacuum maintains, and the resource sequence is moved past the loaded numbers so the maintenance pass
inserts like an application would.

The PostgreSQL container turns off durability settings that only affect writes (`fsync`, WAL level,
synchronous commit). Planner settings are the usual SSD values.
