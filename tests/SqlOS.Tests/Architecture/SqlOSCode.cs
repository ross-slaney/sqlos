using Microsoft.EntityFrameworkCore;
using Mono.Cecil;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Architecture;

/// <summary>Which SqlOS code each architecture rule applies to.</summary>
internal static class SqlOSCode
{
    private static readonly Lazy<IReadOnlyList<Type>> LazyEntities = new(LoadEntities);
    private static readonly Lazy<IReadOnlySet<string>> LazyEntityNames = new(
        () => Entities.Select(type => type.FullName!).ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// SqlOS's entities: every type the SqlOS EF model maps with a key. Keyless query results
    /// (the FGA table-valued function rows) are read models, not persisted state.
    /// </summary>
    public static IReadOnlyList<Type> Entities => LazyEntities.Value;

    public static IReadOnlySet<string> EntityNames => LazyEntityNames.Value;

    /// <summary>Domain code: the building blocks, policies and entities. No clock, no HTTP.</summary>
    public static bool IsDomain(TypeDefinition type)
        => IsDomainNamespace(type.Namespace) || EntityNames.Contains(IlScanner.TypeName(type));

    /// <summary><c>SqlOS.Domain</c> and its namespaces, and every <c>*.Policies</c> namespace.</summary>
    public static bool IsDomainNamespace(string @namespace)
        => HasSegment(@namespace, "Domain") || HasSegment(@namespace, "Policies");

    /// <summary>Process code: every <c>*.Processes.*</c> namespace.</summary>
    public static bool IsProcess(TypeDefinition type) => HasSegment(type.Namespace, "Processes");

    /// <summary>
    /// Adapters: endpoints, dashboard middleware and renderers, which parse transport, call a
    /// process and map its outcome, and never touch the database.
    /// </summary>
    public static bool IsAdapter(TypeDefinition type)
        => HasSegment(type.Namespace, "Endpoints")
            || HasSegment(type.Namespace, "Dashboard")
            || type.Name.EndsWith("Endpoints", StringComparison.Ordinal)
            || type.Name.EndsWith("EndpointRouteBuilderExtensions", StringComparison.Ordinal)
            || type.Name.EndsWith("Middleware", StringComparison.Ordinal)
            || type.Name.EndsWith("Renderer", StringComparison.Ordinal);

    public static bool HasSegment(string @namespace, string segment)
        => @namespace.Split('.').Contains(segment, StringComparer.Ordinal);

    private static IReadOnlyList<Type> LoadEntities()
    {
        using var context = new TestSqlOSInMemoryDbContext(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(nameof(SqlOSCode))
            .Options);
        var sqlos = typeof(SqlOS.Configuration.SqlOSOptions).Assembly;
        return context.Model.GetEntityTypes()
            .Where(entity => entity.ClrType.Assembly == sqlos && entity.FindPrimaryKey() is not null)
            .Select(entity => entity.ClrType)
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();
    }
}
