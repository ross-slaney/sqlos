using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlOS.BehaviorLock.Infrastructure.Scenarios;

/// <summary>
/// Marks a behavior-lock scenario. Use it instead of <c>[TestMethod]</c>: it records where the
/// scenario lives (approved transcripts sit next to its source file) and, when
/// <c>BEHAVIOR_LOCK_REPEAT</c> is 2 or more, runs the scenario that many times on fresh hosts and
/// fails unless every run produces the identical scrubbed transcript.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ScenarioAttribute : TestMethodAttribute
{
    public const string RepeatEnvironmentVariable = "BEHAVIOR_LOCK_REPEAT";

    public ScenarioAttribute([CallerFilePath] string sourceFile = "")
    {
        SourceFile = sourceFile;
    }

    public string SourceFile { get; }

    public static int Repeat
        => int.TryParse(Environment.GetEnvironmentVariable(RepeatEnvironmentVariable), out var repeat) && repeat > 1
            ? repeat
            : 1;

    public override TestResult[] Execute(ITestMethod testMethod)
    {
        var repeat = Repeat;
        var context = new ScenarioContext(testMethod.MethodInfo, SourceFile, repeat);
        var results = new List<TestResult>();
        for (var run = 1; run <= repeat; run++)
        {
            context.BeginRun(run);
            using (ScenarioContext.Use(context))
            {
                var result = testMethod.Invoke(null);
                if (result.Outcome == UnitTestOutcome.Passed && !context.ApprovedThisRun)
                {
                    // A scenario that never approves its transcript locks nothing.
                    result.Outcome = UnitTestOutcome.Failed;
                    result.TestFailureException = new AssertFailedException(
                        $"{context.Name} finished without calling Transcript.ApproveAsync() (or another approval).");
                }

                results.Add(result);
                if (result.Outcome != UnitTestOutcome.Passed)
                {
                    break;
                }
            }
        }

        // One result per scenario: the last run's, which carries a determinism failure if any.
        return [results[^1]];
    }
}

/// <summary>
/// Declares a route the scenario exercises, as <c>"METHOD /route/template"</c> exactly as the
/// endpoint's <c>RoutePattern.RawText</c> (for example <c>"POST /sqlos/auth/token"</c>) or as a
/// dashboard manifest entry. The coverage gate counts these; the transcript fails when a declared
/// route was not hit by an observed exchange.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CoversAttribute : Attribute
{
    public CoversAttribute(string route)
    {
        var separator = route.IndexOf(' ', StringComparison.Ordinal);
        if (separator <= 0 || separator == route.Length - 1)
        {
            throw new ArgumentException($"Covers route '{route}' must be 'METHOD /route/template'.", nameof(route));
        }

        Method = route[..separator].Trim().ToUpperInvariant();
        Template = route[(separator + 1)..].Trim();
    }

    public string Method { get; }

    public string Template { get; }

    public string Route => $"{Method} {Template}";
}

/// <summary>The scenario being executed, flowed to <c>Transcript.StartAsync</c> by <see cref="ScenarioAttribute"/>.</summary>
public sealed class ScenarioContext
{
    private static readonly AsyncLocal<ScenarioContext?> CurrentContext = new();
    private readonly List<string> _runTranscripts = [];

    internal ScenarioContext(MethodInfo method, string sourceFile, int repeat)
    {
        Method = method;
        SourceFile = sourceFile;
        Repeat = repeat;
        Covers = method.GetCustomAttributes<CoversAttribute>().ToList();
    }

    public static ScenarioContext? Current => CurrentContext.Value;

    public MethodInfo Method { get; }

    public string ClassName => Method.DeclaringType!.Name;

    public string MethodName => Method.Name;

    /// <summary>The scenario's identity in approvals and reports: <c>Class.Method</c>.</summary>
    public string Name => $"{ClassName}.{MethodName}";

    public string SourceFile { get; }

    public int Repeat { get; }

    public int Run { get; private set; }

    public IReadOnlyList<CoversAttribute> Covers { get; }

    public bool IsFinalRun => Run >= Repeat;

    internal IReadOnlyList<string> RunTranscripts => _runTranscripts;

    /// <summary>Whether the current run reached an approval.</summary>
    public bool ApprovedThisRun { get; private set; }

    internal void BeginRun(int run)
    {
        Run = run;
        ApprovedThisRun = false;
    }

    /// <summary>Called by every approval so a scenario cannot pass without locking anything.</summary>
    public void MarkApproved() => ApprovedThisRun = true;

    internal void RecordRunTranscript(string transcript) => _runTranscripts.Add(transcript);

    internal static IDisposable Use(ScenarioContext context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Restore(previous);
    }

    private sealed class Restore(ScenarioContext? previous) : IDisposable
    {
        public void Dispose() => CurrentContext.Value = previous;
    }
}
