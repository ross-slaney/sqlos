using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SqlOS.Database;

namespace SqlOS.Fga;

/// <summary>
/// Plans each statement that reads a caller's visible resources for that caller, on SQL Server.
/// </summary>
/// <remarks>
/// SQL Server keeps one plan per statement text. When it compiles a statement that reads <c>fn_VisibleSet</c>,
/// it runs the function for whoever is asking at the time and plans around that list's length, and that plan
/// then serves every later caller of the same statement. The right plan depends on the length: a short list is
/// looked up row by row through the table's <c>ResourceId</c> index, a long one is read in the table's order.
/// A plan compiled for a long list reads the whole table for a caller with a short one. So every statement
/// that reads the list function carries <c>OPTION (RECOMPILE)</c>, SQL Server's own instruction to plan a
/// statement for each execution, and each caller gets the plan for their list. PostgreSQL plans the function
/// by its declared row count and needs nothing; statements that check each row (<c>fn_CheckRow</c>) have one
/// plan for everyone and need nothing on either engine.
/// </remarks>
public sealed class SqlOSFgaListStatementInterceptor : DbCommandInterceptor
{
    /// <summary>The one instance a context's options hold (see <c>UseSqlOS</c> on the options builder).</summary>
    public static SqlOSFgaListStatementInterceptor Instance { get; } = new();

    internal const string Hint = "OPTION (RECOMPILE)";

    internal const string ListFunction = "fn_VisibleSet";

    private SqlOSFgaListStatementInterceptor()
    {
    }

    public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ArgumentNullException.ThrowIfNull(result);
        var sqlServer = eventData.Context is { } context
            ? SqlOSDatabase.IsSqlServer(context.Database.ProviderName)
            : result is SqlCommand;
        result.CommandText = Apply(result.CommandText, sqlServer);
        return result;
    }

    /// <summary>The statement as SQL Server should run it: with the hint when it reads the list function, else as it was.</summary>
    internal static string Apply(string commandText, bool sqlServer)
    {
        if (!sqlServer
            || !commandText.Contains(ListFunction, StringComparison.Ordinal)
            || commandText.Contains(Hint, StringComparison.Ordinal))
        {
            return commandText;
        }

        var statement = commandText.TrimEnd();
        if (statement.EndsWith(';'))
        {
            statement = statement[..^1].TrimEnd();
        }

        return statement + Environment.NewLine + Hint;
    }
}
