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

    /// <summary>
    /// <c>fn_ActiveSubjects(@SubjectIds)</c>: the caller's subjects that are alive now, none unless the caller
    /// (the first id) is. The list filter checks it once per query, so a caller deactivated after the filter
    /// was built sees nothing.
    /// </summary>
    internal static IQueryable<SqlOSFgaActiveSubject> ActiveSubjects(string subjectIds)
        => throw new InvalidOperationException("This method is translated to SQL by SqlOS and cannot be called directly.");

    /// <summary>Maps the functions in the model, in the schema SqlOS creates them in.</summary>
    public static void Register(ModelBuilder modelBuilder, string schema)
    {
        modelBuilder.HasDbFunction(ActiveSubjectsMethod).HasName("fn_ActiveSubjects").HasSchema(schema);
    }
}
