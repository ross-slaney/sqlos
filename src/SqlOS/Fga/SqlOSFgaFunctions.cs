using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Models;

namespace SqlOS.Fga;

/// <summary>
/// SqlOS's table-valued functions as EF Core composes them into an application's queries. Each is mapped to a
/// static method here, not to a method on the application's DbContext, so a filter that calls one carries no
/// DbContext instance: it composes into a query on any context whose model includes SqlOS, including one made
/// by a factory or a pool, and applications declare nothing for it. The methods are translated, never run.
/// </summary>
internal static class SqlOSFgaFunctions
{
    internal static readonly MethodInfo ActiveSubjectsMethod = typeof(SqlOSFgaFunctions)
        .GetMethod(nameof(ActiveSubjects), BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo VisibleSetMethod = typeof(SqlOSFgaFunctions)
        .GetMethod(nameof(VisibleSet), BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo IsResourceAccessibleMethod = typeof(SqlOSFgaFunctions)
        .GetMethod(nameof(IsResourceAccessible), BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>
    /// <c>fn_ActiveSubjects(@SubjectIds)</c>: the caller's subjects that are alive now, none unless the caller
    /// (the first id) is.
    /// </summary>
    internal static IQueryable<SqlOSFgaActiveSubject> ActiveSubjects(string subjectIds)
        => throw new InvalidOperationException("This method is translated to SQL by SqlOS and cannot be called directly.");

    /// <summary>
    /// <c>fn_VisibleSet(@SubjectIds, @PermissionId, @TypeId)</c>: the resources the caller's live subjects may
    /// see with the permission, listed from the caller's roots and materialized. The filter for a caller who sees
    /// few rows: the query starts from these rows.
    /// </summary>
    internal static IQueryable<SqlOSFgaVisibleResource> VisibleSet(string subjectIds, string permissionId, string? typeId)
        => throw new InvalidOperationException("This method is translated to SQL by SqlOS and cannot be called directly.");

    /// <summary>
    /// <c>fn_IsResourceAccessible(@ResourceId, @SubjectIds, @PermissionId)</c>: the point check, the grant that
    /// decides whether the caller may use the permission on the resource. The filter for a caller who sees many
    /// rows: each row the query reads, in its order, is checked.
    /// </summary>
    internal static IQueryable<SqlOSFgaAccessMatch> IsResourceAccessible(string resourceId, string subjectIds, string permissionId)
        => throw new InvalidOperationException("This method is translated to SQL by SqlOS and cannot be called directly.");

    /// <summary>Maps the functions in the model, in the schema SqlOS creates them in.</summary>
    public static void Register(ModelBuilder modelBuilder, string schema)
    {
        modelBuilder.HasDbFunction(ActiveSubjectsMethod).HasName("fn_ActiveSubjects").HasSchema(schema);
        modelBuilder.HasDbFunction(VisibleSetMethod).HasName("fn_VisibleSet").HasSchema(schema);
        modelBuilder.HasDbFunction(IsResourceAccessibleMethod).HasName("fn_IsResourceAccessible").HasSchema(schema);
    }
}
