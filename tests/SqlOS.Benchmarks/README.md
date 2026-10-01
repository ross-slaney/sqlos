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

## Gates (`gates.json`)

- **correctness**: every scenario returned exactly the authorized answer.
- **lineage**: the loaded lineage and scope columns, the same after the maintenance pass, and SqlOS's
  rebuild are identical (counts and hashes).
- **scale**: per-page cost must not grow with N. The median at the largest scale may be at most `maxRatio`
  (3.0) times the median at the smallest, plus `slackMilliseconds`; for the scope-columns pages
  `scopedMaxRatio` (1.5), since a single-grant caller's page is one index seek. A lineage page costs the
  cheaper of two plans (start from the caller's scope, or scan the rows in the requested order), and the
  optimizer may switch between them as the table grows, which is what the wider ratio allows for. Both
  scales run on the same machine in the same job, so the ratio holds on shared runners.
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
overflowing fails the benchmark with a SQL error instead of taking down the runner.

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
