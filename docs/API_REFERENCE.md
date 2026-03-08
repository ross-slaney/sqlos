# API Reference

## ISqlzibarAuthService

```csharp
// Check if subject has permission on a resource (hierarchy walk)
Task<SqlzibarAccessCheckResult> CheckAccessAsync(
    string subjectId, string permissionKey, string resourceId);

// Check if subject has permission at root level
Task<bool> HasCapabilityAsync(string subjectId, string permissionKey);

// Get LINQ filter expression for authorized entities
Task<Expression<Func<T, bool>>> GetAuthorizationFilterAsync<T>(
    string subjectId, string permissionKey) where T : IHasResourceId;

// Detailed access trace for diagnostics
Task<SqlzibarResourceAccessTrace> TraceResourceAccessAsync(
    string subjectId, string resourceId, string permissionKey);
```

## ISqlzibarSubjectService

```csharp
Task<SqlzibarSubject> CreateSubjectAsync(
    string displayName, string subjectTypeId,
    string? organizationId = null, string? externalRef = null,
    CancellationToken cancellationToken = default);

Task<SqlzibarUserGroup> CreateGroupAsync(
    string name, string? description = null, string? groupType = null,
    CancellationToken cancellationToken = default);

Task AddToGroupAsync(string subjectId, string userGroupId, CancellationToken ct = default);
Task RemoveFromGroupAsync(string subjectId, string userGroupId, CancellationToken ct = default);
Task<List<string>> ResolveSubjectIdsAsync(string subjectId, CancellationToken ct = default);
Task<List<SqlzibarUserGroup>> GetGroupsForSubjectAsync(string subjectId, CancellationToken ct = default);
```

## ISpecificationExecutor

```csharp
// Cursor-paginated, authorized query
Task<PaginatedResult<TDto>> ExecuteAsync<TEntity, TDto>(
    DbSet<TEntity> dbSet,
    PagedSpecification<TEntity> specification,
    string subjectId,
    Func<TEntity, TDto> selector,
    CancellationToken cancellationToken = default)
    where TEntity : class, IHasResourceId;

// Total count of matching entities (WARNING: runs COUNT(*) — can be slow at scale)
Task<long> CountAsync<TEntity>(
    DbSet<TEntity> dbSet,
    PagedSpecification<TEntity> specification,
    string subjectId,
    CancellationToken cancellationToken = default)
    where TEntity : class, IHasResourceId;
```

## PagedSpec Builder

The fluent builder creates specifications inline without dedicated specification classes:

```csharp
PagedSpec.For<T>(idSelector)
    .RequirePermission("PERMISSION_KEY")
    .SortByString("name", x => x.Name, isDefault: true)
    .SortBy("price", x => x.Price, v => v.ToString(), s => decimal.Parse(s))
    .Search(search, x => x.Name, x => x.Description)
    .Where(x => x.IsActive)
    .Configure(q => q.Include(x => x.Related))
    .Build(pageSize, cursor, sortBy, sortDir);
```

## SortablePagedSpecification&lt;T&gt;

Abstract base class for reusable specifications with declarative sort registration:

```csharp
public class GetItemsSpec : SortablePagedSpecification<Item>
{
    public GetItemsSpec(int pageSize, string? search = null)
    {
        PageSize = pageSize;
        RegisterStringSort("name", i => i.Name, isDefault: true);
        RegisterSort("price", i => i.Price,
            serialize: v => v.ToString(CultureInfo.InvariantCulture),
            deserialize: s => decimal.Parse(s, CultureInfo.InvariantCulture));
        Search(search, i => i.Name, i => i.Sku);
    }

    public override string? RequiredPermission => "ITEM_VIEW";
    protected override Expression<Func<Item, string>> IdSelector => i => i.Id;
}
```

## Convenience Extensions

### CreateResource

```csharp
// Creates a SqlzibarResource and adds it to the context (not yet saved)
string CreateResource(
    this ISqlzibarDbContext context,
    string parentId,
    string name,
    string resourceTypeId,
    string? id = null);
```

### AuthorizedDetailAsync

```csharp
// Fetches entity, checks access, returns 404/403/200 automatically
Task<IResult> AuthorizedDetailAsync<TEntity, TDto>(
    this ISqlzibarAuthService authService,
    IQueryable<TEntity> query,
    Expression<Func<TEntity, bool>> predicate,
    string subjectId,
    string permissionKey,
    Func<TEntity, TDto> selector)
    where TEntity : class, IHasResourceId;
```
