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
project, or the workflow changes. Standard GitHub-hosted runners are free for public repositories.

Database files go on the runner's temporary disk (`/mnt`), since the OS disk is too small at 100M. The
Markdown summary is on the run page, and the JSON results and plans are uploaded as artifacts.

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
