# SqlOS SHRBAC benchmarks

Measures authorization as SqlOS ships it, on SQL Server and PostgreSQL, while the catalog grows from 1M to
100M products, and fails CI when that behavior regresses.

It is the maintained successor to the harness behind the paper's Section 7 (kept in `paper/benchmark`),
which ran a hand-copied version of the schema and function at 1.2M–1.5M resources on SQL Server only.

## What is measured

- **The shipped artifacts.** The FGA schema scripts, their indexes, `fn_AccessRoots`,
  `fn_IsResourceAccessible`, the resource closure with its triggers, and the core seed are created by
  SqlOS's own initializers (`SqlOSFgaSchemaInitializer`, `SqlOSFgaFunctionInitializer`,
  `SqlOSFgaSeedService`). A change to any of them is what gets measured.
- **The application's queries.** Row-filter pages go through `BuildFilterAsync<Product>` and EF Core, so the
  timing includes the SQL EF generates for callers. Closure pages go through `ListVisibleAsync<Product>`.
  Point checks are measured twice: the function itself, and `Allows` (`CheckAccessAsync`), which
  applications call.
- **The previous release's function runs beside the current one.** `Reference/*.sql` holds the row filter as
  the last release shipped it, created as `fn_IsResourceAccessible_Reference`; every row-filter and
  point-function scenario has a twin through it, on the same data in the same job.
- **What is timed.** For a row-filter page, the authorized query alone (the filter is built before the
  clock starts, as the paper measured). For a closure page, the whole `ListVisibleAsync` call: subject
  resolution, the permission, and the one statement that reads the page and its entity rows. Warm cache;
  median and p95 over up to 25 runs.
- **Every answer is checked against ground truth.** The exact page (k + 1 rows after the cursor for the row
  filter, k rows and the cursor for the closure page) and every allow or deny are recomputed from the dataset
  generator, so a fast wrong answer fails the run.
- **The actual plan is captured** for each page scenario (`EXPLAIN (ANALYZE, BUFFERS)` or
  `SET STATISTICS XML`). From it the report takes rows examined (product rows for a row-filter page, closure
  entries for a closure page), server execution time, and server planning time. The plans are uploaded with
  the results.
- **The closure is verified.** At the first scale, the closure the loader generated is compared (count and an
  order-independent hash) with the closure after the maintenance pass and with the closure SqlOS rebuilds
  from scratch with its own procedure.

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
- **Every product is a resource**, as with `ISqlOSResourceEntity`, and `Products.ResourceId` carries the
  unique index such a table should have: the row filter never uses it, the closure page joins through it. At
  100M products that is about 100.07M resource nodes, 83 times the paper's 1.2M, and about 450M closure rows
  (one per ancestor of each resource).
- **Five people, M = 3 each** (the user and two groups): a company admin granted through a group on the
  root, a chain manager, a region manager, a manager of the deep chain, and the manager of a median-sized
  store.

The data is a pure function of the seed, so it grows in place (1M → 10M → 100M) and any page can be verified.
The loaders write resource sequence numbers and closure rows themselves, in clustered-key order; the first
scale checks that closure against SqlOS's rebuild.

## Scenarios

| Id | What it shows |
|---|---|
| `list.admin.first-page`, `list.admin.k100` | Dense access (σ = 1): cost follows k, not N |
| `list.admin.mid-cursor` | A page from the middle of the table costs the same as the first |
| `list.chain.first-page`, `list.region.first-page` | Narrower grants: rows examined ≈ k / σ, still independent of N |
| `list.deep-chain.first-page` | The same at D = 10 |
| `list.store.first-page` | Sparse access (σ ≈ 0.0065%): the row filter examines k / σ ≈ 400K rows. Independent of N, but slow |
| `list.store.by-store` | The same person listing their store with `WHERE StoreId = …` on a `(StoreId, Id)` index: a normal page again |
| `reference.list.*`, `reference.point.function.*` | The same page or check through the previous release's function |
| `visible.list.*` | The same page through `ListVisibleAsync`: at most k closure entries per grant, at any σ |
| `point.function.*` | `fn_IsResourceAccessible` for one product at depth 4 and 9, and a denial that walks to the root |
| `point.api.*` | `Allows` (`CheckAccessAsync`), which resolves and explains the decision in several round trips |
| `density.*` | At the first scale only: the region pages and the denied check re-run while 100 other people hold grants on the root. Only the caller's own grants should matter. Reported, not gated |
| maintenance | At the first scale only: 2,000 single-row inserts with the closure triggers on and off, one 2,000-row insert, one 2,000-row delete, and reparent, deactivate and reactivate of a region subtree; then a rebuild from scratch, compared with the maintained closure |

The sparse row-filter scans are independent of N and cost minutes, so they run only at the first and last
scales, which are the two the scale gate compares.

## Gates (`gates.json`)

- **correctness**: every scenario returned exactly the authorized answer.
- **closure**: the loaded closure, the closure after the maintenance pass, and SqlOS's rebuild are the same
  rows (count and hash).
- **scale**: the paper's claim as a test. The median at the largest scale may be at most `maxRatio` (2.0)
  times the median at the smallest, plus `slackMilliseconds`. Both run on the same machine in the same job,
  so the ratio holds on shared runners. An O(N) plan regression misses it by orders of magnitude.
- **regression**: the current function against the previous release's, at every scale: at most `maxRatio`
  (1.15) times the previous median, plus slack.
- **improvement**: for the sparse page, the closure page must take at most `maxRatio` (0.05) of the
  row-filter page's time.
- **ceiling**: an absolute median budget per scenario and engine, for regressions that slow every scale
  alike. Set them to several times the values CI reports, and raise them only with an explanation.

## Run it

Docker is the only requirement; the harness starts the database with Testcontainers.

```bash
# PostgreSQL, 100K and 1M products (a few minutes)
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
| Pull request | Every pull request that touches `src/SqlOS` | 1M → 10M, without the sparse row-filter scans | under 10 min per engine |
| Full | Every merge to `main`, weekly, on demand, and on a pull request labelled `benchmark-100m` | 1M → 10M → 100M, every scenario | about an hour (PostgreSQL), two (SQL Server) |

Most of the full run is loading: 90M new rows in each of two tables and 400M closure rows, then rebuilding
indexes. PostgreSQL loads in parallel `COPY` streams. SQL Server takes the table lock that minimal logging
needs, so it has one bulk stream per table. SQL Server's data, log, and tempdb files are capped below the
disk's free space, so overflowing fails the benchmark with a SQL error instead of taking down the runner.

The Markdown summary is on the run page, and the JSON results and plans are uploaded as artifacts.

## Results

From the PR-tier CI runs of this harness (4 vCPU runners, warm cache, median ms, 1M → 10M), with the previous
release's function measured beside the current one in the same job. The PostgreSQL run was on an AMD EPYC
9V74, the SQL Server run on an Intel Xeon Platinum 8573C; current numbers are in each run's summary.

| Scenario | PostgreSQL 16: current · previous · `ListVisibleAsync` | SQL Server 2022: current · previous · `ListVisibleAsync` |
|---|---|---|
| Company admin, first page (k = 20) | 6.4 → 7.9 · 9.4 → 10.4 · 5.4 → 6.5 | 15.8 → 16.2 · 16.6 → 17.3 · 6.4 → 6.3 |
| Company admin, first page (k = 100) | 9.4 → 11.5 · 17.4 → 19.8 · 6.2 → 7.4 | 59.9 → 61.5 · 75.9 → 76.5 · 6.2 → 6.9 |
| Chain manager, σ = 7.1% | 13.7 → 16.9 · 29.4 → 32.4 · 5.4 → 6.3 | 54.0 → 55.0 · 72.4 → 75.0 · 5.8 → 6.2 |
| Region manager, σ = 0.97% (1,592 rows examined) | 64 → 72 · 166 → 174 · 5.4 → 5.6 | 298 → 299 · 434 → 441 · 5.9 → 6.3 |
| Chain manager at D = 10 | 13.2 → 14.2 · 27.9 → 30.7 · 5.5 → 6.4 | 59.4 → 59.0 · 78.7 → 77.8 · 6.0 → 6.6 |
| Store manager, sparse, σ = 0.0065% | full tier only · 33 s at every scale (#446) · 5.5 → 6.3 | full tier only · 161 s at every scale (#446) · 6.1 → 6.4 |
| Store manager, filtered to the store | 6.5 → 7.6 · 8.3 → 9.1 · – | 16.0 → 16.0 · 17.0 → 17.7 · – |
| `fn_IsResourceAccessible` at 10M, one product (depth 4 / 9 / denied) | 2.8 / 2.8 / 2.7 · 7.6 / 7.4 / 7.3 · – | 4.1 / 4.5 / 3.1 · 2.8 / 3.3 / 2.0 · – |
| `Allows`, product at depth 9 | 44.9 → 45.8 | 54.8 → 54.2 |

Three things to read off the table:

- **The row filter did not get slower.** On every list page the current function is at or below the previous
  release's (at 10M: PostgreSQL ×0.41–0.83, SQL Server ×0.68–0.94). Point checks on PostgreSQL went from
  about 7.4 ms to 2.8 ms; on SQL Server they cost about 1 ms more (the root set is built once into a spool,
  which a single-row check does not amortize).
- **The closure page is flat in both N and σ.** Every principal, both engines, both scales: 5–7 ms and 20
  closure entries read, including the store manager whose row-filter page took 33 s and 161 s with the
  previous release's function.
- **Per-page cost does not grow with N** for either path: the 10M ÷ 1M ratios are ×0.99–1.26, within the
  scale gate.

Hosted runners come from a mixed pool of CPU models, and the engines respond to it differently; identical
SQL Server plans have run 2.5 times apart on different CPUs, and PostgreSQL about 1.3 times. The report
records the CPU model, and every gate compares within one job on one machine.

## Findings

- **Sparse access is the real cost of the row filter, as Theorem 3 predicts.** A manager of a median store
  who lists "every product I can see" examines about 400K rows per page; with the previous release's
  function that took 33 s on PostgreSQL and 161 s on SQL Server, at every scale (#446). The current function
  examines the same rows at a lower per-row cost; the full tier reports it. Scoping the query by the store
  (`WHERE StoreId = …` on a `(StoreId, Id)` index) brings the same person back to a normal page.
- **The closure page removes σ from the bound.** The same page through `ListVisibleAsync` reads 20 closure
  entries (one index range for the manager's one grant) and 20 resource rows, at every scale; see
  `paper/closure-list-filtering.md` for the proof and the `improvement` gate for the measurement.
- **The closure costs storage, PostgreSQL more than SQL Server.** At 10M products it holds 45.5M rows (about
  4.5 per product: one per ancestor). With the sequence-number indexes and the `Products.ResourceId` index
  the closure page needs, the database grew from 3.8 GB to 10.1 GB on PostgreSQL and from 4.5 GB to 7.3 GB
  on SQL Server. The rows are 20 bytes of keys; PostgreSQL adds a 24-byte header to every heap row and
  keeps the primary key as a separate index, where SQL Server stores the clustered key once.
- **`fn_AccessRoots` moves the grant conditions out of the per-row walk.** The current row filter examines
  the same rows as the previous release's and evaluates the caller's grants once per query instead of once
  per ancestor per row; the `regression` gate compares the two on every run.
- **The row filter must not read the caller's grants up front.** An earlier revision of this PR built the
  caller's whole grant set once per query and tested each ancestor against it. With one grant per person,
  every gate passed; a user with 100,000 grants would have read all of them on every check, and
  PostgreSQL's `= ANY(array)` scans the array per probe (100,000 grants × 10,000 probes: 4.2 s against
  14 ms hashed). The filter now hoists only the caller's live subjects and looks up each ancestor's grants
  by index; the `grants10k` and `grants100k` people check it. Their point checks cost the same as everyone
  else's (about 2 ms on PostgreSQL, against 7 ms for the previous function).
- **The closure page's cost grows with grants; the row filter's with sparsity.** For the 100,000-grant
  person (σ = 1%) the row-filter page takes 15 ms and the closure page 1.5 s, because the page reads every
  grant. For the store manager (one grant, σ = 0.0065%) it is the other way round. Which list to call is a
  property of the caller's grants, not of the data size.
- **On PostgreSQL the roots must be read from the grant side.** At 10M, an inlined `fn_AccessRoots` was
  planned as a merge join along the resource index (60K rows read per call); it now materializes the
  caller's grants first and looks their resources up by key.
- **On SQL Server the point check costs about 1 ms more than before** (3.6 ms against 2.6 ms at 10M): for a
  single row, the inline roots join costs a little more than the old per-ancestor grant probe. List pages
  are ×0.68–0.94 of the previous function's, and the closure page is 6 ms for every principal.
- **On PostgreSQL, a caller used to pay for other people's grants.** With 100 other users' grants added to
  the root, the previous function's region page went from 189 ms to 1,126 ms (×6.0) with the same rows
  examined: the plan probed grants by `ResourceId` alone and applied the subject filter last. The density
  pass reports the current function's ratio.
- **On PostgreSQL, planning is half of a small page.** EF Core and Npgsql do not prepare statements by
  default, so PostgreSQL plans the inlined function body on every query: about 5 ms, against about 2 ms of
  execution for a 21-row page.
- **`Allows` costs several times the function.** `CheckAccessAsync` resolves and explains the decision in
  several round trips: 19–37 ms on PostgreSQL and 30–56 ms on SQL Server, against 2–7 ms for the function
  itself.
- **The resource table carries a duplicate index.** `IX_SqlOSFgaResources_ParentId` and
  `IX_SqlOSFgaResources_ParentId_Id` (`ParentId` with `Id` included) are the same index on SQL Server, where
  the clustered key is already carried. At 100M rows that is several gigabytes and an extra rebuild.

## Loading

Each engine uses its bulk path, into the tables SqlOS created:

- **SQL Server**: ordered `SqlBulkCopy` with a table lock into the clustered primary keys (minimally logged
  under simple recovery). The closure is copied one ancestor position at a time, each pass in key order.
- **PostgreSQL**: parallel `COPY … (FORMAT BINARY)` with foreign-key and closure triggers skipped for the
  loading sessions (`session_replication_role = replica`; the harness supplies the closure rows itself).

Secondary indexes are set aside during a load and rebuilt from their own definitions, so no DDL is copied
into the harness. Foreign keys are revalidated (SQL Server), statistics are refreshed, PostgreSQL tables
are vacuumed so the visibility map matches a table autovacuum maintains, and the resource sequence is moved
past the loaded numbers so the maintenance pass inserts like an application would.

The PostgreSQL container turns off durability settings that only affect writes (`fsync`, WAL level,
synchronous commit). Planner settings are the usual SSD values.
