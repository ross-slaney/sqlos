using System.Data.Common;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SqlOS.IntegrationTests.Fga.Infrastructure;

/// <summary>
/// SQL Server only: how many rows of one table the next query read, from its actual plan. Armed for one
/// query, it runs the exact command EF built, hint included, once more under <c>SET STATISTICS XML ON</c> and
/// adds up the rows every operator over the table produced.
/// </summary>
internal sealed class RowsReadCapture(string table) : DbCommandInterceptor
{
    private static readonly XNamespace Plan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private readonly string _object = $"[{table}]";
    private bool _armed;
    private long? _rows;

    /// <summary>Runs <paramref name="query"/> and returns the rows read from the table by its statement.</summary>
    public async Task<long> MeasureAsync(Func<Task> query)
    {
        _armed = true;
        _rows = null;
        try
        {
            await query();
        }
        finally
        {
            _armed = false;
        }

        return _rows ?? throw new InvalidOperationException($"No statement over {_object} ran.");
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (!_armed || _rows is not null || !command.CommandText.Contains(_object, StringComparison.Ordinal))
        {
            return result;
        }

        await using var explain = command.Connection!.CreateCommand();
        explain.Transaction = command.Transaction;
        explain.CommandText = "SET STATISTICS XML ON;\n" + command.CommandText + "\nSET STATISTICS XML OFF;";
        foreach (DbParameter parameter in command.Parameters)
        {
            explain.Parameters.Add(((ICloneable)parameter).Clone());
        }

        long rows = 0;
        await using (var reader = await explain.ExecuteReaderAsync(cancellationToken))
        {
            do
            {
                var isPlan = reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (isPlan)
                    {
                        rows += RowsRead(reader.GetString(0));
                    }
                }
            }
            while (await reader.NextResultAsync(cancellationToken));
        }

        _rows = rows;
        return result;
    }

    private long RowsRead(string planXml)
    {
        long rows = 0;
        foreach (var relOp in XDocument.Parse(planXml).Descendants(Plan + "RelOp"))
        {
            var overTable = relOp.Elements()
                .SelectMany(op => op.Elements(Plan + "Object"))
                .Any(o => string.Equals((string?)o.Attribute("Table"), _object, StringComparison.OrdinalIgnoreCase));
            if (!overTable)
            {
                continue;
            }

            rows += relOp.Element(Plan + "RunTimeInformation")?.Elements(Plan + "RunTimeCountersPerThread")
                .Sum(t => long.Parse((string?)t.Attribute("ActualRows") ?? "0", CultureInfo.InvariantCulture)) ?? 0;
        }

        return rows;
    }
}
