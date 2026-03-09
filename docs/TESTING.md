# Testing

The repo now has one shared test tree:

- `tests/SqlOS.Tests`
- `tests/SqlOS.IntegrationTests`
- `tests/SqlOS.IntegrationTests.AppHost`
- `examples/SqlOS.Example.Tests`
- `examples/SqlOS.Example.IntegrationTests`
- `tests/SqlOS.Benchmarks`

## Run Everything

```bash
dotnet test SqlOS.sln
```

## Real SQL Coverage

The integration suites use Aspire plus a real SQL Server container.

That covers:
- auth schema bootstrap
- FGA schema bootstrap and TVF registration
- auth flows
- FGA checks and query composition
- shared example API and web flows

## Coverage Settings

Coverage filters live in `tests/coverlet.runsettings`.
