using System.Diagnostics;
using System.Net;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>
/// Runs the flows: creates every account first and brings each to the state its flow starts from,
/// warms up, records the SQL of one more sign-in per flow, then measures. Warm-up and measurement
/// interleave the flows round-robin (one sign-in of each per round), so drift on the machine during
/// a run affects every flow alike. Every measured sign-in has an account of its own and comes from
/// a client address no other request used.
/// </summary>
internal sealed class SignInRunner(BenchmarkHost host, SqlCommandMeter meter, IReadOnlyList<SignInFlow> flows, int warmup, int iterations)
{
    private uint _nextAddress;

    public async Task<SignInMeasurement> RunAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        // Per flow: the warm-up's accounts, one for the traced sign-in, then the measured ones.
        var accounts = new Dictionary<SignInFlow, IReadOnlyList<BenchmarkUser>>();
        foreach (var flow in flows)
        {
            accounts[flow] = await host.CreateUsersAsync(flow.Name, warmup + 1 + iterations, flow.UsesPassword, cancellationToken);
        }

        foreach (var flow in flows)
        {
            await flow.PrepareAsync(accounts[flow], user => SignInAsync(flow, user, cancellationToken), cancellationToken);
        }

        Console.WriteLine($"Prepared {flows.Count * (warmup + 1 + iterations)} accounts in {clock.Elapsed.TotalSeconds:F0}s; warming up with {warmup} sign-ins per scenario");
        for (var round = 0; round < warmup; round++)
        {
            foreach (var flow in flows)
            {
                await SignInAsync(flow, accounts[flow][round], cancellationToken);
            }
        }

        var traces = new Dictionary<SignInFlow, IReadOnlyList<string>>();
        foreach (var flow in flows)
        {
            meter.StartRecording();
            await SignInAsync(flow, accounts[flow][warmup], cancellationToken);
            traces[flow] = meter.StopRecording();
        }

        // Measure from a collected heap, so a collection the warm-up made due does not land in the
        // first samples; collections the sign-ins cause themselves are part of their latency.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var samples = flows.ToDictionary(flow => flow, _ => new List<IterationSample>(iterations));
        var loadBefore = MachineLoad.Read();
        var collections = (Gen0: GC.CollectionCount(0), Gen1: GC.CollectionCount(1), Gen2: GC.CollectionCount(2));
        clock.Restart();
        for (var round = 0; round < iterations; round++)
        {
            foreach (var flow in flows)
            {
                meter.ProfileScope = flow.Name;
                samples[flow].Add(await SignInAsync(flow, accounts[flow][warmup + 1 + round], cancellationToken));
                meter.ProfileScope = null;
            }

            if ((round + 1) % 100 == 0 || round + 1 == iterations)
            {
                Console.WriteLine($"  measured {round + 1}/{iterations} rounds ({clock.Elapsed.TotalSeconds:F0}s)");
            }
        }

        return new SignInMeasurement(
            flows.Select(flow => new FlowMeasurement(flow, samples[flow], traces[flow], meter.Profile(flow.Name, iterations))).ToList(),
            new GarbageCollections(
                GC.CollectionCount(0) - collections.Gen0,
                GC.CollectionCount(1) - collections.Gen1,
                GC.CollectionCount(2) - collections.Gen2),
            loadBefore,
            MachineLoad.Read());
    }

    private async Task<IterationSample> SignInAsync(SignInFlow flow, BenchmarkUser user, CancellationToken cancellationToken)
    {
        var recorder = new IterationRecorder(host.Client, meter, NextClientAddress(), flow.Steps.Count);
        await flow.RunAsync(user, recorder, cancellationToken);
        return recorder.Complete();
    }

    /// <summary>The next address in 198.18.0.0/15, the range RFC 2544 reserves for benchmarks.</summary>
    private string NextClientAddress()
    {
        var offset = _nextAddress++;
        if (offset >= 1u << 17)
        {
            throw new InvalidOperationException("The run needs more client addresses than 198.18.0.0/15 holds.");
        }

        return new IPAddress([198, (byte)(18 + (offset >> 16)), (byte)(offset >> 8), (byte)offset]).ToString();
    }
}

/// <param name="Flows">Each flow's measurement, in run order.</param>
/// <param name="Collections">Garbage collections during measurement, per generation.</param>
/// <param name="LoadBefore">The machine's load averages when measurement started.</param>
/// <param name="LoadAfter">The machine's load averages when it ended.</param>
internal sealed record SignInMeasurement(
    IReadOnlyList<FlowMeasurement> Flows,
    GarbageCollections Collections,
    string? LoadBefore,
    string? LoadAfter);

/// <param name="Flow">The flow.</param>
/// <param name="Samples">Its measured sign-ins.</param>
/// <param name="SqlTrace">The commands of the traced sign-in, in order.</param>
/// <param name="SqlProfile">Each statement's executions and time per measured sign-in.</param>
internal sealed record FlowMeasurement(
    SignInFlow Flow,
    IReadOnlyList<IterationSample> Samples,
    IReadOnlyList<string> SqlTrace,
    IReadOnlyList<StatementProfile> SqlProfile);
