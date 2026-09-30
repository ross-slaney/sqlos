# Paper benchmark harness

This is the harness that produced the tables in `paper/shrbac-compsac-2026.md` (Section 7): SQL Server 2022,
1.2M resources at D = 5 and 1.5M resources at D = 10.

It is kept verbatim so the published numbers stay reproducible. It creates its own copy of the schema and
the TVF as they were when the paper was written (principal tables, `STRING_SPLIT`), so it does not exercise
the schema, indexes, or `fn_IsResourceAccessible` that SqlOS ships today, and it is not part of `SqlOS.sln`.

The maintained suite, which runs the shipped schema and function on SQL Server and PostgreSQL up to 100M
resources and gates CI on regressions, is `tests/SqlOS.Benchmarks`.

```bash
dotnet run --project paper/benchmark -c Release -- "Server=localhost,1433;Database=SqlOSFga_Benchmark;User Id=sa;Password=...;TrustServerCertificate=True;"
```
