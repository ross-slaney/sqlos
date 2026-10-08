using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlOS.Database;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Paging;

/// <summary>
/// The executor's backend on the application's connection: one prelude statement per page (the caller's live
/// subjects, the permission's roles and type), then one command per round holding the two statements the
/// provider builds, with the opens and the streams sent as JSON. Commands enlist in the context's current
/// transaction when there is one. On PostgreSQL the command is prepared, every lookup in it is an equality on
/// columns of its inputs so one generic plan serves every execution, and JIT is off for it: these are
/// sub-millisecond index seeks that a generic plan's cost estimate would otherwise send to the compiler.
/// </summary>
internal sealed class SqlOSFgaPageBackend<T>(
    DbContext context,
    ISqlOSDatabaseProvider provider,
    SqlOSFgaOptions options,
    SqlOSFgaPageQuery<T> query,
    string subjectIdsJson,
    string permissionId) : ISqlOSFgaPageBackend
    where T : class, IHasResourceId
{
    private static readonly JsonWriterOptions WriterOptions = new() { SkipValidation = true };

    private List<string> _live = [];
    private List<string> _roles = [];
    private int? _typeSeq;
    private long _root = -1;
    private string? _roundSql;
    private readonly List<(string Principal, string Role)> _direct = [];

    public long RootNode => _root;

    public IReadOnlyList<(string Principal, string Role)> DirectStreams => _direct;

    /// <summary>Principals whose grant counts had fallen behind the clock and were rebuilt before this page.</summary>
    public int CountsRebuilt { get; private set; }

    public async Task<bool> BeginAsync(CancellationToken cancellationToken)
    {
        var (connection, opened) = await OpenAsync(cancellationToken);
        try
        {
            List<string> stale;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = provider.BuildPagePreludeSql(options);
                Enlist(command);
                command.Parameters.Add(Parameter(command, "@SubjectIds", subjectIdsJson, DbType.String));
                command.Parameters.Add(Parameter(command, "@PermissionId", permissionId, DbType.String));
                command.Parameters.Add(Parameter(command, "@RootId", options.RootResourceId, DbType.String));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                _live = [];
                while (await reader.ReadAsync(cancellationToken))
                {
                    _live.Add(reader.GetString(0));
                }

                await reader.NextResultAsync(cancellationToken);
                _roles = [];
                while (await reader.ReadAsync(cancellationToken))
                {
                    _roles.Add(reader.GetString(0));
                }

                await reader.NextResultAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return false;
                }

                _typeSeq = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                _root = reader.IsDBNull(2) ? -1 : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);

                stale = [];
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    stale.Add(reader.GetString(0));
                }
            }

            // A grant's window opened or closed since these principals' counts were built: the counts say
            // where the walk may jump, so they are rebuilt first. The hosted refresh does this ahead of time;
            // the page does not depend on it.
            if (stale.Count > 0)
            {
                await using var rebuild = connection.CreateCommand();
                rebuild.CommandText = provider.BuildCountsRebuildSql(options);
                Enlist(rebuild);
                rebuild.Parameters.Add(Parameter(rebuild, "@Subjects", JsonSerializer.Serialize(stale), DbType.String));
                await rebuild.ExecuteNonQueryAsync(cancellationToken);
                CountsRebuilt = stale.Count;
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }

        _direct.Clear();
        foreach (var principal in _live)
        {
            foreach (var role in _roles)
            {
                _direct.Add((principal, role));
            }
        }

        return _live.Count > 0 && _roles.Count > 0 && _root >= 0;
    }

    public async Task<SqlOSFgaRound> RoundAsync(IReadOnlyList<SqlOSFgaOpenRequest> opens, IReadOnlyList<SqlOSFgaStream> streams, CancellationToken cancellationToken)
    {
        _roundSql ??= provider.BuildPageRoundSql(options, new SqlOSFgaPageSpec(
            query.Table,
            query.TableAlias,
            query.OrderColumns.Select(c => new SqlOSFgaScopeColumn(c.Column, c.Mapping.StoreType, c.Property.IsNullable)).ToList(),
            query.IndexSuffix,
            query.PredicateSql,
            _typeSeq is not null,
            query.Descending));

        var (connection, opened) = await OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            var ambient = Enlist(command);
            command.CommandText = provider.Kind == SqlOSDatabaseProviderKind.PostgreSql
                ? (ambient ? PostgreSqlSettings + _roundSql : "BEGIN;\n" + PostgreSqlSettings + _roundSql + "\nCOMMIT;")
                : _roundSql;
            if (context.Database.GetCommandTimeout() is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

            command.Parameters.Add(Parameter(command, "@Opens", Json(w => WriteOpens(w, opens)), DbType.String));
            command.Parameters.Add(Parameter(command, "@Streams", Json(w => WriteStreams(w, streams)), DbType.String));
            command.Parameters.Add(Parameter(command, "@Live", JsonSerializer.Serialize(_live), DbType.String));
            command.Parameters.Add(Parameter(command, "@Roles", JsonSerializer.Serialize(_roles), DbType.String));
            command.Parameters.Add(Parameter(command, "@Type", _typeSeq is { } seq ? SqlOSFgaScope.Bytes(seq) : DBNull.Value, DbType.Binary));
            command.Parameters.Add(Parameter(command, "@TypeSeq", _typeSeq.HasValue ? _typeSeq.Value : DBNull.Value, DbType.Int32));
            foreach (var p in query.PredicateParameters)
            {
                var clone = command.CreateParameter();
                clone.ParameterName = p.ParameterName;
                clone.Value = p.Value ?? DBNull.Value;
                clone.DbType = p.DbType;
                clone.Size = p.Size;
                clone.Precision = p.Precision;
                clone.Scale = p.Scale;
                command.Parameters.Add(clone);
            }

            if (provider.Kind == SqlOSDatabaseProviderKind.PostgreSql)
            {
                await command.PrepareAsync(cancellationToken);
            }

            var openedNodes = new List<SqlOSFgaOpenedNode>();
            var rows = new List<SqlOSFgaFetchedRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            // Skip the result-less statements (BEGIN, SET) to the first result set.
            while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken))
            {
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                openedNodes.Add(new SqlOSFgaOpenedNode(
                    RequestId: reader.GetInt32(0),
                    Node: reader.GetInt64(1),
                    Level: reader.GetInt32(2),
                    Active: Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Granted: Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture),
                    HasChildren: Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Threshold: Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                    CutGrants: Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                    Fetch: Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture)));
            }

            await reader.NextResultAsync(cancellationToken);
            var columns = query.OrderColumns.Count;
            while (await reader.ReadAsync(cancellationToken))
            {
                var position = new object?[columns];
                for (var i = 0; i < columns; i++)
                {
                    position[i] = reader.GetValue(8 + i);
                }

                rows.Add(new SqlOSFgaFetchedRow(
                    Kind: (SqlOSFgaStreamKind)reader.GetInt32(0),
                    Level: reader.GetInt32(1),
                    Node: reader.GetInt64(2),
                    Principal: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Role: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Ordinal: Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Count: Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                    Granted: Convert.ToBoolean(reader.GetValue(7), CultureInfo.InvariantCulture),
                    Position: position));
            }

            return new SqlOSFgaRound(openedNodes, rows, 2);
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private const string PostgreSqlSettings = "SET LOCAL plan_cache_mode = force_generic_plan;\nSET LOCAL jit = off;\n";

    private async Task<(DbConnection Connection, bool Opened)> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State == ConnectionState.Open)
        {
            return (connection, false);
        }

        await connection.OpenAsync(cancellationToken);
        return (connection, true);
    }

    /// <summary>Joins the context's transaction, when it has one; returns whether it did.</summary>
    private bool Enlist(DbCommand command)
    {
        if (context.Database.CurrentTransaction is { } transaction)
        {
            command.Transaction = transaction.GetDbTransaction();
            return true;
        }

        return false;
    }

    private static DbParameter Parameter(DbCommand command, string name, object value, DbType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        parameter.DbType = type;
        return parameter;
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            write(writer);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void WriteOpens(Utf8JsonWriter writer, IReadOnlyList<SqlOSFgaOpenRequest> opens)
    {
        foreach (var o in opens)
        {
            writer.WriteStartObject();
            writer.WriteNumber("req", o.Id);
            writer.WriteNumber("seq", o.Node);
            writer.WriteBoolean("children", o.Children);
            writer.WriteNumber("level", o.Level);
            writer.WriteBoolean("cut", o.CutMode);
            writer.WriteNumber("f", o.Fetch);
            WritePosition(writer, o.After);
            writer.WriteEndObject();
        }
    }

    private void WriteStreams(Utf8JsonWriter writer, IReadOnlyList<SqlOSFgaStream> streams)
    {
        foreach (var s in streams)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sid", s.Id);
            writer.WriteNumber("kind", (int)s.Kind);
            writer.WriteNumber("level", s.Level);
            writer.WriteNumber("seq", s.Node);
            if (s.Principal is null) { writer.WriteNull("principal"); } else { writer.WriteString("principal", s.Principal); }
            if (s.Role is null) { writer.WriteNull("role"); } else { writer.WriteString("role", s.Role); }
            writer.WriteNumber("f", s.Fetch);
            WritePosition(writer, s.After);
            writer.WriteEndObject();
        }
    }

    private void WritePosition(Utf8JsonWriter writer, object?[]? position)
    {
        writer.WriteBoolean("has_after", position is not null);
        for (var i = 0; i < query.OrderColumns.Count; i++)
        {
            var name = "a" + i.ToString(CultureInfo.InvariantCulture);
            if (position is null)
            {
                writer.WriteNull(name);
                continue;
            }

            WriteValue(writer, name, position[i]);
        }
    }

    /// <summary>A position value in the JSON the engine types by the column's store type.</summary>
    private void WriteValue(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                writer.WriteNull(name);
                break;
            case int i:
                writer.WriteNumber(name, i);
                break;
            case long l:
                writer.WriteNumber(name, l);
                break;
            case short s:
                writer.WriteNumber(name, s);
                break;
            case byte b:
                writer.WriteNumber(name, b);
                break;
            case decimal d:
                writer.WriteNumber(name, d);
                break;
            case double d:
                writer.WriteNumber(name, d);
                break;
            case float f:
                writer.WriteNumber(name, f);
                break;
            case bool b:
                writer.WriteBoolean(name, b);
                break;
            case string s:
                writer.WriteString(name, s);
                break;
            case Guid g:
                writer.WriteString(name, g.ToString("D"));
                break;
            case DateTime dt:
                writer.WriteString(name, dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                writer.WriteString(name, dto.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));
                break;
            case DateOnly d:
                writer.WriteString(name, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimeOnly t:
                writer.WriteString(name, t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                break;
            case byte[] bytes:
                writer.WriteString(name, provider.Kind == SqlOSDatabaseProviderKind.PostgreSql ? "\\x" + Convert.ToHexString(bytes) : Convert.ToBase64String(bytes));
                break;
            default:
                writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
