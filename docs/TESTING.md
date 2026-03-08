# Testing

## Unit Tests (InMemory)

For unit tests with EF Core InMemory provider, use the overload **without** `GetType()` to skip TVF registration (TVFs require real SQL Server):

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplySqlzibarModel(); // No GetType() — skips TVF registration
}
```

## Integration Tests (Real SQL Server)

Sqlzibar's integration tests use .NET Aspire to spin up a real SQL Server container. The `TestSqlzibarDbContext` uses the full registration including TVF:

```csharp
public class TestSqlzibarDbContext : DbContext, ISqlzibarDbContext
{
    public IQueryable<SqlzibarAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlzibarModel(GetType()); // With TVF registration
    }
}
```

## Running Tests

**Docker must be running** for integration tests. Aspire spins up a SQL Server Linux container automatically — no manual setup required.

```bash
# Unit tests (no Docker needed)
dotnet test tests/Sqlzibar.Tests/

# Integration tests (requires Docker)
dotnet test tests/Sqlzibar.IntegrationTests/

# Example project unit tests (no Docker needed)
dotnet test examples/Sqlzibar.Example.Tests/

# Example project integration tests (requires Docker)
dotnet test examples/Sqlzibar.Example.IntegrationTests/
```
