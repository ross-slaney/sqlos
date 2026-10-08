using System.Diagnostics;

namespace SqlOS.Benchmarks;

/// <summary>Console progress with elapsed time, so CI logs show where the minutes go.</summary>
internal sealed class Log
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public TimeSpan Elapsed => _clock.Elapsed;

    public void Info(string message)
        => Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] {message}");
}
