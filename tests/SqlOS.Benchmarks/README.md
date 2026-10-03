# SqlOS SHRBAC benchmarks

Measures authorized list pages and point checks as SqlOS ships them, on SQL Server and PostgreSQL, while the
catalog grows from 1M to 50M products in CI, and fails CI when that behavior regresses.

It is the maintained successor to the harness behind the paper's Section 7 (kept in `paper/benchmark`),
which ran a hand-copied version of the schema and function at 1.2M–1.5M resources on SQL Server only.

Every list page (`list.*`) goes through `BuildFilterAsync`: the row's own scope column holds its resource's
ancestor at every level access flows down from, so a single-grant caller's page is one seek of an index that
starts with that level's part of the column and continues with the order the page asks for. Point checks
measure `fn_IsResourceAccessible` and `Allows` (`CheckAccessAsync`), which applications call.

## What is measured

- **The shipped artifacts.** The FGA schema scripts, their indexes, the lineage columns and their triggers,
  the scope column with its per-level indexes and triggers, `fn_ActiveSubjects`, `fn_AccessRoots`,
  `fn_IsResourceAccessible`, and the core seed are created by SqlOS's own initializers
  (`SqlOSFgaSchemaInitializer`, `SqlOSFgaFunctionInitializer`, `SqlOSFgaSeedService`) from an application
  context. A change to any of them is what gets measured.
- **The application's queries.** Pages go through `BuildFilterAsync<Product>` and EF Core, so the timing
  includes the SQL EF generates for callers. The filter is built before the clock starts, as the paper
  measured; the authorized query alone is timed. Warm cache; median and p95 over up to 25 runs.
- **Every answer is checked against ground truth.** The exact page (k + 1 rows after the cursor, or the first
  k + 1 rows by price) and every allow or deny are recomputed from the dataset generator, so a fast wrong
  answer fails the run.
- **The actual plan is captured** for each page (`EXPLAIN (ANALYZE, BUFFERS)` or `SET STATISTICS XML`). From
  it the report takes product rows read, server execution time, and server planning time. The plans are
  uploaded with the results.
- **The lineage is verified.** At the first scale, the lineage and scope values the loader generated are
  compared (counts and order-independent hashes over every column) with the same after the maintenance pass
  and with what SqlOS rebuilds from the resource tree alone with its own procedure.
- **Every query has a budget** (`--scenario-budget`, 600 s by default). A scenario whose first execution
  exceeds it is reported as `> 600 s‡` and counted at the budget, a lower bound, in every ratio, so a
  pathological page bounds the run instead of ending it.

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
  `Price`. SqlOS mirrors the key and the price index once per level of the scope column.
- **Five people, M = 3 each** (the user and two groups): a company admin granted through a group on the
  root, a chain manager, a region manager, a manager of the deep chain, and the manager of a median-sized
  store. Two more people hold 10,000 and 100,000 grants on single products.

The data is a pure function of the seed, so it grows in place (1M → 10M → 50M) and any page can be verified.
The loaders write sequence numbers, lineage, and scope values themselves; the first scale checks them
against SqlOS's rebuild.

## Scenarios

| Id | What it shows |
|---|---|
| `list.admin.first-page`, `list.admin.k100` | Dense access (σ = 1): cost follows k, not N |
| `list.admin.mid-cursor` | A page from the middle of the table costs the same as the first |
| `list.admin.by-price`, `list.region.by-price`, `list.store.by-price` | Pages in an order the application declared an index for, dense to sparse |
| `list.chain.first-page`, `list.region.first-page` | Narrower grants (σ = 7%, 1%) |
| `list.deep-chain.first-page` | The same at D = 10 |
| `list.store.first-page` | Sparse access (σ ≈ 0.0065%): one seek, the same as a dense page |
| `list.store.by-store` | The same person listing their store with `WHERE StoreId = …`: the application narrowed the page itself |
| `list.grants10k.first-page`, `list.grants100k.first-page` | Thousands of roots: the same predicate, with the roots at the product level sent as one list parameter. Callers with tens of thousands of single-resource grants are outside the model's intended shape (access flows down a hierarchy); these pages are measured and held to their own limits, not expected to be flat |
| `point.function.*`, `point.api.*` | `fn_IsResourceAccessible` for one product at depth 4 and 9, a denial, the many-grants people, and `Allows` |
| `density.*` | At the first scale only: the region pages and the denied check re-run while 100 other people hold grants on the root. Only the caller's own grants should matter. Reported, not gated |
| maintenance | At the first scale only: 2,000 single-row inserts with the lineage triggers on and off, one 2,000-row insert, one 2,000-row delete, and reparent, deactivate and reactivate of a region subtree; then a rebuild from scratch, compared with the maintained lineage |

## The run that replaced the tree walk

Run 36964060038 (2026-10-02, hosted `ubuntu-latest`, 4 vCPU, 17 GB, 8 GB to the engine), 1M → 10M → 50M on
both engines, measured the scope column against the function it replaced, which walked up the tree from
every candidate row. The harness no longer runs that function; these tables are the record of why. **Before**
is that function through the same `BuildFilterAsync`; **Now** is the filter reading the row's scope column.
A dash means the old function was not run for that page (its many-grants pages take minutes at 10M). The
many-grants list rows were measured when callers with more than 1,000 roots were still checked row by row;
that path is gone, and those callers now get the same filter as everyone else (their current costs are the
limits in `gates.json`).

**PostgreSQL 16, 1M products** (median ms; × = times faster than before)

| Page | σ | Before | Now | × |
|---|---:|---:|---:|---:|
| Company admin, first page (k = 20) | 1 | 10.67 | 1.82 | ×5.9 |
| Company admin, first page (k = 100) | 1 | 19.83 | 1.91 | ×10 |
| Company admin, page from the middle of the table | 1 | 10.84 | 1.76 | ×6.2 |
| Company admin, first page by price | 1 | 10.79 | 1.74 | ×6.2 |
| Chain manager (D = 5) | 7.1% | 33.14 | 1.75 | ×19 |
| Region manager (D = 5) | 0.9729% | 181 | 1.79 | ×101 |
| Region manager, first page by price | 0.9729% | 251 | 1.75 | ×143 |
| Chain manager (D = 10) | 10% | 31.89 | 1.88 | ×17 |
| Store manager, every visible product (sparse) | 0.0065% | 44.1 s | 1.83 | ×24,089 |
| Store manager, first page by price (sparse) | 0.0065% | 48.2 s | 1.75 | ×27,541 |
| Store manager, filtered to the store (StoreId index) | 0.0065% | 11.66 | 1.83 | ×6.4 |
| 10,000 single-product grants, first page | 0.02% | – | 82.20 | – |
| 100,000 single-product grants, first page | 0.2% | – | 14.10 | – |
| fn_IsResourceAccessible, product at depth 4 | – | 8.25 | 1.95 | ×4.2 |
| fn_IsResourceAccessible, 100,000 grants, a granted product | – | 8.24 | 1.91 | ×4.3 |

**PostgreSQL 16, 50M products** (median ms; × = times faster than before)

| Page | σ | Before | Now | × |
|---|---:|---:|---:|---:|
| Company admin, first page (k = 20) | 1 | 10.67 | 1.77 | ×6.0 |
| Company admin, first page (k = 100) | 1 | 20.16 | 1.90 | ×11 |
| Company admin, page from the middle of the table | 1 | 10.81 | 1.73 | ×6.3 |
| Company admin, first page by price | 1 | 10.61 | 1.70 | ×6.2 |
| Chain manager (D = 5) | 7.1% | 33.88 | 1.73 | ×20 |
| Region manager (D = 5) | 0.9729% | 188 | 1.73 | ×109 |
| Region manager, first page by price | 0.9729% | 310 | 1.69 | ×183 |
| Chain manager (D = 10) | 10% | 31.90 | 1.73 | ×18 |
| Store manager, every visible product (sparse) | 0.0065% | 46.2 s | 1.72 | ×26,856 |
| Store manager, first page by price (sparse) | 0.0065% | 127.4 s | 1.70 | ×75,019 |
| Store manager, filtered to the store (StoreId index) | 0.0065% | 241 | 5.17 | ×47 |
| 10,000 single-product grants, first page | 0.02% | – | 86.28 | – |
| 100,000 single-product grants, first page | 0.2% | – | 14.46 | – |
| fn_IsResourceAccessible, product at depth 4 | – | 8.21 | 1.90 | ×4.3 |
| fn_IsResourceAccessible, 100,000 grants, a granted product | – | 8.16 | 1.95 | ×4.2 |

**SQL Server 2022, 1M products** (median ms; × = times faster than before)

| Page | σ | Before | Now | × |
|---|---:|---:|---:|---:|
| Company admin, first page (k = 20) | 1 | 23.37 | 1.06 | ×22 |
| Company admin, first page (k = 100) | 1 | 111 | 1.75 | ×63 |
| Company admin, page from the middle of the table | 1 | 24.12 | 1.09 | ×22 |
| Company admin, first page by price | 1 | 25.39 | 1.09 | ×23 |
| Chain manager (D = 5) | 7.1% | 110 | 1.38 | ×79 |
| Region manager (D = 5) | 0.9729% | 666 | 1.65 | ×404 |
| Region manager, first page by price | 0.9729% | 890 | 1.15 | ×771 |
| Chain manager (D = 10) | 10% | 112 | 1.33 | ×84 |
| Store manager, every visible product (sparse) | 0.0065% | 161.3 s | 1.08 | ×149,159 |
| Store manager, first page by price (sparse) | 0.0065% | 171.0 s | 1.06 | ×162,037 |
| Store manager, filtered to the store (StoreId index) | 0.0065% | 23.62 | 1.11 | ×21 |
| 10,000 single-product grants, first page | 0.02% | – | 80.65 | – |
| 100,000 single-product grants, first page | 0.2% | – | 13.20 | – |
| fn_IsResourceAccessible, product at depth 4 | – | 2.41 | 0.77 | ×3.2 |
| fn_IsResourceAccessible, 100,000 grants, a granted product | – | 376 | 0.83 | ×453 |

**SQL Server 2022, 50M products** (median ms; × = times faster than before)

| Page | σ | Before | Now | × |
|---|---:|---:|---:|---:|
| Company admin, first page (k = 20) | 1 | 70.10 | 1.10 | ×64 |
| Company admin, first page (k = 100) | 1 | 181 | 1.62 | ×112 |
| Company admin, page from the middle of the table | 1 | 64.41 | 1.29 | ×50 |
| Company admin, first page by price | 1 | 67.96 | 1.08 | ×63 |
| Chain manager (D = 5) | 7.1% | 419 | 1.43 | ×292 |
| Region manager (D = 5) | 0.9729% | 2101 | 1.68 | ×1,252 |
| Region manager, first page by price | 0.9729% | 2434 | 1.13 | ×2,163 |
| Chain manager (D = 10) | 10% | 299 | 1.38 | ×217 |
| Store manager, every visible product (sparse) | 0.0065% | 325.4 s | 1.14 | ×286,631 |
| Store manager, first page by price (sparse) | 0.0065% | 580.4 s | 1.07 | ×541,599 |
| Store manager, filtered to the store (StoreId index) | 0.0065% | 25.69 | 1.31 | ×20 |
| 10,000 single-product grants, first page | 0.02% | – | 87.24 | – |
| 100,000 single-product grants, first page | 0.2% | – | 14.22 | – |
| fn_IsResourceAccessible, product at depth 4 | – | 2.62 | 0.78 | ×3.4 |
| fn_IsResourceAccessible, 100,000 grants, a granted product | – | 394 | 0.88 | ×445 |

Costs measured in the same run, at 1M: a single-row resource insert 3.8 ms with the lineage triggers and
0.75 ms without on PostgreSQL (3.0 ms and 1.2 ms on SQL Server); reparenting a region of 9,959 resources
about 2 s on PostgreSQL and 4 s on SQL Server; the first-start rebuild of 1,074,481 resources and 1,000,000
application rows about two minutes on PostgreSQL and one on SQL Server. Storage at 50M products: 58.6 GB on
PostgreSQL, 70.0 GB on SQL Server, of which the lineage, the scope column, and their indexes are about half.
The store manager's page filtered to the store on PostgreSQL (5 ms at 50M) reads the store's rows through
the application's own `StoreId` index; every other page reads the page's rows and no more.

## Gates (`gates.json`)

- **correctness**: every scenario returned exactly the authorized answer.
- **lineage**: the loaded lineage and scope values, the same after the maintenance pass, and SqlOS's
  rebuild are identical (counts and hashes).
- **scale**: per-page cost must follow the work the paper predicts, not N. The median at the largest scale
  may be at most `maxRatio` (2.0) times the median at the smallest, times the growth of the rows the page has
  to touch, plus `slackMilliseconds`. A page through the scope column touches k rows at any N (one index
  seek), so it has no growth term; a page filtered to a store touches that store's σN rows through its own
  index. The many-grants pages are exempt: their cost follows the caller's grants, not N.
- **regression**: every scenario's median against the constant set for it and the engine in
  `regressionMilliseconds`: what the scenario costs today, with headroom for runner noise (the many-grants
  pages sit at about 1.5× the values CI reports; the rest, several times). A change that makes any page or
  point check slower than its limit fails the run. Raise a limit only with an explanation.

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

| Tier | When | Scales |
|---|---|---|
| CI | Every push to a pull request that touches `src/SqlOS`, every merge to `main`, weekly | 100K → 1M |
| Full | Only when the `benchmark-full` label is added to a pull request (once per labelling: remove and re-add it to run again), or a manual run that asks for 50M | 1M → 10M → 50M |

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
  under simple recovery). Bulk copy fires no triggers, so the lineage and scope values travel with the rows.
- **PostgreSQL**: parallel `COPY … (FORMAT BINARY)` with foreign-key and lineage triggers skipped for the
  loading sessions (`session_replication_role = replica`).

Secondary indexes, the per-level ancestor and scope indexes among them, are set aside during a load and
rebuilt from their own definitions, so no DDL is copied into the harness. Foreign keys are revalidated (SQL
Server), statistics are refreshed, PostgreSQL tables are vacuumed so the visibility map matches a table
autovacuum maintains, and the resource sequence is moved past the loaded numbers so the maintenance pass
inserts like an application would.

The PostgreSQL container turns off durability settings that only affect writes (`fsync`, WAL level,
synchronous commit). Planner settings are the usual SSD values.
