using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using SqlOS.BehaviorLock.Host;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>
/// Counts the commands sent to one database and the time they took, at the ADO.NET provider:
/// Microsoft.Data.SqlClient's diagnostic events on SQL Server, Npgsql's activities on PostgreSQL.
/// Counting at the provider rather than with an EF Core interceptor also sees the commands SqlOS
/// runs on the context's raw connection (the distributed rate-limit store). Commands to other
/// databases, such as the Aspire health checks against the server's default database, are ignored.
/// Durations run from when the command is sent until its first results are back (SqlClient) or its
/// reader is closed (Npgsql), so compare them only within one provider. While recording, it also
/// keeps each command's text, which shows what a sign-in asks of the database; while a profile
/// scope is set, it sums each statement's executions and time under that scope.
/// </summary>
internal sealed class SqlCommandMeter : IDisposable
{
    private const string SqlClientListener = "SqlClientDiagnosticListener";
    private const string SqlClientCommandPrefix = "Microsoft.Data.SqlClient.WriteCommand";
    private const string NpgsqlSource = "Npgsql";

    private readonly string _database;
    private readonly ConcurrentDictionary<Guid, (long Timestamp, string Text)> _started = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ActivityListener? _npgsql;
    private readonly ConcurrentDictionary<(string Scope, string Text), StatementTally> _profile = new();
    private List<string>? _recording;
    private string? _profileScope;
    private long _commands;
    private long _elapsedTicks;

    public SqlCommandMeter(DatabaseProvider provider, string database)
    {
        _database = database;
        if (provider == DatabaseProvider.SqlServer)
        {
            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(new ListenerObserver(this)));
            return;
        }

        _npgsql = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NpgsqlSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnNpgsqlActivityStopped
        };
        ActivitySource.AddActivityListener(_npgsql);
    }

    public SqlCommandTally Read()
        => new(Interlocked.Read(ref _commands), TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks)));

    /// <summary>
    /// The scope (a flow's name) the commands counted from now on are profiled under, or null to
    /// stop profiling. Sign-ins run one at a time, so a command belongs to the scope set when it ran.
    /// </summary>
    public string? ProfileScope
    {
        get => Volatile.Read(ref _profileScope);
        set => Volatile.Write(ref _profileScope, value);
    }

    /// <summary>
    /// Every statement profiled under <paramref name="scope"/>, with its executions and its time
    /// divided by <paramref name="signIns"/>, most time first.
    /// </summary>
    public IReadOnlyList<StatementProfile> Profile(string scope, int signIns)
        => _profile
            .Where(entry => entry.Key.Scope == scope)
            .Select(entry => new StatementProfile(
                entry.Key.Text,
                (double)Interlocked.Read(ref entry.Value.Executions) / signIns,
                TimeSpan.FromTicks(Interlocked.Read(ref entry.Value.ElapsedTicks)).TotalMilliseconds / signIns))
            .OrderByDescending(statement => statement.MillisecondsPerSignIn)
            .ToList();

    /// <summary>Starts keeping the text of every command counted from now on.</summary>
    public void StartRecording() => Volatile.Write(ref _recording, []);

    /// <summary>The texts of the commands counted since <see cref="StartRecording"/>, in completion order.</summary>
    public IReadOnlyList<string> StopRecording()
    {
        var recording = Interlocked.Exchange(ref _recording, null) ?? [];
        lock (recording)
        {
            return recording.ToList();
        }
    }

    public void Dispose()
    {
        _npgsql?.Dispose();
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }
    }

    private void OnNpgsqlActivityStopped(Activity activity)
    {
        // Command activities carry the statement; anything else Npgsql traces is not a command.
        if (activity.GetTagItem("db.statement") == null
            || !string.Equals(activity.GetTagItem("db.name") as string, _database, StringComparison.Ordinal))
        {
            return;
        }

        Record(activity.Duration, activity.GetTagItem("db.statement") as string);
    }

    private void OnSqlClientEvent(string name, object? payload)
    {
        if (payload == null || !name.StartsWith(SqlClientCommandPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var properties = PayloadProperties.For(payload.GetType());
        if (properties.OperationId.GetValue(payload) is not Guid operation)
        {
            return;
        }

        if (name.EndsWith("Before", StringComparison.Ordinal))
        {
            if (properties.Command.GetValue(payload) is DbCommand command
                && string.Equals(command.Connection?.Database, _database, StringComparison.OrdinalIgnoreCase))
            {
                _started[operation] = ((long)properties.Timestamp.GetValue(payload)!, command.CommandText);
            }

            return;
        }

        // WriteCommandAfter or WriteCommandError: only commands this meter saw start count.
        if (_started.TryRemove(operation, out var started))
        {
            Record(Stopwatch.GetElapsedTime(started.Timestamp, (long)properties.Timestamp.GetValue(payload)!), started.Text);
        }
    }

    private void Record(TimeSpan elapsed, string? text)
    {
        Interlocked.Increment(ref _commands);
        Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);
        text ??= "(no command text)";
        if (Volatile.Read(ref _recording) is { } recording)
        {
            lock (recording)
            {
                recording.Add(text);
            }
        }

        if (ProfileScope is { } scope)
        {
            var tally = _profile.GetOrAdd((scope, text), static _ => new StatementTally());
            Interlocked.Increment(ref tally.Executions);
            Interlocked.Add(ref tally.ElapsedTicks, elapsed.Ticks);
        }
    }

    private sealed class StatementTally
    {
        public long Executions;
        public long ElapsedTicks;
    }

    /// <summary>SqlClient 5.x publishes anonymous payloads; their property lookups are cached per type.</summary>
    private sealed record PayloadProperties(PropertyInfo OperationId, PropertyInfo Command, PropertyInfo Timestamp)
    {
        private static readonly ConcurrentDictionary<Type, PayloadProperties> Cache = new();

        public static PayloadProperties For(Type type)
            => Cache.GetOrAdd(type, payloadType => new PayloadProperties(
                Property(payloadType, "OperationId"),
                Property(payloadType, "Command"),
                Property(payloadType, "Timestamp")));

        private static PropertyInfo Property(Type type, string name)
            => type.GetProperty(name)
                ?? throw new InvalidOperationException($"SqlClient diagnostic payload {type} has no {name} property.");
    }

    private sealed class ListenerObserver(SqlCommandMeter meter) : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != SqlClientListener)
            {
                return;
            }

            var subscription = listener.Subscribe(
                new EventObserver(meter),
                name => name.StartsWith(SqlClientCommandPrefix, StringComparison.Ordinal));
            lock (meter._subscriptions)
            {
                meter._subscriptions.Add(subscription);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private sealed class EventObserver(SqlCommandMeter meter) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value) => meter.OnSqlClientEvent(value.Key, value.Value);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}

/// <param name="Statement">The command text.</param>
/// <param name="ExecutionsPerSignIn">How often a sign-in ran it, on average.</param>
/// <param name="MillisecondsPerSignIn">Its time per sign-in, on average.</param>
internal sealed record StatementProfile(string Statement, double ExecutionsPerSignIn, double MillisecondsPerSignIn);

/// <param name="Commands">Commands completed so far.</param>
/// <param name="Elapsed">Their summed durations.</param>
internal readonly record struct SqlCommandTally(long Commands, TimeSpan Elapsed)
{
    public static SqlCommandTally operator +(SqlCommandTally left, SqlCommandTally right)
        => new(left.Commands + right.Commands, left.Elapsed + right.Elapsed);

    public static SqlCommandTally operator -(SqlCommandTally after, SqlCommandTally before)
        => new(after.Commands - before.Commands, after.Elapsed - before.Elapsed);
}
