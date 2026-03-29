# Testing

The repo now has one shared test tree:

- `tests/SqlOS.Tests`
- `tests/SqlOS.IntegrationTests`
- `tests/SqlOS.IntegrationTests.AppHost`
- `examples/SqlOS.Example.Tests`
- `examples/SqlOS.Example.IntegrationTests`
- `examples/SqlOS.Todo.IntegrationTests`
- `tests/SqlOS.Benchmarks`

## Run Everything

```bash
dotnet build SqlOS.sln
./scripts/unit-tests.sh
./scripts/integration-tests.sh
./scripts/docs-check.sh
```

## Real SQL Coverage

Integration tests use Aspire and a real SQL Server container.

They cover:
- auth schema bootstrap
- FGA schema bootstrap and TVF registration
- auth flows
- client registration and resource binding
- FGA checks and query composition
- shared example API and web flows
- Todo sample hosted/headless/prereg/CIMD/DCR flows

## Docs Checks

`./scripts/docs-check.sh` runs:

- website lint
- website production build
- local markdown and MDX link validation across repo docs

## Coverage Settings

Coverage filters live in `tests/coverlet.runsettings`.
