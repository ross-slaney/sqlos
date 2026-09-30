# SqlOS SHRBAC benchmarks

Measures authorization as SqlOS ships it, on SQL Server and PostgreSQL, while the catalog grows from 1M to
100M products, and fails CI when that behavior regresses.

It is the maintained successor to the harness behind the paper's Section 7 (kept in `paper/benchmark`),
which ran a hand-copied version of the schema and function at 1.2M–1.5M resources on SQL Server only.

## What is measured

- **The shipped artifacts.** The FGA schema scripts, their indexes, `fn_IsResourceAccessible`, and the core
  seed are created by SqlOS's own initializers (`SqlOSFgaSchemaInitializer`, `SqlOSFgaFunctionInitializer`,
  `SqlOSFgaSeedService`). A change to any of them is what gets measured.
- **The application's query.** List pages go through `BuildFilterAsync<Product>` and EF Core, so the timing
  includes the SQL EF generates for callers. Point checks are measured twice: the function itself, and
  `Allows` (`CheckAccessAsync`), which applications call.
- **Only the authorized query is timed**, with a warm cache: median and p95 over up to 25 runs.
- **Every answer is checked against ground truth.** The exact page (k + 1 rows after the cursor) and every
  allow or deny are recomputed from the dataset generator, so a fast wrong answer fails the run.
- **The actual plan is captured** for each list scenario (`EXPLAIN (ANALYZE, BUFFERS)` or
  `SET STATISTICS XML`). From it the report takes rows examined, server execution time, and server planning
  time. The plans are uploaded with the results.

## The dataset

A retail company under the SqlOS root: twelve chains, about 13,800 stores and 74,000 organizational nodes.

- **Mixed depth.** Chains 1–11 are D = 5 (root → chain → region → store → product). Chain 12 is D = 10
  (root → chain → region → district → area → zone → store → department → section → product), the deepest
  tree the function walks. It holds about 10% of the catalog.
- **Uneven fan-out.** Regions per chain and stores per region vary. Store sizes are log-normal, so some
  stores are several times the median.
- **Realistic row order.** Each product is placed by a hash of its id, so a store's products are spread
  through the whole id range, the way rows arrive over time. (The paper's harness inserted products store by
  store, which put the store manager's rows at the front of the table and hid the cost of sparse access.)
- **Every product is a resource**, as with `ISqlOSResourceEntity`. At 100M products that is about 100.07M
  resource nodes, 83 times the paper's 1.2M.
- **Five people, M = 3 each** (the user and two groups): a company admin granted through a group on the
  root, a chain manager, a region manager, a manager of the deep chain, and the manager of a median-sized
  store.

The data is a pure function of the seed, so it grows in place (1M → 10M → 100M) and any page can be verified.

## Scenarios

| Id | What it shows |
|---|---|
| `list.admin.first-page`, `list.admin.k100` | Dense access (σ = 1): cost follows k, not N |
| `list.admin.mid-cursor` | A page from the middle of the table costs the same as the first |
| `list.chain.first-page`, `list.region.first-page` | Narrower grants: rows examined ≈ k / σ, still independent of N |
| `list.deep-chain.first-page` | The same at D = 10 |
| `list.store.first-page` | Sparse access (σ ≈ 0.0065%): the scan examines k / σ ≈ 400K rows. Independent of N, but slow |
| `list.store.by-store` | The same person listing their store with `WHERE StoreId = …` on a `(StoreId, Id)` index: a normal page again |
| `point.function.*` | `fn_IsResourceAccessible` for one product at depth 4 and 9, and a denial that walks to the root |
| `point.api.*` | `Allows` (`CheckAccessAsync`), which resolves and explains the decision in several round trips |
| `density.*` | At the first scale only: the region page and the denied check re-run while 100 other people hold grants on the root. Only the caller's own grants should matter. Reported, not gated |

The sparse scan is independent of N and costs minutes, so it runs only at the first and last scales, which
are the two the scale gate compares.

## Gates (`gates.json`)

- **correctness**: every scenario returned exactly the authorized answer.
- **scale**: the paper's claim as a test. The median at the largest scale may be at most `maxRatio` (2.0)
  times the median at the smallest, plus `slackMilliseconds`. Both run on the same machine in the same job,
  so the ratio holds on shared runners. An O(N) plan regression misses it by orders of magnitude.
- **ceiling**: an absolute median budget per scenario and engine, for regressions that slow every scale
  alike. Set them to several times the values CI reports, and raise them only with an explanation.

## Run it

Docker is the only requirement; the harness starts the database with Testcontainers.

```bash
# PostgreSQL, 100K and 1M products (about a minute)
dotnet run --project tests/SqlOS.Benchmarks -c Release -- --provider postgresql

# SQL Server, as CI runs it
dotnet run --project tests/SqlOS.Benchmarks -c Release -- --provider sqlserver --scales 1m,10m,100m --data-dir /mnt/sqlos-bench

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

| Tier | When | Scales | Time per job |
|---|---|---|---|
| Pull request | Every pull request that touches `src/SqlOS` | 1M → 10M, without the sparse scan | about 5 min (PostgreSQL), 8 min (SQL Server) |
| Full | Every merge to `main`, weekly, on demand, and on a pull request labelled `benchmark-100m` | 1M → 10M → 100M, every scenario | about 30 min (PostgreSQL), 55 min (SQL Server) |

Most of the full run is loading: 90M new rows in each of two tables, then rebuilding indexes. PostgreSQL
loads in parallel `COPY` streams. SQL Server takes the table lock that minimal logging needs, so it has one
bulk stream per table. The 100M database is 37 GB on PostgreSQL and 45 GB on SQL Server (`nvarchar` doubles
the text). SQL Server's data, log, and tempdb files are capped below the disk's free space, so overflowing
fails the benchmark with a SQL error instead of taking down the runner.

The Markdown summary is on the run page, and the JSON results and plans are uploaded as artifacts.

## Results

From CI calibration runs (4 vCPU runner, warm cache, median ms). Current numbers are in each run's summary.

| Scenario | PostgreSQL 16, 1M → 100M | SQL Server 2022, 1M → 100M |
|---|---|---|
| Company admin, first page (k = 20) | 10.1 → 10.6 | 25.9 → 27.0 |
| Company admin, page from the middle | 10.1 → 10.6 | 25.6 → 28.9 |
| Chain manager, σ = 7.1% | 33 → 27 | 108 → 116 |
| Region manager, σ = 0.97% (1,592 rows examined) | 189 → 137 | 645 → 679 |
| Chain manager at D = 10 | 31 → 26 | 112 → 118 |
| Store manager, sparse, σ = 0.0065% (399,129 rows examined) | 31.8 s → 33.3 s | 160.7 s → 161.8 s |
| Store manager, filtered to the store | 10.5 → 9.7 | 27.5 → 26.6 |
| `fn_IsResourceAccessible`, one product | 6.6 → 6.0 | 2.6 → 2.7 |
| `Allows`, product at depth 9 | 37.7 → 34.4 | 56.0 → 55.8 |

Per-page cost does not grow with N on either engine across 100 times the data: every ratio is between ×0.72
and ×1.13. What sets the cost is the number of rows the scan examines, k / σ, times a per-row constant: about
90–110 µs of server execution on PostgreSQL and 410–540 µs on SQL Server.

## Findings

- **Sparse access is the real cost, as Theorem 3 predicts.** A manager of a median store who lists "every
  product I can see" examines about 400K rows per page: 33 s on PostgreSQL, 161 s on SQL Server, at every
  scale. Scoping the query by the store (`WHERE StoreId = …` on a `(StoreId, Id)` index) brings the same
  person back to 10 ms and 27 ms. Applications should filter by the scope key whenever the screen already
  knows it.
- **On PostgreSQL, a caller pays for other people's grants.** With 100 other users' grants added to the root,
  the region manager's page goes from 189 ms to 1,126 ms (×6.0) with the same rows examined. The plan probes
  grants by `ResourceId` alone, joins every grant it finds to the subject tables, and applies
  `SubjectId IN (jsonb_array_elements_text(…))` last. SQL Server seeks on `(ResourceId, SubjectId)` and is
  unaffected (×1.0). The model's per-row bound depends only on the caller's own grants.
- **On PostgreSQL, planning is half of a small page.** EF Core and Npgsql do not prepare statements by
  default, so PostgreSQL plans the inlined function body on every query: about 5.3 ms, against about 2 ms of
  execution for a 21-row page.
- **The shipped SQL Server function costs several times the paper's.** The paper reported 3.47 ms for a
  k = 20 page at D = 5. The same page through the shipped function takes 26 ms, and a single row about 400 µs.
  Since the paper, the function has gained subject-type validation, cycle detection over `NVARCHAR(MAX)`
  paths, a caller-validation `EXISTS`, and `OPENJSON` parsing, all evaluated per candidate row.
- **`Allows` costs several times the function.** `CheckAccessAsync` resolves and explains the decision in
  several round trips: 19–37 ms on PostgreSQL and 30–56 ms on SQL Server, against 2–7 ms for the function
  itself.
- **The resource table carries a duplicate index.** `IX_SqlOSFgaResources_ParentId` and
  `IX_SqlOSFgaResources_ParentId_Id` (`ParentId` with `Id` included) are the same index on SQL Server, where
  the clustered key is already carried. At 100M rows that is several gigabytes and an extra rebuild.
- **PostgreSQL chooses a different plan at 1M.** At 1M products, the chain and region pages run about 35%
  slower than at 10M and 100M. The captured plans show a different join order inside the inlined function
  at that size, so the scale gate is somewhat lenient for those two scenarios; their ceilings still apply.

## Loading

Each engine uses its bulk path, into the tables SqlOS created:

- **SQL Server**: ordered `SqlBulkCopy` with a table lock into the clustered primary keys (minimally logged
  under simple recovery).
- **PostgreSQL**: parallel `COPY … (FORMAT BINARY)` with foreign-key triggers deferred for the loading
  sessions.

Secondary indexes are set aside during a load and rebuilt from their own definitions, so no DDL is copied
into the harness. Foreign keys are revalidated (SQL Server), statistics are refreshed, and PostgreSQL tables
are vacuumed so the visibility map matches a table autovacuum maintains.

The PostgreSQL container turns off durability settings that only affect writes (`fsync`, WAL level,
synchronous commit). Planner settings are the usual SSD values.
